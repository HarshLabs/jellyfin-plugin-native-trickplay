using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Auto-resume service: at plugin startup, scans the cache root for items
/// whose previous encode was interrupted (Jellyfin crash, plugin upgrade,
/// power failure, manual kill, etc.) and re-queues them via the existing
/// warmup machinery so they finish in the background without the user
/// having to play the item or wait for the daily pre-gen task.
///
/// Detection is structural: a cache directory containing
/// <c>iframe.m4s.tmp</c> with no successfully-written
/// <c>.source-mtime</c> stamp means a previous <c>GenerateAsync</c> was
/// running but never reached its final move/stamp step. Empty dirs and
/// dirs with valid finished caches are left alone.
///
/// Runs once, 30 seconds after the plugin loads, so it doesn't compete
/// with Jellyfin's own boot work or with active playback that's spinning
/// up its segment ffmpeg.
/// </summary>
public sealed class StartupResumeService : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IframeAssetCache _cache;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StartupResumeService> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

    public StartupResumeService(
        IframeAssetCache cache,
        IApplicationPaths paths,
        ILibraryManager libraryManager,
        ILogger<StartupResumeService> logger)
    {
        _cache = cache;
        _paths = paths;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                ResumeInterruptedEncodes();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[NativeTrickplay] startup resume scan failed");
            }
        }, _shutdownCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _shutdownCts.Dispose();

    private void ResumeInterruptedEncodes()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.ResumeInterruptedEncodesOnStartup) return;

        // Resolve the same cache root IframeAssetCache uses (custom path
        // if configured, else the Jellyfin default).
        string root;
        if (!string.IsNullOrWhiteSpace(cfg.CacheDirectory) && Directory.Exists(cfg.CacheDirectory))
        {
            root = cfg.CacheDirectory;
        }
        else
        {
            root = Path.Combine(_paths.CachePath, "native-trickplay");
        }
        if (!Directory.Exists(root))
        {
            _logger.LogDebug("[NativeTrickplay] resume scan: cache root does not exist, nothing to resume");
            return;
        }

        int scanned = 0, resumed = 0, skipped = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            scanned++;
            var name = Path.GetFileName(dir);
            if (!Guid.TryParseExact(name, "N", out var itemId)) continue;

            var tmp = Path.Combine(dir, "iframe.m4s.tmp");
            var seg = Path.Combine(dir, "iframe.m4s");
            var stamp = Path.Combine(dir, ".source-mtime");

            // Heuristic for "interrupted": a .tmp file is present, AND
            // either the final segment is missing OR the stamp is. Either
            // condition means GenerateAsync didn't make it to its
            // commit step (File.Move + WriteAllText for the stamp).
            if (!File.Exists(tmp)) { skipped++; continue; }
            if (File.Exists(seg) && File.Exists(stamp)) { skipped++; continue; }

            // Item must still exist in the library — otherwise the
            // orphan dir is dead and should be cleaned by the pruner,
            // not regenerated.
            if (_libraryManager.GetItemById(itemId) is not BaseItem)
            {
                _logger.LogDebug("[NativeTrickplay] resume scan: skipping orphan dir for missing item {ItemId}", itemId);
                skipped++;
                continue;
            }

            _cache.Warmup(itemId);
            resumed++;
            _logger.LogInformation("[NativeTrickplay] resume scan: re-queued interrupted encode for {ItemId}", itemId);
        }

        if (resumed > 0)
        {
            _logger.LogInformation(
                "[NativeTrickplay] resume scan complete: {Scanned} dirs checked, {Resumed} re-queued, {Skipped} skipped",
                scanned, resumed, skipped);
        }
        else
        {
            _logger.LogDebug(
                "[NativeTrickplay] resume scan complete: {Scanned} dirs checked, nothing to resume",
                scanned);
        }
    }
}
