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
    /// <summary>
    /// x264 encoder preset. <c>ultrafast</c> (default) is the right choice for
    /// 320p I-frame-only thumbnails — every output frame is an IDR (no motion
    /// estimation), output is tiny, and the visual quality difference vs slower
    /// presets is unmeasurable at this resolution and use case. Slower presets
    /// (<c>fast</c> → <c>veryslow</c>) cost 2–10× more CPU per encode for no
    /// perceptible benefit on trickplay thumbnails.
    /// Lower this only if you've raised IframeWidth to 480p+ AND care about
    /// thumbnail crispness for some reason.
    /// </summary>
    public string IframePreset { get; set; } = "ultrafast";
    public int MaxConcurrentGenerations { get; set; } = 1;

    /// <summary>
    /// Seconds between trickplay thumbnails. 2.0 = one I-frame every two
    /// seconds of source (smooth-enough scrubbing UX matching Apple's HLS
    /// authoring spec recommendation of 2-5s). Halving the frame count
    /// halves the encode work — exactly linear speedup with no impact on
    /// scrubbing quality for human use. Set to 1.0 for the densest possible
    /// scrubbing, or 5.0+ if disk space is tight. Must be &gt;= 1.0.
    /// </summary>
    public double IframeIntervalSeconds { get; set; } = 2.0;

    /// <summary>
    /// Threads each ffmpeg encode is allowed to use. Default <c>1</c> prevents
    /// thread oversubscription when MaxConcurrentGenerations > 1: without
    /// this cap, every ffmpeg auto-detects "all cores" so 4 concurrent on an
    /// 8-core box becomes 32-thread contention with massive context-switch
    /// overhead. Pinning to 1 thread per job typically gives 2–4× better
    /// aggregate throughput on bulk encodes.
    /// Set to <c>0</c> to let ffmpeg auto-pick — only useful when running a
    /// single encode at a time (MaxConcurrentGenerations=1) on a multi-core
    /// box, where you want one job to use all cores.
    /// </summary>
    public int EncodeThreadsPerJob { get; set; } = 1;
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

    /// <summary>
    /// Seconds to wait after a PlaybackStart event before kicking off the
    /// iframe encoder. The defer prevents the heavy decode+tonemap ffmpeg
    /// from racing Jellyfin's stream-copy ffmpeg startup, which manifested
    /// as -1008 "resource unavailable" stalls on the very first segment
    /// fetch in earlier versions. 30s is conservative and safe on slow
    /// disks / NAS deployments; on fast hardware (Apple Silicon, NVMe)
    /// you can lower it to ~10s for snappier queue feedback. Clamped
    /// to 5–300s.
    /// </summary>
    public int WarmupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// When true, queues an iframe encode at Normal priority every time
    /// Jellyfin's library scanner adds a new video item. Mirrors
    /// Jellyfin's own "Extract trickplay images during the library scan"
    /// option — but for native HLS trickplay rather than the BIF/JPEG
    /// sprite path.
    /// Default off because a brand-new library import can fire thousands
    /// of ItemAdded events; opt in once you've sized
    /// MaxConcurrentGenerations appropriately. The daily pre-generate
    /// task at 4 AM also covers uncached items, so leaving this off just
    /// means new items get cached overnight instead of immediately.
    /// </summary>
    public bool EncodeOnLibraryAdd { get; set; } = false;
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
        // Optional: queue an encode every time the library scanner adds a
        // new video item. Gated by the EncodeOnLibraryAdd config flag
        // (default off). Mirrors Jellyfin's "Extract trickplay images
        // during the library scan" toggle.
        serviceCollection.AddHostedService<LibraryAddListener>();
        serviceCollection.PostConfigure<MvcOptions>(opts =>
        {
            opts.Filters.AddService<MasterPlaylistInjector>();
        });
    }
}
