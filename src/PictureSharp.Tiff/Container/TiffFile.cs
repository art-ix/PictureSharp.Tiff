namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// The header and the IFD chain of a classic TIFF or BigTIFF file. The chain walk stops at an offset seen before, at
/// an IFD outside the file and after <see cref="MaxDirectories"/> IFDs, so loops and broken links end it instead of
/// failing.
/// </summary>
internal sealed class TiffFile
{
    public const int MaxDirectories = 65536;

    private TiffFile(TiffDirectory image, int pages, long link)
    {
        Image = image;
        Pages = pages;
        Link = link;
    }

    /// <summary>
    /// The selected full-resolution IFD; for page 0, the first IFD when every one is reduced-resolution.
    /// </summary>
    public TiffDirectory Image { get; }

    /// <summary>The number of full-resolution IFDs in the chain (at least 1); only counted when requested.</summary>
    public int Pages { get; }

    /// <summary>
    /// The position of the offset that points to <see cref="Image"/>: in the header, or the next-IFD field of the
    /// previous IFD.
    /// </summary>
    public long Link { get; }

    /// <summary>Reads the chain up to <paramref name="page"/>, the index among full-resolution IFDs, or to its end.</summary>
    public static TiffFile Open(ReadOnlySpan<byte> data, int page, bool countPages)
    {
        TiffHeader header = TiffHeader.Read(data);
        var visited = new HashSet<ulong>();
        ulong selected = 0;
        long link = header.BigTiff ? 8 : 4, selectedLink = link;
        bool found = false;
        int pages = 0;
        ulong offset = header.FirstIfd;
        while (offset != 0 && visited.Count < MaxDirectories && visited.Add(offset))
        {
            if (!TiffDirectory.TryScan(data, header, offset, out bool reduced, out ulong next, out long nextLink))
            {
                if (offset == header.FirstIfd)
                {
                    throw TiffDecoder.Error($"The first TIFF directory at offset {offset} lies outside the file.");
                }

                break;
            }

            if (!reduced)
            {
                if (!found && pages == page)
                {
                    selected = offset;
                    selectedLink = link;
                    found = true;
                    if (!countPages)
                    {
                        break;
                    }
                }

                pages++;
            }

            offset = next;
            link = nextLink;
        }

        if (visited.Count == 0)
        {
            throw TiffDecoder.Error("The TIFF file has no image directory.");
        }

        if (!found && page > 0)
        {
            throw TiffDecoder.Error($"The TIFF file has {Math.Max(1, pages)} page(s); page {page} does not exist.");
        }

        return new TiffFile(TiffDirectory.Parse(data, header, found ? selected : header.FirstIfd), Math.Max(1, pages), selectedLink);
    }
}
