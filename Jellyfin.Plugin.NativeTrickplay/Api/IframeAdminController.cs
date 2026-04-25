using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NativeTrickplay.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Api;

/// <summary>
/// Admin-only endpoints powering the cache management section of the
/// dashboard config page. Lets administrators inspect cache state,
/// trigger generation for selected items, and evict cached entries
/// without going through the playback flow.
///
/// All endpoints require elevation (admin user) since they can drive
/// substantial CPU/disk use.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/NativeTrickplay")]
public sealed class IframeAdminController : ControllerBase
{
    private readonly IframeAssetCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<IframeAdminController> _logger;

    public IframeAdminController(
        IframeAssetCache cache,
        ILibraryManager libraryManager,
        ILogger<IframeAdminController> logger)
    {
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Cache overview: total cached items + bytes, plus a per-library
    /// breakdown of cached vs uncached counts.
    /// </summary>
    /// <summary>
    /// Live in-flight generations. Polled by the dashboard every couple of
    /// seconds while encoding is happening so the admin sees progress
    /// without re-clicking "Refresh status".
    /// </summary>
    [HttpGet("Progress")]
    public ActionResult<ProgressResponse> GetProgress()
    {
        var inflight = _cache.EnumerateInFlight().ToList();
        return new ProgressResponse(inflight.Count, DateTime.UtcNow, inflight);
    }

    [HttpGet("Status")]
    public ActionResult<StatusResponse> GetStatus()
    {
        var cachedEntries = _cache.EnumerateCache().ToList();
        long cachedBytes = cachedEntries.Sum(e => e.SizeBytes);
        var cachedIds = new HashSet<Guid>(cachedEntries.Select(e => e.ItemId));

        // Library breakdown — count of playable video items per top-level
        // library and how many are cached.
        var libraries = new List<LibraryStatus>();
        var views = _libraryManager.GetUserRootFolder().Children?.OfType<Folder>().ToList() ?? new List<Folder>();
        foreach (var lib in views)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = PlayableVideoKinds,
                Recursive = true,
                IsVirtualItem = false,
                ParentId = lib.Id,
            };
            var items = _libraryManager.GetItemList(query);
            int cached = items.Count(i => cachedIds.Contains(i.Id));
            libraries.Add(new LibraryStatus(lib.Id, lib.Name ?? "(unnamed)", items.Count, cached));
        }

