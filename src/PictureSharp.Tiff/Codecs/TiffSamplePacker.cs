using System.Runtime.InteropServices;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Turns <see cref="Rgba32"/> rows into contiguous 8-bit TIFF samples: grey (the luminance of colour pixels), grey and
/// alpha, RGB, or RGBA with the straight alpha as unassociated alpha.
/// </summary>
internal static class TiffSamplePacker
{
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

    /// <summary>The grey value of a pixel: itself when R = G = B, otherwise BT.601 luminance as the PNG encoder computes it.</summary>
    public static byte Gray(Rgba32 p) =>
        p.R == p.G && p.G == p.B ? p.R : (byte)(((19595 * p.R) + (38470 * p.G) + (7471 * p.B) + 32768) >> 16);
}
