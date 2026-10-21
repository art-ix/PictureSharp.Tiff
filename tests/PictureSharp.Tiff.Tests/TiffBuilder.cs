using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>Writes small little-endian TIFF files with one IFD after the pixel data, for edge cases no tool writes.</summary>
internal sealed class TiffBuilder
{
    public const ushort Short = 3;
    public const ushort Long = 4;

    private readonly SortedDictionary<ushort, (ushort Type, uint[] Values)> entries = [];
    private readonly byte[] pixels;

    private TiffBuilder(byte[] pixels) => this.pixels = pixels;

    /// <summary>An uncompressed 8-bit greyscale image in one strip; StripOffsets is filled in by <see cref="Build"/>.</summary>
    public static TiffBuilder Gray(int width, int height)
    {
        byte[] pixels = [.. Enumerable.Range(0, width * height).Select(i => (byte)(i * 7))];
        return new TiffBuilder(pixels)
            .Set(256, Long, (uint)width)
            .Set(257, Long, (uint)height)
            .Set(258, Short, 8)
            .Set(259, Short, 1)
            .Set(262, Short, 1)
            .Set(278, Long, (uint)height)
            .Set(279, Long, (uint)pixels.Length);
    }

    /// <summary>An 8-bit greyscale image in one strip of already compressed data.</summary>
    public static TiffBuilder GrayStrip(int width, int height, ushort compression, byte[] strip) =>
        new TiffBuilder(strip)
            .Set(256, Long, (uint)width)
            .Set(257, Long, (uint)height)
            .Set(258, Short, 8)
            .Set(259, Short, compression)
            .Set(262, Short, 1)
            .Set(278, Long, (uint)height)
            .Set(279, Long, (uint)strip.Length);

    public TiffBuilder Set(ushort tag, ushort type, params uint[] values)
    {
        entries[tag] = (type, values);
        return this;
    }

    public TiffBuilder Remove(ushort tag)
    {
        entries.Remove(tag);
        return this;
    }

    /// <summary>Builds the file; <paramref name="next"/> maps the IFD offset to the next-IFD link (0 by default).</summary>
    public byte[] Build(Func<uint, uint>? next = null)
    {
        entries.TryAdd((ushort)273, (Long, [8u]));
        uint ifdOffset = (uint)(8 + pixels.Length + (pixels.Length & 1));
        var extra = new List<byte>();
        uint extraOffset = ifdOffset + 2 + 12 * (uint)entries.Count + 4;
        var ifd = new List<byte>();
        Add16(ifd, (ushort)entries.Count);
        foreach ((ushort tag, (ushort type, uint[] values)) in entries)
        {
            byte[] raw = [.. values.SelectMany(v => type == Short ? BitConverter.GetBytes((ushort)v) : BitConverter.GetBytes(v))];
            Add16(ifd, tag);
            Add16(ifd, type);
            Add32(ifd, (uint)values.Length);
            if (raw.Length <= 4)
            {
                ifd.AddRange(raw);
                ifd.AddRange(new byte[4 - raw.Length]);
            }
            else
            {
                Add32(ifd, extraOffset + (uint)extra.Count);
                extra.AddRange(raw);
            }
        }

        Add32(ifd, next?.Invoke(ifdOffset) ?? 0);
        byte[] header = [(byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ifdOffset);
        return [.. header, .. pixels, .. new byte[pixels.Length & 1], .. ifd, .. extra];
    }

    private static void Add16(List<byte> list, ushort value) => list.AddRange(BitConverter.GetBytes(value));

    private static void Add32(List<byte> list, uint value) => list.AddRange(BitConverter.GetBytes(value));
}
