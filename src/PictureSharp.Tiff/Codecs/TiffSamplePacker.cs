using System.Runtime.InteropServices;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Turns <see cref="Rgba32"/> rows into contiguous 8-bit TIFF samples: grey (the luminance of colour pixels), grey and
/// alpha, RGB, RGBA with the straight alpha as unassociated alpha, palette indices, or 1-bit black and white.
/// </summary>
internal static class TiffSamplePacker
{
    /// <summary>The alpha byte of a packed <see cref="Rgba32"/>, whose bytes are R, G, B, A in memory.</summary>
    private static readonly uint OpaqueAlpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;

    public static void Pack(ReadOnlySpan<Rgba32> row, Span<byte> samples, int samplesPerPixel)
    {
        switch (samplesPerPixel)
        {
            case 1:
                for (int i = 0; i < row.Length; i++)
                {
                    samples[i] = Gray(row[i]);
                }

                break;
            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    samples[2 * i] = Gray(row[i]);
                    samples[(2 * i) + 1] = row[i].A;
                }

                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    Rgba32 p = row[i];
                    samples[3 * i] = p.R;
                    samples[(3 * i) + 1] = p.G;
                    samples[(3 * i) + 2] = p.B;
                }

                break;
            default:
                MemoryMarshal.AsBytes(row).CopyTo(samples);
                break;
        }
    }

    /// <summary>Writes one bit per pixel, MSB first, 1 for black (luminance below 128); the unused low bits of the last byte are 0.</summary>
    public static void PackBlackWhite(ReadOnlySpan<Rgba32> row, Span<byte> bits)
    {
        bits.Clear();
        for (int i = 0; i < row.Length; i++)
        {
            if (Gray(row[i]) < 128)
            {
                bits[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }
    }

    /// <summary>Writes the index of each pixel's colour (alpha ignored) in <paramref name="palette"/>, which holds every colour.</summary>
    public static void PackIndices(ReadOnlySpan<Rgba32> row, Span<byte> indices, Dictionary<uint, byte> palette)
    {
        ReadOnlySpan<uint> colors = MemoryMarshal.Cast<Rgba32, uint>(row);
        uint previous = 0;
        byte index = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            uint color = colors[i] | OpaqueAlpha;
            if (i == 0 || color != previous)
            {
                previous = color;
                index = palette[color];
            }

            indices[i] = index;
        }
    }

    /// <summary>
    /// The distinct colours of the pixels with alpha ignored, as packed <see cref="Rgba32"/> values made opaque mapped to
    /// their index in order of first appearance; <see langword="null"/> when there are more than 256.
    /// </summary>
    public static Dictionary<uint, byte>? Palette(ReadOnlySpan<Rgba32> pixels)
    {
        var palette = new Dictionary<uint, byte>();
        uint previous = 0;
        foreach (uint packed in MemoryMarshal.Cast<Rgba32, uint>(pixels))
        {
            uint color = packed | OpaqueAlpha;
            if (color == previous || palette.ContainsKey(color))
            {
                previous = color;
                continue;
            }

            if (palette.Count == 256)
            {
                return null;
            }

            palette.Add(color, (byte)palette.Count);
            previous = color;
        }

        return palette;
    }

    /// <summary>The grey value of a pixel: itself when R = G = B, otherwise BT.601 luminance as the PNG encoder computes it.</summary>
    public static byte Gray(Rgba32 p) =>
        p.R == p.G && p.G == p.B ? p.R : (byte)(((19595 * p.R) + (38470 * p.G) + (7471 * p.B) + 32768) >> 16);
}
