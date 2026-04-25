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
/// Subscribes to ISessionManager.PlaybackStart and schedules iframe asset
/// generation in the background. The encode is intentionally DEFERRED past
/// the first-segment fetch window — see WarmupDelay below — so the heavy
/// full-decode + tone-map ffmpeg pipeline never races Jellyfin's stream-copy
/// ffmpeg startup, which would manifest as a client-side -1008 "resource
/// unavailable" on the very first segment fetch (HTTP playback is supposed
/// to be unaffected by this plugin, ever).
///
/// By the time the user reaches for the scrubber (typically 30s+ in), the
/// asset is already cached and the controller's hot path serves it immediately.
/// </summary>
public sealed class PlaybackWarmupService : IHostedService, IDisposable
{
    /// <summary>
    /// Reads the configured warmup delay (`WarmupDelaySeconds`), clamping to
    /// [5, 300]s defensively in case the user enters something out of range.
    /// Default 30 — see PluginConfiguration.WarmupDelaySeconds for rationale.
    /// </summary>
    private static TimeSpan GetWarmupDelay()
    {
        var seconds = Plugin.Instance?.Configuration.WarmupDelaySeconds ?? 30;
        seconds = Math.Clamp(seconds, 5, 300);
        return TimeSpan.FromSeconds(seconds);
    }

    private readonly ISessionManager _sessionManager;
    private readonly IframeAssetCache _cache;
    private readonly ILogger<PlaybackWarmupService> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();

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
        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _shutdownCts.Dispose();

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled) return;
        if (e.Item is null || e.Item.IsFolder) return;
        if (e.Item is not Video) return;     // skip audio, photos, etc.
        if (e.Item.Path is null) return;     // virtual / live tv items

        // Skip the deferred encode entirely if the asset is already cached —
        // there's nothing to warm up, and we don't want to log a misleading
        // "queued in 30s" line for a cache hit.
        if (_cache.TryGetCached(e.Item.Id) is not null)
        {
            _logger.LogDebug(
                "[NativeTrickplay] warmup skipped for {Id} ({Name}) — already cached",
                e.Item.Id, e.Item.Name);
            return;
        }

        var itemId = e.Item.Id;
        var name = e.Item.Name ?? "(unnamed)";
        var delay = GetWarmupDelay();
        _logger.LogInformation(
            "[NativeTrickplay] warmup scheduled for {Id} ({Name}) — deferred {Delay}s so it doesn't compete with Jellyfin's segment ffmpeg startup",
            itemId, name, (int)delay.TotalSeconds);

        // Fire-and-forget. _shutdownCts ensures pending warmups stop firing
        // when the plugin is unloaded; the cache itself uses a separate
        // application-lifetime token so the actual ffmpeg encode (once
        // started) survives until completion.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Re-check cache before launching — user may have already scrubbed
            // and triggered a synchronous regen via the controller's TryGetCached
            // miss path during the 30-second window.
            if (_cache.TryGetCached(itemId) is not null) return;

            // Playback is active for this item — mark High priority so the
            // encode leapfrogs any in-flight bulk/admin queue. Without this,
            // a user who clicks Play in the middle of a library-wide
            // pre-gen would wait behind every other item.
            _cache.Warmup(itemId, isPriority: true);
        }, _shutdownCts.Token);
    }
}
