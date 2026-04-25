using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Buffers responses from /Videos/{id}/master.m3u8 and /Videos/{id}/main.m3u8.
/// - master playlist: appends an EXT-X-I-FRAME-STREAM-INF line.
/// - main (media) playlist: replaces the body with a synthetic master that
///   references the original media playlist (with skipIframeInjection=1 to avoid
///   recursion) plus our I-frame variant.
/// </summary>
public sealed class MasterPlaylistInjector : IAsyncResultFilter
{
    private const string SkipMarker = "skipIframeInjection";
    private const string IframeMarker = "#EXT-X-I-FRAME-STREAM-INF";
    private const string IframeVariantCodec = "avc1.4D401E";
    private const long IframeBandwidth = 200_000;

    private readonly ILogger<MasterPlaylistInjector> _logger;

    public MasterPlaylistInjector(ILogger<MasterPlaylistInjector> logger)
    {
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
            await next().ConfigureAwait(false);
            return;
        }

        var origBody = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);

            // Restore body before any further writes
            http.Response.Body = origBody;

            buffer.Position = 0;
            if (buffer.Length == 0
                || (http.Response.StatusCode != 200 && http.Response.StatusCode != 206))
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            var bodyText = Encoding.UTF8.GetString(buffer.ToArray());
            if (!bodyText.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
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
                    buffer.Position = 0;
                    await buffer.CopyToAsync(origBody, http.RequestAborted).ConfigureAwait(false);
                    return;
                }
                transformed = AppendIframeVariant(bodyText, itemId, http.Request.QueryString.Value ?? string.Empty);
            }
            else if (isMediaContent && isMain)
            {
                transformed = WrapAsMaster(itemId, http.Request);
            }
            else
            {
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

    private static string AppendIframeVariant(string master, Guid itemId, string queryString)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH={IframeBandwidth},CODECS=\"{IframeVariantCodec}\",URI=\"/Videos/{itemId:N}/iframe.m3u8{queryString}\"");
        var sb = new StringBuilder(master.Length + line.Length + 2);
        sb.Append(master);
        if (master.Length > 0 && master[^1] != '\n') sb.Append('\n');
        sb.AppendLine(line);
        return sb.ToString();
    }

    private static string WrapAsMaster(Guid itemId, HttpRequest req)
    {
        var query = req.Query;
        long bandwidth = TryParseFirstLong(query["VideoBitrate"], 8_000_000);
        string videoCodec = MapVideoCodec(query["VideoCodec"], query);
        string audioCodec = MapAudioCodec(query["AudioCodec"]);

        var origQs = req.QueryString.Value ?? string.Empty;
        var innerQs = QueryHelpers.AddQueryString(string.Empty + origQs, SkipMarker, "1");
        // QueryHelpers.AddQueryString returns a string starting with "?" if path was empty.
        if (innerQs.StartsWith('?')) innerQs = innerQs[..]; // already has '?'
        else innerQs = "?" + innerQs.TrimStart('&');

        var sb = new StringBuilder(512);
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:6");
        sb.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},CODECS=\"{videoCodec},{audioCodec}\"")
          .AppendLine();
        sb.Append("main.m3u8").Append(innerQs).AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH={IframeBandwidth},CODECS=\"{IframeVariantCodec}\",URI=\"/Videos/{itemId:N}/iframe.m3u8{origQs}\"")
          .AppendLine();
        return sb.ToString();
    }

    private static long TryParseFirstLong(Microsoft.Extensions.Primitives.StringValues v, long fallback)
    {
        var s = v.FirstOrDefault();
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static string MapVideoCodec(Microsoft.Extensions.Primitives.StringValues v, IQueryCollection query)
    {
        // Jellyfin's URL carries codec-specific hints — `h264-level`, `h264-profile`, `hevc-level`,
        // `av1-*` — only when that codec is the actual transcode/copy target. Treat presence of
        // these hints as authoritative; the VideoCodec list is just preference order.
        string? actual = null;
        foreach (var c in (v.FirstOrDefault() ?? string.Empty).Split(','))
        {
            var name = c.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) continue;
            if (query.ContainsKey(name + "-level"))
            {
                actual = name;
                break;
            }
        }
        actual ??= (v.FirstOrDefault() ?? string.Empty).Split(',').FirstOrDefault()?.Trim().ToLowerInvariant();

        return actual switch
        {
            "hevc" or "h265" => "hvc1.1.6.L153.B0",
            "av1" => "av01.0.05M.10",
            _ => BuildAvc1String(query)
        };
    }

    private static string BuildAvc1String(IQueryCollection query)
    {
        // avc1.PPCCLL — profile_idc | profile_compat | level_idc, all hex.
        int level = TryParseInt(query["h264-level"].FirstOrDefault(), 40);
        var profile = (query["h264-profile"].FirstOrDefault() ?? "high").ToLowerInvariant();
        int profileIdc = profile switch
        {
            "baseline" => 0x42,
            "main" => 0x4D,
            "high10" => 0x6E,
            "high422" => 0x7A,
            _ => 0x64 // high
        };
        int compat = profile == "baseline" ? 0xE0 : 0x00;
        return string.Create(CultureInfo.InvariantCulture, $"avc1.{profileIdc:X2}{compat:X2}{level:X2}");
    }

    private static int TryParseInt(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string MapAudioCodec(Microsoft.Extensions.Primitives.StringValues v)
    {
        var first = (v.FirstOrDefault() ?? string.Empty).Split(',').FirstOrDefault()?.Trim().ToLowerInvariant();
        return first switch
        {
            "eac3" => "ec-3",
            "ac3" => "ac-3",
            "flac" => "fLaC",
            "alac" => "alac",
            "opus" => "opus",
            _ => "mp4a.40.2"
        };
    }
}
