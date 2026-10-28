using System.Runtime.InteropServices;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Turns rows of unpacked samples into straight-alpha <see cref="Rgba32"/>. Samples of b bits become
/// <c>round(v * 255 / (2^b - 1))</c>: exact for 1, 2, 4 and 8 bits, <c>round(v / 257)</c> for 16 and
/// <c>round(v / 16843009)</c> for 32 (no ties occur). Signed samples arrive offset by <c>2^(b - 1)</c>, so their whole
/// range maps linearly. WhiteIsZero is inverted first, then premultiplied colour is divided by alpha at the source depth as Pillow's <c>unpackRGBa</c>
/// does (<c>min(max, v * max / a)</c>, 0 where alpha is 0). Colour map entries become <c>round(v / 257)</c>. CMYK
/// converts after the 8-bit reduction as Pillow's <c>cmyk2rgb</c> does. 16-bit pixels follow the same steps at 16 bits:
/// samples become <c>round(v * 65535 / (2^b - 1))</c> (the identity for 16 bits, <c>round(v / 65537)</c> for 32) and CMYK
/// converts with <c>nk - round(ink * nk / 65535)</c>.
/// </summary>
internal sealed class TiffColorConverter
{
    private readonly int photometric;
    private readonly int samples;
    private readonly int alpha;
    private readonly bool associated;
    private readonly int bits;
    private readonly int max;
    private readonly byte[] scale;
    private readonly Rgba32[]? palette;

    public TiffColorConverter(TiffLayout layout, Rgba32[]? palette)
    {
        photometric = layout.Photometric;
        samples = layout.SamplesPerPixel;
        alpha = layout.AlphaSample;
        associated = layout.Associated;
        bits = layout.BitsPerSample;
        max = bits < 32 ? (1 << bits) - 1 : 0;
        this.palette = palette;
        scale = new byte[bits < 16 ? max + 1 : 0];
        for (int v = 0; v < scale.Length; v++)
        {
            scale[v] = (byte)((v * 510 + max) / (2 * max));
        }
    }

    /// <summary>Reads 3 * 2^bits 16-bit colour map entries: all reds, then greens, then blues.</summary>
    public static Rgba32[] ReadPalette(ReadOnlySpan<byte> data, TiffDirectory ifd, int bits)
    {
        int size = 1 << bits;
        if (!ifd.TryGet(TiffTag.ColorMap, out TiffEntry map) || map.Type != TiffEntry.Short || map.Count < 3 * size)
        {
            throw TiffDecoder.Error($"The TIFF palette image has no colour map with {3 * size} entries.");
        }

        var colors = new Rgba32[size];
        for (int i = 0; i < size; i++)
        {
            colors[i] = new Rgba32(
                Reduce16(ifd.GetUInt(data, map, i)),
                Reduce16(ifd.GetUInt(data, map, size + i)),
                Reduce16(ifd.GetUInt(data, map, 2 * size + i)));
        }

        return colors;
    }

    public void Convert(ReadOnlySpan<ushort> row, Span<Rgba32> pixels)
    {
        if (photometric == TiffLayout.Palette)
        {
            Rgba32[] colors = palette!;
            for (int x = 0; x < pixels.Length; x++)
            {
                Rgba32 c = colors[row[x * samples]];
                pixels[x] = alpha >= 0 ? new Rgba32(c.R, c.G, c.B, To8(row[x * samples + alpha])) : c;
            }

            return;
        }

        bool invert = photometric == TiffLayout.WhiteIsZero;
        for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
        {
            int a = alpha >= 0 ? row[s + alpha] : max;
            if (photometric == TiffLayout.Rgb)
            {
                pixels[x] = new Rgba32(Color(row[s], a), Color(row[s + 1], a), Color(row[s + 2], a), To8(a));
            }
            else if (photometric == TiffLayout.Separated)
            {
                int nk = 255 - To8(row[s + 3]);
                pixels[x] = new Rgba32(Ink(To8(row[s]), nk), Ink(To8(row[s + 1]), nk), Ink(To8(row[s + 2]), nk), To8(a));
            }
            else
            {
                byte g = Color(invert ? max - row[s] : row[s], a);
                pixels[x] = new Rgba32(g, g, g, To8(a));
            }
        }
    }

    /// <summary>
    /// The same conversion for 8-bit samples read straight from a contiguous chunk row, where the scale is the identity:
    /// opaque or straight-alpha grey, grey with alpha, RGB and RGBA have their own loops.
    /// </summary>
    public void Convert(ReadOnlySpan<byte> row, Span<Rgba32> pixels)
    {
        if (photometric == TiffLayout.Palette)
        {
            Rgba32[] colors = palette!;
            for (int x = 0; x < pixels.Length; x++)
            {
                Rgba32 c = colors[row[x * samples]];
                pixels[x] = alpha >= 0 ? new Rgba32(c.R, c.G, c.B, row[x * samples + alpha]) : c;
            }

            return;
        }

        bool straight = alpha < 0 || !associated;
        if (photometric == TiffLayout.Rgb && straight && samples == 4 && alpha == 3)
        {
            MemoryMarshal.Cast<byte, Rgba32>(row[..(pixels.Length * 4)]).CopyTo(pixels);
            return;
        }

        if (photometric == TiffLayout.Rgb && straight)
        {
            for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
            {
                pixels[x] = new Rgba32(row[s], row[s + 1], row[s + 2], alpha >= 0 ? row[s + alpha] : (byte)255);
            }

            return;
        }

        if (photometric == TiffLayout.BlackIsZero && straight)
        {
            for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
            {
                byte g = row[s];
                pixels[x] = new Rgba32(g, g, g, alpha >= 0 ? row[s + alpha] : (byte)255);
            }

            return;
        }

        bool invert = photometric == TiffLayout.WhiteIsZero;
        for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
        {
            int a = alpha >= 0 ? row[s + alpha] : 255;
            if (photometric == TiffLayout.Rgb)
            {
                pixels[x] = new Rgba32(Color(row[s], a), Color(row[s + 1], a), Color(row[s + 2], a), (byte)a);
            }
            else if (photometric == TiffLayout.Separated)
            {
                int nk = 255 - row[s + 3];
                pixels[x] = new Rgba32(Ink(row[s], nk), Ink(row[s + 1], nk), Ink(row[s + 2], nk), (byte)a);
            }
            else
            {
                byte g = Color(invert ? 255 - row[s] : row[s], a);
                pixels[x] = new Rgba32(g, g, g, (byte)a);
            }
        }
    }

