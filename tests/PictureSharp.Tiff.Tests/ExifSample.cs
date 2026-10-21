using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Builds EXIF blocks in either byte order for the metadata-writing tests and lists the entries of any EXIF block in a
/// byte-order-independent form, without the plugin's container code.
/// </summary>
internal static class ExifSample
{
    private const ushort ExifIfd = 34665, GpsIfd = 34853, InteropIfd = 40965;

    /// <summary>
    /// A camera-like block: IFD0 with descriptive tags, the orientation, structural tags the writer must drop and a
    /// thumbnail IFD1; EXIF IFD with every EXIF value type, a maker note and an interoperability IFD; and a GPS IFD.
    /// </summary>
    public static byte[] Block(bool bigEndian, ushort orientation = 6)
    {
        List<Field> ifd0 =
        [
            Ascii(270, "A test picture"), Ascii(271, "Camera Maker"), Ascii(272, "Model X"), Short(274, orientation),
            Rational(282, 72, 1), Rational(283, 72, 1), Short(296, 2), Ascii(305, "Camera firmware 1.0"),
            Ascii(306, "2026:10:21 09:30:00"), Ascii(315, "Someone"), Ascii(33432, "(c) Someone"), Short(531, 2),
        ];
        List<Field> exif =
        [
            Rational(33434, 1, 250), Rational(33437, 28, 10), Short(34855, 400), Undefined(36864, "0232"u8),
            Ascii(36867, "2026:10:21 09:30:00"), Ascii(36868, "2026:10:21 09:30:00"),
            new(37380, 10, 1, [.. BE(-1), .. BE(3)]), Short(37396, 100, 200, 30, 40), Rational(37386, 50, 1),
            Undefined(37500, "MAKERNOTE\0\x01\x02\x03"u8), Undefined(37510, "\0\0\0\0\0\0\0\0Hello"u8),
            Short(40961, 1), new(40962, 4, 1, BE32(4000)), new(40963, 4, 1, BE32(3000)),
        ];
        List<Field> interop = [Ascii(1, "R98"), Undefined(2, "0100"u8)];
        List<Field> gps =
        [
            new(0, 1, 4, [2, 3, 0, 0]), Ascii(1, "N"), Rational(2, 52, 1, 13, 1, 3600, 100), Ascii(3, "E"),
            Rational(4, 21, 1, 0, 1, 0, 1), new(5, 1, 1, [0]), Rational(6, 1234, 10), Ascii(29, "2026:10:21"),
        ];
        List<Field> thumbnail = [Short(259, 6), new(513, 4, 1, BE32(0)), new(514, 4, 1, BE32(0))];
        return Write(bigEndian, [ifd0, exif, interop, gps, thumbnail]);
    }