        return new StatusResponse(
            cachedEntries.Count,
            cachedBytes,
            libraries.OrderBy(l => l.Name).ToList());
    }

    /// <summary>
    /// Paginated, filterable item listing with per-item cache status.
    /// Default page size 100; the dashboard UI uses search to narrow down.
    /// </summary>
    [HttpGet("Items")]
    public ActionResult<ItemsResponse> ListItems(
        [FromQuery] string? search,
        [FromQuery] Guid? libraryId,
        [FromQuery] string cached = "all",
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = PlayableVideoKinds,
            Recursive = true,
            IsVirtualItem = false,
        };
        if (libraryId.HasValue && libraryId.Value != Guid.Empty) query.ParentId = libraryId.Value;

        var items = _libraryManager.GetItemList(query);

        // Search filter applied here, not via InternalItemsQuery.SearchTerm:
        // Jellyfin's SearchTerm only matches the item's Name, but TV episodes
        // are typically named "Episode 1", "Episode 2"… and the user expects
        // typing "dabba" to surface every episode of "Dabba Cartel". We also
        // match on SeriesName so episode searches behave intuitively.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            items = items.Where(i =>
                (i.Name?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i is Episode ep && (ep.SeriesName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false))
            ).ToList();
        }

        // Pre-compute the cache size map once — EnumerateCache walks the
        // filesystem; calling it per-item is O(N²).
        var sizeByItem = _cache.EnumerateCache().ToDictionary(e => e.ItemId, e => e.SizeBytes);

        // Build status list — sort newest first so user-relevant items
        // (recent additions) bubble up.
        var rows = items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .OrderByDescending(i => i.DateCreated)
            .Select(i => MakeRow(i, sizeByItem))
            .ToList();

        if (cached.Equals("true", StringComparison.OrdinalIgnoreCase))
            rows = rows.Where(r => r.Cached).ToList();
        else if (cached.Equals("false", StringComparison.OrdinalIgnoreCase))
            rows = rows.Where(r => !r.Cached).ToList();

        var page = rows.Skip(offset).Take(limit).ToList();
        return new ItemsResponse(rows.Count, offset, limit, page);
    }

    /// <summary>
    /// Queue generation for one or more items. Idempotent: items already
    /// cached or in flight return immediately. Non-existent items are skipped.
    /// </summary>
    [HttpPost("Generate")]
    public ActionResult<MutationResponse> Generate([FromBody] ItemIdsRequest req)
    {
        if (req?.ItemIds is null || req.ItemIds.Count == 0)
            return new MutationResponse(0, 0);

        int queued = 0, skipped = 0;
        foreach (var id in req.ItemIds)
        {
            if (id == Guid.Empty || _libraryManager.GetItemById(id) is not BaseItem item || item.IsFolder)
            {
                skipped++;
                continue;
            }
            _cache.Warmup(id);
            queued++;
        }
        _logger.LogInformation(
            "[NativeTrickplay] admin Generate: {Queued} queued, {Skipped} skipped",
            queued, skipped);
        return new MutationResponse(queued, skipped);
    }

    /// <summary>
    /// Evict cache entries for one or more items. In-flight encodes are
    /// skipped (the cache returns false from TryEvict in that case).
    /// </summary>
    [HttpPost("Evict")]
    public ActionResult<MutationResponse> Evict([FromBody] ItemIdsRequest req)
    {
        if (req?.ItemIds is null || req.ItemIds.Count == 0)
            return new MutationResponse(0, 0);

        int evicted = 0, skipped = 0;
        foreach (var id in req.ItemIds)
        {
            if (id == Guid.Empty || !_cache.TryEvict(id)) skipped++;
            else evicted++;
        }
        _logger.LogInformation(
            "[NativeTrickplay] admin Evict: {Evicted} evicted, {Skipped} skipped",
            evicted, skipped);
        return new MutationResponse(evicted, skipped);
    }

    /// <summary>
    /// Bulk generate every item in a specific library. The dashboard
    /// uses this for the "Generate all in [Library]" button — saves the
    /// client from having to enumerate + post N item IDs over HTTP.
    /// </summary>
    [HttpPost("GenerateLibrary")]
    public ActionResult<MutationResponse> GenerateLibrary([FromBody] LibraryRequest req)
    {
        if (req is null || req.LibraryId == Guid.Empty) return new MutationResponse(0, 0);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = PlayableVideoKinds,
            Recursive = true,
            IsVirtualItem = false,
            ParentId = req.LibraryId,
        };
        var items = _libraryManager.GetItemList(query);
        int queued = 0;
        foreach (var item in items)
        {
            if (item.IsFolder || string.IsNullOrEmpty(item.Path)) continue;
            _cache.Warmup(item.Id);
            queued++;
        }
        _logger.LogInformation(
            "[NativeTrickplay] admin GenerateLibrary: queued {Count} items from library {Library}",
            queued, req.LibraryId);
        return new MutationResponse(queued, items.Count - queued);
    }

    private static readonly BaseItemKind[] PlayableVideoKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.MusicVideo,
        BaseItemKind.Video,
    ];

    private ItemRow MakeRow(BaseItem item, IReadOnlyDictionary<Guid, long> sizeByItem)
    {
        var cached = _cache.TryGetCached(item.Id) is not null;
        string? series = null;
        int? season = null, ep = null;
        if (item is Episode episode)
        {
            series = episode.SeriesName;
            season = episode.ParentIndexNumber;
            ep = episode.IndexNumber;
        }
        long? size = sizeByItem.TryGetValue(item.Id, out var s) ? s : null;
        return new ItemRow(
            item.Id,
            item.Name ?? "(untitled)",
            item.GetType().Name,
            series,
            season,
            ep,
            item.RunTimeTicks,
            cached,
            size);
    }

    public sealed record ProgressResponse(int InflightCount, DateTime ServerUtc, IReadOnlyList<InflightState> Inflight);
    public sealed record StatusResponse(int CachedItems, long CachedBytes, IReadOnlyList<LibraryStatus> Libraries);
    public sealed record LibraryStatus(Guid Id, string Name, int TotalItems, int CachedItems);
    public sealed record ItemsResponse(int Total, int Offset, int Limit, IReadOnlyList<ItemRow> Items);
    public sealed record ItemRow(
        Guid Id, string Name, string Type, string? SeriesName,
        int? SeasonNumber, int? EpisodeNumber, long? RunTimeTicks,
        bool Cached, long? CacheSizeBytes);
    public sealed record ItemIdsRequest(List<Guid> ItemIds);
    public sealed record LibraryRequest(Guid LibraryId);
    public sealed record MutationResponse(int Queued, int Skipped);
}
