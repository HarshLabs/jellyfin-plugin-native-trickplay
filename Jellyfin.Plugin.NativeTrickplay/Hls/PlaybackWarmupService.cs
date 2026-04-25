using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Subscribes to ISessionManager.PlaybackStart and fires off iframe asset
/// generation as soon as the user begins playing an item. By the time the
/// user reaches for the scrubber (typically 5-30s later), the asset is
/// already cached, so the controller's hot path serves it immediately.
///
/// Without this, every first-scrub of a fresh item would block on a 10-30s
/// ffmpeg encode that AVPlayer times out before completing — leaving the
/// I-frame variant permanently broken for that item.
/// </summary>
public sealed class PlaybackWarmupService : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly IframeAssetCache _cache;
    private readonly ILogger<PlaybackWarmupService> _logger;

    public PlaybackWarmupService(
        ISessionManager sessionManager,
        IframeAssetCache cache,
        ILogger<PlaybackWarmupService> logger)
    {
        _sessionManager = sessionManager;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _logger.LogInformation("[NativeTrickplay] playback warmup service hooked");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled) return;
        if (e.Item is null || e.Item.IsFolder) return;
        if (e.Item is not Video) return;     // skip audio, photos, etc.
        if (e.Item.Path is null) return;     // virtual / live tv items

        // Fire-and-forget. The cache dedupes concurrent calls for the same item.
        _cache.Warmup(e.Item.Id);
        _logger.LogInformation("[NativeTrickplay] warmup queued for {Id} ({Name})", e.Item.Id, e.Item.Name);
    }
}
