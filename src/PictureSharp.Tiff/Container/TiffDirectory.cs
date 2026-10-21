using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// One image file directory of a classic TIFF or BigTIFF file. Entries of unknown types or with values outside the
/// file are dropped and the first of duplicate tags wins, as libtiff <c>TIFFReadDirectory</c> does; values are read on
/// demand.
/// </summary>
internal sealed class TiffDirectory
{
    private readonly TiffEntry[] entries;

    private TiffDirectory(TiffEntry[] entries, TiffHeader header, ulong next)
    {
        this.entries = entries;
        Header = header;
        Next = next;
    }

    public TiffHeader Header { get; }

    public bool BigEndian => Header.BigEndian;

    /// <summary>The offset of the next IFD in the chain; 0 at the end.</summary>
    public ulong Next { get; }

    public ReadOnlySpan<TiffEntry> Entries => entries;

    public static TiffDirectory Parse(ReadOnlySpan<byte> data, TiffHeader header, ulong offset)
    {
        if (!TryCount(data, header, offset, out int count))
        {
            throw TiffDecoder.Error($"The TIFF directory at offset {offset} lies outside the file.");
        }

        var entries = new List<TiffEntry>(count);
        var seen = new HashSet<ushort>();
        int entrySize = header.EntrySize, inline = header.OffsetSize;
        int pos = (int)offset + header.CountSize;
        for (int i = 0; i < count; i++, pos += entrySize)
        {
            ushort tag = U16(data, pos, header.BigEndian);
            ushort type = U16(data, pos + 2, header.BigEndian);
            ulong values = header.ReadOffset(data, pos + 4);
            int size = TiffEntry.TypeSize(type, header.BigTiff);
            if (size == 0 || values > uint.MaxValue || seen.Contains(tag))
            {
                continue;
            }

            ulong length = values * (uint)size;
            int valueField = pos + 4 + inline;
            ulong valueOffset = length <= (ulong)inline ? (ulong)valueField : header.ReadOffset(data, valueField);
            if (valueOffset > (ulong)data.Length || length > (ulong)data.Length - valueOffset)
            {
                continue;
            }

            seen.Add(tag);
            entries.Add(new TiffEntry(tag, type, (uint)values, (int)valueOffset));
        }

        return new TiffDirectory([.. entries], header, NextOffset(data, header, offset, count));
    }

    /// <summary>Parses an IFD that a tag points to (EXIF, GPS), or returns null when it lies outside the file.</summary>
    public TiffDirectory? TryParseSubdirectory(ReadOnlySpan<byte> data, ushort tag)
    {
        if (!TryGet(tag, out TiffEntry entry) || entry.Count == 0 || !TiffEntry.IsInteger(entry.Type))
        {
            return null;
        }

        ulong offset = GetULong(data, entry, 0);
        return TryCount(data, Header, offset, out _) ? Parse(data, Header, offset) : null;
    }

    /// <summary>
    /// Reads only what walking the IFD chain needs: whether the IFD is a reduced-resolution image
    /// (<c>NewSubfileType</c> bit 0 or <c>SubfileType</c> 2), the next offset and where it is stored. Returns false for an
    /// IFD outside the file.
    /// </summary>
    public static bool TryScan(ReadOnlySpan<byte> data, TiffHeader header, ulong offset, out bool reduced, out ulong next, out long link)
    {
        reduced = false;
        next = 0;
        link = 0;
        if (!TryCount(data, header, offset, out int count))
        {
            return false;
        }

        int pos = (int)offset + header.CountSize;
        for (int i = 0; i < count; i++, pos += header.EntrySize)
        {
            ushort tag = U16(data, pos, header.BigEndian);
            if (tag is TiffTag.NewSubfileType or TiffTag.SubfileType)
            {
                ushort type = U16(data, pos + 2, header.BigEndian);
                int field = pos + 4 + header.OffsetSize;
                uint value = type == TiffEntry.Short ? U16(data, field, header.BigEndian) : type == TiffEntry.Long ? U32(data, field, header.BigEndian) : 0;
                reduced |= tag == TiffTag.NewSubfileType ? (value & 1) != 0 : value == 2;
            }
        }

        next = NextOffset(data, header, offset, count);
        link = (long)offset + header.CountSize + ((long)header.EntrySize * count);
        return true;
    }

