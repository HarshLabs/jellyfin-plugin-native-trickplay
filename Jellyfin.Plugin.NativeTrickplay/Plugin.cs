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
    /// When true, follows Jellyfin's global "Hardware Acceleration" setting
    /// (Dashboard → Playback → Transcoding) and adds the appropriate
    /// -hwaccel flag to the ffmpeg invocation. Big speedup for HEVC/HDR
    /// source decode; encoding stays on CPU (libx264) because hardware
    /// H.264 encoders don't support keyint=1 reliably.
    /// </summary>
    public bool UseHardwareDecoding { get; set; } = true;

    // Pruner knobs (used by PruneTrickplayCacheTask)
    public bool PruneOrphans { get; set; } = true;
    public int MaxAgeDays { get; set; } = 90;             // 0 = disabled
    public int MaxCacheGigabytes { get; set; } = 0;        // 0 = disabled (no size cap)
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
        serviceCollection.PostConfigure<MvcOptions>(opts =>
        {
            opts.Filters.AddService<MasterPlaylistInjector>();
        });
    }
}
