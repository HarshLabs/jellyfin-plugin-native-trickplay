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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

public sealed record IframeAsset(string PlaylistTemplate, string SegmentPath);

public sealed record CacheEntry(Guid ItemId, string Directory, long SizeBytes, DateTime LastAccessUtc, bool IsComplete);

/// <summary>
/// Outcome of an orphan-reclaim pass. Orphans = complete cache entries whose
/// directory GUID no longer resolves to a library item; Relinked = how many of
/// those were matched to a current item and renamed to its id.
/// </summary>
public sealed record ReclaimResult(int Orphans, int Relinked);

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
    long? PartialBytes,
    long? EstimatedTotalBytes,
    long? EncodedSourceMicros,
    long? SourceDurationMicros,
    double? EncodingSpeed,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? SourceProfile,
    string? HardwarePath);

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
    private int _maxSlots;
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
        // Restamped when status flips from "queued" to "running" so the UI's
        // elapsed-time column reflects encode time, not queue-wait time. With
        // a bulk "Generate library" of thousands of items, the queue-time
        // value would otherwise show every actively-encoding item as having
        // been "running" for the entire duration of the bulk job.
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "queued";
        public string? TmpSegmentPath { get; set; }
        // Rough projected output size; used by the dashboard to render a
        // progress percentage (PartialBytes / EstimatedTotalBytes). Computed
        // once at encode start from item duration + iframe interval + a
        // tuned bytes-per-iframe constant. Real output usually lands within
        // ±20% of this; the UI caps the displayed percent at 99 to avoid
        // flickering past 100 if the estimate undershoots.
        // Used as a fallback for items with unknown source duration; the
        // primary progress signal is now time-based (see below).
        public long? EstimatedTotalBytes { get; set; }
        // Microseconds of source video that ffmpeg has consumed so far,
        // updated continuously from `-progress pipe:1` output (out_time_us
        // line). Combined with SourceDurationMicros this gives the dashboard
        // a real, monotonic progress signal — independent of how well the
        // byte estimate matches actual output. Null until the first progress
        // line lands or for sources without a known duration.
        public long? EncodedSourceMicros { get; set; }
        // Source video duration in microseconds, derived once from
        // item.RunTimeTicks at encode start. Null for items without metadata
        // duration (live recordings, damaged files); when null the UI falls
        // back to byte-based progress.
        public long? SourceDurationMicros { get; init; }
        // Per-item cancellation source, linked to the application-lifetime
        // token. Calling Cts.Cancel() interrupts whichever stage the encode
        // is in: queued (waitQueue removal in AcquireSlotAsync), running
        // (ffmpeg kill via Process.Kill(entireProcessTree:true)), or
        // post-process (Mp4BoxScanner / probe). Disposed by GenerateAsync
        // when the encode finishes — TryCancel must tolerate
        // ObjectDisposedException on a race with completion.
        public CancellationTokenSource? Cts { get; init; }
        // Current encoding speed multiplier vs realtime (e.g. 2.13x means
        // 1 minute of source consumed per 28s of wall time). Sourced from
        // ffmpeg's `speed=N.NNx` progress line. Used by the UI to render an
        // accurate ETA: eta_sec = (SourceDurationMicros - EncodedSourceMicros)
        // / 1e6 / EncodingSpeed. Null for items without a known source
        // duration or before the first progress block lands.
        public double? EncodingSpeed { get; set; }
        // Series + season/episode info for TV episode rows so the UI can
        // render "Series · S2E12 — Episode title" instead of a bare cryptic
        // episode name. Null for movies / non-episode items.
        public string? SeriesName { get; init; }
        public int? SeasonNumber { get; init; }
        public int? EpisodeNumber { get; init; }
        // Pre-formatted source profile for at-a-glance "why is this slow"
        // context (e.g. "2160p HEVC 10-bit HDR", "1080p H.264"). Populated
        // from the existing ProbeSourceVideo call once the encode actually
        // starts running; null while queued or for sources that probe fails on.
        public string? SourceProfile { get; set; }
        // Short tag for the decode hwaccel actually in use this run:
        // "QSV", "VAAPI", "CUDA", "VideoToolbox", "D3D11VA", or "SW".
        // Suffixed with the GPU tone-map path ("+OCL", "+CUDA", "+VT") when
        // active, e.g. "QSV+OCL" for Intel iGPU OpenCL-bridge tone-mapping.
        // Reset to "SW" on the software-decode retry path so the dashboard
        // accurately reflects which path each in-flight encode is taking.
        public string? HardwarePath { get; set; }
    }

    /// <summary>
    /// Which GPU tone-mapping path the current encode is using. Selected
    /// from (hwaccel × OS × ffmpeg-filter availability) in
    /// <see cref="SelectGpuTonemapPath"/>. <c>None</c> means CPU tone-mapping
    /// via the zscale + tonemap=hable chain (current behaviour, pre-1.1.49).
    /// </summary>
    private enum GpuTonemapPath { None, OpenCL, Cuda, VideoToolbox }

    /// <summary>
    /// Which <c>tonemap_*</c> filters the host ffmpeg build ships with.
    /// Probed once via <c>ffmpeg -filters</c> the first time an HDR encode
    /// runs, then cached for the lifetime of the plugin process. Defaulted
    /// to all-false on probe timeout / error — safer than over-eagerly
    /// emitting flags the build can't honor.
    /// </summary>
    private sealed record GpuTonemapSupport(bool HasOpenCL, bool HasCuda, bool HasVideoToolbox);

    private Task<GpuTonemapSupport>? _gpuTonemapSupportTask;

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
        _maxSlots = Math.Max(1, max);
        _slotsAvailable = _maxSlots;

        // React to dashboard config saves so "Concurrent encodes" applies
        // without a server restart (the config page promises changes take
        // effect immediately). Growing hands freed slots to waiters right
        // away; shrinking lets running encodes finish and drains the excess
        // naturally (_slotsAvailable goes negative until enough finish).
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, cfg) =>
            {
                if (cfg is PluginConfiguration pc) UpdateMaxConcurrency(pc.MaxConcurrentGenerations);
            };
        }
    }

    private void UpdateMaxConcurrency(int newMax)
    {
        newMax = Math.Max(1, newMax);
        int delta;
        lock (_slotLock)
        {
            delta = newMax - _maxSlots;
            if (delta == 0) return;
            _maxSlots = newMax;
            if (delta < 0) _slotsAvailable += delta;
        }
        _logger.LogInformation(
            "[NativeTrickplay] concurrency updated to {Max} ({Delta:+#;-#} slots)", newMax, delta);
        // Added slots are handed out through the normal release path so
        // queued waiters wake in priority order.
        for (int i = 0; i < delta; i++) ReleaseSlot();
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
        // Loop because TrySetResult may fail when a waiter is concurrently
        // canceled in the tiny race window between our IsCanceled check
        // (under _slotLock) and the actual TrySetResult call (outside the
        // lock). On TrySetResult-failed, retry with the next waiter; if
        // none remain, increment slotsAvailable. Without this loop, an
        // admin doing a "Cancel all" in the same tick as an encode finishing
        // could permanently lose a slot from the pool.
        while (true)
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
                if (hand == null)
                {
                    _slotsAvailable++;
                    return;
                }
            }
            if (hand.TrySetResult(true)) return;
            // TrySetResult failed — waiter was canceled in the race window.
            // Loop and try the next waiter.
        }
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
        if (!ValidateCached(itemId, out var playlistPath, out var segmentPath)) return null;
        try
        {
            var template = File.ReadAllText(playlistPath);
            _logger.LogDebug(
                "[NativeTrickplay] cache HIT for {ItemId}: playlist={PlaylistBytes}B",
                itemId, template.Length);
            return new IframeAsset(template, segmentPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] cache lookup IO error for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Presence-only variant of <see cref="TryGetCached"/> for callers that
    /// need a yes/no answer, not the playlist body. TryGetCached reads the
    /// full playlist text on every hit — harmless on the playback hot path
    /// (one item), but the dashboard's item listing, the playlist injector's
    /// pre-checks, and the prune task's stale scan were each paying a full
    /// playlist read PER ITEM just to test existence: on a fully-cached
    /// multi-thousand-item library that's hundreds of MB of file reads per
    /// dashboard page load. Same validation semantics, no body read.
    /// </summary>
    public bool IsCached(Guid itemId) => ValidateCached(itemId, out _, out _);

    /// <summary>
    /// Shared validation core: item exists with a path, all three cache
    /// files are present, the stamp parses, the source file's mtime still
    /// matches, and the encoder tag matches what we'd produce today.
    /// </summary>
    private bool ValidateCached(Guid itemId, out string playlistPath, out string segmentPath)
    {
        playlistPath = string.Empty;
        segmentPath = string.Empty;
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrEmpty(item.Path))
        {
            _logger.LogDebug("[NativeTrickplay] cache lookup miss for {ItemId}: item not found or has no path", itemId);
            return false;
        }

        var dir = Path.Combine(GetCacheRoot(), itemId.ToString("N"));
        playlistPath = Path.Combine(dir, "iframe.m3u8");
        segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        if (!File.Exists(playlistPath) || !File.Exists(segmentPath) || !File.Exists(stampPath))
        {
            _logger.LogDebug(
                "[NativeTrickplay] cache lookup miss for {ItemId}: files absent (playlist={HasPlaylist} segment={HasSegment} stamp={HasStamp})",
                itemId, File.Exists(playlistPath), File.Exists(segmentPath), File.Exists(stampPath));
            return false;
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
                return false;
            }
            if (stampedMtime != sourceMtime.Ticks)
            {
                _logger.LogInformation(
                    "[NativeTrickplay] cache invalidated for {ItemId} ({Name}): source file modified since last encode (stamp={StampMtime} source={SourceMtime})",
                    itemId, item.Name, stampedMtime, sourceMtime.Ticks);
                return false;
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
                return false;
            }

            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] cache lookup IO error for {ItemId}", itemId);
            return false;
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

    /// <summary>
    /// Walk the cache root and yield one entry per item-id directory.
    /// Includes BOTH completed encodes (with `.source-mtime` stamp) and
    /// incomplete leftovers (queued/in-flight directory + .tmp file, or a
    /// directory from a prior crashed encode). Completion is exposed via
    /// <see cref="CacheEntry.IsComplete"/> so callers can filter — the
    /// dashboard only shows complete entries (so the count matches what's
    /// actually playable), while the prune task wants the full set so it
    /// can clean up incomplete leftovers in Phase 0.
    /// </summary>
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
            bool stampExists = false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath))
                {
                    var fi = new FileInfo(file);
                    size += fi.Length;
                    var t = fi.LastAccessTimeUtc;
                    if (t > lastAccess) lastAccess = t;
                    if (fi.Name == ".source-mtime") stampExists = true;
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (lastAccess == DateTime.MinValue) lastAccess = DateTime.UtcNow;
            // IsComplete reflects "encode finished cleanly enough to write
            // the stamp" — the stamp is the LAST thing GenerateAsync writes,
            // so its presence implies playlist + segment also exist. After
            // a server restart mid-encode, the directory + .tmp file persist
            // but no stamp — those entries return IsComplete=false and the
            // dashboard correctly excludes them from "cached" counts.
            yield return new CacheEntry(itemId, dirPath, size, lastAccess, stampExists);
        }
    }

    /// <summary>
    /// Relink orphaned cache entries to their current library items. An entry
    /// becomes an "orphan" when the item GUID its directory is named after no
    /// longer exists — which happens to EVERY entry after a server reinstall
    /// or database rebuild, because Jellyfin derives item ids from library
    /// paths and re-mints them on the new install. The media files (and the
    /// finished trickplay assets pointing at them) are typically untouched,
    /// so instead of re-encoding the whole library we re-match each orphan to
    /// its item and rename the directory to the new id.
    ///
    /// Matching, strongest first:
    ///   1. Exact source-path match via the `.source-path` sidecar (written
    ///      since v1.1.52; older entries don't have one).
    ///   2. Source-file mtime ticks from the `.source-mtime` stamp vs the
    ///      item's current file mtime. Ticks are 100 ns resolution, so a
    ///      collision means two files share an identical timestamp — we only
    ///      accept a strict 1:1 match (one candidate item ↔ one orphan) and
    ///      leave anything ambiguous alone rather than guess wrong.
    ///
    /// Both paths require the stamp mtime to equal the file's CURRENT mtime —
    /// an entry whose source changed since encoding would be invalidated by
    /// TryGetCached anyway, so adopting it would be pointless.
    /// </summary>
    public ReclaimResult ReclaimOrphans()
    {
        var root = GetCacheRoot();

        // 1. Collect complete orphans with a parseable stamp.
        var orphans = new List<OrphanEntry>();
        foreach (var entry in EnumerateCache())
        {
            if (!entry.IsComplete) continue;
            if (_libraryManager.GetItemById(entry.ItemId) is not null) continue;
            try
            {
                var stampContent = File.ReadAllText(Path.Combine(entry.Directory, ".source-mtime"));
                if (!ParseStamp(stampContent, out var ticks, out _)) continue;
                var pathFile = Path.Combine(entry.Directory, ".source-path");
                string? sourcePath = File.Exists(pathFile) ? File.ReadAllText(pathFile).Trim() : null;
                orphans.Add(new OrphanEntry(entry.Directory, ticks, sourcePath));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (orphans.Count == 0) return new ReclaimResult(0, 0);

        _logger.LogInformation(
            "[NativeTrickplay] reclaim: {Count} orphaned cache entries found, matching against library...",
            orphans.Count);

        // 2. Candidate items: every playable video with a path that does NOT
        //    already have a complete cache entry under its current id. Stat
        //    each source file once — mtime is both the match key and the
        //    freshness requirement.
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = PlayableVideoKinds,
            Recursive = true,
            IsVirtualItem = false,
        };
        var candidates = new List<(BaseItem Item, long MtimeTicks)>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            if (item.IsFolder || string.IsNullOrEmpty(item.Path)) continue;
            if (File.Exists(Path.Combine(root, item.Id.ToString("N"), ".source-mtime"))) continue;
            try
            {
                candidates.Add((item, File.GetLastWriteTimeUtc(item.Path).Ticks));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        int relinked = 0;
        var claimed = new HashSet<string>(StringComparer.Ordinal); // orphan dirs already adopted
        var matchedItems = new HashSet<Guid>();

        // Pass 1: exact path matches (unique path per orphan only).
        var byPath = orphans
            .Where(o => !string.IsNullOrEmpty(o.SourcePath))
            .GroupBy(o => o.SourcePath!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (item, mtimeTicks) in candidates)
        {
            if (!byPath.TryGetValue(item.Path, out var orphan)) continue;
            if (claimed.Contains(orphan.Directory)) continue;
            if (orphan.MtimeTicks != mtimeTicks) continue; // source modified since encode — stale, skip
            if (AdoptOrphan(orphan, item))
            {
                relinked++;
                claimed.Add(orphan.Directory);
                matchedItems.Add(item.Id);
            }
        }

        // Pass 2: mtime-ticks matches, strict 1:1 on both sides.
        var orphansByTicks = orphans
            .Where(o => !claimed.Contains(o.Directory))
            .GroupBy(o => o.MtimeTicks)
            .ToDictionary(g => g.Key, g => g.ToList());
        var candidatesByTicks = candidates
            .Where(c => !matchedItems.Contains(c.Item.Id))
            .GroupBy(c => c.MtimeTicks)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (ticks, cands) in candidatesByTicks)
        {
            if (cands.Count != 1) continue;
            if (!orphansByTicks.TryGetValue(ticks, out var os) || os.Count != 1) continue;
            if (AdoptOrphan(os[0], cands[0].Item))
            {
                relinked++;
                claimed.Add(os[0].Directory);
            }
        }

        _logger.LogInformation(
            "[NativeTrickplay] reclaim complete: relinked {Relinked} of {Orphans} orphaned entries",
            relinked, orphans.Count);
        return new ReclaimResult(orphans.Count, relinked);
    }

    private sealed record OrphanEntry(string Directory, long MtimeTicks, string? SourcePath);

    /// <summary>
    /// Rename an orphan directory to the adopting item's id and refresh its
    /// `.source-path` sidecar. Skips items that are mid-encode (the encode
    /// owns the target directory) — a rare race there surfaces as an
    /// IOException from Move and is treated as "not adopted".
    /// </summary>
    private bool AdoptOrphan(OrphanEntry orphan, BaseItem item)
    {
        var key = item.Id.ToString("N");
        if (_inflight.ContainsKey(key)) return false;

        var newDir = Path.Combine(GetCacheRoot(), key);
        try
        {
            if (Directory.Exists(newDir))
            {
                // Candidates were filtered to items with no stamp, so anything
                // here is an incomplete leftover — replace it with the
                // finished orphan.
                Directory.Delete(newDir, recursive: true);
            }
            Directory.Move(orphan.Directory, newDir);
            File.WriteAllText(Path.Combine(newDir, ".source-path"), item.Path);
            _logger.LogInformation(
                "[NativeTrickplay] reclaim: relinked cache entry to {ItemId} ({Name})",
                item.Id, item.Name);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] reclaim: failed to relink entry for {ItemId}", item.Id);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] reclaim: failed to relink entry for {ItemId}", item.Id);
            return false;
        }
    }

    private static readonly Jellyfin.Data.Enums.BaseItemKind[] PlayableVideoKinds =
    [
        Jellyfin.Data.Enums.BaseItemKind.Movie,
        Jellyfin.Data.Enums.BaseItemKind.Episode,
        Jellyfin.Data.Enums.BaseItemKind.MusicVideo,
        Jellyfin.Data.Enums.BaseItemKind.Video,
    ];

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
                // Mirror the encode-path's finally cleanup. Otherwise a
                // Warmup→GetOrCreate→fast-path-hit chain leaves the Lazy
                // permanently in _inflight, which TryEvict treats as
                // "currently encoding" and refuses to delete the cache.
                _inflight.TryRemove(itemId.ToString("N"), out _);
                return new IframeAsset(
                    await File.ReadAllTextAsync(playlistPath, ct).ConfigureAwait(false), segmentPath);
            }
        }

        _logger.LogInformation(
            "[NativeTrickplay] generation START for {ItemId} ({Name}): source={SourcePath} ({SourceMb} MiB), encoder={Encoder}, awaiting concurrency slot...",
            itemId, item.Name, sourcePath, sourceSize / (1024 * 1024), encoderTag);

        var key = itemId.ToString("N");
        // Episode metadata for the dashboard's display name. Movies and
        // other non-episode items leave these null.
        string? seriesName = null;
        int? seasonNumber = null, episodeNumber = null;
        if (item is Episode ep)
        {
            seriesName = ep.SeriesName;
            seasonNumber = ep.ParentIndexNumber;
            episodeNumber = ep.IndexNumber;
        }
        // Per-item cancellation source linked to the application-lifetime
        // token. Calling progress.Cts.Cancel() (via TryCancel) interrupts
        // whichever stage we're in: queueing, ffmpeg, mp4-box scan, or PTS
        // probe. Server shutdown still cancels everything via the parent ct.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ictk = linkedCts.Token;

        var progress = new InflightProgress
        {
            ItemId = itemId,
            Name = item.Name ?? "(unnamed)",
            Status = "queued",
            EstimatedTotalBytes = EstimateTotalEncodedBytes(item),
            // RunTimeTicks is in 100-nanosecond ticks; divide by 10 to get
            // microseconds, the unit ffmpeg's `-progress pipe:1` emits.
            SourceDurationMicros = item.RunTimeTicks is long ticks and > 0 ? ticks / 10 : null,
            SeriesName = seriesName,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Cts = linkedCts,
        };
        _inflightProgress[key] = progress;

        // slotAcquired tracks whether AcquireSlotAsync succeeded so the outer
        // finally only releases when there's something to release. Without
        // this guard, a cancel-during-queue path would over-release and
        // wedge the slot manager.
        bool slotAcquired = false;
        try
        {
            var slotWaitSw = Stopwatch.StartNew();
            await AcquireSlotAsync(priority, itemId, ictk).ConfigureAwait(false);
            slotAcquired = true;
            slotWaitSw.Stop();
            if (slotWaitSw.ElapsedMilliseconds > 100)
            {
                _logger.LogInformation(
                    "[NativeTrickplay] generation queued behind {WaitMs}ms wait for {ItemId} (priority={Priority})",
                    slotWaitSw.ElapsedMilliseconds, itemId, priority);
            }

            var tmpSegmentPath = segmentPath + ".tmp";
            if (File.Exists(tmpSegmentPath)) File.Delete(tmpSegmentPath);

            // Mark progress as running and expose the .tmp path so the
            // admin UI can read its size and report % done. Restamp
            // StartedUtc so the elapsed column tracks encode time rather
            // than queue-wait time.
            progress.StartedUtc = DateTime.UtcNow;
            progress.Status = "running";
            progress.TmpSegmentPath = tmpSegmentPath;

            var ffmpegSw = Stopwatch.StartNew();
            await RunFfmpegAsync(sourcePath, tmpSegmentPath, variant, progress, ictk).ConfigureAwait(false);
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
            var durations = await ProbePtsDeltasAsync(tmpSegmentPath, fragments.Count, ictk).ConfigureAwait(false);
            probeSw.Stop();
            var template = BuildPlaylist(initSize, fragments, durations);
            var totalDuration = durations.Sum();
            _logger.LogInformation(
                "[NativeTrickplay] PTS probe for {ItemId} took {ElapsedMs}ms; total trickplay duration {DurationSec:F1}s, playlist={PlaylistBytes}B",
                itemId, probeSw.ElapsedMilliseconds, totalDuration, template.Length);

            File.Move(tmpSegmentPath, segmentPath, overwrite: true);
            await File.WriteAllTextAsync(playlistPath, template, ictk).ConfigureAwait(false);
            // Source path sidecar — lets ReclaimOrphans re-match this entry to
            // its item by exact path if the item's GUID ever changes (server
            // reinstall / DB rebuild / library move). Written BEFORE the stamp
            // so the stamp stays the completeness marker (last file written).
            await File.WriteAllTextAsync(Path.Combine(dir, ".source-path"), sourcePath, ictk)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(stampPath, FormatStamp(sourceMtime.Ticks, encoderTag), ictk)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[NativeTrickplay] generation DONE for {ItemId} ({Name}): {Frames} {Variant} I-frames, {EncodedMb} MiB, {Elapsed}ms total",
                itemId, item.Name, fragments.Count, encoderTag, encodedBytes / (1024 * 1024),
                ffmpegSw.ElapsedMilliseconds + scanSw.ElapsedMilliseconds + probeSw.ElapsedMilliseconds);
            return new IframeAsset(template, segmentPath);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Per-item cancel via TryCancel (not server shutdown). Log
            // distinctly so admins can correlate with their dashboard click.
            _logger.LogInformation(
                "[NativeTrickplay] generation CANCELED for {ItemId} ({Name}) by admin",
                itemId, item.Name);
            throw;
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
            // Drop the Lazy<Task> so subsequent Warmup/GetOrCreate calls for
            // this item don't reuse a completed task — and, more importantly,
            // so TryEvict (which checks _inflight as a "currently encoding"
            // proxy) stops returning false after the encode is done.
            _inflight.TryRemove(key, out _);

            // Cancellation cleanup: delete the partial .tmp segment file so
            // it doesn't sit around eating disk space. (Successful encodes
            // already moved tmp → final, so File.Exists returns false; this
            // only matches the canceled / failed case.) The cache directory,
            // any prior valid stamp file, and any prior valid segment all
            // remain — a previous successful encode keeps serving.
            if (linkedCts.IsCancellationRequested && progress.TmpSegmentPath is { } tmpPath)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* best effort */ }
            }

            if (slotAcquired) ReleaseSlot();
        }
    }

    /// <summary>
    /// Cancel an in-flight encode (queued or running). Returns true if the
    /// item was found and cancellation was triggered, false if the item
    /// wasn't in flight (already done, never queued, or raced with completion).
    /// Idempotent — calling repeatedly for the same item is safe.
    /// </summary>
    public bool TryCancel(Guid itemId)
    {
        var key = itemId.ToString("N");
        if (!_inflightProgress.TryGetValue(key, out var p) || p.Cts is null)
        {
            return false;
        }
        try
        {
            p.Cts.Cancel();
            _logger.LogInformation(
                "[NativeTrickplay] cancel requested for {ItemId} ({Name})",
                itemId, p.Name);
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Encode finished and disposed its CTS between the dict lookup
            // and our Cancel call — treat as a no-op cancel (the item was
            // already going away anyway).
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] TryCancel: error canceling {ItemId}", itemId);
            return false;
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
            yield return new InflightState(
                p.ItemId, p.Name, p.StartedUtc, p.Status, partial,
                p.EstimatedTotalBytes, p.EncodedSourceMicros, p.SourceDurationMicros, p.EncodingSpeed,
                p.SeriesName, p.SeasonNumber, p.EpisodeNumber, p.SourceProfile, p.HardwarePath);
        }
    }

    /// <summary>
    /// Cheap projected output-size estimate for an in-progress encode.
    /// Multiplies expected I-frame count by an empirically-tuned per-frame
    /// average (~14.4 KB at the default 480p / CRF 30 / fps=1 settings).
    /// Real outputs land within roughly ±20% — fine for a UX progress bar
    /// where the dashboard caps the displayed percent at 99 to absorb
    /// undershoot. Returns null when item duration is unknown (live TV,
    /// damaged metadata).
    /// </summary>
    private static long? EstimateTotalEncodedBytes(BaseItem item)
    {
        var ticks = item.RunTimeTicks;
        if (ticks is null or <= 0) return null;
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var interval = cfg.IframeIntervalSeconds <= 0 ? 1.0 : cfg.IframeIntervalSeconds;
        var width = cfg.IframeWidth <= 0 ? 320 : cfg.IframeWidth;
        var sourceSeconds = ticks.Value / 10_000_000.0;
        var frameCount = sourceSeconds / interval;

        // Per-frame size scales roughly with the resolution area. Calibrated
        // from observed encodes: 480p ≈ 14.4 KB/frame, CRF 30. A 320p
        // baseline at the same CRF is ~6.5 KB; we scale linearly with
        // (width / 480)² to interpolate. Keep it simple — the cap-at-99
        // percent in the UI hides any inaccuracy.
        var scale = Math.Pow(width / 480.0, 2);
        var bytesPerFrame = 14400.0 * scale;
        var total = frameCount * bytesPerFrame;
        return total > 0 ? (long)total : null;
    }

    private async Task RunFfmpegAsync(string inputPath, string outputPath, IframeVariant variant, InflightProgress? progress, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var (isHdrSource, is10BitSource, srcCodec, srcHeight) = ProbeSourceVideo(inputPath);
        if (progress is not null)
        {
            progress.SourceProfile = FormatSourceProfile(srcHeight, srcCodec, is10BitSource, isHdrSource);
        }

        // Decide whether to attempt GPU tone-mapping on the primary pass.
        // Probe is lazy + cached: only the first HDR encode ever pays its
        // ~100 ms cost. Skip the probe entirely when neither the source nor
        // the config could use it (the common case for SDR content / users
        // with hwaccel disabled).
        var initialGpuPath = GpuTonemapPath.None;
        if (isHdrSource && cfg.UseHardwareDecoding && cfg.EnableGpuTonemap)
        {
            var hwType = TryGetHardwareAccelerationType(cfg);
            if (hwType != HardwareAccelerationType.none)
            {
                var support = await GetGpuTonemapSupportAsync().ConfigureAwait(false);
                initialGpuPath = SelectGpuTonemapPath(cfg, hwType, isHdrSource, support);
            }
        }

        // ffmpeg's `-progress pipe:1` emits key=value lines every 0.5s. We
        // care about `out_time_us` — microseconds of source consumed so far —
        // which combined with the source duration gives a real, monotonic
        // progress signal for the dashboard. `out_time_ms` is also emitted
        // by some ffmpeg builds with the same microsecond meaning (despite
        // the name); accept either as a fallback.
        Action<string>? onLine = progress is null ? null : line =>
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            var key = line.AsSpan(0, eq);
            var val = line.AsSpan(eq + 1);
            if ((key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
                && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
            {
                progress.EncodedSourceMicros = us;
            }
            // speed=2.13x — multiplier vs realtime. Strip the trailing 'x'
            // before parsing. Occasionally ffmpeg emits "N/A" in the very
            // first block before warm-up; ignore that.
            else if (key.SequenceEqual("speed") && val.Length > 1 && val[^1] == 'x'
                && double.TryParse(val[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
                && speed > 0)
            {
                progress.EncodingSpeed = speed;
            }
        };

        // Two-tier retry state machine.
        //   tryGpuTonemap=true,  tryHwaccel=true  → primary attempt
        //   tryGpuTonemap=false, tryHwaccel=true  → drop GPU tonemap only
        //   tryGpuTonemap=false, tryHwaccel=false → software decode + CPU tonemap
        // Failure classifier order is important: GPU-tonemap stderr patterns
        // are checked first because some of them (e.g. "hwmap") would also
        // match the broader hwaccel-failure heuristic.
        var tryGpuTonemap = initialGpuPath != GpuTonemapPath.None;
        var tryHwaccel = true;
        var attemptIndex = 0;

        while (true)
        {
            var effectiveGpu = tryGpuTonemap ? initialGpuPath : GpuTonemapPath.None;
            var hwArgs = new List<string>();
            string? hwName = tryHwaccel ? AppendHwaccelArgs(cfg, hwArgs, effectiveGpu) : null;

            if (progress is not null)
            {
                if (attemptIndex > 0)
                {
                    // Reset progress between retries so the dashboard doesn't
                    // briefly display stale "near 100%" from the failed run.
                    progress.EncodedSourceMicros = 0;
                    progress.EncodingSpeed = null;
                }
                progress.HardwarePath = hwName is null
                    ? "SW"
                    : FormatHwPath(hwName) + TonemapSuffix(effectiveGpu);
            }

            var args = BuildFfmpegArgs(cfg, inputPath, outputPath, variant, hwArgs, isHdrSource, is10BitSource, effectiveGpu);

            if (attemptIndex == 0)
            {
                _logger.LogInformation(
                    "[NativeTrickplay] ffmpeg invoke (hwaccel={Hwaccel}, tonemap={Tonemap}, preset={Preset}, crf={Crf}, width={Width}, interval={Interval}s, sourceHdr={IsHdr}, source10bit={Is10Bit})",
                    hwName ?? "none", effectiveGpu,
                    cfg.IframePreset, cfg.IframeCrf, cfg.IframeWidth, cfg.IframeIntervalSeconds, isHdrSource, is10BitSource);
                _logger.LogDebug(
                    "[NativeTrickplay] ffmpeg cmd: {Encoder} {Args}",
                    _encoder.EncoderPath, string.Join(' ', args));
            }
            else
            {
                _logger.LogInformation(
                    "[NativeTrickplay] ffmpeg retry attempt {N} (hwaccel={Hwaccel}, tonemap={Tonemap})",
                    attemptIndex + 1, hwName ?? "none", effectiveGpu);
            }

            try
            {
                await RunProcessAsync(_encoder.EncoderPath, args, ct, stdoutLineCallback: onLine).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex)
            {
                // GPU-tonemap failure → drop to CPU tonemap, keep hwaccel decode.
                if (tryGpuTonemap && IsLikelyGpuTonemapFailure(ex.Message))
                {
                    _logger.LogWarning(
                        "[NativeTrickplay] GPU tone-map failed for {Input}, retrying with CPU tone-map.\nffmpeg stderr:\n{Stderr}",
                        inputPath, TailLines(ex.Message, 12));
                    tryGpuTonemap = false;
                    attemptIndex++;
                    continue;
                }

                // Hwaccel-decode failure (from either pass) → drop to software decode.
                // ffmpeg can always handle the source if the GPU path is
                // misbehaving for this specific codec.
                if (tryHwaccel && IsLikelyHwaccelFailure(ex.Message))
                {
                    _logger.LogWarning(
                        "[NativeTrickplay] hwaccel decode failed for {Input}, retrying with software decode.\nffmpeg stderr:\n{Stderr}",
                        inputPath, TailLines(ex.Message, 12));
                    tryGpuTonemap = false;
                    tryHwaccel = false;
                    attemptIndex++;
                    continue;
                }

                // Not a recoverable failure pattern (or we already exhausted
                // every fallback above) — surface to the caller.
                throw;
            }
        }
    }

    /// <summary>
    /// Returns Jellyfin's configured hwaccel type, or
    /// <see cref="HardwareAccelerationType.none"/> when hardware decoding is
    /// disabled in the plugin config or when EncodingOptions can't be read.
    /// Used only by the GPU-tonemap selector to short-circuit the probe.
    /// </summary>
    private HardwareAccelerationType TryGetHardwareAccelerationType(PluginConfiguration cfg)
    {
        if (!cfg.UseHardwareDecoding) return HardwareAccelerationType.none;
        try { return _serverConfig.GetEncodingOptions().HardwareAccelerationType; }
        catch { return HardwareAccelerationType.none; }
    }

    /// <summary>
    /// Container-metadata probe used by the encoder. Returns:
    /// <list type="bullet">
    /// <item>IsHdr — true for PQ (smpte2084) or HLG (arib-std-b67).</item>
    /// <item>Is10Bit — true if the source decodes to 10-bit frames.
    /// Drives hwdownload's target sw_format: 8-bit codecs decode into
    /// nv12, 10-bit into p010le. Picking the wrong one triggers
    /// "Invalid output format … for hwframe download" and aborts the
    /// entire filter graph (ffmpeg picks the first sw_format from any
    /// alternation, then errors hard if the device's valid_sw_formats
    /// list doesn't include it — alternation `nv12|p010le` does NOT
    /// auto-fall-back to p010le on 10-bit source, despite intuition).</item>
    /// <item>Codec — lowercase ffmpeg codec_name (e.g. "h264", "hevc").
    /// Used by the dashboard's source-profile column; not consumed by the
    /// encoder itself.</item>
    /// <item>Height — integer height in pixels for the dashboard's
    /// resolution tag ("1080p", "2160p", etc.). Width is omitted because
    /// resolution-class is conventionally named by height.</item>
    /// </list>
    /// On any probe failure: treat as SDR + 8-bit, codec/height null.
    /// SDR-on-HDR mis-tag just produces washed-out thumbnails (acceptable);
    /// 8-bit-on-10-bit hwdownload is recoverable via the software-decode
    /// fallback.
    /// </summary>
    private (bool IsHdr, bool Is10Bit, string? Codec, int? Height) ProbeSourceVideo(string inputPath)
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
                    "-show_entries", "stream=color_transfer,bits_per_raw_sample,pix_fmt,codec_name,height",
                    "-of", "default=noprint_wrappers=1",
                    inputPath,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return (false, false, null, null);
            // Drain both pipes CONCURRENTLY with the wait. Reading stdout only
            // after WaitForExit deadlocks when the child fills the ~64 KB
            // stderr pipe buffer (corrupt files make ffprobe spew errors even
            // at -v error): the child blocks on write, never exits, we burn
            // the 3 s timeout and mis-probe the source as SDR/8-bit.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(3000))
            {
                proc.Kill(entireProcessTree: true);
                return (false, false, null, null);
            }
            var output = stdoutTask.GetAwaiter().GetResult().ToLowerInvariant();
            _ = stderrTask;
            string Get(string key)
            {
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.Trim();
                    var prefix = key + "=";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line[prefix.Length..].Trim();
                }
                return string.Empty;
            }
            var transfer = Get("color_transfer");
            var bitsRaw = Get("bits_per_raw_sample");
            var pixFmt = Get("pix_fmt");
            var codec = Get("codec_name");
            var heightStr = Get("height");
            var isHdr = transfer is "smpte2084" or "arib-std-b67";
            // bits_per_raw_sample may be N/A for some sources; fall back to
            // pix_fmt heuristic. Common 10-bit pixel formats include
            // yuv420p10le, p010le, yuv444p10le, yuv422p10le, etc.
            var is10Bit =
                bitsRaw == "10" ||
                pixFmt.Contains("p10", StringComparison.Ordinal) ||
                pixFmt.Contains("10le", StringComparison.Ordinal) ||
                pixFmt.Contains("10be", StringComparison.Ordinal);
            int? height = int.TryParse(heightStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : null;
            return (isHdr, is10Bit, string.IsNullOrEmpty(codec) ? null : codec, height);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[NativeTrickplay] source probe failed for {Path}, defaulting to SDR/8-bit", inputPath);
            return (false, false, null, null);
        }
    }

    /// <summary>
    /// Format probe results into a single human-readable source-profile tag
    /// for the dashboard ("2160p HEVC 10-bit HDR", "1080p H.264", etc.).
    /// Bit depth is only labeled when 10-bit (8-bit is the implied default);
    /// HDR suffix only appears for PQ/HLG sources. Returns null if probe
    /// produced nothing usable (no codec AND no height).
    /// </summary>
    private static string? FormatSourceProfile(int? height, string? codec, bool is10Bit, bool isHdr)
    {
        if (height is null && string.IsNullOrEmpty(codec)) return null;
        var parts = new List<string>(4);
        if (height is int h) parts.Add(h + "p");
        if (!string.IsNullOrEmpty(codec))
        {
            parts.Add(codec switch
            {
                "h264" => "H.264",
                "hevc" or "h265" => "HEVC",
                "av1" => "AV1",
                "vp9" => "VP9",
                "vp8" => "VP8",
                "mpeg4" => "MPEG-4",
                "mpeg2video" => "MPEG-2",
                _ => codec.ToUpperInvariant(),
            });
        }
        if (is10Bit) parts.Add("10-bit");
        if (isHdr) parts.Add("HDR");
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Map ffmpeg's `-hwaccel` decoder name (lowercase, e.g. "cuda", "qsv")
    /// to a short display tag for the dashboard. "cuda" maps to "CUDA"
    /// because that's the decoder name an admin would recognize from a
    /// Jellyfin transcoding settings page; we don't surface "NVENC" here
    /// since this column tracks the *decode* path, not the encoder.
    /// </summary>
    private static string FormatHwPath(string raw) => raw switch
    {
        "videotoolbox" => "VideoToolbox",
        "qsv" => "QSV",
        "vaapi" => "VAAPI",
        "cuda" => "CUDA",
        "d3d11va" => "D3D11VA",
        _ => raw.ToUpperInvariant(),
    };

    private static List<string> BuildFfmpegArgs(
        PluginConfiguration cfg, string inputPath, string outputPath, IframeVariant variant,
        IReadOnlyList<string>? hwaccelArgs, bool isHdrSource, bool is10BitSource,
        GpuTonemapPath gpuPath)
    {
        var args = new List<string>(56)
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
            // Stream key=value progress lines to stdout every 0.5s so the
            // dashboard can render real time-based progress (out_time_us /
            // source duration) instead of relying on a byte-size estimate
            // that often pegs the bar at 99% mid-encode for high-detail
            // content. -nostats suppresses ffmpeg's default stderr "frame=
            // … time=…" status line which would otherwise duplicate this
            // signal in noisier form.
            "-progress", "pipe:1", "-nostats",
        };

        // Hardware decode args go BEFORE -i.
        if (hwaccelArgs is { Count: > 0 }) args.AddRange(hwaccelArgs);

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
        // Filter chain construction — three families, ordered by GPU pressure
        // off-loaded from the CPU:
        //
        //   * gpuPath != None: GPU tone-mapping. Frames stay on the device
        //     (decode hwaccel + matching -hwaccel_output_format in
        //     AppendHwaccelArgs) until tonemap_{opencl,cuda,videotoolbox}
        //     converts BT.2020/PQ → BT.709 SDR. fps thinning runs ON THE
        //     HARDWARE frames (timestamps only — no pixel work) so we don't
        //     pay GPU→CPU transfer for the 99%+ of frames the trickplay
        //     thumbnail won't keep. This is the path that got the reporting
        //     user from 90 min to ~10 min on a 2-hour 4K HDR film.
        //
        //   * gpuPath == None + isHdrSource: CPU tone-mapping via the
        //     zscale + tonemap=hable chain. Used when GPU tone-mapping is
        //     disabled, not supported, or has fallen back on retry. fps
        //     STILL leads the chain so the QSV hwdownload (only hwaccel
        //     that requires an explicit download for the CPU filter chain)
        //     transfers thinned frames, not every decoded frame.
        //
        //   * gpuPath == None + SDR: simple fps + scale chain.
        //
        // The fps filter thins the decoded stream to 1/interval Hz before
        // the encoder sees it, so trickplay density is uniform regardless
        // of the source's GOP layout. x264's keyint=1 then makes every
        // (thinned) output frame an IDR. Unlike x265, x264 honors keyint=1
        // cleanly without flipping into a profile Apple decoders reject.
        //
        // Note: only the QSV CPU-tonemap path needs explicit hwdownload;
        // VideoToolbox / VAAPI / CUDA / D3D11VA auto-transfer decoded
        // frames to system memory when -hwaccel_output_format is not set,
        // which it isn't on the CPU-tonemap path (see AppendHwaccelArgs).
        // Earlier plugin versions (v1.1.28–v1.1.32) over-generalised the
        // hwdownload prefix to non-QSV hwaccels and broke their decode by
        // "downloading" frames already on CPU. Reverted in v1.1.33.
        var qsvCpuTonemap = gpuPath == GpuTonemapPath.None
            && hwaccelArgs is { Count: > 0 } && hwaccelArgs.Contains("qsv");
        var hwSwFormat = is10BitSource ? "p010le" : "nv12";
        var hwdownloadPrefix = qsvCpuTonemap ? $"hwdownload,format={hwSwFormat}," : string.Empty;

        string filterChain;
        if (gpuPath == GpuTonemapPath.OpenCL)
        {
            // QSV / VAAPI / AMF / rkmpp via OpenCL tone-map bridge.
            // hwmap=derive_device transitions the hwframe context to OpenCL
            // for the tonemap kernel; tonemap_opencl outputs nv12 BT.709 SDR
            // directly. hwdownload pulls the tonemapped (already-thinned)
            // frames to system memory for libx264.
            filterChain =
                $"fps={fps}," +
                "hwmap=derive_device=opencl,format=opencl," +
                "tonemap_opencl=tonemap=hable:format=nv12," +
                "hwdownload,format=nv12," +
                $"scale=-2:{width},format=yuv420p";
        }
        else if (gpuPath == GpuTonemapPath.Cuda)
        {
            // NVIDIA via native tonemap_cuda — no OpenCL bridge needed since
            // the filter operates on CUDA frames directly.
            filterChain =
                $"fps={fps}," +
                "tonemap_cuda=tonemap=hable:format=nv12," +
                "hwdownload,format=nv12," +
                $"scale=-2:{width},format=yuv420p";
        }
        else if (gpuPath == GpuTonemapPath.VideoToolbox)
        {
            // macOS Metal-backed tonemap_videotoolbox. Picks up the VT
            // device automatically from the input frame's hwframe context.
            filterChain =
                $"fps={fps}," +
                "tonemap_videotoolbox=tonemap=hable:format=nv12," +
                "hwdownload,format=nv12," +
                $"scale=-2:{width},format=yuv420p";
        }
        else if (isHdrSource)
        {
            // CPU tone-map fallback. Identical operator chain to the pre-
            // 1.1.49 code, with fps lifted to the head so QSV's hwdownload
            // (which only appears here in the QSV CPU-tonemap case) does
            // not transfer every decoded frame.
            filterChain =
                $"fps={fps}," +
                hwdownloadPrefix +
                "zscale=t=linear:npl=100," +
                "format=gbrpf32le," +
                "zscale=p=bt709," +
                "tonemap=tonemap=hable:desat=0," +
                "zscale=t=bt709:m=bt709:r=tv," +
                "format=yuv420p," +
                $"scale=-2:{width}";
        }
        else
        {
            // SDR — no tone-mapping work needed. fps first; QSV CPU path
            // (rare in SDR but possible) gets its hwdownload prefix.
            filterChain =
                $"fps={fps}," +
                hwdownloadPrefix +
                $"scale=-2:{width},format=yuv420p";
        }
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

        // Cap encoder threads to avoid oversubscription with multiple
        // concurrent encodes. Default is 1 thread per job — when
        // MaxConcurrentGenerations > 1, each ffmpeg would otherwise
        // auto-detect "all cores" and produce N×cores threads fighting for
        // CPU. Setting EncodeThreadsPerJob=0 disables the cap (use ffmpeg's
        // auto-thread heuristic), useful for the single-encode case.
        if (cfg.EncodeThreadsPerJob > 0)
        {
            args.Add("-threads");
            args.Add(cfg.EncodeThreadsPerJob.ToString(CultureInfo.InvariantCulture));
        }
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
            // hwdownload-side negotiation failures: occur when the probed
            // sw_format (nv12 vs p010le) doesn't match the hwframe ctx's
            // actual sw_format. Catches mis-probed bit depths.
            || stderr.Contains("Invalid output format", StringComparison.Ordinal)
            || stderr.Contains("hwframe download", StringComparison.Ordinal)
            || stderr.Contains("Failed to configure output pad", StringComparison.Ordinal)
            // Generic hwaccel keyword fallback — catches any pattern the
            // explicit list above misses.
            || stderr.Contains("hwaccel", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("hwframe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Heuristic: does the ffmpeg stderr look like a GPU tone-map filter
    /// failure (rather than a deeper hwaccel-decode failure)? When true,
    /// the retry drops only the GPU tone-map step and keeps hwaccel decode —
    /// this is the cheaper fallback. Must be checked BEFORE
    /// <see cref="IsLikelyHwaccelFailure"/> in the retry classifier so
    /// generic substrings like "hwframe" don't mis-route a tone-map failure
    /// to the slower software-decode retry.
    /// </summary>
    private static bool IsLikelyGpuTonemapFailure(string stderr)
    {
        return stderr.Contains("tonemap_opencl", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("tonemap_cuda", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("tonemap_videotoolbox", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("OpenCL", StringComparison.OrdinalIgnoreCase)
            // Common OpenCL init / interop failure patterns:
            || stderr.Contains("Failed to create OpenCL context", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Failed to derive device", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("cl_get_platforms", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Failed to init HW filter", StringComparison.OrdinalIgnoreCase)
            // hwmap is the QSV/VAAPI→OpenCL bridge filter — its failures are
            // tone-map-pipeline failures rather than decode failures.
            || stderr.Contains("hwmap", StringComparison.OrdinalIgnoreCase)
            // CUDA-specific init/alloc patterns:
            || stderr.Contains("cuMemAlloc", StringComparison.Ordinal)
            || stderr.Contains("Could not load CUDA", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Emit the `-hwaccel ...` (and, when GPU tone-mapping is active, the
    /// `-init_hw_device ... -filter_hw_device ... -hwaccel_output_format ...`)
    /// flags ahead of `-i`. Returns the canonical ffmpeg decoder name actually
    /// used (e.g. "qsv", "vaapi", "cuda", "d3d11va", "videotoolbox", "rkmpp",
    /// "drm") so the caller can render an accurate dashboard label, or null
    /// when no hwaccel was wired up.
    /// </summary>
    private string? AppendHwaccelArgs(PluginConfiguration cfg, List<string> args, GpuTonemapPath gpuPath)
    {
        if (!cfg.UseHardwareDecoding) return null;

        EncodingOptions opts;
        try { opts = _serverConfig.GetEncodingOptions(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NativeTrickplay] could not read EncodingOptions, falling back to software decode");
            return null;
        }

        // VAAPI device path used for both VAAPI itself and as the base device
        // for the Linux QSV→OpenCL bridge. Jellyfin's own fallback ladder is
        // mirrored here: VaapiDevice → QsvDevice → /dev/dri/renderD128.
        var vaapiPath = !string.IsNullOrEmpty(opts.VaapiDevice) ? opts.VaapiDevice
                       : !string.IsNullOrEmpty(opts.QsvDevice) ? opts.QsvDevice
                       : "/dev/dri/renderD128";

        // Map Jellyfin's HardwareAccelerationType enum to the corresponding ffmpeg
        // -hwaccel decoder. NVENC/AMF are encoder names, not decoders — for those
        // we map to the matching decode-side hwaccel that the same GPU exposes.
        // Per-hwaccel refinements mirror Jellyfin's own EncodingHelper.GetHwaccelType
        // (MediaBrowser.Controller/MediaEncoding/EncodingHelper.cs).
        //
        // When gpuPath != None, we also emit the device-init chain that lets the
        // filter graph keep frames on-device through the tone-mapper (avoiding a
        // round-trip to CPU between decode and tone-map). The exact incantation
        // is OS- and hwaccel-specific — see the table in plan
        // robust-wondering-hollerith.md and Jellyfin's EncodingHelper for the
        // device-alias conventions (va / hw / ocl / d3d / cu).
        switch (opts.HardwareAccelerationType)
        {
            case HardwareAccelerationType.videotoolbox:
                args.Add("-hwaccel"); args.Add("videotoolbox");
                // Trickplay extraction is background work — yield GPU time to
                // foreground playback transcodes. Supported in jellyfin-ffmpeg 7.x.
                args.Add("-hwaccel_flags"); args.Add("+low_priority");
                if (gpuPath == GpuTonemapPath.VideoToolbox)
                {
                    // Keep decoded frames on the VideoToolbox device so
                    // tonemap_videotoolbox can read them directly via the
                    // input frame's hwframe context. The filter picks up the
                    // VT device automatically — no -filter_hw_device needed.
                    args.Add("-hwaccel_output_format"); args.Add("videotoolbox_vld");
                }
                return "videotoolbox";

            case HardwareAccelerationType.qsv:
                if (gpuPath == GpuTonemapPath.OpenCL)
                {
                    // Bridge QSV → OpenCL for tonemap_opencl. On Linux the
                    // intermediate is VAAPI (shared drm render node); on
                    // Windows it's D3D11VA (shared adapter). Both vendors
                    // (Intel iGPU primarily) expose OpenCL interop on the
                    // same underlying device, so the hwmap=derive in the
                    // filter chain is zero-copy.
                    if (OperatingSystem.IsWindows())
                    {
                        args.Add("-init_hw_device"); args.Add("d3d11va=d3d:0");
                        args.Add("-init_hw_device"); args.Add("qsv=hw@d3d");
                        args.Add("-init_hw_device"); args.Add("opencl=ocl@d3d");
                    }
                    else
                    {
                        args.Add("-init_hw_device"); args.Add($"vaapi=va:{vaapiPath}");
                        args.Add("-init_hw_device"); args.Add("qsv=hw@va");
                        args.Add("-init_hw_device"); args.Add("opencl=ocl@va");
                    }
                    args.Add("-filter_hw_device"); args.Add("ocl");
                    args.Add("-hwaccel"); args.Add("qsv");
                    args.Add("-hwaccel_output_format"); args.Add("qsv");
                }
                else
                {
                    args.Add("-hwaccel"); args.Add("qsv");
                    if (!string.IsNullOrEmpty(opts.QsvDevice))
                    {
                        args.Add("-qsv_device"); args.Add(opts.QsvDevice);
                    }
                }
                return "qsv";

            case HardwareAccelerationType.vaapi:
                if (gpuPath == GpuTonemapPath.OpenCL)
                {
                    // tonemap_vaapi is the VPP-based filter that does NOT
                    // work on Intel iGPUs that lack VPP tone-mapping support
                    // (see the reported case). tonemap_opencl bridges via
                    // OpenCL interop instead — Mesa supports this on AMD
                    // too, so this is the portable choice.
                    args.Add("-init_hw_device"); args.Add($"vaapi=va:{vaapiPath}");
                    args.Add("-init_hw_device"); args.Add("opencl=ocl@va");
                    args.Add("-filter_hw_device"); args.Add("ocl");
                    args.Add("-hwaccel"); args.Add("vaapi");
                    args.Add("-hwaccel_output_format"); args.Add("vaapi");
                }
                else
                {
                    args.Add("-hwaccel"); args.Add("vaapi");
                    if (!string.IsNullOrEmpty(opts.VaapiDevice))
                    {
                        args.Add("-vaapi_device"); args.Add(opts.VaapiDevice);
                    }
                }
                return "vaapi";

            case HardwareAccelerationType.nvenc:
                // NVENC is the NVIDIA encoder; the matching decode hwaccel is `cuda`.
                // nvdec is single-threaded internally — ffmpeg's auto-thread heuristic
                // oversubscribes if we don't pin to 1.
                args.Add("-hwaccel"); args.Add("cuda");
                args.Add("-threads"); args.Add("1");
                if (gpuPath == GpuTonemapPath.Cuda)
                {
                    // Keep decoded frames on the CUDA device so tonemap_cuda
                    // (native CUDA, no OpenCL bridge needed) can read them
                    // directly. No -filter_hw_device — tonemap_cuda picks up
                    // the device from the input hwframe context.
                    args.Add("-hwaccel_output_format"); args.Add("cuda");
                }
                return "cuda";

            case HardwareAccelerationType.amf:
                // AMF is AMD's encode framework; AMD decode in ffmpeg is platform-specific:
                // d3d11va on Windows, vaapi on Linux (via Mesa's amdgpu driver).
                if (OperatingSystem.IsWindows())
                {
                    if (gpuPath == GpuTonemapPath.OpenCL)
                    {
                        args.Add("-init_hw_device"); args.Add("d3d11va=d3d:0");
                        args.Add("-init_hw_device"); args.Add("opencl=ocl@d3d");
                        args.Add("-filter_hw_device"); args.Add("ocl");
                        args.Add("-hwaccel"); args.Add("d3d11va");
                        args.Add("-hwaccel_output_format"); args.Add("d3d11");
                    }
                    else
                    {
                        args.Add("-hwaccel"); args.Add("d3d11va");
                        args.Add("-threads"); args.Add("2");
                    }
                    return "d3d11va";
                }
                if (OperatingSystem.IsLinux())
                {
                    if (gpuPath == GpuTonemapPath.OpenCL)
                    {
                        args.Add("-init_hw_device"); args.Add($"vaapi=va:{vaapiPath}");
                        args.Add("-init_hw_device"); args.Add("opencl=ocl@va");
                        args.Add("-filter_hw_device"); args.Add("ocl");
                        args.Add("-hwaccel"); args.Add("vaapi");
                        args.Add("-hwaccel_output_format"); args.Add("vaapi");
                    }
                    else
                    {
                        args.Add("-hwaccel"); args.Add("vaapi");
                        if (!string.IsNullOrEmpty(opts.VaapiDevice))
                        {
                            args.Add("-vaapi_device"); args.Add(opts.VaapiDevice);
                        }
                    }
                    return "vaapi";
                }
                // macOS doesn't host AMD-specific hwaccel; fall through to software.
                return null;

            case HardwareAccelerationType.v4l2m2m:
                // Used on embedded ARM Linux boards (Raspberry Pi, etc.). The DRM hwaccel
                // is the standard pipeline. No GPU tone-map filter is reliably available
                // for this combo, so we never emit the device-init chain here.
                args.Add("-hwaccel"); args.Add("drm");
                return "drm";

            case HardwareAccelerationType.rkmpp:
                if (gpuPath == GpuTonemapPath.OpenCL)
                {
                    // Rockchip Mali OpenCL: init OpenCL standalone (no
                    // derived-from-rkmpp interop exists). The hwmap in the
                    // filter chain transfers via the drm_prime intermediate.
                    args.Add("-init_hw_device"); args.Add("opencl=ocl");
                    args.Add("-filter_hw_device"); args.Add("ocl");
                    args.Add("-hwaccel"); args.Add("rkmpp");
                    args.Add("-hwaccel_output_format"); args.Add("drm_prime");
                }
                else
                {
                    args.Add("-hwaccel"); args.Add("rkmpp");
                }
                return "rkmpp";

            case HardwareAccelerationType.none:
            default:
                return null;
        }
    }

    /// <summary>
    /// Choose the best GPU tone-mapping path for this encode based on the
    /// configured hwaccel, the host OS, and which <c>tonemap_*</c> filters
    /// the ffmpeg build ships with. Returns <c>None</c> if any precondition
    /// fails — the caller then uses the CPU tone-map chain (current
    /// behaviour, fps-reordered).
    /// </summary>
    private static GpuTonemapPath SelectGpuTonemapPath(
        PluginConfiguration cfg,
        HardwareAccelerationType hwaccel,
        bool isHdrSource,
        GpuTonemapSupport support)
    {
        if (!cfg.UseHardwareDecoding) return GpuTonemapPath.None;
        if (!cfg.EnableGpuTonemap) return GpuTonemapPath.None;
        if (!isHdrSource) return GpuTonemapPath.None;

        return hwaccel switch
        {
            HardwareAccelerationType.qsv => support.HasOpenCL ? GpuTonemapPath.OpenCL : GpuTonemapPath.None,
            HardwareAccelerationType.vaapi => support.HasOpenCL ? GpuTonemapPath.OpenCL : GpuTonemapPath.None,
            HardwareAccelerationType.nvenc => support.HasCuda ? GpuTonemapPath.Cuda : GpuTonemapPath.None,
            HardwareAccelerationType.amf => support.HasOpenCL ? GpuTonemapPath.OpenCL : GpuTonemapPath.None,
            HardwareAccelerationType.videotoolbox => support.HasVideoToolbox ? GpuTonemapPath.VideoToolbox : GpuTonemapPath.None,
            HardwareAccelerationType.rkmpp => support.HasOpenCL ? GpuTonemapPath.OpenCL : GpuTonemapPath.None,
            _ => GpuTonemapPath.None,
        };
    }

    /// <summary>
    /// Lazy single-shot probe of <c>ffmpeg -filters</c> output, caching the
    /// set of GPU tone-map filters the build supports. Subsequent callers
    /// share the same Task; the probe itself is bounded to 5s and falls
    /// back to "no GPU support" on timeout, non-zero exit, or any exception.
    /// </summary>
    private Task<GpuTonemapSupport> GetGpuTonemapSupportAsync()
    {
        var existing = Volatile.Read(ref _gpuTonemapSupportTask);
        if (existing is not null) return existing;
        var fresh = ProbeGpuTonemapSupportAsync();
        var prior = Interlocked.CompareExchange(ref _gpuTonemapSupportTask, fresh, null);
        return prior ?? fresh;
    }

    private async Task<GpuTonemapSupport> ProbeGpuTonemapSupportAsync()
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = await RunProcessAsync(
                _encoder.EncoderPath,
                new[] { "-hide_banner", "-filters" },
                timeoutCts.Token,
                captureStdout: true).ConfigureAwait(false);
            var hasOpenCL = stdout.Contains("tonemap_opencl", StringComparison.Ordinal);
            var hasCuda = stdout.Contains("tonemap_cuda", StringComparison.Ordinal);
            var hasVt = stdout.Contains("tonemap_videotoolbox", StringComparison.Ordinal);
            _logger.LogInformation(
                "[NativeTrickplay] GPU tone-map probe: opencl={OCL} cuda={Cuda} videotoolbox={VT}",
                hasOpenCL, hasCuda, hasVt);
            return new GpuTonemapSupport(hasOpenCL, hasCuda, hasVt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[NativeTrickplay] GPU tone-map probe failed; assuming no GPU tone-map support");
            return new GpuTonemapSupport(false, false, false);
        }
    }

    /// <summary>
    /// Dashboard tag suffix for an active GPU tone-map path. Empty string
    /// for None, so concatenation against the decode-path label yields a
    /// clean "QSV" / "QSV+OCL" / "CUDA+CUDA" pattern.
    /// </summary>
    private static string TonemapSuffix(GpuTonemapPath path) => path switch
    {
        GpuTonemapPath.OpenCL => "+OCL",
        GpuTonemapPath.Cuda => "+CUDA",
        GpuTonemapPath.VideoToolbox => "+VT",
        _ => string.Empty,
    };

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

    private async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct,
        bool captureStdout = false,
        Action<string>? stdoutLineCallback = null)
    {
        // When a line callback is supplied, force stdout redirection — the
        // caller (RunFfmpegAsync) needs to consume `-progress pipe:1` output
        // for live progress updates regardless of whether captureStdout is
        // also requested.
        var redirectStdout = captureStdout || stdoutLineCallback is not null;
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger.LogDebug("[NativeTrickplay] {File} {Args}", fileName, string.Join(' ', args));

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        Task<string> stdoutTask;
        if (stdoutLineCallback is not null)
        {
            // Stream stdout line-by-line off the threadpool so the callback
            // fires as ffmpeg emits progress (~every 0.5s) instead of only
            // once at process exit. Accumulate the full output too so the
            // captureStdout=true callers (currently none for this path, but
            // a future caller that wants both progress + final stdout would
            // get correct semantics) still see what they expect.
            stdoutTask = Task.Run(async () =>
            {
                var sb = captureStdout ? new System.Text.StringBuilder() : null;
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    try { stdoutLineCallback(line); }
                    catch { /* swallow callback exceptions — never let UI plumbing kill an encode */ }
                    sb?.AppendLine(line);
                }
                return sb?.ToString() ?? string.Empty;
            }, ct);
        }
        else
        {
            stdoutTask = captureStdout ? proc.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
        }

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    // Reap the zombie so the OS releases its file
                    // descriptors. Without this final wait, repeated
                    // cancel-cycles leak FDs in the parent process.
                    // Use a separate CT so we never await on the already-
                    // canceled outer token (would re-throw immediately).
                    try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best effort */ }
                    _logger.LogInformation(
                        "[NativeTrickplay] {Bin} killed (exit={ExitCode}) — cancellation acknowledged",
                        Path.GetFileName(fileName), proc.HasExited ? proc.ExitCode : -1);
                }
            }
            catch { /* already gone */ }
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
