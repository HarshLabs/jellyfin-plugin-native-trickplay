using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.NativeTrickplay.Hls;
using Jellyfin.Plugin.NativeTrickplay.Tasks;

namespace Jellyfin.Plugin.NativeTrickplay;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public int IframeWidth { get; set; } = 320;
    public int IframeCrf { get; set; } = 32;
    public string IframePreset { get; set; } = "ultrafast";
    public int MaxConcurrentGenerations { get; set; } = 1;

    /// <summary>
    /// Seconds between trickplay thumbnails. 1.0 = one I-frame per second of
    /// source (smooth scrubbing UX, ~30 MB cache for a 24-min episode at 320p).
    /// Larger values trade scrubbing granularity for cache size — set to e.g.
    /// 5.0 if disk space is tight. Must be &gt;= 1.0.
    /// </summary>
    public double IframeIntervalSeconds { get; set; } = 1.0;
    /// <summary>
    /// When true, follows Jellyfin's global "Hardware Acceleration" setting
    /// (Dashboard → Playback → Transcoding) and adds the appropriate
    /// -hwaccel flag to the ffmpeg invocation. Big speedup for HEVC/HDR
    /// source decode; encoding stays on CPU (libx264) because hardware
    /// H.264 encoders don't support keyint=1 reliably.
    /// </summary>
    public bool UseHardwareDecoding { get; set; } = true;

    /// <summary>
    /// Absolute filesystem path where iframe assets are stored. Empty = use
    /// Jellyfin's default cache directory. Useful for putting the cache on a
    /// larger / faster / dedicated drive. The plugin doesn't migrate existing
    /// cache when this is changed — old assets stay at the old path and become
    /// orphaned (delete manually, or just let them sit).
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    // Pruner knobs (used by PruneTrickplayCacheTask)
    public bool PruneOrphans { get; set; } = true;
    public int MaxAgeDays { get; set; } = 90;             // 0 = disabled
    public int MaxCacheGigabytes { get; set; } = 0;        // 0 = disabled (no size cap)

    /// <summary>
    /// When true (default), the plugin scans the cache root on startup
    /// and re-queues any item whose previous encode was interrupted —
    /// detected by a half-written `iframe.m4s.tmp` file with no valid
    /// `.source-mtime` stamp alongside it. This recovers automatically
    /// from plugin upgrades, Jellyfin crashes, or power failures
    /// without the user having to manually trigger a re-encode.
    /// Disable if you'd rather restart Jellyfin to abort a runaway
    /// bulk encode and have those items stay un-encoded.
    /// </summary>
    public bool ResumeInterruptedEncodesOnStartup { get; set; } = true;
}

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Native Trickplay";

    public override Guid Id => Guid.Parse("6911edf0-840d-4a71-965d-1319cbb2efd1");

    public override string Description =>
        "Generates HLS I-frame playlists so AVPlayer-based clients (Apple TV) get native scrubbing thumbnails.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        }
    ];
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IframeAssetCache>();
        serviceCollection.AddSingleton<MasterPlaylistInjector>();
        serviceCollection.AddSingleton<IScheduledTask, PruneTrickplayCacheTask>();
        // Walks the whole library off-hours and encodes any item that isn't
        // already cached. Eliminates the first-scrub wait entirely once it
        // has run once.
        serviceCollection.AddSingleton<IScheduledTask, PreGenerateIframeAssetsTask>();
        // Pre-warms the cache when playback starts so the user's first scrub is
        // served from disk instead of blocking on a 10-30s ffmpeg encode.
        serviceCollection.AddHostedService<PlaybackWarmupService>();
        // Detects encodes that were interrupted by a previous shutdown
        // (plugin upgrade, crash, power loss) and re-queues them 30s after
        // boot so the cache self-heals without manual intervention.
        serviceCollection.AddHostedService<StartupResumeService>();
        serviceCollection.PostConfigure<MvcOptions>(opts =>
        {
            opts.Filters.AddService<MasterPlaylistInjector>();
        });
    }
}