    /// <summary>The same conversion for 32-bit integer samples (signed ones already offset), greyscale or RGB.</summary>
    public void Convert(ReadOnlySpan<uint> row, Span<Rgba32> pixels)
    {
        bool invert = photometric == TiffLayout.WhiteIsZero;
        for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
        {
            ulong a = alpha >= 0 ? row[s + alpha] : uint.MaxValue;
            if (photometric == TiffLayout.Rgb)
            {
                pixels[x] = new Rgba32(Color32(row[s], a), Color32(row[s + 1], a), Color32(row[s + 2], a), Reduce32(a));
            }
            else
            {
                byte g = Color32(invert ? uint.MaxValue - row[s] : row[s], a);
                pixels[x] = new Rgba32(g, g, g, Reduce32(a));
            }
        }
    }

    /// <summary>16-bit pixels from samples of 9 to 16 bits (palette images are always 8-bit).</summary>
    public void Convert(ReadOnlySpan<ushort> row, Span<Rgba64> pixels)
    {
        bool invert = photometric == TiffLayout.WhiteIsZero;
        for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
        {
            int a = alpha >= 0 ? row[s + alpha] : max;
            if (photometric == TiffLayout.Rgb)
            {
                pixels[x] = new Rgba64(Color16(row[s], a), Color16(row[s + 1], a), Color16(row[s + 2], a), To16(a));
            }
            else if (photometric == TiffLayout.Separated)
            {
                uint nk = ushort.MaxValue - (uint)To16(row[s + 3]);
                pixels[x] = new Rgba64(Ink16(To16(row[s]), nk), Ink16(To16(row[s + 1]), nk), Ink16(To16(row[s + 2]), nk), To16(a));
            }
            else
            {
                ushort g = Color16(invert ? max - row[s] : row[s], a);
                pixels[x] = new Rgba64(g, g, g, To16(a));
            }
        }
    }

    /// <summary>16-bit pixels from 32-bit integer samples (signed ones already offset), greyscale or RGB.</summary>
    public void Convert(ReadOnlySpan<uint> row, Span<Rgba64> pixels)
    {
        bool invert = photometric == TiffLayout.WhiteIsZero;
        for (int x = 0, s = 0; x < pixels.Length; x++, s += samples)
        {
            ulong a = alpha >= 0 ? row[s + alpha] : uint.MaxValue;
            if (photometric == TiffLayout.Rgb)
            {
                pixels[x] = new Rgba64(Color32To16(row[s], a), Color32To16(row[s + 1], a), Color32To16(row[s + 2], a), Reduce32To16(a));
            }
            else
            {
                ushort g = Color32To16(invert ? uint.MaxValue - row[s] : row[s], a);
                pixels[x] = new Rgba64(g, g, g, Reduce32To16(a));
            }
        }
    }

    private static byte Reduce16(uint v) => (byte)((v + 128) / 257);

    // 2^32 - 1 = 65535 * 65537, and v + 32768 never ties.
    private static ushort Reduce32To16(ulong v) => (ushort)((v + 32768) / 65537);

    private ushort Color32To16(ulong v, ulong a) =>
        Reduce32To16(associated && a != uint.MaxValue ? a == 0 ? 0 : Math.Min(uint.MaxValue, v * uint.MaxValue / a) : v);

    private static ushort Ink16(uint ink, uint nk) => (ushort)(nk - ((ink * nk) + 32767) / 65535);

    private ushort Color16(int v, int a)
    {
        if (associated && a != max)
        {
            v = a == 0 ? 0 : (int)Math.Min(max, (uint)v * (uint)max / (uint)a);
        }

        return To16(v);
    }

    private ushort To16(int v) => bits == 16 ? (ushort)v : (ushort)((((long)v * ushort.MaxValue) + (max / 2)) / max);

    private static byte Reduce32(ulong v) => (byte)((2 * v + 16843009) / 33686018);

    private byte Color32(ulong v, ulong a) =>
        Reduce32(associated && a != uint.MaxValue ? a == 0 ? 0 : Math.Min(uint.MaxValue, v * uint.MaxValue / a) : v);

    // port: Pillow 10.2.0 Convert.c cmyk2rgb: nk - MULDIV255(ink, nk), where nk is 255 minus black.
    private static byte Ink(int ink, int nk)
    {
        int t = ink * nk + 128;
        return (byte)(nk - (((t >> 8) + t) >> 8));
    }

    private byte Color(int v, int a)
    {
        if (associated && a != max)
        {
            v = a == 0 ? 0 : (int)Math.Min(max, (uint)v * (uint)max / (uint)a);
        }

        return To8(v);
    }

    private byte To8(int v) => bits == 16 ? Reduce16((uint)v) : scale[v];
}
