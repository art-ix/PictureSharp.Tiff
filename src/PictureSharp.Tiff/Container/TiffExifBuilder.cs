using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Builds the standalone EXIF block (a classic TIFF stream) that <see cref="ImageMetadata.Exif"/> holds: IFD0 carries
/// the descriptive tags and the orientation of the image IFD, linked to copies of the EXIF, GPS and interoperability
/// IFDs. Values keep the file's byte order and are copied as stored; BigTIFF-only types are dropped.
/// </summary>
internal static class TiffExifBuilder
{
    private const ushort ExifIfd = 34665;
    private const ushort GpsIfd = 34853;
    private const ushort InteropIfd = 40965;

    // ImageDescription, Make, Model, Orientation, Software, DateTime, Artist, Copyright.
    private static readonly ushort[] Ifd0Tags = [270, 271, 272, TiffTag.Orientation, 305, 306, 315, 33432];

    public static byte[]? Build(ReadOnlySpan<byte> data, TiffDirectory image, ImageDecodeContext context)
    {
        TiffDirectory? exif = image.TryParseSubdirectory(data, ExifIfd);
        TiffDirectory? gps = image.TryParseSubdirectory(data, GpsIfd);
        TiffDirectory? interop = exif?.TryParseSubdirectory(data, InteropIfd);
        List<TiffEntry> ifd0 = [.. Copyable(image).Where(e => Array.IndexOf(Ifd0Tags, e.Tag) >= 0)];
        bool onlyDefaultOrientation = ifd0 is [{ Tag: TiffTag.Orientation } o] && image.GetUInt(data, o, 0) == 1;
        if (exif is null && gps is null && (ifd0.Count == 0 || onlyDefaultOrientation))
        {
            return null;
        }

        var root = new Node(ifd0);
        var exifNode = exif is null ? null : new Node(Copyable(exif));
        var gpsNode = gps is null ? null : new Node(Copyable(gps));
        var interopNode = interop is null ? null : new Node(Copyable(interop));
        root.Link(ExifIfd, exifNode);
        root.Link(GpsIfd, gpsNode);
        exifNode?.Link(InteropIfd, interopNode);

        Node[] nodes = [.. new[] { root, exifNode, interopNode, gpsNode }.OfType<Node>()];
        long size = 8;
        foreach (Node node in nodes)
        {
            node.Offset = size;
            size += node.Size(image.Header.BigTiff);
        }

        context.ReserveMetadata(size);
        var block = new byte[size];
        bool bigEndian = image.BigEndian;
        block[0] = block[1] = (byte)(bigEndian ? 'M' : 'I');
        Write16(block.AsSpan(2), 42, bigEndian);
        Write32(block.AsSpan(4), 8, bigEndian);
        foreach (Node node in nodes)
        {
            node.Write(data, block, bigEndian);
        }

        return block;
    }

    // Pointer tags are relinked by the builder; values of the BigTIFF-only types have no classic encoding.
    private static List<TiffEntry> Copyable(TiffDirectory ifd)
    {
        var copied = new List<TiffEntry>();
        foreach (TiffEntry e in ifd.Entries)
        {
            if (e.Tag is not (ExifIfd or GpsIfd or InteropIfd) && e.Type is not (TiffEntry.Ifd or TiffEntry.Long8 or TiffEntry.SLong8 or TiffEntry.Ifd8))
            {
                copied.Add(e);
            }
        }

        return copied;
    }

    private static void Write16(Span<byte> span, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }
    }

    private static void Write32(Span<byte> span, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
    }

    /// <summary>One output IFD: copied entries plus links to child IFDs, sorted by tag as EXIF readers expect.</summary>
    private sealed class Node(List<TiffEntry> entries)
    {
        private readonly List<(ushort Tag, Node Child)> links = [];

        public long Offset { get; set; }

        private int Count => entries.Count + links.Count;

        public void Link(ushort tag, Node? child)
        {
            if (child is not null)
            {
                links.Add((tag, child));
            }
        }

        public long Size(bool bigTiff)
        {
            long size = 2 + 12L * Count + 4;
            foreach (TiffEntry e in entries)
            {
                long length = (long)e.Count * e.Size(bigTiff);
                size += length > 4 ? length + (length & 1) : 0;
            }

            return size;
        }

        public void Write(ReadOnlySpan<byte> data, Span<byte> block, bool bigEndian)
        {
            var rows = new List<(ushort Tag, int Index)>(Count);
            rows.AddRange(entries.Select((e, i) => (e.Tag, i)));
            rows.AddRange(links.Select((l, i) => (l.Tag, -1 - i)));
            rows.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            int pos = (int)Offset;
            long values = Offset + 2 + 12L * Count + 4;
            Write16(block[pos..], (ushort)Count, bigEndian);
            pos += 2;
            foreach ((ushort tag, int index) in rows)
            {
                Write16(block[pos..], tag, bigEndian);
                if (index < 0)
                {
                    Write16(block[(pos + 2)..], TiffEntry.Long, bigEndian);
                    Write32(block[(pos + 4)..], 1, bigEndian);
                    Write32(block[(pos + 8)..], (uint)links[-1 - index].Child.Offset, bigEndian);
                }
                else
                {
                    TiffEntry e = entries[index];
                    int length = (int)e.Count * e.Size(bigTiff: true);
                    Write16(block[(pos + 2)..], e.Type, bigEndian);
                    Write32(block[(pos + 4)..], e.Count, bigEndian);
                    ReadOnlySpan<byte> value = data.Slice(e.ValueOffset, length);
                    if (length <= 4)
                    {
                        value.CopyTo(block[(pos + 8)..]);
                    }
                    else
                    {
                        Write32(block[(pos + 8)..], (uint)values, bigEndian);
                        value.CopyTo(block[(int)values..]);
                        values += length + (length & 1);
                    }
                }

                pos += 12;
            }

            // The next-IFD link stays 0: the block holds no thumbnail IFD.
        }
    }
}
