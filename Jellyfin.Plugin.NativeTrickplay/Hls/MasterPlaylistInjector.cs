using System;
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
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Buffers responses from /Videos/{id}/master.m3u8 and /Videos/{id}/main.m3u8.
/// - master playlist: appends an EXT-X-I-FRAME-STREAM-INF line.
/// - main (media) playlist: replaces the body with a synthetic master that
///   references the original media playlist (with skipIframeInjection=1 to
///   avoid recursion) plus our I-frame variant. CRITICAL: the synthetic
///   STREAM-INF must include accurate CODECS, RESOLUTION, FRAME-RATE, and
///   VIDEO-RANGE derived from the actual MediaStream — otherwise AVPlayer
///   refuses to play HDR content because the declared codec capability
///   doesn't match the real bitstream.
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

        _logger.LogDebug(
            "[NativeTrickplay] injector intercepting {Path}{Query} (item={ItemId})",
            path, http.Request.QueryString.Value, itemId);

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

            if (isMasterContent)
            {
                if (bodyText.Contains(IframeMarker, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "[NativeTrickplay] injector: master.m3u8 for {ItemId} already has I-FRAME-STREAM-INF — pass-through",
                        itemId);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
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
                _logger.LogInformation(
                    "[NativeTrickplay] injector: appended I-FRAME variant to master.m3u8 for {ItemId} ({Bytes}B → {NewBytes}B)",
                    itemId, bodyText.Length, transformed.Length);
            }
            else if (isMediaContent && isMain)
            {
                if (!TryWrapAsMaster(itemId, http.Request, out transformed))
                {
                    _logger.LogInformation(
                        "[NativeTrickplay] injector: NOT wrapping main.m3u8 for {ItemId} (HDR pass-through, no metadata, or already-handled client)",
                        itemId);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                    return;
                }
                _logger.LogInformation(
                    "[NativeTrickplay] injector: wrapped main.m3u8 as synthetic master for {ItemId} ({Bytes}B → {NewBytes}B)",
                    itemId, bodyText.Length, transformed.Length);
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
    /// Attempts to construct a synthetic multivariant playlist that wraps a media
    /// playlist + our I-frame variant. Returns false if we cannot derive accurate
    /// STREAM-INF attributes from the item's MediaStream metadata — in which case
    /// the caller passes the original media playlist through unchanged. This is the
    /// HDR-safety guarantee: we never emit an inaccurate STREAM-INF for HDR content
    /// (which would cause AVPlayer to refuse playback due to codec mismatch).
    /// </summary>
    private bool TryWrapAsMaster(Guid itemId, HttpRequest req, out string output)
    {
        output = string.Empty;

        // Browser-based clients (Jellyfin Web UI via HLS.js, embedded webviews
        // on mobile apps) request /main.m3u8 expecting a *media* playlist
        // with #EXTINF segment lines. Wrapping it as a synthetic master
        // playlist breaks HLS.js's MediaSource Extensions setup — the
        // player sees STREAM-INF instead of EXTINF, has to chase the inner
        // variant URL, and ends up with broken codec negotiation / stalled
        // playback. Browsers don't use HLS I-frame trickplay variants
        // anyway (they have their own scrubbing UI), so passing through
        // unchanged is both correct and lossless.
        // The wrap is for Apple TV / AVPlayer-based clients (UA contains
        // "AppleCoreMedia" or similar) which fetch /main.m3u8 directly and
        // need the I-frame variant discoverable for native trickplay.
        var ua = req.Headers.UserAgent.ToString();
        if (!string.IsNullOrEmpty(ua) && ua.Contains("Mozilla", StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "[NativeTrickplay] TryWrapAsMaster: skipping browser client for {ItemId} (UA contains Mozilla — web UI / HLS.js doesn't need synthetic master)",
                itemId);
            return false;
        }

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
        // (e.g. JellySeerTV's HDR-Filter) build their own synthetic master
        // client-side that points at /main.m3u8 as its variant. If we wrap
        // here, AVPlayer ends up parsing two layered synthetic masters and
        // stalls waiting for a media playlist that's actually a master. Server-
        // side trickplay for HDR content has to come through the master.m3u8
        // path (where we just append #EXT-X-I-FRAME-STREAM-INF without
        // restructuring), not through main.m3u8 wrapping.
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
