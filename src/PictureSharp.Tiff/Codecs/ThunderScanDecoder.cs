namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>ThunderScan 4-bit greyscale compression (32809): runs, 2- and 3-bit deltas and raw values per byte.</summary>
internal static class ThunderScanDecoder
{
    private const int Run = 0x00;
    private const int TwoBitDeltas = 0x40;
    private const int ThreeBitDeltas = 0x80;
    private const int TwoBitSkip = 2;
    private const int ThreeBitSkip = 4;

    private static readonly int[] TwoBit = [0, 1, 0, -1];
    private static readonly int[] ThreeBit = [0, 1, 2, 3, 0, -3, -2, -1];

    // port: libtiff/tif_thunder.c ThunderDecodeRow and ThunderDecode, row by row from one stream of codes; a row
    // with fewer or more pixels than the width is an error, as in libtiff. A run that ends exactly at the end of the
    // row stores none of its pixels (libtiff's npixels < maxpixels); libtiff leaves whatever its buffer held there,
    // the plugin zeros.
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output, int rowBytes, int width)
    {
        output.Clear();
        int bp = 0;
        for (int row = 0; row + rowBytes <= output.Length; row += rowBytes)
        {
            Span<byte> op = output.Slice(row, rowBytes);
            int o = 0, pixels = 0;
            int last = 0;
            while (bp < input.Length && pixels < width)
            {
                int n = input[bp++];
                switch (n & 0xC0)
                {
                    case Run:
                        n &= 0x3F;
                        if ((pixels & 1) != 0)
                        {
                            op[o] |= (byte)last;
                            last = op[o++];
                            pixels++;
                            n--;
                        }
                        else
                        {
                            last |= last << 4;
                        }

                        pixels += n;
                        if (pixels < width)
                        {
                            for (; n > 0; n -= 2)
                            {
                                op[o++] = (byte)last;
                            }
                        }

                        if (n == -1)
                        {
                            op[--o] &= 0xF0;
                        }

                        last &= 0xF;
                        break;
                    case TwoBitDeltas:
                        Delta(op, ref o, ref pixels, ref last, width, (n >> 4) & 3, TwoBitSkip, TwoBit);
                        Delta(op, ref o, ref pixels, ref last, width, (n >> 2) & 3, TwoBitSkip, TwoBit);
                        Delta(op, ref o, ref pixels, ref last, width, n & 3, TwoBitSkip, TwoBit);
                        break;
                    case ThreeBitDeltas:
                        Delta(op, ref o, ref pixels, ref last, width, (n >> 3) & 7, ThreeBitSkip, ThreeBit);
                        Delta(op, ref o, ref pixels, ref last, width, n & 7, ThreeBitSkip, ThreeBit);
                        break;
                    default:
                        Set(op, ref o, ref pixels, ref last, width, n);
                        break;
                }
            }

            if (pixels != width)
            {
                throw TiffDecoder.Error($"The TIFF ThunderScan data has {(pixels < width ? "too few" : "too many")} pixels for a row.");
            }
        }
    }

    private static void Delta(Span<byte> op, ref int o, ref int pixels, ref int last, int width, int delta, int skip, int[] deltas)
    {
        if (delta != skip)
        {
            Set(op, ref o, ref pixels, ref last, width, last + deltas[delta]);
        }
    }

    // SETPIXEL: the high nibble first; pixels past the width are counted but not stored.
    private static void Set(Span<byte> op, ref int o, ref int pixels, ref int last, int width, int value)
    {
        last = value & 0xF;
        if (pixels < width)
        {
            if ((pixels++ & 1) != 0)
            {
                op[o++] |= (byte)last;
            }
            else
            {
                op[o] = (byte)(last << 4);
            }
        }
    }
}