    /// <summary>
    /// Every entry of the block's IFD0 (only <paramref name="ifd0Tags"/> when given), EXIF, interoperability and GPS
    /// IFDs, pointers left out, as "ifd tag type count value" with values in little-endian hex.
    /// </summary>
    public static List<string> Entries(byte[]? block, ReadOnlySpan<ushort> ifd0Tags = default)
    {
        var result = new List<string>();
        if (block is null)
        {
            return result;
        }

        bool be = block[0] == 'M';
        uint ifd0 = U32(block, 4, be);
        var pointers = new Dictionary<ushort, uint>();
        foreach (var entry in Read(block, ifd0, be))
        {
            if (entry.Tag is ExifIfd or GpsIfd)
            {
                pointers[entry.Tag] = U32(block, entry.Position + 8, be);
            }
            else if (ifd0Tags.IsEmpty || ifd0Tags.Contains(entry.Tag))
            {
                result.Add(Describe("0", block, entry, be));
            }
        }

        foreach ((ushort pointer, uint offset) in pointers)
        {
            foreach (var entry in Read(block, offset, be))
            {
                if (entry.Tag == InteropIfd && pointer == ExifIfd)
                {
                    result.AddRange(Read(block, U32(block, entry.Position + 8, be), be).Select(e => Describe("interop", block, e, be)));
                }
                else
                {
                    result.Add(Describe(pointer == ExifIfd ? "exif" : "gps", block, entry, be));
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>The IFD0 ASCII value of <paramref name="tag"/> in a block, or null.</summary>
    public static string? Ascii(byte[]? block, ushort tag) =>
        Entries(block).FirstOrDefault(e => e.StartsWith($"0 {tag} 2 ", StringComparison.Ordinal)) is { } line
            ? Encoding.ASCII.GetString(Convert.FromHexString(line.Split(' ')[4])).TrimEnd('\0')
            : null;

    private static string Describe(string ifd, byte[] block, (ushort Tag, ushort Type, uint Count, int Position) e, bool be)
    {
        int size = e.Type switch { 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 1 };
        int length = (int)e.Count * size;
        int offset = length <= 4 ? e.Position + 8 : (int)U32(block, e.Position + 8, be);
        byte[] value = block[offset..(offset + length)];
        int unit = e.Type is 5 or 10 ? 4 : size;
        for (int i = 0; be && unit > 1 && i < value.Length; i += unit)
        {
            Array.Reverse(value, i, unit);
        }

        return $"{ifd} {e.Tag} {e.Type} {e.Count} {Convert.ToHexString(value)}";
    }

    private static List<(ushort Tag, ushort Type, uint Count, int Position)> Read(byte[] block, uint offset, bool be)
    {
        int count = U16(block, (int)offset, be);
        var entries = new List<(ushort, ushort, uint, int)>();
        for (int i = 0; i < count; i++)
        {
            int pos = (int)offset + 2 + (i * 12);
            entries.Add((U16(block, pos, be), U16(block, pos + 2, be), U32(block, pos + 4, be), pos));
        }

        return entries;
    }

    private static byte[] Write(bool be, List<Field>[] ifds)
    {
        // IFD0 links to EXIF and GPS, EXIF to interop; IFD1 (the thumbnail) follows IFD0.
        ifds[0].Add(new(ExifIfd, 4, 1, new byte[4]));
        ifds[0].Add(new(GpsIfd, 4, 1, new byte[4]));
        ifds[1].Add(new(InteropIfd, 4, 1, new byte[4]));
        var offsets = new int[ifds.Length];
        int size = 8;
        for (int i = 0; i < ifds.Length; i++)
        {
            ifds[i].Sort((a, b) => a.Tag.CompareTo(b.Tag));
            offsets[i] = size;
            size += 2 + (12 * ifds[i].Count) + 4 + ifds[i].Sum(f => f.Value.Length > 4 ? (f.Value.Length + 1) & ~1 : 0);
        }

        Set(ifds[0], ExifIfd, offsets[1]);
        Set(ifds[0], GpsIfd, offsets[3]);
        Set(ifds[1], InteropIfd, offsets[2]);
        var block = new byte[size];
        block[0] = block[1] = (byte)(be ? 'M' : 'I');
        W16(block, 2, 42, be);
        W32(block, 4, 8, be);
        for (int i = 0; i < ifds.Length; i++)
        {
            int pos = offsets[i], values = pos + 2 + (12 * ifds[i].Count) + 4;
            W16(block, pos, (ushort)ifds[i].Count, be);
            W32(block, values - 4, i == 0 ? (uint)offsets[4] : 0, be);
            foreach (Field f in ifds[i])
            {
                W16(block, pos + 2, f.Tag, be);
                W16(block, pos + 4, f.Type, be);
                W32(block, pos + 6, f.Count, be);
                byte[] value = Order(f, be);
                if (value.Length <= 4)
                {
                    value.CopyTo(block, pos + 10);
                }
                else
                {
                    W32(block, pos + 10, (uint)values, be);
                    value.CopyTo(block, values);
                    values += (value.Length + 1) & ~1;
                }

                pos += 12;
            }
        }

        return block;
    }

    // Field values are given big-endian.
    private static byte[] Order(Field f, bool be)
    {
        byte[] value = (byte[])f.Value.Clone();
        int unit = f.Type switch { 3 or 8 => 2, 4 or 9 or 11 or 13 or 5 or 10 => 4, 12 => 8, _ => 1 };
        for (int i = 0; !be && unit > 1 && i < value.Length; i += unit)
        {
            Array.Reverse(value, i, unit);
        }

        return value;
    }

    private static void Set(List<Field> ifd, ushort tag, int offset)
    {
        int i = ifd.FindIndex(f => f.Tag == tag);
        ifd[i] = ifd[i] with { Value = BE32((uint)offset) };
    }

    private static Field Ascii(ushort tag, string text) => new(tag, 2, (uint)text.Length + 1, [.. Encoding.ASCII.GetBytes(text), 0]);

    private static Field Undefined(ushort tag, ReadOnlySpan<byte> value) => new(tag, 7, (uint)value.Length, value.ToArray());

    private static Field Short(ushort tag, params ushort[] values) =>
        new(tag, 3, (uint)values.Length, [.. values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v })]);

    private static Field Rational(ushort tag, params uint[] pairs) =>
        new(tag, 5, (uint)pairs.Length / 2, [.. pairs.SelectMany(BE32)]);

    private static byte[] BE32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] BE(int value) => BE32((uint)value);

    private static ushort U16(byte[] b, int pos, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(pos));

    private static uint U32(byte[] b, int pos, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos));

    private static void W16(byte[] b, int pos, ushort v, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(pos), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(pos), v);
        }
    }

    private static void W32(byte[] b, int pos, uint v, bool be)
    {
        if (be)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(pos), v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(pos), v);
        }
    }

    private sealed record Field(ushort Tag, ushort Type, uint Count, byte[] Value);
}
