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

public sealed class IframeAssetCache
{
    private readonly ILogger<IframeAssetCache> _logger;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _encoder;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentDictionary<string, Lazy<Task<IframeAsset>>> _inflight = new();
    private readonly SemaphoreSlim _globalLimit;

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
        _globalLimit = new SemaphoreSlim(Math.Max(1, max));
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
            return null;

        var dir = Path.Combine(GetCacheRoot(), itemId.ToString("N"));
        var playlistPath = Path.Combine(dir, "iframe.m3u8");
        var segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        if (!File.Exists(playlistPath) || !File.Exists(segmentPath) || !File.Exists(stampPath))
            return null;

        try
        {
            var sourceMtime = File.GetLastWriteTimeUtc(item.Path);
            var stampContent = File.ReadAllText(stampPath);
            if (!ParseStamp(stampContent, out var stampedMtime, out var stampedEncoder)
                || stampedMtime != sourceMtime.Ticks)
                return null;

            // Encoder must match the variant we'd produce now for this source —
            // catches v1.0→v1.1 upgrade where HDR items have stale SDR caches,
            // or any future format change.
            var expectedEncoder = IframeFormat.EncoderTag(IframeFormatFor(item));
            if (!string.Equals(stampedEncoder, expectedEncoder, StringComparison.Ordinal))
                return null;

            var template = File.ReadAllText(playlistPath);
            return new IframeAsset(template, segmentPath);
        }
        catch (IOException)
        {
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
    public void Warmup(Guid itemId)
    {
        _ = GetOrCreateAsync(itemId).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception?.GetBaseException(),
                    "[NativeTrickplay] warmup failed for {ItemId}", itemId);
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
    public Task<IframeAsset> GetOrCreateAsync(Guid itemId)
    {
        var key = itemId.ToString("N");
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<IframeAsset>>(
            () => GenerateAsync(itemId, _lifetime.ApplicationStopping),
            LazyThreadSafetyMode.ExecutionAndPublication));
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

    private async Task<IframeAsset> GenerateAsync(Guid itemId, CancellationToken ct)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrEmpty(item.Path))
        {
            throw new InvalidOperationException($"Item {itemId} not found or has no path.");
        }

        var sourcePath = item.Path;
        var sourceMtime = File.GetLastWriteTimeUtc(sourcePath);
        var variant = IframeFormatFor(item);
        var encoderTag = IframeFormat.EncoderTag(variant);

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
                return new IframeAsset(
                    await File.ReadAllTextAsync(playlistPath, ct).ConfigureAwait(false), segmentPath);
            }
        }

        await _globalLimit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmpSegmentPath = segmentPath + ".tmp";
            if (File.Exists(tmpSegmentPath)) File.Delete(tmpSegmentPath);

            await RunFfmpegAsync(sourcePath, tmpSegmentPath, variant, ct).ConfigureAwait(false);

            var (initSize, fragments) = Mp4BoxScanner.Scan(tmpSegmentPath);
            if (fragments.Count == 0)
            {
                throw new InvalidOperationException("ffmpeg produced no fragments.");
            }

            var durations = await ProbePtsDeltasAsync(tmpSegmentPath, fragments.Count, ct).ConfigureAwait(false);
            var template = BuildPlaylist(initSize, fragments, durations);

            File.Move(tmpSegmentPath, segmentPath, overwrite: true);
            await File.WriteAllTextAsync(playlistPath, template, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(stampPath, FormatStamp(sourceMtime.Ticks, encoderTag), ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "[NativeTrickplay] generated {Frames} {Variant} I-frames for {ItemId}",
                fragments.Count, encoderTag, itemId);
            return new IframeAsset(template, segmentPath);
        }
        finally
        {
            _globalLimit.Release();
        }
    }

    private async Task RunFfmpegAsync(string inputPath, string outputPath, IframeVariant variant, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // First attempt: with hwaccel if configured.
        var hwaccelArgs = new List<string>();
        AppendHwaccelArgs(cfg, hwaccelArgs);

        try
        {
            await RunProcessAsync(_encoder.EncoderPath,
                BuildFfmpegArgs(cfg, inputPath, outputPath, variant, hwaccelArgs), ct).ConfigureAwait(false);
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
                "[NativeTrickplay] hwaccel decode failed for {Input}, retrying with software decode. ffmpeg said: {FirstLine}",
                inputPath,
                FirstLine(ex.Message));
        }

        await RunProcessAsync(_encoder.EncoderPath,
            BuildFfmpegArgs(cfg, inputPath, outputPath, variant, hwaccelArgs: null), ct).ConfigureAwait(false);
    }

    private static List<string> BuildFfmpegArgs(
        PluginConfiguration cfg, string inputPath, string outputPath, IframeVariant variant, IReadOnlyList<string>? hwaccelArgs)
    {
        var args = new List<string>(48)
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
        };

        // Hardware decode args go BEFORE -i. With bare `-hwaccel <type>` (no
        // `-hwaccel_output_format`), ffmpeg auto-transfers decoded frames from
        // GPU to system memory in their native CPU pixel format (nv12 for 8-bit,
        // p010 for 10-bit) — the software encoder + the scale/format CPU filter
        // chain consume them directly. Mirrors Jellyfin's GetSwVidFilterChain
        // "copy-back" path.
        if (hwaccelArgs is { Count: > 0 }) args.AddRange(hwaccelArgs);

        var width = cfg.IframeWidth.ToString(CultureInfo.InvariantCulture);

        args.AddRange(new[]
        {
            "-skip_frame", "nokey",
            "-i", inputPath,
            "-an", "-sn",
            "-fps_mode", "passthrough",
        });

        if (variant == IframeVariant.Sdr)
        {
            // SDR: 8-bit H.264 Main 3.0. Universal Apple TV decode, fast encode.
            args.AddRange(new[]
            {
                "-vf", $"scale=-2:{width},format=yuv420p",
                "-c:v", "libx264",
                "-preset", cfg.IframePreset,
                "-crf", cfg.IframeCrf.ToString(CultureInfo.InvariantCulture),
                "-profile:v", "main", "-level:v", "3.0",
                "-x264-params", "keyint=1:scenecut=0:open-gop=0",
            });
        }
        else
        {
            // HDR: 10-bit HEVC Main10 with PQ or HLG signaling so AVPlayer
            // accepts the I-frame variant alongside HDR primaries (Apple HLS
            // Authoring Spec §4.4.4 — variant VIDEO-RANGE must match family).
            // -tag:v hvc1 is critical: Apple players only honor `hvc1`-tagged
            // HEVC; `hev1` (in-band parameter sets) won't initialize the decoder.
            var transfer = variant == IframeVariant.HdrHlg ? "arib-std-b67" : "smpte2084";
            var hdrFlag = variant == IframeVariant.HdrPq ? ":hdr10=1" : string.Empty;
            args.AddRange(new[]
            {
                "-vf", $"scale=-2:{width},format=yuv420p10le",
                "-c:v", "libx265",
                "-preset", cfg.IframePreset,
                "-crf", cfg.IframeCrf.ToString(CultureInfo.InvariantCulture),
                "-profile:v", "main10", "-level:v", "5.0",
                "-tag:v", "hvc1",
                "-x265-params",
                $"colorprim=bt2020:transfer={transfer}:colormatrix=bt2020nc{hdrFlag}:repeat-headers=1:keyint=1:scenecut=0:open-gop=0:log-level=error",
            });
        }

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
