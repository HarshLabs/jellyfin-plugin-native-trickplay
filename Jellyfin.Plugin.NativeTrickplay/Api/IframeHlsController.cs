using System;
using System.Globalization;
using Jellyfin.Plugin.NativeTrickplay.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Api;

[ApiController]
[Authorize]
[Route("")]
public sealed class IframeHlsController : ControllerBase
{
    // No-cache header on the cache-miss stub so AVPlayer re-fetches
    // on the next playback session and gets real iframes if the
    // background encode has completed in the meantime.
    private const string NoStoreCacheControl = "no-store, max-age=0";

    // Empty I-frames-only HLS playlist returned when the cache is cold.
    // EXT-X-ENDLIST signals "complete, 0 segments" so AVPlayer marks
    // the variant unusable for this session and immediately proceeds
    // with primary playback. Real iframes will appear on the NEXT
    // playback session once the background encode (kicked off by
    // PlaybackWarmupService) finishes.
    private const string EmptyIframePlaylist =
        "#EXTM3U\n" +
        "#EXT-X-VERSION:7\n" +
        "#EXT-X-TARGETDURATION:1\n" +
        "#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXT-X-I-FRAMES-ONLY\n" +
        "#EXT-X-ENDLIST\n";

    private readonly IframeAssetCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<IframeHlsController> _logger;

    public IframeHlsController(IframeAssetCache cache, ILibraryManager libraryManager, ILogger<IframeHlsController> logger)
    {
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // [HttpHead] in addition to [HttpGet] so HDR-aware clients (JellyseerTV)
    // can cheaply probe cache state via the X-Trickplay-Status response
    // header. ASP.NET Core does not map HEAD onto GET routes automatically;
    // without an explicit attribute, HEAD returns 405 Method Not Allowed.
    [HttpGet("Videos/{itemId:guid}/iframe.m3u8")]
    [HttpHead("Videos/{itemId:guid}/iframe.m3u8")]
    [Produces("application/vnd.apple.mpegurl")]
    public IActionResult GetPlaylist([FromRoute] Guid itemId)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.IsFolder || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        var cached = _cache.TryGetCached(itemId);
        if (cached is not null)
        {
            var auth = BuildAuthSuffix(Request);
            var body = cached.PlaylistTemplate.Replace("{AUTH}", auth, StringComparison.Ordinal);
            _logger.LogInformation(
                "[NativeTrickplay] iframe.m3u8 served for {ItemId} ({Bytes} bytes)",
                itemId, body.Length);
            // Custom status header lets HDR-aware clients (JellyseerTV) HEAD-probe
            // before deciding whether to advertise the I-frame variant in their
            // synthetic master. tvOS 26's SUPPLEMENTAL-CODECS validator is strict
            // about variant validity; clients can only safely include the variant
            // when this header is "ready".
            Response.Headers["X-Trickplay-Status"] = "ready";
            return Content(body, "application/vnd.apple.mpegurl");
        }

        // Cache miss. Serve a valid empty I-frames-only playlist so
        // AVPlayer can complete its master parse and immediately
        // proceed with primary playback.
        //
        // Crucially: do NOT call _cache.Warmup() here. Doing so kicks
        // off the iframe encoder ffmpeg within ~100ms of AVPlayer's
        // iframe.m3u8 fetch, which lands in the same 1-second window
        // where Jellyfin's main TranscodeManager probe ffmpeg is
        // trying to start. On macOS videotoolbox the two ffmpegs
        // compete and Jellyfin's main probe never fires, so primary
        // playback stalls indefinitely — observed empirically in
        // user logs (see v1.1.32 diagnosis in
        // memory/native-trickplay-plugin.md).
        //
        // PlaybackWarmupService already hooks ISessionManager.PlaybackStart
        // and schedules the encode with a 30-second defer specifically
        // to avoid this race. Trust that path; it's the only Warmup
        // trigger needed during normal playback.
        //
        // The trade-offs:
        //   * 503 + Retry-After: AVPlayer waits for variant to become
        //     available, stalling primary playback for the full
        //     encode duration (~6 min for a 4K HDR episode). NOT OK.
        //   * 404: AVPlayer drops variant cleanly, BUT (per logs) it
        //     also seems to inhibit Jellyfin's main TranscodeManager
        //     from firing on rapid stop/replay sessions. NOT OK.
        //   * Empty playlist + EXT-X-ENDLIST (this branch): AVPlayer
        //     parses, sees 0 segments, marks the variant unusable for
        //     this session, primary playback proceeds normally. OK.
        _logger.LogInformation(
            "[NativeTrickplay] iframe.m3u8 requested for {ItemId} but not cached — serving empty stub (PlaybackWarmupService will run encode 30s after PlaybackStart; trickplay available next session)",
            itemId);
        Response.Headers.CacheControl = NoStoreCacheControl;
        Response.Headers["X-Trickplay-Status"] = "encoding";
        return Content(EmptyIframePlaylist, "application/vnd.apple.mpegurl");
    }

    [HttpGet("Videos/{itemId:guid}/iframe.m4s")]
    public IActionResult GetSegment([FromRoute] Guid itemId)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }

        var cached = _cache.TryGetCached(itemId);
        if (cached is null)
        {
            _logger.LogInformation(
                "[NativeTrickplay] iframe.m4s requested for {ItemId} but no cache present — 404",
                itemId);
            return NotFound();
        }

        var range = Request.Headers.Range.ToString();
        _logger.LogInformation(
            "[NativeTrickplay] iframe.m4s served for {ItemId} (range='{Range}')",
            itemId, string.IsNullOrEmpty(range) ? "<none>" : range);
        return PhysicalFile(cached.SegmentPath, "video/iso.segment", enableRangeProcessing: true);
    }

    private static string BuildAuthSuffix(HttpRequest request)
    {
        var apiKey = (string?)request.Query["ApiKey"]
                     ?? (string?)request.Query["api_key"];
        return string.IsNullOrEmpty(apiKey) ? string.Empty : "?ApiKey=" + Uri.EscapeDataString(apiKey);
    }
}
