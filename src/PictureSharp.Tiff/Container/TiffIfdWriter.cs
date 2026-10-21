using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Collects the entries of one little-endian classic TIFF IFD and serializes them sorted by tag, with values of more
/// than four bytes stored after the entries at word-aligned offsets.
/// </summary>
internal sealed class TiffIfdWriter
{
    private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Value)> _entries = [];

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

    /// <summary>An ASCII value with its terminating NUL.</summary>
    public void AddAscii(ushort tag, string text)
    {
        byte[] value = new byte[Encoding.ASCII.GetByteCount(text) + 1];
        Encoding.ASCII.GetBytes(text, value);
        Add(tag, TiffEntry.Ascii, (uint)value.Length, value);
    }

    public void AddBytes(ushort tag, ushort type, byte[] value) => Add(tag, type, (uint)value.Length, value);

    /// <summary>The serialized size: the entry count, the entries, the next-IFD link and the out-of-line values.</summary>
    public int Size
    {
        get
        {
            int size = 2 + (_entries.Count * 12) + 4;
            foreach (var entry in _entries)
            {
                if (entry.Value.Length > 4)
                {
                    size += Align(entry.Value.Length);
                }
            }

            return size;
        }
    }

    /// <summary>Serializes the IFD as it will lie at <paramref name="offset"/> (even) in the file, with no next IFD.</summary>
    public byte[] ToArray(uint offset)
    {
        _entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        byte[] ifd = new byte[Size];
        Span<byte> span = ifd;
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)_entries.Count);
        int values = 2 + (_entries.Count * 12) + 4;
        for (int i = 0; i < _entries.Count; i++)
        {
            (ushort tag, ushort type, uint count, byte[] value) = _entries[i];
            Span<byte> entry = span.Slice(2 + (i * 12), 12);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
            if (value.Length <= 4)
            {
                value.CopyTo(entry[8..]);
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked(offset + (uint)values));
            value.CopyTo(span[values..]);
            values += Align(value.Length);
        }

        return ifd;
    }

    private void Add(ushort tag, ushort type, uint count, byte[] value)
    {
        if (_entries.Exists(entry => entry.Tag == tag))
        {
            throw new InvalidOperationException($"Tag {tag} is already set.");
        }

        _entries.Add((tag, type, count, value));
    }

    private static int Align(int length) => (length + 1) & ~1;
}
