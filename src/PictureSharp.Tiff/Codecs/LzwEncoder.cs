namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// TIFF LZW compression: MSB-first codes with "early change", a hashed string table and libtiff's adaptive reset, so the
/// output is byte for byte what libtiff writes. One instance compresses every strip of an image.
/// </summary>
internal sealed class LzwEncoder
{
    private const int BitsMin = 9;
    private const int BitsMax = 12;
    private const int CodeClear = 256;
    private const int CodeEoi = 257;
    private const int CodeFirst = 258;
    private const int CodeMax = (1 << BitsMax) - 1;

    // port: libtiff/tif_lzw.c HSIZE (91% occupancy), HSHIFT and CHECK_GAP
    private const int HSize = 9001;
    private const int HShift = 13 - 8;
    private const int CheckGap = 10000;

    private readonly int[] hashes = new int[HSize];
    private readonly ushort[] codes = new ushort[HSize];

    /// <summary>An upper bound of the output for <paramref name="length"/> input bytes: at most a code and a clear code per byte.</summary>
    public static long MaxLength(long length) => (3 * length) + 8;

    // port: libtiff/tif_lzw.c LZWPreEncode, LZWEncode and LZWPostEncode for one whole strip. The output always has room,
    // so libtiff's buffer flushing is left out; it does not change the bytes.
    public int Encode(ReadOnlySpan<byte> data, Span<byte> output)
    {
        int nbits = BitsMin, maxCode = MaxCode(BitsMin), freeEnt = CodeFirst;
        ulong nextData = 0;
        int nextBits = 0, op = 0;
        long inCount = 0, outCount = 0, checkpoint = CheckGap, ratio = 0;
        hashes.AsSpan().Fill(-1);

        PutNextCode(output, CodeClear);
        int ent = data[0];
        inCount++;
        for (int i = 1; i < data.Length; i++)
        {
            int c = data[i];
            inCount++;
            int fcode = (c << BitsMax) + ent;
            int h = (c << HShift) ^ ent;
            if (hashes[h] == fcode)
            {
                ent = codes[h];
                continue;
            }

            if (hashes[h] >= 0)
            {
                // Primary hash failed, check secondary hash.
                int disp = h == 0 ? 1 : HSize - h;
                bool hit = false;
                do
                {
                    if ((h -= disp) < 0)
                    {
                        h += HSize;
                    }

                    if (hashes[h] == fcode)
                    {
                        ent = codes[h];
                        hit = true;
                        break;
                    }
                }
                while (hashes[h] >= 0);
                if (hit)
                {
                    continue;
                }
            }

            // New entry: emit the code and add the string to the table.
            PutNextCode(output, ent);
            ent = c;
            codes[h] = (ushort)freeEnt++;
            hashes[h] = fcode;
            if (freeEnt == CodeMax - 1)
            {
                Reset(output);
            }
            else if (freeEnt > maxCode)
            {
                nbits++;
                maxCode = MaxCode(nbits);
            }
            else if (inCount >= checkpoint)
            {
                // Clear the table when the compression ratio (24.8 fixed point) stops improving.
                checkpoint = inCount + CheckGap;
                long rat;
                if (inCount > 0x007fffff)
                {
                    rat = outCount >> 8;
                    rat = rat == 0 ? 0x7fffffff : inCount / rat;
                }
                else
                {
                    rat = (inCount << 8) / outCount;
                }

                if (rat <= ratio)
                {
                    Reset(output);
                }
                else
                {
                    ratio = rat;
                }
            }
        }

        // Flush the last string and end the strip.
        PutNextCode(output, ent);
        freeEnt++;
        if (freeEnt == CodeMax - 1)
        {
            PutNextCode(output, CodeClear);
            nbits = BitsMin;
        }
        else if (freeEnt > maxCode)
        {
            nbits++;
        }

        PutNextCode(output, CodeEoi);
        if (nextBits > 0)
        {
            output[op++] = (byte)(nextData << (8 - nextBits));
        }

        return op;

        void Reset(Span<byte> output)
        {
            hashes.AsSpan().Fill(-1);
            ratio = 0;
            inCount = 0;
            outCount = 0;
            freeEnt = CodeFirst;
            PutNextCode(output, CodeClear);
            nbits = BitsMin;
            maxCode = MaxCode(BitsMin);
        }

        // port: libtiff/tif_lzw.c PutNextCode
        void PutNextCode(Span<byte> output, int code)
        {
            nextData = (nextData << nbits) | (uint)code;
            nextBits += nbits;
            output[op++] = (byte)(nextData >> (nextBits - 8));
            nextBits -= 8;
            if (nextBits >= 8)
            {
                output[op++] = (byte)(nextData >> (nextBits - 8));
                nextBits -= 8;
            }

            outCount += nbits;
        }
    }

    private static int MaxCode(int bits) => (1 << bits) - 1;
}