    public bool TryGet(ushort tag, out TiffEntry entry)
    {
        foreach (TiffEntry e in entries)
        {
            if (e.Tag == tag)
            {
                entry = e;
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>The first value of an integer tag, or <paramref name="fallback"/> when the tag is absent or empty.</summary>
    public uint GetUInt(ReadOnlySpan<byte> data, ushort tag, uint fallback) =>
        TryGet(tag, out TiffEntry e) && e.Count > 0 ? GetUInt(data, e, 0) : fallback;

    /// <summary>Value <paramref name="index"/> of an integer entry; 64-bit values above 2^32 - 1 saturate.</summary>
    public uint GetUInt(ReadOnlySpan<byte> data, TiffEntry entry, int index) =>
        (uint)Math.Min(GetULong(data, entry, index), uint.MaxValue);

    /// <summary>Value <paramref name="index"/> of a BYTE, SHORT, LONG, IFD, LONG8 or IFD8 entry.</summary>
    public ulong GetULong(ReadOnlySpan<byte> data, TiffEntry entry, int index) => entry.Type switch
    {
        TiffEntry.Byte => data[entry.ValueOffset + index],
        TiffEntry.Short => U16(data, entry.ValueOffset + index * 2, BigEndian),
        TiffEntry.Long or TiffEntry.Ifd => U32(data, entry.ValueOffset + index * 4, BigEndian),
        TiffEntry.Long8 or TiffEntry.Ifd8 => U64(data, entry.ValueOffset + index * 8, BigEndian),
        _ => throw TiffDecoder.Error($"The TIFF tag {entry.Tag} has the non-integer type {entry.Type}."),
    };

    /// <summary>The first value of a RATIONAL tag, or NaN when the tag is absent or of another type.</summary>
    public double GetRational(ReadOnlySpan<byte> data, ushort tag) =>
        TryGet(tag, out TiffEntry e) && e.Count > 0 ? GetRational(data, e, 0) : double.NaN;

    /// <summary>Value <paramref name="index"/> of a RATIONAL entry, or NaN for another type or a zero denominator.</summary>
    public double GetRational(ReadOnlySpan<byte> data, TiffEntry entry, int index)
    {
        if (entry.Type != TiffEntry.Rational)
        {
            return double.NaN;
        }

        uint numerator = U32(data, entry.ValueOffset + index * 8, BigEndian);
        uint denominator = U32(data, entry.ValueOffset + index * 8 + 4, BigEndian);
        return denominator == 0 ? double.NaN : (double)numerator / denominator;
    }

    /// <summary>The raw value bytes of an entry of a one-byte type (BYTE, ASCII, UNDEFINED); empty otherwise.</summary>
    public ReadOnlySpan<byte> GetBytes(ReadOnlySpan<byte> data, ushort tag) =>
        TryGet(tag, out TiffEntry e) && e.Size(Header.BigTiff) == 1 ? data.Slice(e.ValueOffset, (int)e.Count) : default;

    public static ushort U16(ReadOnlySpan<byte> data, int pos, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);

    public static uint U32(ReadOnlySpan<byte> data, int pos, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[pos..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);

    public static ulong U64(ReadOnlySpan<byte> data, int pos, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(data[pos..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);

    private static bool TryCount(ReadOnlySpan<byte> data, TiffHeader header, ulong offset, out int count)
    {
        count = 0;
        if (offset > (ulong)data.Length || (ulong)data.Length - offset < (ulong)header.CountSize)
        {
            return false;
        }

        ulong entries = header.BigTiff ? U64(data, (int)offset, header.BigEndian) : U16(data, (int)offset, header.BigEndian);
        if (entries > ((ulong)data.Length - offset - (ulong)header.CountSize) / (ulong)header.EntrySize)
        {
            return false;
        }

        count = (int)entries;
        return true;
    }

    // A missing next-IFD offset after the last entry ends the chain, as libtiff tolerates it.
    private static ulong NextOffset(ReadOnlySpan<byte> data, TiffHeader header, ulong offset, int count)
    {
        long pos = (long)offset + header.CountSize + (long)header.EntrySize * count;
        return pos + header.OffsetSize <= data.Length ? header.ReadOffset(data, (int)pos) : 0;
    }
}
