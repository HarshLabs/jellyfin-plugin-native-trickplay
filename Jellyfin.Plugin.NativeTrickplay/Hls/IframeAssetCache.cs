using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

public sealed record IframeAsset(string PlaylistTemplate, string SegmentPath);

public sealed record CacheEntry(Guid ItemId, string Directory, long SizeBytes, DateTime LastAccessUtc);

/// <summary>
/// Snapshot of an in-flight generation, exposed to the dashboard for the
/// progress UI. Status is one of "queued" (waiting on the global encoder
/// semaphore) or "running" (ffmpeg actively encoding).
/// </summary>
public sealed record InflightState(
    Guid ItemId,
    string ItemName,
    DateTime StartedUtc,
    string Status,
    long? PartialBytes);

public sealed class IframeAssetCache
{
    private readonly ILogger<IframeAssetCache> _logger;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _encoder;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentDictionary<string, Lazy<Task<IframeAsset>>> _inflight = new();
    // Parallel tracking dictionary for the admin UI's progress feed. Keyed by
    // the same string (Guid "N" form) the inflight Lazy uses, so they stay in
    // sync. Holds runtime state (status + .tmp size) that the admin endpoint
    // can return without poking into Lazy<Task> internals.
    private readonly ConcurrentDictionary<string, InflightProgress> _inflightProgress = new();

    // Priority-aware slot manager. Replaces a plain SemaphoreSlim because
    // the user wants playback-triggered encodes to leapfrog a long
    // bulk-generate queue (e.g. "I just clicked Play; I don't want to
    // wait for the other 199 items to encode first"). Non-preemptive:
    // the currently-running encode finishes naturally, and pending High
    // items move to the front of the wait queue ahead of pending Normal
    // items.
    private readonly object _slotLock = new();
    private int _slotsAvailable;
    private readonly List<WaitNode> _waitQueue = new();
    private long _waitSequence;

    private const int PriorityHigh = 100;   // playback / scrub triggers
    private const int PriorityNormal = 50;  // admin Generate, pre-gen task
    private const int PriorityLow = 10;     // startup resume scan

    private sealed record WaitNode(int Priority, TaskCompletionSource<bool> Tcs, Guid ItemId, long Sequence);

    private sealed class InflightProgress
    {
        public Guid ItemId { get; init; }
        public string Name { get; init; } = string.Empty;
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public string Status { get; set; } = "queued";
        public string? TmpSegmentPath { get; set; }
    }

    public IframeAssetCache(
        ILogger<IframeAssetCache> logger,
        IApplicationPaths paths,
        ILibraryManager libraryManager,
        IMediaEncoder encoder,
        IServerConfigurationManager serverConfig,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _paths = paths;
        _libraryManager = libraryManager;
        _encoder = encoder;
        _serverConfig = serverConfig;
        _lifetime = lifetime;
        var max = Plugin.Instance?.Configuration.MaxConcurrentGenerations ?? 1;
        _slotsAvailable = Math.Max(1, max);
    }

