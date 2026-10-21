using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// One classic TIFF image file directory. Entries of unknown types or with values outside the file are dropped and
/// the first of duplicate tags wins, as libtiff <c>TIFFReadDirectory</c> does; values are read on demand.
/// </summary>
internal sealed class TiffDirectory
{
    private readonly TiffEntry[] entries;

    private TiffDirectory(TiffEntry[] entries, bool bigEndian, uint next)
    {
        this.entries = entries;
        BigEndian = bigEndian;
        Next = next;
    }

    public bool BigEndian { get; }

    /// <summary>The offset of the next IFD in the chain; 0 at the end.</summary>
    public uint Next { get; }

    public static TiffDirectory Parse(ReadOnlySpan<byte> data, bool bigEndian, uint offset)
    {
        int count = EntryCount(data, bigEndian, offset);
        var entries = new List<TiffEntry>(count);
        var seen = new HashSet<ushort>();
        int pos = (int)offset + 2;
        for (int i = 0; i < count; i++, pos += 12)
        {
            ushort tag = U16(data, pos, bigEndian);
            ushort type = U16(data, pos + 2, bigEndian);
            uint values = U32(data, pos + 4, bigEndian);
            int size = TiffEntry.TypeSize(type);
            if (size == 0 || seen.Contains(tag))
            {
                continue;
            }

            ulong length = (ulong)values * (uint)size;
            ulong valueOffset = length <= 4 ? (ulong)(pos + 8) : U32(data, pos + 8, bigEndian);
            if (valueOffset + length > (ulong)data.Length)
            {
                continue;
            }

            seen.Add(tag);
            entries.Add(new TiffEntry(tag, type, values, (int)valueOffset));
        }

        return new TiffDirectory([.. entries], bigEndian, NextOffset(data, bigEndian, offset, count));
    }

    /// <summary>
    /// Reads only what walking the IFD chain needs: whether the IFD is a reduced-resolution image
    /// (<c>NewSubfileType</c> bit 0 or <c>SubfileType</c> 2) and the next offset. Returns false for an IFD outside the file.
    /// </summary>
    public static bool TryScan(ReadOnlySpan<byte> data, bool bigEndian, uint offset, out bool reduced, out uint next)
    {
        reduced = false;
        next = 0;
        if (!HasEntries(data, bigEndian, offset))
        {
            return false;
        }

        int count = U16(data, (int)offset, bigEndian);
        int pos = (int)offset + 2;
        for (int i = 0; i < count; i++, pos += 12)
        {
            ushort tag = U16(data, pos, bigEndian);
            if (tag is TiffTag.NewSubfileType or TiffTag.SubfileType)
            {
                ushort type = U16(data, pos + 2, bigEndian);
                uint value = type == TiffEntry.Short ? U16(data, pos + 8, bigEndian) : type == TiffEntry.Long ? U32(data, pos + 8, bigEndian) : 0;
                reduced |= tag == TiffTag.NewSubfileType ? (value & 1) != 0 : value == 2;
            }
        }

        next = NextOffset(data, bigEndian, offset, count);
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

    /// <summary>Value <paramref name="index"/> of a BYTE, SHORT or LONG entry.</summary>
    public uint GetUInt(ReadOnlySpan<byte> data, TiffEntry entry, int index) => entry.Type switch
    {
        TiffEntry.Byte => data[entry.ValueOffset + index],
        TiffEntry.Short => U16(data, entry.ValueOffset + index * 2, BigEndian),
        TiffEntry.Long => U32(data, entry.ValueOffset + index * 4, BigEndian),
        _ => throw TiffDecoder.Error($"The TIFF tag {entry.Tag} has the non-integer type {entry.Type}."),
    };

    /// <summary>The first value of a RATIONAL tag, or NaN when the tag is absent or of another type.</summary>
    public double GetRational(ReadOnlySpan<byte> data, ushort tag)
    {
        if (!TryGet(tag, out TiffEntry e) || e.Type != TiffEntry.Rational || e.Count == 0)
        {
            return double.NaN;
        }

        uint numerator = U32(data, e.ValueOffset, BigEndian);
        uint denominator = U32(data, e.ValueOffset + 4, BigEndian);
        return denominator == 0 ? double.NaN : (double)numerator / denominator;
    }

    /// <summary>The raw value bytes of an entry of a one-byte type (BYTE, ASCII, UNDEFINED); empty otherwise.</summary>
    public ReadOnlySpan<byte> GetBytes(ReadOnlySpan<byte> data, ushort tag) =>
        TryGet(tag, out TiffEntry e) && e.Size == 1 ? data.Slice(e.ValueOffset, (int)e.Count) : default;

    public static ushort U16(ReadOnlySpan<byte> data, int pos, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[pos..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);

    public static uint U32(ReadOnlySpan<byte> data, int pos, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[pos..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);

    private static bool HasEntries(ReadOnlySpan<byte> data, bool bigEndian, uint offset) =>
        data.Length >= 2 && offset <= (uint)data.Length - 2
        && offset + 2 + 12L * U16(data, (int)offset, bigEndian) <= data.Length;

    private static int EntryCount(ReadOnlySpan<byte> data, bool bigEndian, uint offset)
    {
        if (!HasEntries(data, bigEndian, offset))
        {
            throw TiffDecoder.Error($"The TIFF directory at offset {offset} lies outside the file.");
        }

        return U16(data, (int)offset, bigEndian);
    }

    // A missing next-IFD offset after the last entry ends the chain, as libtiff tolerates it.
    private static uint NextOffset(ReadOnlySpan<byte> data, bool bigEndian, uint offset, int count)
    {
        long pos = offset + 2 + 12L * count;
        return pos + 4 <= data.Length ? U32(data, (int)pos, bigEndian) : 0;
    }
}
