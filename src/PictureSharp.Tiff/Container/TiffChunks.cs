namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// The offsets and byte counts of an IFD's strips or tiles, read as libtiff reads them: either tag of a pair
/// (StripOffsets and TileOffsets, StripByteCounts and TileByteCounts), any integer type, and byte counts that are
/// missing or implausible replaced by an estimate.
/// </summary>
// port: libtiff/tif_dirread.c TIFFReadDirectory (one field for both tags of a pair, the StripByteCounts checks),
// TIFFFetchStripThing, TIFFReadDirEntryLong8ArrayWithLimit, ByteCountLooksBad and EstimateStripByteCounts.
internal sealed class TiffChunks
{
    private readonly TiffDirectory ifd;
    private readonly TiffEntry offsets;
    private readonly TiffEntry counts;
    private readonly Estimate estimate;

    private TiffChunks(TiffDirectory ifd, TiffEntry offsets, TiffEntry counts, Estimate estimate)
    {
        this.ifd = ifd;
        this.offsets = offsets;
        this.counts = counts;
        this.estimate = estimate;
    }

    private enum Estimate
    {
        /// <summary>The stored byte counts.</summary>
        None,

        /// <summary>Uncompressed data: each chunk holds all its bytes (libtiff computes them from the layout).</summary>
        Full,

        /// <summary>Compressed data in one chunk per plane: everything from the offset to the end of the file.</summary>
        RestOfFile,
    }

    public static TiffChunks Read(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout)
    {
        long chunks = layout.ChunksPerPlane * layout.Planes;
        string kind = layout.Tiled ? "tile" : "strip";
        if (!TryGetArray(ifd, layout.Tiled ? TiffTag.TileOffsets : TiffTag.StripOffsets, layout.Tiled ? TiffTag.StripOffsets : TiffTag.TileOffsets, out TiffEntry offsets)
            || offsets.Count < chunks)
        {
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {kind} offsets.");
        }

        bool uncompressed = layout.Compression == TiffLayout.CompressionNone;
        bool hasCounts = TryGetArray(ifd, layout.Tiled ? TiffTag.TileByteCounts : TiffTag.StripByteCounts,
            layout.Tiled ? TiffTag.StripByteCounts : TiffTag.TileByteCounts, out TiffEntry counts);
        var table = new TiffChunks(ifd, offsets, counts, Estimate.None);
        Estimate estimate;
        if (!hasCounts)
        {
            // libtiff assumes one chunk per plane ("calculating from imagelength") and gives up otherwise; the
            // plugin also reads uncompressed chunks of any number, each assumed full.
            bool onePerPlane = layout.ChunksPerPlane == 1;
            estimate = uncompressed ? Estimate.Full : onePerPlane ? Estimate.RestOfFile
                : throw TiffDecoder.Error($"The TIFF image needs {chunks} {kind} byte counts.");
        }
        else if (counts.Count < chunks)
        {
            // libtiff pads the array with zeros, and every padded chunk fails to read.
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {kind} byte counts.");
        }
        else if (chunks == 1 && !layout.Tiled && table.LooksBad(data, layout))
        {
            // "Bogus StripByteCounts field, ignoring and calculating from imagelength."
            estimate = uncompressed ? Estimate.Full : Estimate.RestOfFile;
        }
        else if (!layout.Separate && chunks > 2 && uncompressed && table.Stored(data, 0) != table.Stored(data, 1)
            && table.Stored(data, 0) != 0 && table.Stored(data, 1) != 0)
        {
            // "Wrong StripByteCounts field, ignoring and calculating from imagelength": the first two chunks of
            // uncompressed data always have the same size.
            estimate = Estimate.Full;
        }
        else
        {
            estimate = Estimate.None;
        }

        return new TiffChunks(ifd, offsets, counts, estimate);
    }

    /// <summary>The offset of chunk <paramref name="index"/> (planes one after the other).</summary>
    public ulong Offset(ReadOnlySpan<byte> data, long index) => Value(data, offsets, (int)index);

    /// <summary>
    /// The byte count of chunk <paramref name="index"/>; <paramref name="full"/> is its uncompressed size, used when
    /// the counts are estimated.
    /// </summary>
    public ulong Count(ReadOnlySpan<byte> data, long index, int full) => estimate switch
    {
        Estimate.Full => (ulong)full,
        Estimate.RestOfFile => (ulong)data.Length - Math.Min((ulong)data.Length, Offset(data, index)),
        _ => Stored(data, index),
    };

    private ulong Stored(ReadOnlySpan<byte> data, long index) => Value(data, counts, (int)index);

    // ByteCountLooksBad: zero (with a non-zero offset), or, uncompressed, more than the file holds or less than the
    // image needs.
    private bool LooksBad(ReadOnlySpan<byte> data, TiffLayout layout)
    {
        ulong offset = Offset(data, 0), count = Stored(data, 0);
        if (offset == 0)
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        if (layout.Compression != TiffLayout.CompressionNone)
        {
            return false;
        }

        return (offset <= (ulong)data.Length && count > (ulong)data.Length - offset) || count < (ulong)layout.RowBytes * (ulong)layout.Height;
    }

    private static bool TryGetArray(TiffDirectory ifd, ushort tag, ushort other, out TiffEntry entry) =>
        (ifd.TryGet(tag, out entry) && IsArrayType(entry.Type)) || (ifd.TryGet(other, out entry) && IsArrayType(entry.Type));

    // TIFFReadDirEntryLong8ArrayWithLimit: BYTE, SBYTE, SHORT, SSHORT, LONG, SLONG, LONG8 and SLONG8.
    private static bool IsArrayType(ushort type) => type is 1 or 6 or 3 or 8 or 4 or 9 or 16 or 17;

    private ulong Value(ReadOnlySpan<byte> data, TiffEntry entry, int index)
    {
        bool be = ifd.BigEndian;
        long value = entry.Type switch
        {
            6 => (sbyte)data[entry.ValueOffset + index],
            8 => (short)TiffDirectory.U16(data, entry.ValueOffset + index * 2, be),
            9 => (int)TiffDirectory.U32(data, entry.ValueOffset + index * 4, be),
            17 => (long)TiffDirectory.U64(data, entry.ValueOffset + index * 8, be),
            _ => -1,
        };
        if (entry.Type is 6 or 8 or 9 or 17)
        {
            // TIFFReadDirEntryCheckRange*: a negative offset or count makes the whole array unreadable.
            return value >= 0 ? (ulong)value : throw TiffDecoder.Error($"The TIFF tag {entry.Tag} holds the negative offset or byte count {value}.");
        }

        return ifd.GetULong(data, entry, index);
    }
}
