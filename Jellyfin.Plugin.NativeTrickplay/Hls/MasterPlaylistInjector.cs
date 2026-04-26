using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Buffers HLS playlist responses for /Videos/{id}/master.m3u8 and
/// /Videos/{id}/main.m3u8 and injects an I-frame trickplay variant
/// based on which path the client took.
///
/// Two distinct client flows both need to work:
///
/// 1. <b>Standard transcode flow</b> (e.g. Apple TV via JellyseerTV when
///    the file requires full transcoding): client uses Jellyfin's
///    `TranscodingUrl` = master.m3u8. We append #EXT-X-I-FRAME-STREAM-INF
///    and AVPlayer follows the standard master → primary variant chain.
///    The main.m3u8 fetch that follows must <b>pass through unchanged</b>
///    — wrapping it as a synthetic master here causes AVPlayer to chase
///    a layered master/master/media chain and stalls primary playback on
///    slow transcodes (observed end-to-end: 0.0s buffered forever for
///    HEVC 10-bit hev1 forced transcodes).
///
/// 2. <b>Direct-stream / client-built URL flow</b> (e.g. JellyseerTV's
///    "manually constructed HLS URL" for mkv direct-stream): client
///    bypasses master.m3u8 entirely and points AVPlayer at /main.m3u8
///    directly. We never get a chance to add the I-frame variant via
///    master injection. To deliver trickplay here we must <b>wrap the
///    main.m3u8 response as a synthetic master</b> that lists the
///    original media playlist as primary plus our I-frame variant.
///
/// Telling the two apart: track PlaySessionIds that fetched master.m3u8.
/// On a later main.m3u8 fetch with the same session, the client already
/// saw our injection in the master — pass through. Without a prior
/// master fetch, the client built its own URL — wrap.
/// </summary>
public sealed class MasterPlaylistInjector : IAsyncResultFilter
{
    private const string SkipMarker = "skipIframeInjection";
    private const string IframeMarker = "#EXT-X-I-FRAME-STREAM-INF";
    // Conservative declared peak. Apple's spec table (§6.7) puts a 1080p H.264
    // 1fps I-frame variant around 580 kbps; 480p / 720p land much lower.
    // Declaring 1 Mbps comfortably covers any segment peak our encoder produces
    // at typical IframeWidth (320–720) without violating the "measured peak <
    // 110% of BANDWIDTH" tolerance from the spec.
    private const long IframeBandwidth = 1_000_000;

    // Track PlaySessionIds whose master.m3u8 we've already augmented with
    // the I-frame variant. If a later main.m3u8 fetch comes in for the
    // same session, AVPlayer already knows about trickplay — don't wrap
    // (wrapping a media playlist as a synthetic master breaks primary
    // playback on slow transcodes). Sessions absent from this set came
    // from clients that built their own URL pointing directly at main.m3u8;
    // for those we must wrap so trickplay is discoverable.
    private static readonly ConcurrentDictionary<string, DateTime> MasterSeenSessions = new();
    private static readonly TimeSpan MasterSeenTtl = TimeSpan.FromMinutes(30);
    // Lazy cleanup interval — prune stale entries at most once per minute
    // when an injector call comes in. Keeps the dict bounded without a
    // dedicated background timer.
    private static DateTime _lastSweepUtc = DateTime.MinValue;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly IframeAssetCache _cache;
    private readonly ILogger<MasterPlaylistInjector> _logger;

    public MasterPlaylistInjector(ILibraryManager libraryManager, IframeAssetCache cache, ILogger<MasterPlaylistInjector> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        var path = http.Request.Path.Value;

        bool isMaster = path is not null && path.EndsWith("/master.m3u8", StringComparison.OrdinalIgnoreCase);
        bool isMain = path is not null && path.EndsWith("/main.m3u8", StringComparison.OrdinalIgnoreCase);

        if (Plugin.Instance is null
            || !Plugin.Instance.Configuration.Enabled
            || (!isMaster && !isMain)
            || !path!.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase)
            || http.Request.Query.ContainsKey(SkipMarker))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (!TryExtractItemId(path, out var itemId))
        {
            _logger.LogDebug("[NativeTrickplay] injector: could not parse itemId from {Path}", path);
            await next().ConfigureAwait(false);
            return;
        }