    /// <summary>
    /// Acquire one of the limited encoder slots, honoring priority. High-
    /// priority callers (playback / scrub) leapfrog Normal-priority callers
    /// (admin Generate, pre-gen task) and Low-priority (startup resume).
    /// Non-preemptive: a slot that's already encoding stays with that
    /// encode until it finishes; priority only affects WHO gets the next
    /// slot when one frees up.
    /// </summary>
    private async Task AcquireSlotAsync(int priority, Guid itemId, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        WaitNode node;
        lock (_slotLock)
        {
            if (_slotsAvailable > 0 && _waitQueue.Count == 0)
            {
                _slotsAvailable--;
                return;
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = new WaitNode(priority, tcs, itemId, Interlocked.Increment(ref _waitSequence));
            _waitQueue.Add(node);
            ReorderQueueLocked();
        }
        using var reg = ct.Register(() =>
        {
            lock (_slotLock) { _waitQueue.Remove(node); }
            tcs.TrySetCanceled(ct);
        });
        await tcs.Task.ConfigureAwait(false);
    }

    private void ReleaseSlot()
    {
        TaskCompletionSource<bool>? hand = null;
        lock (_slotLock)
        {
            // Pick the highest-priority, oldest-sequence waiter that
            // hasn't been canceled. Skip any canceled stragglers.
            while (_waitQueue.Count > 0)
            {
                var head = _waitQueue[0];
                _waitQueue.RemoveAt(0);
                if (!head.Tcs.Task.IsCanceled)
                {
                    hand = head.Tcs;
                    break;
                }
            }
            if (hand == null) _slotsAvailable++;
        }
        hand?.TrySetResult(true);
    }

    /// <summary>
    /// Promote a queued item from a lower priority to High. No-op if the
    /// item isn't currently waiting (already running, never queued, or
    /// already at High priority). Used when a High-priority Warmup
    /// arrives for an item that's already queued at Normal priority —
    /// e.g. user kicked off a bulk library encode then clicked Play on
    /// one of those items.
    /// </summary>
    private void PromoteToPriority(Guid itemId, int newPriority = PriorityHigh)
    {
        lock (_slotLock)
        {
            for (int i = 0; i < _waitQueue.Count; i++)
            {
                var w = _waitQueue[i];
                if (w.ItemId == itemId && w.Priority < newPriority)
                {
                    _waitQueue[i] = w with { Priority = newPriority };
                    ReorderQueueLocked();
                    _logger.LogInformation(
                        "[NativeTrickplay] queue: promoted {ItemId} to priority {Priority}",
                        itemId, newPriority);
                    return;
                }
            }
        }
    }

    private void ReorderQueueLocked()
    {
        // Stable priority sort: descending priority, ascending sequence
        // within the same priority so original submission order is
        // preserved among same-priority items.
        _waitQueue.Sort((a, b) =>
        {
            var byPrio = b.Priority.CompareTo(a.Priority);
            return byPrio != 0 ? byPrio : a.Sequence.CompareTo(b.Sequence);
        });
    }

    /// <summary>
    /// Resolves the cache root, honoring the plugin's CacheDirectory config
    /// (empty falls back to Jellyfin's default cache path). If the configured
    /// path can't be created/accessed, logs a warning and uses the default —
    /// we never throw from the cache root resolver because every endpoint
    /// calls it on the hot path.
    /// </summary>
    private string GetCacheRoot()
    {
        var custom = Plugin.Instance?.Configuration.CacheDirectory;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                Directory.CreateDirectory(custom);
                return custom;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[NativeTrickplay] custom cache path {Path} unusable; using default",
                    custom);
            }
        }
        return Path.Combine(_paths.CachePath, "native-trickplay");
    }

    /// <summary>
    /// Synchronous, no-side-effect check: is the asset already on disk, fresh
    /// (mtime matches), AND encoded in the format the current source range
    /// requires? Returns the asset or null. Used by the controller hot path
    /// so that fully-cached items respond in &lt; 1 ms with no generation work.
    /// </summary>
    public IframeAsset? TryGetCached(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrEmpty(item.Path))
        {
            _logger.LogDebug("[NativeTrickplay] cache lookup miss for {ItemId}: item not found or has no path", itemId);
            return null;
        }

        var dir = Path.Combine(GetCacheRoot(), itemId.ToString("N"));
        var playlistPath = Path.Combine(dir, "iframe.m3u8");
        var segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        if (!File.Exists(playlistPath) || !File.Exists(segmentPath) || !File.Exists(stampPath))
        {
            _logger.LogDebug(
                "[NativeTrickplay] cache lookup miss for {ItemId}: files absent (playlist={HasPlaylist} segment={HasSegment} stamp={HasStamp})",
                itemId, File.Exists(playlistPath), File.Exists(segmentPath), File.Exists(stampPath));
            return null;
        }

        try
        {
            var sourceMtime = File.GetLastWriteTimeUtc(item.Path);
            var stampContent = File.ReadAllText(stampPath);
            if (!ParseStamp(stampContent, out var stampedMtime, out var stampedEncoder))
            {
                // Pre-v1.1.0 stamp files held only the raw mtime number with
                // no encoder tag suffix. They no longer parse with the
                // current `<ticks>:<encoder>` format. Surface this distinct
                // from a real mtime mismatch so users debugging logs can
                // tell "old plugin version" from "source file changed".
                _logger.LogInformation(
                    "[NativeTrickplay] cache invalidated for {ItemId} ({Name}): legacy/corrupt stamp file — will re-encode (raw='{Raw}')",
                    itemId, item.Name,
                    stampContent.Length > 60 ? stampContent[..60] + "…" : stampContent);
                return null;
            }
            if (stampedMtime != sourceMtime.Ticks)
            {
                _logger.LogInformation(
                    "[NativeTrickplay] cache invalidated for {ItemId} ({Name}): source file modified since last encode (stamp={StampMtime} source={SourceMtime})",
                    itemId, item.Name, stampedMtime, sourceMtime.Ticks);
                return null;
            }

            // Encoder must match the variant we'd produce now for this source —
            // catches v1.0→v1.1 upgrade where HDR items have stale SDR caches,
            // or any future format change.
            var expectedEncoder = IframeFormat.EncoderTag(IframeFormatFor(item));
            if (!string.Equals(stampedEncoder, expectedEncoder, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[NativeTrickplay] cache invalidated for {ItemId} ({Name}): encoder tag mismatch (stamp='{StampEncoder}' expected='{ExpectedEncoder}')",
                    itemId, item.Name, stampedEncoder, expectedEncoder);
                return null;
            }

            var template = File.ReadAllText(playlistPath);
            _logger.LogDebug(
                "[NativeTrickplay] cache HIT for {ItemId} ({Name}): playlist={PlaylistBytes}B encoder={Encoder}",
                itemId, item.Name, template.Length, stampedEncoder);
            return new IframeAsset(template, segmentPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] cache lookup IO error for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// What I-frame variant should we encode/advertise for this item, based on
    /// its primary video stream's color range? Single source of truth used by
    /// both the cache (encoder selection) and the playlist injector
    /// (codec/VIDEO-RANGE declaration).
    /// </summary>
    internal IframeVariant IframeFormatFor(BaseItem item)
    {
        var ms = item.GetMediaSources(false)?.Count > 0 ? item.GetMediaSources(false)[0] : null;
        var video = ms?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return video is null ? IframeVariant.Sdr : IframeFormat.FromVideoRange(video.VideoRangeType);
    }

    /// <summary>
    /// Stamp file format: "&lt;mtime-ticks&gt;:&lt;encoder-tag&gt;". Old v1.0
    /// stamps contain only the ticks (no colon) and parse as invalid here —
    /// natural cache invalidation on upgrade.
    /// </summary>
    private static bool ParseStamp(string content, out long mtimeTicks, out string encoder)
    {
        mtimeTicks = 0;
        encoder = string.Empty;
        var colon = content.IndexOf(':');
        if (colon <= 0) return false;
        if (!long.TryParse(content.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out mtimeTicks))
            return false;
        encoder = content[(colon + 1)..].Trim();
        return encoder.Length > 0;
    }

    private static string FormatStamp(long mtimeTicks, string encoder) =>
        string.Create(CultureInfo.InvariantCulture, $"{mtimeTicks}:{encoder}");

    /// <summary>
    /// Fire-and-forget warmup. Idempotent under concurrency thanks to the
    /// in-flight Lazy dictionary. Safe to call from event handlers
    /// (PlaybackStart, etc) where we do not want to block.
    /// </summary>
    public void Warmup(Guid itemId) => Warmup(itemId, isPriority: false);

    /// <summary>
    /// Fire-and-forget warmup with priority hint. <paramref name="isPriority"/>
    /// =true marks the encode as playback-relevant so it leapfrogs any
    /// pending bulk/background work in the queue. If the item is already
    /// queued at a lower priority when this is called, it gets promoted
    /// in-place — the user clicking Play on item #150 of a 200-item bulk
    /// library encode moves that item to the front instead of waiting
    /// behind 149 others.
    /// </summary>
    public void Warmup(Guid itemId, bool isPriority)
    {
        _logger.LogInformation(
            "[NativeTrickplay] warmup requested for {ItemId} (priority={Priority})",
            itemId, isPriority ? "high" : "normal");
        if (isPriority) PromoteToPriority(itemId);
        _ = GetOrCreateAsync(itemId, isPriority).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception?.GetBaseException(),
                    "[NativeTrickplay] warmup failed for {ItemId}", itemId);
            }
            else if (t.IsCompletedSuccessfully)
            {
                _logger.LogInformation("[NativeTrickplay] warmup completed for {ItemId}", itemId);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Returns the in-flight or completed task that produces the asset. Internally
    /// uses the application lifetime cancellation token, NOT a per-request token —
    /// this is critical: if we tied generation to the HTTP request, AVPlayer's
    /// timeout on the slow first manifest fetch would kill ffmpeg, leaving the
    /// asset perpetually broken.
    /// </summary>
    public Task<IframeAsset> GetOrCreateAsync(Guid itemId) => GetOrCreateAsync(itemId, isPriority: false);

    /// <summary>
    /// Same as <see cref="GetOrCreateAsync(Guid)"/> but lets the caller mark
    /// this request as playback-priority so it leapfrogs background work in
    /// the encoder slot queue. If a Lazy is already in flight for this item,
    /// the priority is also pushed through <see cref="PromoteToPriority"/> so
    /// an item already queued at Normal jumps to High in place.
    /// </summary>
    public Task<IframeAsset> GetOrCreateAsync(Guid itemId, bool isPriority)
    {
        var key = itemId.ToString("N");
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<IframeAsset>>(
            () => GenerateAsync(itemId, isPriority, _lifetime.ApplicationStopping),
            LazyThreadSafetyMode.ExecutionAndPublication));
        if (isPriority) PromoteToPriority(itemId);
        var task = lazy.Value;
        if (task.IsCompleted) _inflight.TryRemove(key, out _);
        return task;
    }

    public IEnumerable<CacheEntry> EnumerateCache()
    {
        var root = GetCacheRoot();
        if (!Directory.Exists(root)) yield break;

        foreach (var dirPath in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dirPath);
            if (!Guid.TryParseExact(name, "N", out var itemId)) continue;

            long size = 0;
            DateTime lastAccess = DateTime.MinValue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath))
                {
                    var fi = new FileInfo(file);
                    size += fi.Length;
                    var t = fi.LastAccessTimeUtc;
                    if (t > lastAccess) lastAccess = t;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (lastAccess == DateTime.MinValue) lastAccess = DateTime.UtcNow;
            yield return new CacheEntry(itemId, dirPath, size, lastAccess);
        }
    }

    public bool TryEvict(Guid itemId)
    {
        var key = itemId.ToString("N");
        if (_inflight.ContainsKey(key)) return false;

        var dir = Path.Combine(GetCacheRoot(), key);
        if (!Directory.Exists(dir)) return false;

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<IframeAsset> GenerateAsync(Guid itemId, bool isPriority, CancellationToken ct)
    {
        var priority = isPriority ? PriorityHigh : PriorityNormal;
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrEmpty(item.Path))
        {
            throw new InvalidOperationException($"Item {itemId} not found or has no path.");
        }

        var sourcePath = item.Path;
        var sourceMtime = File.GetLastWriteTimeUtc(sourcePath);
        var variant = IframeFormatFor(item);
        var encoderTag = IframeFormat.EncoderTag(variant);
        var sourceSize = new FileInfo(sourcePath).Length;

        var dir = Path.Combine(GetCacheRoot(), itemId.ToString("N"));
        Directory.CreateDirectory(dir);

        var playlistPath = Path.Combine(dir, "iframe.m3u8");
        var segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        // Fast path: already cached, fresh, and encoded in the right format.
        if (File.Exists(playlistPath) && File.Exists(segmentPath) && File.Exists(stampPath))
        {
            var stampContent = await File.ReadAllTextAsync(stampPath, ct).ConfigureAwait(false);
            if (ParseStamp(stampContent, out var stampedMtime, out var stampedEncoder)
                && stampedMtime == sourceMtime.Ticks
                && stampedEncoder == encoderTag)
            {
                _logger.LogDebug(
                    "[NativeTrickplay] GenerateAsync fast-path hit for {ItemId} ({Name})",
                    itemId, item.Name);
                return new IframeAsset(
                    await File.ReadAllTextAsync(playlistPath, ct).ConfigureAwait(false), segmentPath);
            }
        }

        _logger.LogInformation(
            "[NativeTrickplay] generation START for {ItemId} ({Name}): source={SourcePath} ({SourceMb} MiB), encoder={Encoder}, awaiting concurrency slot...",
            itemId, item.Name, sourcePath, sourceSize / (1024 * 1024), encoderTag);

        var key = itemId.ToString("N");
        var progress = new InflightProgress
        {
            ItemId = itemId,
            Name = item.Name ?? "(unnamed)",
            Status = "queued",
        };
        _inflightProgress[key] = progress;

        var slotWaitSw = Stopwatch.StartNew();
        await AcquireSlotAsync(priority, itemId, ct).ConfigureAwait(false);
        slotWaitSw.Stop();
        if (slotWaitSw.ElapsedMilliseconds > 100)
        {
            _logger.LogInformation(
                "[NativeTrickplay] generation queued behind {WaitMs}ms wait for {ItemId} (priority={Priority})",
                slotWaitSw.ElapsedMilliseconds, itemId, priority);
        }
        try
        {
            var tmpSegmentPath = segmentPath + ".tmp";
            if (File.Exists(tmpSegmentPath)) File.Delete(tmpSegmentPath);

            // Mark progress as running and expose the .tmp path so the
            // admin UI can read its size and report % done.
            progress.Status = "running";
            progress.TmpSegmentPath = tmpSegmentPath;

            var ffmpegSw = Stopwatch.StartNew();
            await RunFfmpegAsync(sourcePath, tmpSegmentPath, variant, ct).ConfigureAwait(false);
            ffmpegSw.Stop();
            var encodedBytes = File.Exists(tmpSegmentPath) ? new FileInfo(tmpSegmentPath).Length : 0;
            _logger.LogInformation(
                "[NativeTrickplay] ffmpeg encode finished for {ItemId} in {ElapsedMs}ms ({EncodedKb} KiB output)",
                itemId, ffmpegSw.ElapsedMilliseconds, encodedBytes / 1024);

            var scanSw = Stopwatch.StartNew();
            var (initSize, fragments) = Mp4BoxScanner.Scan(tmpSegmentPath);
            scanSw.Stop();
            if (fragments.Count == 0)
            {
                _logger.LogError(
                    "[NativeTrickplay] box scan found 0 fragments in {Path} ({Bytes}B) — encoder produced no output",
                    tmpSegmentPath, encodedBytes);
                throw new InvalidOperationException("ffmpeg produced no fragments.");
            }
            _logger.LogInformation(
                "[NativeTrickplay] box scan for {ItemId} found {Fragments} fragments + init={InitBytes}B in {ElapsedMs}ms",
                itemId, fragments.Count, initSize, scanSw.ElapsedMilliseconds);

            var probeSw = Stopwatch.StartNew();
            var durations = await ProbePtsDeltasAsync(tmpSegmentPath, fragments.Count, ct).ConfigureAwait(false);
            probeSw.Stop();
            var template = BuildPlaylist(initSize, fragments, durations);
            var totalDuration = durations.Sum();
            _logger.LogInformation(
                "[NativeTrickplay] PTS probe for {ItemId} took {ElapsedMs}ms; total trickplay duration {DurationSec:F1}s, playlist={PlaylistBytes}B",
                itemId, probeSw.ElapsedMilliseconds, totalDuration, template.Length);

            File.Move(tmpSegmentPath, segmentPath, overwrite: true);
            await File.WriteAllTextAsync(playlistPath, template, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(stampPath, FormatStamp(sourceMtime.Ticks, encoderTag), ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[NativeTrickplay] generation DONE for {ItemId} ({Name}): {Frames} {Variant} I-frames, {EncodedMb} MiB, {Elapsed}ms total",
                itemId, item.Name, fragments.Count, encoderTag, encodedBytes / (1024 * 1024),
                ffmpegSw.ElapsedMilliseconds + scanSw.ElapsedMilliseconds + probeSw.ElapsedMilliseconds);
            return new IframeAsset(template, segmentPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "[NativeTrickplay] generation FAILED for {ItemId} ({Name})",
                itemId, item.Name);
            throw;
        }
        finally
        {
            _inflightProgress.TryRemove(key, out _);
            ReleaseSlot();
        }
    }

    /// <summary>
    /// Snapshot of every generation currently queued or running. Used by the
    /// admin dashboard's progress polling endpoint. Returns at most one entry
    /// per item; ordering is unspecified.
    /// </summary>
    public IEnumerable<InflightState> EnumerateInFlight()
    {
        foreach (var p in _inflightProgress.Values)
        {
            long? partial = null;
            if (p.TmpSegmentPath is not null)
            {
                try { if (File.Exists(p.TmpSegmentPath)) partial = new FileInfo(p.TmpSegmentPath).Length; }
                catch (IOException) { /* file briefly inaccessible mid-write — surface as null */ }
            }
            yield return new InflightState(p.ItemId, p.Name, p.StartedUtc, p.Status, partial);
        }
    }

    private async Task RunFfmpegAsync(string inputPath, string outputPath, IframeVariant variant, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var isHdrSource = DetectHdrFromPath(inputPath);

        // First attempt: with hwaccel if configured.
        var hwaccelArgs = new List<string>();
        AppendHwaccelArgs(cfg, hwaccelArgs);

        var primaryArgs = BuildFfmpegArgs(cfg, inputPath, outputPath, variant, hwaccelArgs, isHdrSource);
        _logger.LogInformation(
            "[NativeTrickplay] ffmpeg invoke (hwaccel={Hwaccel}, preset={Preset}, crf={Crf}, width={Width}, interval={Interval}s, sourceHdr={IsHdr})",
            hwaccelArgs.Count > 0 ? string.Join(' ', hwaccelArgs) : "none",
            cfg.IframePreset, cfg.IframeCrf, cfg.IframeWidth, cfg.IframeIntervalSeconds, isHdrSource);
        _logger.LogDebug(
            "[NativeTrickplay] ffmpeg cmd: {Encoder} {Args}",
            _encoder.EncoderPath, string.Join(' ', primaryArgs));

        try
        {
            await RunProcessAsync(_encoder.EncoderPath, primaryArgs, ct).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException ex)
            when (hwaccelArgs.Count > 0 && IsLikelyHwaccelFailure(ex.Message))
        {
            // Recognizable hwaccel handoff failure (e.g. QSV+HEVC EBUSY,
            // VAAPI auto_scale gap, "Failed to transfer data to output frame").
            // Retry once with software decode — ffmpeg can always handle the
            // source if the GPU path is misbehaving for this specific codec.
            _logger.LogWarning(
                "[NativeTrickplay] hwaccel decode failed for {Input}, retrying with software decode.\nffmpeg stderr:\n{Stderr}",
                inputPath,
                TailLines(ex.Message, 12));
        }

        await RunProcessAsync(_encoder.EncoderPath,
            BuildFfmpegArgs(cfg, inputPath, outputPath, variant, hwaccelArgs: null, isHdrSource), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Quick HDR detection from the source file's container metadata. Uses
    /// ffprobe to read the first video stream's color_transfer; values like
    /// `smpte2084` (PQ) or `arib-std-b67` (HLG) signal HDR. Falls back to false
    /// (treat as SDR) on any error — branching to the SDR encoder path for an
    /// HDR source would just produce slightly-washed-out thumbnails, but
    /// branching to HDR for SDR fails with zscale "no path between colorspaces".
    /// </summary>
    private bool DetectHdrFromPath(string inputPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _encoder.ProbePath,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=color_transfer",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    inputPath,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(3000))
            {
                proc.Kill(entireProcessTree: true);
                return false;
            }
            var transfer = proc.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
            return transfer is "smpte2084" or "arib-std-b67";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[NativeTrickplay] HDR detection failed for {Path}, treating as SDR", inputPath);
            return false;
        }
    }

    private static List<string> BuildFfmpegArgs(
        PluginConfiguration cfg, string inputPath, string outputPath, IframeVariant variant, IReadOnlyList<string>? hwaccelArgs, bool isHdrSource)
    {
        var args = new List<string>(48)
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
        };

        // Hardware decode args go BEFORE -i.
        if (hwaccelArgs is { Count: > 0 }) args.AddRange(hwaccelArgs);

        // VideoToolbox auto-transfers decoded frames to CPU memory when no
        // -hwaccel_output_format is specified, so a CPU filter chain works
        // out of the box. QSV / VAAPI / CUDA / D3D11VA do NOT auto-transfer:
        // their decoder leaves frames in GPU memory in a hardware pixel
        // format. The first CPU-only filter (`fps`) then can't bridge to
        // them and ffmpeg fails with:
        //   "Impossible to convert between the formats supported by the
        //    filter 'Parsed_fps_0' and the filter 'auto_scale_0'"
        //
        // Fix: explicitly download GPU frames to CPU memory at the START
        // of the filter chain via `hwdownload,format=nv12|p010le`. The
        // alternation accepts either 8-bit (nv12) or 10-bit (p010le)
        // hardware output formats so we don't lose source bit depth before
        // tonemap. Skip for VideoToolbox where bare `-hwaccel` already
        // produces CPU frames — adding hwdownload there would fail because
        // there's nothing on the GPU side to download.
        var needsHwdownload = hwaccelArgs is { Count: > 0 } && (
            hwaccelArgs.Contains("qsv") ||
            hwaccelArgs.Contains("vaapi") ||
            hwaccelArgs.Contains("cuda") ||
            hwaccelArgs.Contains("d3d11va"));
        var hwdownloadPrefix = needsHwdownload ? "hwdownload,format=nv12|p010le," : string.Empty;

        var width = cfg.IframeWidth.ToString(CultureInfo.InvariantCulture);
        var interval = cfg.IframeIntervalSeconds <= 0 ? 1.0 : cfg.IframeIntervalSeconds;
        var fps = (1.0 / interval).ToString("0.######", CultureInfo.InvariantCulture);
        var intervalStr = interval.ToString("0.######", CultureInfo.InvariantCulture);

        args.AddRange(new[]
        {
            "-i", inputPath,
            // Track / metadata stripping. AVPlayer's I-frame variant decoder
            // silently rejects fMP4 init segments whose moov box contains
            // anything other than a single video track — the playlist
            // downloads but no segments are ever fetched, manifesting as
            // "thumbnail only at the current playhead".
            //
            // `-map 0:v:0 -an -sn -dn` is necessary but NOT sufficient: the
            // mp4 muxer auto-rewrites source chapters into a `text` data
            // track on the output side regardless of input mapping. To get a
            // clean video-only mdhd, also nuke chapters and global metadata.
            "-map", "0:v:0",
            "-an", "-sn", "-dn",
            "-map_chapters", "-1",
            "-map_metadata", "-1",
        });

        // Always SDR H.264 Main Level 4.0 per Apple HLS Authoring Spec §6.16
        // ("SDR trick play streams MUST be provided"). The CODECS string in
        // the I-FRAME-STREAM-INF line is `avc1.4d0028` — pinning -level:v 4.0
        // makes the encoder's emitted level_idc match exactly. AVPlayer
        // strictly validates declared-vs-actual; a level mismatch makes it
        // silently bypass the I-frame variant (playlist downloads OK,
        // segments never fetched).
        //
        // For HDR/DV sources (PQ or HLG), we ALSO must convert the bitstream's
        // color metadata to SDR BT.709, not just downscale 10→8 bit. Without
        // tone-mapping, ffmpeg passes through the source's `color_transfer=
        // smpte2084 / color_primaries=bt2020` tags into the H.264 VUI — so
        // the bitstream claims to be PQ HDR while the playlist declares
        // VIDEO-RANGE=SDR. AVPlayer parses init+first I-frame, sees the
        // bitstream/manifest disagree, and silently abandons the variant
        // (manifesting as "thumbnail only at the current playhead" for HDR
        // primaries — exactly the symptom v1.1.6 still had).
        //
        // For HDR sources: zscale + tonemap chain converts BT.2020/PQ→BT.709
        // with the Hable operator. For SDR sources: this chain throws "no path
        // between colorspaces" because the linear-light intermediate (npl=100)
        // is only defined for PQ-curve input. Branching is mandatory; the
        // chain is NOT safe to apply unconditionally despite earlier intuition
        // (v1.1.8 broke every SDR encode in the library).
        //
        // The fps filter thins the decoded stream to 1/interval Hz before the
        // encoder sees it, so trickplay density is uniform regardless of the
        // source's GOP layout. x264's keyint=1 then makes every (thinned)
        // output frame an IDR. Unlike x265, x264 honors keyint=1 cleanly
        // without flipping into a profile Apple decoders reject.
        var filterChain = isHdrSource
            ? hwdownloadPrefix +
              $"fps={fps}," +
              "zscale=t=linear:npl=100," +
              "format=gbrpf32le," +
              "zscale=p=bt709," +
              "tonemap=tonemap=hable:desat=0," +
              "zscale=t=bt709:m=bt709:r=tv," +
              "format=yuv420p," +
              $"scale=-2:{width}"
            : hwdownloadPrefix + $"fps={fps},scale=-2:{width},format=yuv420p";
        // x264-params color overrides are required: ffmpeg's -color_* output
        // flags set the AVStream metadata but x264 only embeds primaries /
        // transfer / matrix into the H.264 VUI when its own params are given
        // explicitly. Without this, color_transfer/primaries come out as
        // "unknown" in the bitstream VUI, defeating AVPlayer's variant-vs-
        // primary range-family check.
        args.AddRange(new[]
        {
            "-vf", filterChain,
            "-c:v", "libx264",
            "-preset", cfg.IframePreset,
            "-crf", cfg.IframeCrf.ToString(CultureInfo.InvariantCulture),
            "-profile:v", "main", "-level:v", "4.0",
            "-x264-params", "keyint=1:scenecut=0:open-gop=0:colorprim=bt709:transfer=bt709:colormatrix=bt709",
            "-color_primaries", "bt709",
            "-color_trc", "bt709",
            "-colorspace", "bt709",
            "-color_range", "tv",
        });
        _ = variant; // single variant — kept in API for callers; intentionally unused here
        _ = intervalStr;

        args.AddRange(new[]
        {
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-f", "mp4", outputPath
        });

        return args;
    }

    /// <summary>
    /// Heuristic: does the ffmpeg stderr look like a hwaccel handoff failure
    /// rather than a genuine source-file or argument problem? When true, the
    /// software-decode retry is worth attempting; when false, the failure
    /// would recur and we shouldn't waste another encode pass.
    /// </summary>
    private static bool IsLikelyHwaccelFailure(string stderr)
    {
        // Specific patterns observed across QSV/VAAPI/CUDA on real-world bug
        // reports. Cheap substring checks; each one is unambiguous about its
        // origin in the hwaccel pipeline.
        return stderr.Contains("Failed to transfer data to output frame", StringComparison.Ordinal)
            || stderr.Contains("Error synchronizing the operation", StringComparison.Ordinal)
            || stderr.Contains("Impossible to convert between the formats", StringComparison.Ordinal)
            || stderr.Contains("Cannot use AVHWFramesContext", StringComparison.Ordinal)
            || stderr.Contains("Unsupported or mismatching pixel format", StringComparison.Ordinal)
            || stderr.Contains("hwaccel", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOf('\n');
        return idx < 0 ? s : s[..idx];
    }

    /// <summary>
    /// Return the last <paramref name="n"/> non-empty lines of a multi-line
    /// string, joined with newlines. Used for hwaccel failure logging:
    /// libva / ffmpeg often print informational lines first ("VA-API version
    /// X.Y.Z") and the actual error at the end, so the tail tends to be
    /// the actionable part for the user.
    /// </summary>
    private static string TailLines(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var lines = s.Split('\n');
        var keep = new List<string>(n);
        for (int i = lines.Length - 1; i >= 0 && keep.Count < n; i--)
        {
            var line = lines[i].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line)) keep.Add(line);
        }
        keep.Reverse();
        return string.Join('\n', keep);
    }

    private void AppendHwaccelArgs(PluginConfiguration cfg, List<string> args)
    {
        if (!cfg.UseHardwareDecoding) return;

        EncodingOptions opts;
        try { opts = _serverConfig.GetEncodingOptions(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] could not read EncodingOptions, falling back to software decode");
            return;
        }

        // Map Jellyfin's HardwareAccelerationType enum to the corresponding ffmpeg
        // -hwaccel decoder. NVENC/AMF are encoder names, not decoders — for those
        // we map to the matching decode-side hwaccel that the same GPU exposes.
        // Per-hwaccel refinements mirror Jellyfin's own EncodingHelper.GetHwaccelType
        // (MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs).
        switch (opts.HardwareAccelerationType)
        {
            case HardwareAccelerationType.videotoolbox:
                args.Add("-hwaccel"); args.Add("videotoolbox");
                // Trickplay extraction is background work — yield GPU time to
                // foreground playback transcodes. Supported in jellyfin-ffmpeg 7.x.
                args.Add("-hwaccel_flags"); args.Add("+low_priority");
                break;
            case HardwareAccelerationType.qsv:
                args.Add("-hwaccel"); args.Add("qsv");
                if (!string.IsNullOrEmpty(opts.QsvDevice))
                {
                    args.Add("-qsv_device"); args.Add(opts.QsvDevice);
                }
                break;
            case HardwareAccelerationType.vaapi:
                args.Add("-hwaccel"); args.Add("vaapi");
                if (!string.IsNullOrEmpty(opts.VaapiDevice))
                {
                    args.Add("-vaapi_device"); args.Add(opts.VaapiDevice);
                }
                break;
            case HardwareAccelerationType.nvenc:
                // NVENC is the NVIDIA encoder; the matching decode hwaccel is `cuda`.
                // nvdec is single-threaded internally — ffmpeg's auto-thread heuristic
                // oversubscribes if we don't pin to 1.
                args.Add("-hwaccel"); args.Add("cuda");
                args.Add("-threads"); args.Add("1");
                break;
            case HardwareAccelerationType.amf:
                // AMF is AMD's encode framework; AMD decode in ffmpeg is platform-specific:
                // d3d11va on Windows, vaapi on Linux (via Mesa's amdgpu driver).
                if (OperatingSystem.IsWindows())
                {
                    args.Add("-hwaccel"); args.Add("d3d11va");
                    args.Add("-threads"); args.Add("2");
                }
                else if (OperatingSystem.IsLinux())
                {
                    args.Add("-hwaccel"); args.Add("vaapi");
                    if (!string.IsNullOrEmpty(opts.VaapiDevice))
                    {
                        args.Add("-vaapi_device"); args.Add(opts.VaapiDevice);
                    }
                }
                // macOS doesn't host AMD-specific hwaccel; fall through to software.
                break;
            case HardwareAccelerationType.v4l2m2m:
                // Used on embedded ARM Linux boards (Raspberry Pi, etc.). The DRM hwaccel
                // is the standard pipeline.
                args.Add("-hwaccel"); args.Add("drm");
                break;
            case HardwareAccelerationType.rkmpp:
                args.Add("-hwaccel"); args.Add("rkmpp");
                break;
            case HardwareAccelerationType.none:
            default:
                break;
        }
    }

    private async Task<IReadOnlyList<double>> ProbePtsDeltasAsync(string filePath, int expectedCount, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(_encoder.EncoderPath)!, "ffprobe");
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
            filePath
        };

        var stdout = await RunProcessAsync(ffprobe, args, ct, captureStdout: true).ConfigureAwait(false);
        var ptsList = new List<double>(expectedCount);
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                ptsList.Add(v);
            }
        }

        var durations = new double[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            if (i + 1 < ptsList.Count)
            {
                durations[i] = Math.Max(0.001, ptsList[i + 1] - ptsList[i]);
            }
            else if (i > 0 && durations[i - 1] > 0)
            {
                durations[i] = durations[i - 1];
            }
            else
            {
                durations[i] = 1.0;
            }
        }
        return durations;
    }

    private async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = captureStdout,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger.LogDebug("[NativeTrickplay] {File} {Args}", fileName, string.Join(' ', args));

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = captureStdout ? proc.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited {proc.ExitCode}: {stderr}");
        }
        return stdout;
    }

    private static string BuildPlaylist(long initSize, IReadOnlyList<FragmentRange> fragments, IReadOnlyList<double> durations)
    {
        var ci = CultureInfo.InvariantCulture;
        var maxDur = 0.0;
        for (int i = 0; i < durations.Count; i++) if (durations[i] > maxDur) maxDur = durations[i];

        var sb = new System.Text.StringBuilder(fragments.Count * 80);
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:7");
        sb.Append("#EXT-X-TARGETDURATION:").AppendLine(((int)Math.Ceiling(Math.Max(1, maxDur))).ToString(ci));
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        sb.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");
        sb.AppendLine("#EXT-X-I-FRAMES-ONLY");
        sb.Append("#EXT-X-MAP:URI=\"iframe.m4s{AUTH}\",BYTERANGE=\"")
          .Append(initSize.ToString(ci))
          .AppendLine("@0\"");

        for (int i = 0; i < fragments.Count; i++)
        {
            sb.Append("#EXTINF:").Append(durations[i].ToString("F3", ci)).AppendLine(",");
            sb.Append("#EXT-X-BYTERANGE:")
              .Append(fragments[i].Size.ToString(ci)).Append('@')
              .Append(fragments[i].Offset.ToString(ci))
              .AppendLine();
            sb.AppendLine("iframe.m4s{AUTH}");
        }
        sb.AppendLine("#EXT-X-ENDLIST");
        return sb.ToString();
    }
}
