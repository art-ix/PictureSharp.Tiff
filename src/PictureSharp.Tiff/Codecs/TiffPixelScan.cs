using System.Numerics;
using System.Runtime.InteropServices;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// The pixel properties the encoder's automatic colour type depends on, checked with <see cref="Vector{T}"/> over the
/// pixels as 32-bit words (R in the low byte on little-endian machines, which the vector path requires).
/// </summary>
internal static class TiffPixelScan
{
    // How many pixels pass between checks for an early stop.
    private const int Block = 4096;

    /// <summary>
    /// Whether every pixel is opaque, has R = G = B, and has R of 0 or 255. Stops as soon as each property asked for
    /// with <paramref name="opaque"/> and <paramref name="gray"/> is known to be false, since the caller's answer can no
    /// longer change; the other results are then incomplete.
    /// </summary>
    public static (bool Opaque, bool Gray, bool Bilevel) Scan(ReadOnlySpan<Rgba32> pixels, bool opaque, bool gray)
    {
        (bool allOpaque, bool allGray, bool allBilevel) = (true, true, true);
        int start = 0;
        if (BitConverter.IsLittleEndian && Vector.IsHardwareAccelerated)
        {
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<Rgba32, uint>(pixels);
            var alphaBits = new Vector<uint>(0x00FFFFFFu);
            var lowWord = new Vector<uint>(0xFFFFu);
            var red = new Vector<uint>(0xFFu);
            int n = Vector<uint>.Count;
            while (start + n <= words.Length && !Stopped(opaque, gray, allOpaque, allGray))
            {
                Vector<uint> opaqueMask = Vector<uint>.AllBitsSet, grayMask = Vector<uint>.AllBitsSet, bilevelMask = Vector<uint>.AllBitsSet;
                int end = Math.Min(words.Length - n, start + Block);
                for (; start <= end; start += n)
                {
                    var v = new Vector<uint>(words.Slice(start, n));
                    opaqueMask &= Vector.Equals(v | alphaBits, Vector<uint>.AllBitsSet);
                    grayMask &= Vector.Equals((v ^ (v >> 8)) & lowWord, Vector<uint>.Zero);
                    Vector<uint> r = v & red;
                    bilevelMask &= Vector.Equals(r, Vector<uint>.Zero) | Vector.Equals(r, red);
                }

                allOpaque &= opaqueMask == Vector<uint>.AllBitsSet;
                allGray &= grayMask == Vector<uint>.AllBitsSet;
                allBilevel &= bilevelMask == Vector<uint>.AllBitsSet;
            }
        }

        for (int i = start; i < pixels.Length && !Stopped(opaque, gray, allOpaque, allGray); i++)
        {
            Rgba32 p = pixels[i];
            allOpaque &= p.A == 255;
            allGray &= p.R == p.G && p.G == p.B;
            allBilevel &= p.R is 0 or 255;
        }

        return (allOpaque, allGray, allBilevel);
    }

    private static bool Stopped(bool opaque, bool gray, bool allOpaque, bool allGray) =>
        (!opaque || !allOpaque) && (!gray || !allGray);
}
