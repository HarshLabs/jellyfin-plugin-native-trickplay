using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

/// <summary>
/// Per-source decision: what kind of I-frame asset do we generate, and what
/// codec/VIDEO-RANGE does AVPlayer need to see in the master playlist?
///
/// AVPlayer rejects a master if the I-frame variant's VIDEO-RANGE family
/// doesn't match the primary's. SDR primaries get an SDR H.264 I-frame;
/// HDR/DV primaries get HEVC Main10 with PQ or HLG signaling so the I-frame
/// variant matches.
/// </summary>
internal enum IframeVariant
{
    Sdr,
    HdrPq,   // HDR10, HDR10+, all DV profiles whose base is PQ-encoded
    HdrHlg,  // HLG and DV-on-HLG
}

internal static class IframeFormat
{
    public static IframeVariant FromVideoRange(VideoRangeType type) => type switch
    {
        VideoRangeType.HLG or VideoRangeType.DOVIWithHLG => IframeVariant.HdrHlg,
        VideoRangeType.HDR10 or VideoRangeType.HDR10Plus
            or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL => IframeVariant.HdrPq,
        _ => IframeVariant.Sdr,
    };

    /// <summary>
    /// Codec string for the EXT-X-I-FRAME-STREAM-INF CODECS attribute.
    /// HEVC Main10 Level 5.0 covers 4K HDR; H.264 Main 3.0 covers SDR thumbnails.
    /// </summary>
    public static string CodecString(IframeVariant v) => v switch
    {
        IframeVariant.HdrPq or IframeVariant.HdrHlg => "hvc1.2.4.L150.B0",
        _ => "avc1.4D401E",
    };

    /// <summary>VIDEO-RANGE attribute value for the I-frame variant.</summary>
    public static string VideoRangeAttribute(IframeVariant v) => v switch
    {
        IframeVariant.HdrPq => "PQ",
        IframeVariant.HdrHlg => "HLG",
        _ => "SDR",
    };

    /// <summary>
    /// Short encoder identifier embedded in the on-disk cache stamp. When the
    /// expected variant for an item changes (e.g. user upgraded from a v1.0
    /// SDR-only release and the item is HDR), this mismatch invalidates the
    /// stale cache so the next request triggers a re-encode in the right format.
    /// </summary>
    public static string EncoderTag(IframeVariant v) => v switch
    {
        // v2 suffix = "encoded with -force_key_frames instead of x265-params keyint=1",
        // produces standard Main 10 instead of Main 10 Intra (Rext) profile —
        // bumped in v1.1.1 to invalidate v1.1.0's broken caches.
        IframeVariant.HdrPq => "hevc-pq-v2",
        IframeVariant.HdrHlg => "hevc-hlg-v2",
        _ => "h264",
    };
}
