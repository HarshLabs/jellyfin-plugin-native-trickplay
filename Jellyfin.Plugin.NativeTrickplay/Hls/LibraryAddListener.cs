using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Subscribes to ILibraryManager.ItemAdded and queues an iframe encode
/// at Normal priority when the EncodeOnLibraryAdd setting is enabled.
/// Mirrors Jellyfin's own "Extract trickplay images during the library
/// scan" toggle, but for the native-trickplay path.
///
/// Default-off: a fresh library import can fire thousands of ItemAdded
/// events, and the daily 4 AM pre-generate task already handles uncached
/// items overnight. Users who want the eager behavior opt in.
/// </summary>
public sealed class LibraryAddListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IframeAssetCache _cache;
    private readonly ILogger<LibraryAddListener> _logger;

    public LibraryAddListener(
        ILibraryManager libraryManager,
        IframeAssetCache cache,
        ILogger<LibraryAddListener> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _logger.LogInformation("[NativeTrickplay] library-add listener hooked");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.Enabled || !cfg.EncodeOnLibraryAdd) return;
        if (e.Item is not Video video || video.IsFolder) return;
        if (string.IsNullOrEmpty(video.Path)) return;

        // Skip if already cached (rare for ItemAdded but possible if the
        // file was re-added after a library remove).
        if (_cache.TryGetCached(video.Id) is not null) return;

        _logger.LogInformation(
            "[NativeTrickplay] library-add: queueing encode for {Id} ({Name})",
            video.Id, video.Name);
        _cache.Warmup(video.Id, isPriority: false);
    }
}
