using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    /// Synchronous, no-side-effect check: is the asset already on disk and fresh
    /// (mtime matches)? Returns the asset or null. Used by the controller hot path
    /// so that fully-cached items respond in &lt; 1 ms with no generation work.
    /// </summary>
    public IframeAsset? TryGetCached(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || string.IsNullOrEmpty(item.Path))
            return null;

        var dir = Path.Combine(_paths.CachePath, "native-trickplay", itemId.ToString("N"));
        var playlistPath = Path.Combine(dir, "iframe.m3u8");
        var segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        if (!File.Exists(playlistPath) || !File.Exists(segmentPath) || !File.Exists(stampPath))
            return null;

        try
        {
            var sourceMtime = File.GetLastWriteTimeUtc(item.Path);
            if (!long.TryParse(File.ReadAllText(stampPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamped)
                || stamped != sourceMtime.Ticks)
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
        var root = Path.Combine(_paths.CachePath, "native-trickplay");
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

        var dir = Path.Combine(_paths.CachePath, "native-trickplay", key);
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

        var dir = Path.Combine(_paths.CachePath, "native-trickplay", itemId.ToString("N"));
        Directory.CreateDirectory(dir);

        var playlistPath = Path.Combine(dir, "iframe.m3u8");
        var segmentPath = Path.Combine(dir, "iframe.m4s");
        var stampPath = Path.Combine(dir, ".source-mtime");

        // Fast path: already cached and fresh.
        if (File.Exists(playlistPath) && File.Exists(segmentPath) && File.Exists(stampPath)
            && long.TryParse(await File.ReadAllTextAsync(stampPath, ct).ConfigureAwait(false),
                             NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamped)
            && stamped == sourceMtime.Ticks)
        {
            return new IframeAsset(await File.ReadAllTextAsync(playlistPath, ct).ConfigureAwait(false), segmentPath);
        }

        await _globalLimit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmpSegmentPath = segmentPath + ".tmp";
            if (File.Exists(tmpSegmentPath)) File.Delete(tmpSegmentPath);

            await RunFfmpegAsync(sourcePath, tmpSegmentPath, ct).ConfigureAwait(false);

            var (initSize, fragments) = Mp4BoxScanner.Scan(tmpSegmentPath);
            if (fragments.Count == 0)
            {
                throw new InvalidOperationException("ffmpeg produced no fragments.");
            }

            var durations = await ProbePtsDeltasAsync(tmpSegmentPath, fragments.Count, ct).ConfigureAwait(false);
            var template = BuildPlaylist(initSize, fragments, durations);

            File.Move(tmpSegmentPath, segmentPath, overwrite: true);
            await File.WriteAllTextAsync(playlistPath, template, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(stampPath, sourceMtime.Ticks.ToString(CultureInfo.InvariantCulture), ct)
                .ConfigureAwait(false);

            _logger.LogInformation("[NativeTrickplay] generated {Frames} I-frames for {ItemId}", fragments.Count, itemId);
            return new IframeAsset(template, segmentPath);
        }
        finally
        {
            _globalLimit.Release();
        }
    }

    private async Task RunFfmpegAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var args = new List<string>(32)
        {
            "-nostdin", "-y", "-hide_banner", "-loglevel", "error",
        };

        // Hardware decode args go BEFORE -i. For VAAPI / QSV / VideoToolbox / CUDA the
        // hardware-decoded frames are auto-copied back to system memory by ffmpeg when
        // they enter a CPU filter chain (our scale + format conversion), so libx264
        // can encode them. No explicit hwdownload needed for our case.
        AppendHwaccelArgs(cfg, args);

        args.AddRange(new[]
        {
            "-skip_frame", "nokey",
            "-i", inputPath,
            "-an", "-sn",
            "-fps_mode", "passthrough",
            "-vf", $"scale=-2:{cfg.IframeWidth.ToString(CultureInfo.InvariantCulture)},format=yuv420p",
            "-c:v", "libx264",
            "-preset", cfg.IframePreset,
            "-crf", cfg.IframeCrf.ToString(CultureInfo.InvariantCulture),
            "-profile:v", "main", "-level:v", "3.0",
            "-x264-params", "keyint=1:scenecut=0:open-gop=0",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-f", "mp4", outputPath
        });

        await RunProcessAsync(_encoder.EncoderPath, args, ct).ConfigureAwait(false);
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
        switch (opts.HardwareAccelerationType)
        {
            case HardwareAccelerationType.videotoolbox:
                args.Add("-hwaccel"); args.Add("videotoolbox");
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
                args.Add("-hwaccel"); args.Add("cuda");
                break;
            case HardwareAccelerationType.amf:
                // AMF is AMD's encode framework; AMD decode in ffmpeg is platform-specific:
                // d3d11va on Windows, vaapi on Linux (via Mesa's amdgpu driver).
                if (OperatingSystem.IsWindows())
                {
                    args.Add("-hwaccel"); args.Add("d3d11va");
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
