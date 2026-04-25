using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NativeTrickplay.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Tasks;

/// <summary>
/// Three-phase cache pruner. Runs daily by default; configurable + manually runnable
/// from the dashboard. Each phase is gated by config so the user can disable any of
/// them independently. Phases run in cost order: orphan removal is free, age-based
/// is cheap, size cap is the most aggressive.
/// </summary>
public sealed class PruneTrickplayCacheTask : IScheduledTask
{
    private readonly IframeAssetCache _cache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PruneTrickplayCacheTask> _logger;

    public PruneTrickplayCacheTask(
        IframeAssetCache cache,
        ILibraryManager libraryManager,
        ILogger<PruneTrickplayCacheTask> logger)
    {
        _cache = cache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Prune Native Trickplay Cache";
    public string Key => "PruneNativeTrickplayCache";
    public string Description =>
        "Removes I-frame cache entries for items no longer in the library, items not "
        + "scrubbed in a configurable number of days, and (optionally) the least-recently-"
        + "used entries when total cache size exceeds a configured cap.";
    public string Category => "Native Trickplay";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        }
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Snapshot config once per run — admin edits during a run shouldn't change behavior mid-flight.
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        // Materialize the snapshot up front. Iteration is cheap (file metadata only),
        // and we want a stable view for the size-cap phase that may need to evict by LRU order.
        var snapshot = _cache.EnumerateCache().ToList();
        if (snapshot.Count == 0)
        {
            _logger.LogInformation("[NativeTrickplay] prune: cache is empty, nothing to do");
            progress.Report(100);
            return Task.CompletedTask;
        }

        long totalBytes = snapshot.Sum(e => e.SizeBytes);
        _logger.LogInformation(
            "[NativeTrickplay] prune start: {Count} entries, {SizeMb} MB total",
            snapshot.Count, totalBytes / (1024 * 1024));

        var evicted = new HashSet<Guid>();
        int orphans = 0, aged = 0, capped = 0;
        long orphanBytes = 0, agedBytes = 0, cappedBytes = 0;

        // Phase 1: orphan removal — fast, no policy knobs (you almost never want to keep them).
        if (cfg.PruneOrphans)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = snapshot[i];
                if (_libraryManager.GetItemById(entry.ItemId) is null
                    && _cache.TryEvict(entry.ItemId))
                {
                    evicted.Add(entry.ItemId);
                    orphans++;
                    orphanBytes += entry.SizeBytes;
                }
                progress.Report(33.0 * (i + 1) / snapshot.Count);
            }
            _logger.LogInformation(
                "[NativeTrickplay] phase 1 (orphans): evicted {Count} ({SizeMb} MB)",
                orphans, orphanBytes / (1024 * 1024));
        }
        else
        {
            progress.Report(33);
        }

        // Phase 2: age-based eviction.
        if (cfg.MaxAgeDays > 0)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(cfg.MaxAgeDays);
            int processed = 0;
            foreach (var entry in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress.Report(33 + 33.0 * processed / snapshot.Count);

                if (evicted.Contains(entry.ItemId)) continue;
                if (entry.LastAccessUtc >= cutoff) continue;
                if (_cache.TryEvict(entry.ItemId))
                {
                    evicted.Add(entry.ItemId);
                    aged++;
                    agedBytes += entry.SizeBytes;
                }
            }
            _logger.LogInformation(
                "[NativeTrickplay] phase 2 (age > {Days}d): evicted {Count} ({SizeMb} MB)",
                cfg.MaxAgeDays, aged, agedBytes / (1024 * 1024));
        }
        else
        {
            progress.Report(66);
        }

        // Phase 3: size cap (LRU). Only acts on entries that survived phases 1 and 2.
        if (cfg.MaxCacheGigabytes > 0)
        {
            long capBytes = (long)cfg.MaxCacheGigabytes * 1024L * 1024L * 1024L;
            long remainingBytes = snapshot
                .Where(e => !evicted.Contains(e.ItemId))
                .Sum(e => e.SizeBytes);

            if (remainingBytes > capBytes)
            {
                var lru = snapshot
                    .Where(e => !evicted.Contains(e.ItemId))
                    .OrderBy(e => e.LastAccessUtc)
                    .ToList();
                int processed = 0;
                foreach (var entry in lru)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;
                    progress.Report(66 + 33.0 * processed / Math.Max(1, lru.Count));

                    if (remainingBytes <= capBytes) break;
                    if (_cache.TryEvict(entry.ItemId))
                    {
                        evicted.Add(entry.ItemId);
                        capped++;
                        cappedBytes += entry.SizeBytes;
                        remainingBytes -= entry.SizeBytes;
                    }
                }
            }
            _logger.LogInformation(
                "[NativeTrickplay] phase 3 (cap {Cap} GB): evicted {Count} ({SizeMb} MB)",
                cfg.MaxCacheGigabytes, capped, cappedBytes / (1024 * 1024));
        }

        progress.Report(100);
        long postBytes = totalBytes - orphanBytes - agedBytes - cappedBytes;
        int postCount = snapshot.Count - evicted.Count;
        _logger.LogInformation(
            "[NativeTrickplay] prune done: evicted {EvictCount} ({EvictMb} MB); kept {KeepCount} ({KeepMb} MB)",
            evicted.Count, (orphanBytes + agedBytes + cappedBytes) / (1024 * 1024),
            postCount, postBytes / (1024 * 1024));

        return Task.CompletedTask;
    }
}
