using System.Numerics;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// CCITT Group 4 (T.6, Compression 4) strips of 1-bit rows, MSB first with 1 = black: each row is coded against the one
/// above it, the first against a white row, and the strip ends with EOFB padded to a byte, as libtiff writes it.
/// </summary>
internal sealed class Fax4Encoder
{
    private const int Eol = 0x001;

    private static readonly (ushort Code, byte Length) HorizontalCode = (0x1, 3);
    private static readonly (ushort Code, byte Length) PassCode = (0x1, 4);
    private static readonly (ushort Code, byte Length)[] VerticalCodes =
        [(0x03, 7), (0x03, 6), (0x03, 3), (0x1, 1), (0x2, 3), (0x02, 6), (0x02, 7)];

    private byte[] output = new byte[4096];
    private byte[] white = [];
    private int length;
    private int data;
    private int bit;

    // port: libtiff/tif_fax3.c Fax3PreEncode (white reference line), Fax4Encode and Fax4PostEncode.
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> strip, int rowBytes, int width)
    {
        if (white.Length != rowBytes)
        {
            white = new byte[rowBytes];
        }

        (length, data, bit) = (0, 0, 8);
        ReadOnlySpan<byte> reference = white;
        for (int row = 0; row < strip.Length; row += rowBytes)
        {
            ReadOnlySpan<byte> line = strip.Slice(row, rowBytes);
            EncodeRow(line, reference, width);
            reference = line;
        }

        PutBits(Eol, 12);
        PutBits(Eol, 12);
        if (bit != 8)
        {
            Flush();
        }

        return output.AsSpan(0, length);
    }

    // port: libtiff/tif_fax3.c Fax3Encode2DRow
    private void EncodeRow(ReadOnlySpan<byte> bp, ReadOnlySpan<byte> rp, int bits)
    {
        int a0 = 0;
        int a1 = Pixel(bp, 0) != 0 ? 0 : FindDiff(bp, 0, bits, 0);
        int b1 = Pixel(rp, 0) != 0 ? 0 : FindDiff(rp, 0, bits, 0);
        while (true)
        {
            int b2 = FindDiff2(rp, b1, bits);
            if (b2 >= a1)
            {
                int d = b1 - a1;
                if (d is < -3 or > 3)
                {
                    // Horizontal mode.
                    int a2 = FindDiff2(bp, a1, bits);
                    Put(HorizontalCode);
                    if (a0 + a1 == 0 || Pixel(bp, a0) == 0)
                    {
                        PutSpan(a1 - a0, FaxTables.WhiteCodes);
                        PutSpan(a2 - a1, FaxTables.BlackCodes);
                    }
                    else
                    {
                        PutSpan(a1 - a0, FaxTables.BlackCodes);
                        PutSpan(a2 - a1, FaxTables.WhiteCodes);
                    }

                    a0 = a2;
                }
                else
                {
                    // Vertical mode.
                    Put(VerticalCodes[d + 3]);
                    a0 = a1;
                }
            }
            else
            {
                // Pass mode.
                Put(PassCode);
                a0 = b2;
            }

            if (a0 >= bits)
            {
                break;
            }

            int color = Pixel(bp, a0);
            a1 = FindDiff(bp, a0, bits, color);
            b1 = FindDiff(rp, a0, bits, color ^ 1);
            b1 = FindDiff(rp, b1, bits, color);
        }
    }

    // port: libtiff/tif_fax3.c putspan
    private void PutSpan(int span, (ushort Code, byte Length)[] table)
    {
        while (span >= 2624)
        {
            (ushort code, byte bits) = table[63 + (2560 >> 6)];
            PutBits(code, bits);
            span -= 2560;
        }

        if (span >= 64)
        {
            (ushort code, byte bits) = table[63 + (span >> 6)];
            PutBits(code, bits);
            span -= 64 * (span >> 6);
        }

        PutBits(table[span].Code, table[span].Length);
    }

    private void Put((ushort Code, byte Length) entry) => PutBits(entry.Code, entry.Length);

    // port: libtiff/tif_fax3.c _PutBits
    private void PutBits(int bits, int count)
    {
        while (count > bit)
        {
            data |= bits >> (count - bit);
            count -= bit;
            Flush();
        }

        data |= (bits & ((1 << count) - 1)) << (bit - count);
        bit -= count;
        if (bit == 0)
        {
            Flush();
        }
    }

    // port: libtiff/tif_fax3.c _FlushBits
    private void Flush()
    {
        if (length == output.Length)
        {
            Array.Resize(ref output, output.Length * 2);
        }

        output[length++] = (byte)data;
        (data, bit) = (0, 8);
    }

    private static int Pixel(ReadOnlySpan<byte> line, int x) => (line[x >> 3] >> (7 - (x & 7))) & 1;

    /// <summary>The offset of the first bit in [<paramref name="bs"/>, <paramref name="be"/>) that differs from <paramref name="color"/>, or <paramref name="be"/>.</summary>
    private static int FindDiff(ReadOnlySpan<byte> line, int bs, int be, int color) => bs + FindSpan(line, bs, be, color);

    /// <summary><see cref="FindDiff"/> from the colour of the start bit, which may be at or past the end.</summary>
    private static int FindDiff2(ReadOnlySpan<byte> line, int bs, int be) => bs < be ? FindDiff(line, bs, be, Pixel(line, bs)) : be;

    // port: libtiff/tif_fax3.c find0span and find1span, without the word-at-a-time scan (same result); the zeroruns
    // and oneruns tables are leading-zero counts.
    private static int FindSpan(ReadOnlySpan<byte> line, int bs, int be, int color)
    {
        int bits = be - bs, span, n;
        int i = bs >> 3;
        int full = color == 0 ? 0x00 : 0xFF;
        if (bits > 0 && (n = bs & 7) != 0)
        {
            span = Run(line[i] << n, color);
            span = Math.Min(Math.Min(span, 8 - n), bits);
            if (n + span < 8)
            {
                return span;
            }

            bits -= span;
            i++;
        }
        else
        {
            span = 0;
        }

        while (bits >= 8)
        {
            if (line[i] != full)
            {
                return span + Run(line[i], color);
            }

            span += 8;
            bits -= 8;
            i++;
        }

        if (bits > 0)
        {
            span += Math.Min(Run(line[i], color), bits);
        }

        return span;
    }

    /// <summary>The number of leading bits of the byte that equal <paramref name="color"/>.</summary>
    private static int Run(int value, int color) =>
        BitOperations.LeadingZeroCount(((uint)((color == 0 ? value : ~value) & 0xFF) << 24) | 0x00FFFFFF);
}
