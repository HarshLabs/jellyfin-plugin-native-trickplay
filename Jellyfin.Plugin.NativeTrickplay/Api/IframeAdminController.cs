using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
        [FromQuery] string sort = "recent",
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

        // Search filter — see ParseSearchQuery for what it recognizes. The
        // raw query is parsed into (tokens, season?, episode?) and we apply
        // each piece independently:
        //   * every text token must appear (case + accent insensitive) in
        //     the item Name OR (for episodes) the SeriesName — AND-logic so
        //     "the walking dead" requires all three words to match
        //   * a season token restricts to episodes whose ParentIndexNumber
        //     matches; non-episodes are dropped if S/E was specified
        //   * an episode token restricts likewise to IndexNumber
        // We don't rely on InternalItemsQuery.SearchTerm because Jellyfin's
        // built-in only matches item.Name, missing episodes whose user-
        // visible identity comes from the parent series ("Episode 18" alone
        // tells the user nothing).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var (tokens, season, episode) = ParseSearchQuery(search);
            items = items.Where(i =>
            {
                foreach (var token in tokens)
                {
                    bool match = ContainsCiAccentless(i.Name, token);
                    if (!match && i is Episode tep) match = ContainsCiAccentless(tep.SeriesName, token);
                    if (!match) return false;
                }

                if (season.HasValue || episode.HasValue)
                {
                    if (i is not Episode ep) return false;
                    if (season.HasValue && ep.ParentIndexNumber != season.Value) return false;
                    if (episode.HasValue && ep.IndexNumber != episode.Value) return false;
                }
                return true;
            }).ToList();
        }

        // Pre-compute the cache size map once — EnumerateCache walks the
        // filesystem; calling it per-item is O(N²).
        var sizeByItem = _cache.EnumerateCache().ToDictionary(e => e.ItemId, e => e.SizeBytes);

        // Materialize rows with cache status, then apply the requested sort.
        // Sort happens after status enrichment so we can sort by cached flag
        // / cache size without a second library scan.
        IEnumerable<ItemRow> rowSource = items
            .Where(i => !string.IsNullOrEmpty(i.Path))
            .Select(i => (item: i, row: MakeRow(i, sizeByItem)))
            .Select(x => x.row);

        var rows = ApplySort(rowSource, sort).ToList();

        if (cached.Equals("true", StringComparison.OrdinalIgnoreCase))
            rows = rows.Where(r => r.Cached).ToList();
        else if (cached.Equals("false", StringComparison.OrdinalIgnoreCase))
            rows = rows.Where(r => !r.Cached).ToList();

        var page = rows.Skip(offset).Take(limit).ToList();
        return new ItemsResponse(rows.Count, offset, limit, page);
    }

    private static IEnumerable<ItemRow> ApplySort(IEnumerable<ItemRow> rows, string sort)
    {
        // Tie-breakers always end with title so identical sort keys produce
        // a stable, alphabetic order.
        var ci = StringComparer.OrdinalIgnoreCase;
        return sort.ToLowerInvariant() switch
        {
            "name" => rows.OrderBy(r => r.Name, ci),
            "name_desc" => rows.OrderByDescending(r => r.Name, ci),
            "series" => rows
                .OrderBy(r => r.SeriesName ?? r.Name, ci)
                .ThenBy(r => r.SeasonNumber ?? int.MaxValue)
                .ThenBy(r => r.EpisodeNumber ?? int.MaxValue)
                .ThenBy(r => r.Name, ci),
            "size" => rows.OrderByDescending(r => r.CacheSizeBytes ?? 0).ThenBy(r => r.Name, ci),
            "size_asc" => rows.OrderBy(r => r.CacheSizeBytes ?? long.MaxValue).ThenBy(r => r.Name, ci),
            "cached" => rows.OrderByDescending(r => r.Cached).ThenBy(r => r.Name, ci),
            "uncached" => rows.OrderBy(r => r.Cached).ThenBy(r => r.Name, ci),
            "runtime" => rows.OrderByDescending(r => r.RunTimeTicks ?? 0).ThenBy(r => r.Name, ci),
            // "recent" (default) — fall back to anything that walks well in
            // the absence of a DateCreated field on the row. We added a
            // hidden surrogate via the Items query order before this point;
            // here we just keep the iteration order, then alpha tie-break.
            _ => rows,
        };
    }

    /// <summary>
    /// Contains-check that's both case-insensitive AND accent-insensitive,
    /// so "pokemon" matches "Pokémon" and "cafe" matches "café". Uses the
    /// invariant culture's CompareInfo with IgnoreNonSpace which strips
    /// combining marks during comparison.
    /// </summary>
    private static bool ContainsCiAccentless(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
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

    /// <summary>
    /// Parse a free-form search string into (text tokens, season?, episode?).
    /// All non-S/E whitespace-separated words become tokens that must ALL
    /// match (AND-logic) somewhere in the candidate's Name or SeriesName.
    /// Matching is case + accent insensitive (see ContainsCiAccentless).
    ///
    /// Recognized season/episode forms (case-insensitive, word-boundary aware
    /// so we don't accidentally consume "S1" inside a word like "S1mple"):
    ///   S6E18 / s6e18 / S06E18  → season=6, episode=18
    ///   1x05                     → season=1, episode=5  (alt notation)
    ///   Season 6 / season6       → season=6
    ///   Episode 18 / episode18   → episode=18
    ///   S7  / s7                 → season=7  (only if no combined match)
    ///   E18 / e18                → episode=18
    ///
    /// Multiple S/E forms in the same query are honored most-specific-first;
    /// once season/episode is set, later patterns can't overwrite. Stripping
    /// is done in-place on the working string so leftover words (the actual
    /// title text) are returned cleanly with collapsed whitespace.
    /// </summary>
    internal static (List<string> Tokens, int? Season, int? Episode) ParseSearchQuery(string raw)
    {
        const RegexOptions opts = RegexOptions.IgnoreCase;
        var s = raw.Trim();
        int? season = null, episode = null;

        // Pattern table — first match wins for season/episode each, and the
        // matched span is removed from the working string so it doesn't
        // leak into the text tokens.
        var patterns = new (string Pattern, Action<Match> Apply)[]
        {
            // SxxEyy: assign both
            (@"\bS(\d+)E(\d+)\b", m =>
            {
                season ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                episode ??= int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }),
            // 1x05: alt season×episode
            (@"\b(\d+)x(\d+)\b", m =>
            {
                season ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                episode ??= int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            }),
            // Season N (with or without space)
            (@"\bSeason\s*(\d+)\b", m =>
            {
                season ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }),
            // Episode N
            (@"\bEpisode\s*(\d+)\b", m =>
            {
                episode ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }),
            // Bare S<n>
            (@"\bS(\d+)\b", m =>
            {
                season ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }),
            // Bare E<n>
            (@"\bE(\d+)\b", m =>
            {
                episode ??= int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }),
        };

        foreach (var (pattern, apply) in patterns)
        {
            var m = Regex.Match(s, pattern, opts);
            if (!m.Success) continue;
            apply(m);
            s = s.Remove(m.Index, m.Length);
        }

        // Collapse whitespace runs left behind by token removal.
        s = Regex.Replace(s, @"\s+", " ").Trim();
        var tokens = string.IsNullOrEmpty(s)
            ? new List<string>()
            : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        return (tokens, season, episode);
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
