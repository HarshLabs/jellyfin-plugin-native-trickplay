using System;
using System.IO;
using System.Threading.Tasks;
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
    public async Task<IActionResult> GetPlaylist([FromRoute] Guid itemId)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }
        if (_libraryManager.GetItemById(itemId) is not BaseItem item || item.IsFolder || string.IsNullOrEmpty(item.Path))
        {
            return NotFound();
        }

        IframeAsset asset;
        try
        {
            asset = await _cache.GetOrCreateAsync(itemId, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate iframe asset for {ItemId}", itemId);
            return StatusCode(500);
        }

        var auth = BuildAuthSuffix(Request);
        var body = asset.PlaylistTemplate.Replace("{AUTH}", auth, StringComparison.Ordinal);
        return Content(body, "application/vnd.apple.mpegurl");
    }

    [HttpGet("Videos/{itemId:guid}/iframe.m4s")]
    public async Task<IActionResult> GetSegment([FromRoute] Guid itemId)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            return NotFound();
        }

        IframeAsset asset;
        try
        {
            asset = await _cache.GetOrCreateAsync(itemId, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch iframe segment for {ItemId}", itemId);
            return StatusCode(500);
        }

        if (!System.IO.File.Exists(asset.SegmentPath))
        {
            return NotFound();
        }
        return PhysicalFile(asset.SegmentPath, "video/iso.segment", enableRangeProcessing: true);
    }

    private static string BuildAuthSuffix(HttpRequest request)
    {
        // Forward ApiKey from query if present (AVPlayer cannot set custom headers on segment fetches).
        // Server side, [Authorize] also accepts the bearer header; clients that supply it on the
        // playlist request will also supply it on segment fetches via the Authorization header.
        var apiKey = (string?)request.Query["ApiKey"]
                     ?? (string?)request.Query["api_key"];
        return string.IsNullOrEmpty(apiKey) ? string.Empty : "?ApiKey=" + Uri.EscapeDataString(apiKey);
    }
}
