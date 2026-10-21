using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Collects the entries of one IFD and serializes them sorted by tag, with values that do not fit the entry stored
/// after the entries at word-aligned offsets, followed by the child IFDs (EXIF, GPS) the entries point to. Little-endian
/// classic TIFF unless the file being rewritten is big-endian or BigTIFF.
/// </summary>
internal sealed class TiffIfdWriter(bool bigEndian = false, bool bigTiff = false)
{
    // Values are kept little-endian and swapped on output for big-endian files.
    private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Value)> _entries = [];
    private readonly List<(ushort Tag, TiffIfdWriter Child)> _children = [];

    private int EntrySize => bigTiff ? 20 : 12;

    private int OffsetSize => bigTiff ? 8 : 4;

    private int CountSize => bigTiff ? 8 : 2;

    public void AddShort(ushort tag, params ReadOnlySpan<ushort> values)
    {
        byte[] value = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(i * 2), values[i]);
        }

        Add(tag, TiffEntry.Short, (uint)values.Length, value);
    }

    public void AddLong(ushort tag, params ReadOnlySpan<uint> values)
    {
        byte[] value = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(i * 4), values[i]);
        }

        Add(tag, TiffEntry.Long, (uint)values.Length, value);
    }

    /// <summary>File offsets or byte counts: LONG in classic TIFF, LONG8 in BigTIFF.</summary>
    public void AddOffsets(ushort tag, ReadOnlySpan<ulong> values)
    {
        int size = OffsetSize;
        byte[] value = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            if (bigTiff)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(i * 8), values[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(i * 4), checked((uint)values[i]));
            }
        }

        Add(tag, bigTiff ? TiffEntry.Long8 : TiffEntry.Long, (uint)values.Length, value);
    }

    public void AddRational(ushort tag, uint numerator, uint denominator) => AddRational(tag, [numerator, denominator]);

    /// <summary>Rationals given as numerator, denominator pairs.</summary>
    public void AddRational(ushort tag, params ReadOnlySpan<uint> pairs)
    {
        byte[] value = new byte[pairs.Length * 4];
        for (int i = 0; i < pairs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(i * 4), pairs[i]);
        }

        Add(tag, TiffEntry.Rational, (uint)(pairs.Length / 2), value);
    }

    /// <summary>XResolution, YResolution and ResolutionUnit (inches), as rationals that read back as the same floats.</summary>
    public void AddResolution(float horizontal, float vertical)
    {
        (uint x, uint xDenominator) = Rational(horizontal);
        (uint y, uint yDenominator) = Rational(vertical);
        AddRational(TiffTag.XResolution, x, xDenominator);
        AddRational(TiffTag.YResolution, y, yDenominator);
        AddShort(TiffTag.ResolutionUnit, 2);
    }

    /// <summary>An ASCII value with its terminating NUL.</summary>
    public void AddAscii(ushort tag, string text)
    {
        byte[] value = new byte[Encoding.ASCII.GetByteCount(text) + 1];
        Encoding.ASCII.GetBytes(text, value);
        Add(tag, TiffEntry.Ascii, (uint)value.Length, value);
    }

    public void AddBytes(ushort tag, ushort type, byte[] value) => Add(tag, type, (uint)value.Length, value);

    /// <summary>
    /// An entry copied as stored in another TIFF stream: <paramref name="value"/> holds its <paramref name="count"/>
    /// values in the byte order <paramref name="sourceBigEndian"/> gives.
    /// </summary>
    public void AddCopy(ushort tag, ushort type, uint count, ReadOnlySpan<byte> value, bool sourceBigEndian)
    {
        byte[] copy = value.ToArray();
        if (sourceBigEndian)
        {
            Swap(copy, type);
        }

        Add(tag, type, count, copy);
    }

    /// <summary>A pointer entry (LONG, or IFD8 in BigTIFF) to <paramref name="child"/>, which is written after this IFD.</summary>
    public void AddChild(ushort tag, TiffIfdWriter child)
    {
        Add(tag, bigTiff ? TiffEntry.Ifd8 : TiffEntry.Long, 1, new byte[OffsetSize]);
        _children.Add((tag, child));
    }

    /// <summary>An empty IFD in the same byte order and variant, for <see cref="AddChild"/>.</summary>
    public TiffIfdWriter CreateChild() => new(bigEndian, bigTiff);

    public bool Contains(ushort tag) => _entries.Exists(entry => entry.Tag == tag);

    /// <summary>
    /// The serialized size: the entry count, the entries, the next-IFD link, the out-of-line values and the child IFDs.
    /// </summary>
    public long Size
    {
        get
        {
            long size = OwnSize;
            foreach (var (_, child) in _children)
            {
                size += child.Size;
            }

            return size;
        }
    }

    private long OwnSize
    {
        get
        {
            long size = CountSize + ((long)_entries.Count * EntrySize) + OffsetSize;
            foreach (var entry in _entries)
            {
                if (entry.Value.Length > OffsetSize)
                {
                    size += Align(entry.Value.Length);
                }
            }

            return size;
        }
    }

    /// <summary>
    /// Serializes the IFD as it will lie at <paramref name="offset"/> (even) in the file, linked to
    /// <paramref name="next"/>, then its child IFDs.
    /// </summary>
    public byte[] ToArray(ulong offset, ulong next = 0)
    {
        byte[] ifd = new byte[Size];
        Write(ifd, offset, next);
        return ifd;
    }

    private void Write(Span<byte> span, ulong offset, ulong next)
    {
        _entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        ulong childOffset = offset + (ulong)OwnSize;
        foreach (var (tag, child) in _children)
        {
            byte[] pointer = _entries.Find(entry => entry.Tag == tag).Value;
            WriteOffset(pointer, bigTiff ? childOffset : checked((uint)childOffset), swap: false);
            child.Write(span[(int)(childOffset - offset)..], childOffset, 0);
            childOffset += (ulong)child.Size;
        }

        WriteOffset(span, (ulong)_entries.Count, bigEndian, CountSize);
        int values = CountSize + (_entries.Count * EntrySize) + OffsetSize;
        WriteOffset(span[(values - OffsetSize)..], next, bigEndian);
        for (int i = 0; i < _entries.Count; i++)
        {
            (ushort tag, ushort type, uint count, byte[] value) = _entries[i];
            Span<byte> entry = span.Slice(CountSize + (i * EntrySize), EntrySize);
            Write16(entry, tag);
            Write16(entry[2..], type);
            WriteOffset(entry[4..], count, bigEndian);
            Span<byte> target = value.Length <= OffsetSize ? entry[(4 + OffsetSize)..] : span[values..];
            value.CopyTo(target);
            if (bigEndian)
            {
                Swap(target[..value.Length], type);
            }

            if (value.Length > OffsetSize)
            {
                ulong position = offset + (ulong)values;
                WriteOffset(entry[(4 + OffsetSize)..], bigTiff ? position : checked((uint)position), bigEndian);
                values += Align(value.Length);
            }
        }
    }

    private void Add(ushort tag, ushort type, uint count, byte[] value)
    {
        if (Contains(tag))
        {
            throw new InvalidOperationException($"Tag {tag} is already set.");
        }

        _entries.Add((tag, type, count, value));
    }

    private void Write16(Span<byte> span, ushort value)
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

    // A count, offset or entry count: 2, 4 or 8 bytes, the offset size unless given.
    private void WriteOffset(Span<byte> span, ulong value, bool swap, int size = 0)
    {
        size = size == 0 ? OffsetSize : size;
        Span<byte> field = span[..size];
        if (size == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)value);
        }
        else if (size == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(field, value);
        }

        if (swap)
        {
            field.Reverse();
        }
    }

    /// <summary>Reverses the byte order of each value (each half of a rational) in place.</summary>
    private static void Swap(Span<byte> value, ushort type)
    {
        int size = type is TiffEntry.Rational or 10 ? 4 : TiffEntry.TypeSize(type, bigTiff: true);
        for (int i = 0; size > 1 && i + size <= value.Length; i += size)
        {
            value.Slice(i, size).Reverse();
        }
    }

    /// <summary>
    /// The resolution as a rational that reads back as the same <see cref="float"/>: the smallest power-of-ten
    /// denominator up to 10⁶ that is exact, or the finest one that fits.
    /// </summary>
    private static (uint Numerator, uint Denominator) Rational(float value)
    {
        (uint, uint) best = ((uint)Math.Min(Math.Round((double)value), uint.MaxValue), 1);
        for (uint denominator = 1; denominator <= 1_000_000; denominator *= 10)
        {
            double numerator = Math.Round(value * (double)denominator);
            if (numerator > uint.MaxValue)
            {
                break;
            }

            best = ((uint)numerator, denominator);
            if ((float)(numerator / denominator) == value)
            {
                break;
            }
        }

        return best;
    }

    private static int Align(int length) => (length + 1) & ~1;
}
