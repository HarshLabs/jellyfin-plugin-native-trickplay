using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Per Apple's HLS Authoring Specification §6.16 ("For backward compatibility,
/// SDR trick play streams MUST be provided"), AVPlayer's transport-bar trickplay
/// uses an SDR H.264 I-frame variant regardless of the primary stream's range.
/// We always emit a single SDR variant — there is no HDR I-frame path. Apple's
/// own example multivariant playlists pair HDR primaries with SDR-CODECS I-frame
/// variants, and AVPlayer silently rejects HDR-only I-frame variants on real
/// devices (playlist downloads but segments never fetched).
/// </summary>
internal enum IframeVariant
{
    Sdr,
}

internal static class IframeFormat
{
    public static IframeVariant FromVideoRange(VideoRangeType type) => IframeVariant.Sdr;

    /// <summary>
    /// CODECS attribute value for EXT-X-I-FRAME-STREAM-INF.
    ///
    /// Format string per RFC 6381 + Apple HLS spec example: avc1.&lt;profile_idc&gt;
    /// &lt;constraint_set_flags&gt;&lt;level_idc&gt; in lowercase hex. Matches the
    /// `-profile:v main -level:v 4.0` encoder pin in IframeAssetCache so AVPlayer's
    /// declared-vs-actual codec validation passes (a level mismatch makes AVPlayer
    /// silently bypass the variant).
    ///
    ///   avc1.4d0028 = H.264 Main profile (0x4d=77), no constraint flags (0x00),
    ///   Level 4.0 (0x28=40) — verbatim from Apple's spec table.
    /// </summary>
    public static string CodecString(IframeVariant v) => "avc1.4d0028";

    /// <summary>VIDEO-RANGE attribute on the I-frame variant. Always SDR per spec.</summary>
    public static string VideoRangeAttribute(IframeVariant v) => "SDR";

    /// <summary>
    /// Cache-stamp encoder identifier. Changing this invalidates all on-disk
    /// caches and forces re-encode with the current pipeline.
    /// </summary>
    public static string EncoderTag(IframeVariant v) => "h264-v6";
}
