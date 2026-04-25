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
    // How long to ask the client to wait before retrying when generation is in flight.
    // Roughly the worst-case generation time for a long episode at default settings.
    private const int RetryAfterSeconds = 15;

    private readonly IframeAssetCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<IframeHlsController> _logger;

    public IframeHlsController(IframeAssetCache cache, ILibraryManager libraryManager, ILogger<IframeHlsController> logger)
    {
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [HttpGet("Videos/{itemId:guid}/iframe.m3u8")]
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
            return Content(body, "application/vnd.apple.mpegurl");
        }

        // Not yet cached. Kick off generation in the background and tell the client
        // to retry. Using 503 + Retry-After (per HTTP spec) lets AVPlayer politely
        // come back later instead of giving up on the I-frame variant for the
        // entire playback session. Mark High priority — by the time we're
        // serving an iframe.m3u8 request the user is mid-playback and would
        // otherwise sit behind any pre-gen / bulk-encode queue.
        _cache.Warmup(itemId, isPriority: true);
        _logger.LogInformation(
            "[NativeTrickplay] iframe.m3u8 requested for {ItemId} but not cached — 503 + Retry-After issued",
            itemId);
        Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
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