        // PlaySessionId distinguishes which client-side session this fetch
        // belongs to. Used to remember whether master.m3u8 was already
        // fetched for this session so we know whether main.m3u8 needs to
        // be wrapped. Both PlaySessionId (Jellyfin server URL) and
        // playSessionId (some clients construct their own URLs) are
        // accepted — Jellyfin itself parses case-insensitively.
        var playSessionId = http.Request.Query["PlaySessionId"].FirstOrDefault()
            ?? http.Request.Query["playSessionId"].FirstOrDefault();
        SweepStaleSessionsIfDue();

        // For main.m3u8: decide passthrough vs wrap based on whether the
        // master path was already taken. If we don't have a session id,
        // err on the side of the wrap (matches pre-1.1.43 behavior for
        // unknown clients).
        bool wrapMain = false;
        if (isMain)
        {
            if (!string.IsNullOrEmpty(playSessionId)
                && MasterSeenSessions.ContainsKey(playSessionId))
            {
                _logger.LogDebug(
                    "[NativeTrickplay] injector: main.m3u8 for {ItemId} session={Session} — master already injected, passing through",
                    itemId, playSessionId);
                await next().ConfigureAwait(false);
                return;
            }
            wrapMain = true;
        }

        _logger.LogDebug(
            "[NativeTrickplay] injector intercepting {Path}{Query} (item={ItemId}, mode={Mode})",
            path, http.Request.QueryString.Value, itemId, isMaster ? "master-inject" : "main-wrap");

