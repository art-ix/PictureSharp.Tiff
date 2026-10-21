namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// The header and the IFD chain of a classic TIFF file. The chain walk stops at an offset seen before, at an IFD
/// outside the file and after <see cref="MaxDirectories"/> IFDs, so loops and broken links end it instead of failing.
/// </summary>
internal sealed class TiffFile
{
    public const int MaxDirectories = 65536;

    private TiffFile(TiffDirectory image, int pages)
    {
        Image = image;
        Pages = pages;
    }

    /// <summary>The first full-resolution IFD, or the first IFD when every one is reduced-resolution.</summary>
    public TiffDirectory Image { get; }

    /// <summary>The number of full-resolution IFDs in the chain (at least 1); only counted when requested.</summary>
    public int Pages { get; }

    public static TiffFile Open(ReadOnlySpan<byte> data, bool countPages)
    {
        if (!TiffDecoder.HasSignature(data))
        {
            throw TiffDecoder.Error("Not a TIFF file.");
        }

        bool bigEndian = data[0] == 'M';
        if (data[2] == 43 || data[3] == 43)
        {
            throw new NotSupportedException("BigTIFF files are not supported yet by PictureSharp.Tiff.");
        }

        if (data.Length < 8)
        {
            throw TiffDecoder.Error("The TIFF header is truncated.");
        }

        uint first = TiffDirectory.U32(data, 4, bigEndian);
        var visited = new HashSet<uint>();
        uint selected = 0;
        bool found = false;
        int pages = 0;
        uint offset = first;
        while (offset != 0 && visited.Count < MaxDirectories && visited.Add(offset))
        {
            if (!TiffDirectory.TryScan(data, bigEndian, offset, out bool reduced, out uint next))
            {
                if (offset == first)
                {
                    throw TiffDecoder.Error($"The first TIFF directory at offset {offset} lies outside the file.");
                }

                break;
            }

            if (!reduced)
            {
                pages++;
                if (!found)
                {
                    selected = offset;
                    found = true;
                    if (!countPages)
                    {
                        break;
                    }
                }
            }

            offset = next;
        }

        if (visited.Count == 0)
        {
            throw TiffDecoder.Error("The TIFF file has no image directory.");
        }

        return new TiffFile(TiffDirectory.Parse(data, bigEndian, found ? selected : first), Math.Max(1, pages));
    }
}
