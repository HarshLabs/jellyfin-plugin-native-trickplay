using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NativeTrickplay.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Tasks;

/// <summary>
/// Walks every video item in the library and pre-encodes its iframe asset
/// if not already cached. Mirrors Jellyfin's existing "Generate Trickplay
/// Images" task — runs daily by default, off-hours, so the user never
/// pays the 10–30s ffmpeg cost at scrub time. First run on a large library
/// can take hours; subsequent runs only catch up newly-added items.
/// </summary>
public sealed class PreGenerateIframeAssetsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IframeAssetCache _cache;
    private readonly ILogger<PreGenerateIframeAssetsTask> _logger;

    public PreGenerateIframeAssetsTask(
        ILibraryManager libraryManager,
        IframeAssetCache cache,
        ILogger<PreGenerateIframeAssetsTask> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    public string Name => "Pre-generate Native Trickplay I-frames";
    public string Key => "PreGenerateNativeTrickplayIframes";
    public string Description =>
        "Encodes I-frame trickplay assets for every video item so the first scrub is instant. "
        + "Long-running on first execution, fast thereafter (skips already-cached items).";
    public string Category => "Native Trickplay";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks   // 4 AM, after Jellyfin's 3 AM trickplay images
        }
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance is null || !Plugin.Instance.Configuration.Enabled)
        {
            _logger.LogInformation("[NativeTrickplay] pre-gen: plugin disabled, skipping");
            return;
        }

        // Limit to actual playable video kinds. Skip virtual / placeholder items.
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.MusicVideo,
                BaseItemKind.Video,
            },
            Recursive = true,
            IsVirtualItem = false,
        };

        var items = _libraryManager.GetItemList(query);
        var total = items.Count;
        _logger.LogInformation("[NativeTrickplay] pre-gen start: {Total} candidate items", total);
        if (total == 0)
        {
            progress.Report(100);
            return;
        }

        int processed = 0, generated = 0, cached = 0, failed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(item.Path))
            {
                processed++;
                progress.Report(100.0 * processed / total);
                continue;
            }

            try
            {
                if (_cache.TryGetCached(item.Id) is not null)
                {
                    cached++;
                }
                else
                {
                    // WaitAsync lets the user cancel the scheduled task without
                    // killing the in-flight ffmpeg — ffmpeg uses
                    // ApplicationStopping internally and will finish encoding
                    // the current item to disk, where the next run picks up.
                    await _cache.GetOrCreateAsync(item.Id)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    generated++;
                    _logger.LogDebug("[NativeTrickplay] pre-gen: encoded {Id} ({Name})", item.Id, item.Name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "[NativeTrickplay] pre-gen: failed for {Id} ({Name})", item.Id, item.Name);
            }

            processed++;
            progress.Report(100.0 * processed / total);
        }

        _logger.LogInformation(
            "[NativeTrickplay] pre-gen done: {Processed}/{Total} processed, {Generated} encoded, {Cached} already-cached, {Failed} failed",
            processed, total, generated, cached, failed);
    }
}