        var origBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);

            http.Response.Body = origBody;

            buffer.Position = 0;
            if (buffer.Length == 0
                || (http.Response.StatusCode != 200 && http.Response.StatusCode != 206))
            {
                _logger.LogDebug(
                    "[NativeTrickplay] injector: pass-through for {ItemId} (status={Status} bodyLen={Bytes})",
                    itemId, http.Response.StatusCode, buffer.Length);
                buffer.Position = 0;
                await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            var bodyText = Encoding.UTF8.GetString(buffer.ToArray());
            if (!bodyText.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "[NativeTrickplay] injector: pass-through for {ItemId} (body not an HLS playlist)",
                    itemId);
                buffer.Position = 0;
                await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            string transformed;
            bool isMasterContent = bodyText.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
            bool isMediaContent = bodyText.Contains("#EXTINF", StringComparison.Ordinal);

            if (isMaster && isMasterContent)
            {
                if (bodyText.Contains(IframeMarker, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "[NativeTrickplay] injector: master.m3u8 for {ItemId} already has I-FRAME-STREAM-INF — pass-through",
                        itemId);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                    // Still record the session so a follow-up main.m3u8 fetch passes through.
                    if (!string.IsNullOrEmpty(playSessionId))
                        MasterSeenSessions[playSessionId] = DateTime.UtcNow;
                    return;
                }
                if (!TryAppendIframeVariant(bodyText, itemId, http.Request.QueryString.Value ?? string.Empty, out transformed))
                {
                    _logger.LogWarning(
                        "[NativeTrickplay] injector: cannot append I-FRAME variant for {ItemId} (item or media stream missing) — pass-through",
                        itemId);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                    return;
                }
                if (!string.IsNullOrEmpty(playSessionId))
                    MasterSeenSessions[playSessionId] = DateTime.UtcNow;
                _logger.LogInformation(
                    "[NativeTrickplay] injector: appended I-FRAME variant to master.m3u8 for {ItemId} session={Session} ({Bytes}B → {NewBytes}B)",
                    itemId, playSessionId ?? "(none)", bodyText.Length, transformed.Length);
            }
            else if (wrapMain && isMediaContent)
            {
                if (!TryWrapAsMaster(itemId, http.Request, out transformed))
                {
                    _logger.LogInformation(
                        "[NativeTrickplay] injector: NOT wrapping main.m3u8 for {ItemId} (HDR pass-through, no metadata)",
                        itemId);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                    return;
                }
                _logger.LogInformation(
                    "[NativeTrickplay] injector: wrapped main.m3u8 as synthetic master for {ItemId} session={Session} ({Bytes}B → {NewBytes}B)",
                    itemId, playSessionId ?? "(none)", bodyText.Length, transformed.Length);
            }
            else
            {
                _logger.LogDebug(
                    "[NativeTrickplay] injector: pass-through for {ItemId} (path={Path}, has-stream-inf={HasStreamInf}, has-extinf={HasExtinf})",
                    itemId, path, isMasterContent, isMediaContent);
                buffer.Position = 0;
                await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(transformed);
            http.Response.ContentLength = bytes.Length;
            http.Response.ContentType = "application/vnd.apple.mpegurl";
            await origBody.WriteAsync(bytes, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NativeTrickplay] injector failed for {Path}, passing through", path);
            try
            {
                http.Response.Body = origBody;
                buffer.Position = 0;
                await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
        finally
        {
            http.Response.Body = origBody;
        }
    }

    /// <summary>
    /// Drop session entries older than <see cref="MasterSeenTtl"/>. Called
    /// at most once per <see cref="SweepInterval"/> to keep the dict
    /// bounded without spawning a dedicated timer.
    /// </summary>
    private static void SweepStaleSessionsIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweepUtc < SweepInterval) return;
        _lastSweepUtc = now;
        var cutoff = now - MasterSeenTtl;
        foreach (var kv in MasterSeenSessions)
        {
            if (kv.Value < cutoff) MasterSeenSessions.TryRemove(kv.Key, out _);
        }
    }

    private static bool TryExtractItemId(string path, out Guid itemId)
    {
        var span = path.AsSpan("/Videos/".Length);
        var slash = span.IndexOf('/');
        if (slash <= 0) { itemId = default; return false; }
        return Guid.TryParse(span[..slash], out itemId);
    }

    private bool TryAppendIframeVariant(string master, Guid itemId, string queryString, out string output)
    {
        output = string.Empty;
        var item = _libraryManager.GetItemById(itemId);
        if (item is null) return false;

        var variant = _cache.IframeFormatFor(item);
        // Look up source dimensions so the I-FRAME-STREAM-INF can declare an
        // accurate RESOLUTION matching what the encoder actually outputs.
        var (w, h) = GetIframeOutputDimensions(item);
        var iframeLine = BuildIframeStreamInfLine(itemId, queryString, variant, w, h);

        var sb = new StringBuilder(master.Length + iframeLine.Length + 2);
        sb.Append(master);
        if (master.Length > 0 && master[^1] != '\n') sb.Append('\n');
        sb.AppendLine(iframeLine);
        output = sb.ToString();
        return true;
    }

    private (int Width, int Height) GetIframeOutputDimensions(BaseItem item)
    {
        var sources = item.GetMediaSources(false);
        var video = sources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var cfgHeight = Plugin.Instance?.Configuration.IframeWidth ?? 320;
        if (video?.Width is not > 0 || video.Height is not > 0)
        {
            // Source dimensions unknown — emit a conservative 16:9 fallback.
            return (RoundEven((cfgHeight * 16) / 9), cfgHeight);
        }
        var aspect = (double)video.Width.Value / video.Height.Value;
        var outW = RoundEven((int)Math.Round(cfgHeight * aspect));
        return (outW, cfgHeight);
    }

    private static int RoundEven(int n) => (n / 2) * 2;

    private static string BuildIframeStreamInfLine(
        Guid itemId, string queryString, IframeVariant variant, int width, int height)
    {
        // Per Apple HLS Authoring Spec §6.16 the I-frame variant uses an SDR
        // H.264 codec string regardless of the primary's range. CodecString and
        // VideoRangeAttribute return the same values whether the source is SDR
        // or HDR (always avc1.4d0028 / SDR) — see IframeFormat.cs for why.
        var codec = IframeFormat.CodecString(variant);
        var range = IframeFormat.VideoRangeAttribute(variant);
        return string.Create(CultureInfo.InvariantCulture,
            $"#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH={IframeBandwidth},AVERAGE-BANDWIDTH={IframeBandwidth},VIDEO-RANGE={range},CODECS=\"{codec}\",RESOLUTION={width}x{height},URI=\"/Videos/{itemId:N}/iframe.m3u8{queryString}\"");
    }

    /// <summary>
    /// Construct a synthetic multivariant playlist that wraps the original
    /// media playlist as the primary variant + appends our I-frame variant.
    /// Returns false when we cannot derive accurate STREAM-INF attributes
    /// from the item's MediaStream metadata — caller passes through unchanged
    /// in that case (HDR-safety: we never emit an inaccurate STREAM-INF for
    /// HDR content, which would cause AVPlayer to refuse playback due to
    /// codec mismatch).
    /// </summary>
    private bool TryWrapAsMaster(Guid itemId, HttpRequest req, out string output)
    {
        output = string.Empty;

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            _logger.LogDebug("[NativeTrickplay] TryWrapAsMaster: item {ItemId} not in library", itemId);
            return false;
        }

        var msId = req.Query["MediaSourceId"].FirstOrDefault();
        var sources = item.GetMediaSources(false);
        var ms = (string.IsNullOrEmpty(msId)
                ? sources?.FirstOrDefault()
                : sources?.FirstOrDefault(s => string.Equals(s.Id, msId, StringComparison.OrdinalIgnoreCase)))
            ?? sources?.FirstOrDefault();

        var streams = ms?.MediaStreams;
        var video = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

        if (video is null)
        {
            _logger.LogDebug("[NativeTrickplay] TryWrapAsMaster: no video stream for {ItemId}", itemId);
            return false;
        }

        // HDR/DV main.m3u8 fetches: pass through unchanged. HDR-aware clients
        // build their own client-side synthetic master; layering ours on top
        // of theirs causes AVPlayer to parse two synthetic masters and stall.
        if (video.VideoRangeType is not (VideoRangeType.SDR or VideoRangeType.Unknown))
        {
            _logger.LogDebug(
                "[NativeTrickplay] TryWrapAsMaster: skipping HDR/DV item {ItemId} (range={Range}) — client is expected to wrap",
                itemId, video.VideoRangeType);
            return false;
        }

        var ci = CultureInfo.InvariantCulture;
        var iframeVariant = IframeFormat.FromVideoRange(video.VideoRangeType);
        var (iframeW, iframeH) = GetIframeOutputDimensions(item);

        var origQs = req.QueryString.Value ?? string.Empty;
        var innerQs = AppendQueryParam(origQs, SkipMarker, "1");

        long bandwidth = ms?.Bitrate ?? video.BitRate ?? 8_000_000;
        // CRITICAL: codec strings must reflect what main.m3u8 actually serves,
        // NOT the source codec. Jellyfin transcodes DTS → AC-3, sometimes
        // converts h264 profile, etc. The URL's AudioCodec / VideoCodec query
        // params carry the negotiated target codec — that's what AVPlayer
        // will see in the segments, so that's what goes in STREAM-INF.
        string videoCodec = BuildVideoCodecString(video, req.Query);
        string audioCodec = BuildAudioCodecStringFromUrl(req.Query, audio);
        string? videoRange = MapVideoRange(video.VideoRangeType);
        int? width = video.Width;
        int? height = video.Height;
        double? fps = video.RealFrameRate ?? video.AverageFrameRate;

        // Match Jellyfin's own master.m3u8 format exactly: plain STREAM-INF with
        // audio codec listed in CODECS. Do NOT emit EXT-X-MEDIA:TYPE=AUDIO with
        // no URI + AUDIO="..." reference on the variant — AVPlayer treats this
        // as "audio rendition is missing", marks the variant unusable, and
        // falls back to the I-FRAME variant for primary playback (slideshow
        // effect) or crashes outright for HEVC content.
        var sb = new StringBuilder(640);
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:6");

        sb.Append("#EXT-X-STREAM-INF:");
        sb.Append(ci, $"BANDWIDTH={bandwidth}");
        sb.Append(ci, $",AVERAGE-BANDWIDTH={bandwidth}");
        if (!string.IsNullOrEmpty(videoRange))
        {
            sb.Append(ci, $",VIDEO-RANGE={videoRange}");
        }
        sb.Append(ci, $",CODECS=\"{videoCodec},{audioCodec}\"");
        if (width is > 0 && height is > 0)
        {
            sb.Append(ci, $",RESOLUTION={width}x{height}");
        }
        if (fps is > 0)
        {
            sb.Append(ci, $",FRAME-RATE={fps.Value:F3}");
        }
        sb.AppendLine();
        sb.Append("main.m3u8").Append(innerQs).AppendLine();

        var iframeLine = BuildIframeStreamInfLine(itemId, origQs, iframeVariant, iframeW, iframeH);
        sb.AppendLine(iframeLine);

        output = sb.ToString();
        _logger.LogDebug(
            "[NativeTrickplay] TryWrapAsMaster: built synthetic master for {ItemId} (primary CODECS=\"{VideoCodec},{AudioCodec}\", I-frame line: {IframeLine})",
            itemId, videoCodec, audioCodec, iframeLine);
        return true;
    }

    private static string AppendQueryParam(string queryString, string key, string value)
    {
        if (string.IsNullOrEmpty(queryString)) return "?" + key + "=" + Uri.EscapeDataString(value);
        var sep = queryString.EndsWith('&') || queryString == "?" ? string.Empty : "&";
        return queryString + sep + key + "=" + Uri.EscapeDataString(value);
    }

    private static string BuildVideoCodecString(MediaStream video, IQueryCollection query)
    {
        var ci = CultureInfo.InvariantCulture;

        // Pick the codec from the URL (target after Jellyfin negotiation), not the
        // source. Jellyfin sends VideoCodec=hevc,h264 as a preference list; the
        // actual chosen codec is the one that has matching <codec>-level hints
        // in the URL.
        var urlList = (query["VideoCodec"].FirstOrDefault() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        string? targetCodec = null;
        foreach (var c in urlList)
        {
            var name = c.Trim().ToLowerInvariant();
            if (query.ContainsKey(name + "-level")) { targetCodec = name; break; }
        }
        // Fallbacks: first codec in list, then source MediaStream's codec.
        targetCodec ??= urlList.FirstOrDefault()?.Trim().ToLowerInvariant();
        targetCodec ??= (video.Codec ?? string.Empty).ToLowerInvariant();

        var codec = targetCodec;
        // Pull profile/level/bit-depth either from URL hints (target) or stream (source).
        int level = TryParseInt(query[$"{codec}-level"].FirstOrDefault(), 0);
        if (level == 0) level = (int)Math.Round(video.Level ?? 0.0);
        var profile = (query[$"{codec}-profile"].FirstOrDefault() ?? video.Profile ?? string.Empty).ToLowerInvariant();
        var bitDepthStr = query[$"{codec}-videobitdepth"].FirstOrDefault();
        var bitDepth = int.TryParse(bitDepthStr, out var bd) ? bd : (video.BitDepth ?? 8);

        if (codec is "hevc" or "h265")
        {
            // hvc1.<profile_idc>.<profile_compat_flags>.L<level>.<constraint_flags>
            // Main 8-bit  → profile_idc=1, compat=6
            // Main10 10-bit → profile_idc=2, compat=4
            int profileIdc = bitDepth >= 10 ? 2 : 1;
            int compat = profileIdc == 2 ? 4 : 6;
            int lvl = level > 0 ? level : 120;   // 4.0 fallback
            return string.Create(ci, $"hvc1.{profileIdc}.{compat:X}.L{lvl}.B0");
        }
        if (codec is "av1")
        {
            return bitDepth >= 10 ? "av01.0.05M.10" : "av01.0.05M.08";
        }

        // h264 / avc / fallback. avc1.PPCCLL hex.
        int profileIdc264 = profile switch
        {
            "baseline" or "constrained baseline" => 0x42,
            "main" => 0x4D,
            "high10" => 0x6E,
            "high422" or "high 4:2:2" => 0x7A,
            _ => 0x64 // high
        };
        int compat264 = profileIdc264 == 0x42 ? 0xE0 : 0x00;
        int level264 = level > 0 ? level : 40; // 4.0
        return string.Create(ci, $"avc1.{profileIdc264:X2}{compat264:X2}{level264:X2}");
    }

    private static string BuildAudioCodecStringFromUrl(IQueryCollection query, MediaStream? sourceAudio)
    {
        // Prefer the negotiated target codec from the URL. Jellyfin transcodes
        // unsupported audio (DTS, TrueHD raw, etc.) to AC-3/AAC for AVPlayer
        // — declaring the source codec in STREAM-INF makes AVPlayer reject the
        // variant. The URL's AudioCodec param is the actual delivered codec.
        var urlAudio = (query["AudioCodec"].FirstOrDefault() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim().ToLowerInvariant();

        var codec = string.IsNullOrEmpty(urlAudio)
            ? (sourceAudio?.Codec ?? string.Empty).ToLowerInvariant()
            : urlAudio;

        return codec switch
        {
            "eac3" => "ec-3",
            "ac3" => "ac-3",
            "flac" => "fLaC",
            "alac" => "alac",
            "opus" => "opus",
            // Any other (dts, truehd, mlpa, vorbis, etc.) — AVPlayer can't decode
            // these natively, so Jellyfin transcodes. If we end up here without an
            // explicit URL hint, declare AAC as a safe lowest-common-denominator;
            // even if the actual segment audio is AC-3/E-AC-3, AVPlayer fetches
            // the variant and re-probes the bitstream.
            _ => "mp4a.40.2"
        };
    }

    private static int TryParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string? MapVideoRange(VideoRangeType type) => type switch
    {
        VideoRangeType.SDR => "SDR",
        VideoRangeType.HLG or VideoRangeType.DOVIWithHLG => "HLG",
        VideoRangeType.HDR10 or VideoRangeType.HDR10Plus
            or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL => "PQ",
        _ => null
    };
}
