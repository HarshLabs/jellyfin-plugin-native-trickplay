using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.NativeTrickplay.Hls;

internal readonly record struct FragmentRange(long Offset, long Size);

internal static class Mp4BoxScanner
{
    /// <summary>
    /// Scans a fragmented MP4 at the top-box level. Returns the size of the init segment
    /// (everything before the first moof) and the byte ranges of each (moof+mdat) fragment.
    /// </summary>
    public static (long InitSize, IReadOnlyList<FragmentRange> Fragments) Scan(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: false);
        var fragments = new List<FragmentRange>();
        var fileLen = fs.Length;
        long pos = 0;
        long initSize = -1;
        long pendingMoofOffset = -1;
        Span<byte> hdr = stackalloc byte[16];

        while (pos < fileLen)
        {
            fs.Position = pos;
            int read = fs.Read(hdr[..8]);
            if (read < 8) break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            string type = Encoding.ASCII.GetString(hdr.Slice(4, 4));
            long headerSize = 8;
            long boxSize;
            if (size32 == 1)
            {
                if (fs.Read(hdr.Slice(8, 8)) < 8) break;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.Slice(8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = fileLen - pos;
            }
            else
            {
                boxSize = size32;
            }

            if (boxSize < headerSize) break; // corrupt; bail

            if (type == "moof")
            {
                if (initSize < 0) initSize = pos;
                pendingMoofOffset = pos;
            }
            else if (type == "mdat" && pendingMoofOffset >= 0)
            {
                long fragStart = pendingMoofOffset;
                long fragEnd = pos + boxSize;
                fragments.Add(new FragmentRange(fragStart, fragEnd - fragStart));
                pendingMoofOffset = -1;
            }

            pos += boxSize;
        }

        if (initSize < 0) initSize = fileLen; // no fragments
        return (initSize, fragments);
    }
}
