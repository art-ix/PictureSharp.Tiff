using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// TIFF LZW: MSB-first codes with "early change", and the old LSB-first codes of libtiff before 3.0. One instance
/// decodes every strip of an image: as in libtiff, the first strip that looks old-style switches the decoder for the
/// rest of the image. Table entries are indices, -1 standing for libtiff's null pointer.
/// </summary>
internal sealed class LzwDecoder
{
    private const int BitsMin = 9;
    private const int BitsMax = 12;
    private const int CodeClear = 256;
    private const int CodeEoi = 257;
    private const int CodeFirst = 258;

    // port: libtiff/tif_lzw.c CSIZE with LZW_COMPAT
    private const int CSize = (1 << BitsMax) - 1 + 1024;

    private readonly CodeEntry[] table = new CodeEntry[CSize];
    private bool compatSelected;
    private bool decodeSelected;

    // port: libtiff/tif_lzw.c LZWSetupDecode
    public LzwDecoder()
    {
        for (int code = 0; code < CSize; code++)
        {
            table[code] = code < 256
                ? new CodeEntry { Next = -1, Length = 1, FirstChar = (byte)code, Value = (byte)code, Repeated = true }
                : new CodeEntry { Next = -1 };
        }
    }

    // port: libtiff/tif_lzw.c LZWPreDecode: picks the decoder from the first two bytes of the strip.
    public void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length >= 2 && input[0] == 0 && (input[1] & 1) != 0 && !decodeSelected)
        {
            compatSelected = true;
        }

        decodeSelected = true;
        if (compatSelected)
        {
            DecodeCompat(input, output);
        }
        else
        {
            DecodeMsb(input, output);
        }
    }

    private static ImageFormatException Corrupt() => TiffDecoder.Error("The TIFF LZW data uses a code not yet in the table.");

    private static ImageFormatException Short() => TiffDecoder.Error("The TIFF LZW data ends before the strip or tile is complete.");

    // port: libtiff/tif_lzw.c GetNextCodeLZW with a 64-bit WordType. Returns false at libtiff's no_eoi.
    private static bool NextCode(ReadOnlySpan<byte> input, ref int bp, ref ulong nextdata, ref long nextbits, ref ulong bitsLeft, long nbits, long mask, out int code)
    {
        nextbits -= nbits;
        if (nextbits < 0)
        {
            if (bitsLeft >= 64)
            {
                uint codetmp = (uint)(nextdata << (int)-nextbits);
                nextdata = BinaryPrimitives.ReadUInt64BigEndian(input[bp..]);
                bp += 8;
                nextbits += 64;
                bitsLeft -= 64;
                code = (int)((codetmp | (nextdata >> (int)nextbits)) & (ulong)mask);
                return true;
            }

            if (bitsLeft < 8)
            {
                code = 0;
                return false;
            }

            nextdata = (nextdata << 8) | input[bp++];
            nextbits += 8;
            bitsLeft -= 8;
            if (nextbits < 0)
            {
                if (bitsLeft < 8)
                {
                    code = 0;
                    return false;
                }

                nextdata = (nextdata << 8) | input[bp++];
                nextbits += 8;
                bitsLeft -= 8;
            }
        }

        code = (int)((nextdata >> (int)nextbits) & (ulong)mask);
        return true;
    }

    // port: libtiff/tif_lzw.c LZWDecode for a whole strip in one call, so the restart state is not needed: a string
    // longer than the space left fills it with its prefix (too_short_buffer) and decoding ends.
    private void DecodeMsb(ReadOnlySpan<byte> input, Span<byte> output)
    {
        CodeEntry[] tab = table;
        int op = 0, occ = output.Length, bp = 0;
        ulong bitsLeft = (ulong)input.Length << 3, nextdata = 0;
        long nbits = BitsMin, nextbits = 0, nbitsmask = (1 << BitsMin) - 1;
        int freeEnt = -1, maxCode = (int)nbitsmask - 1, oldCode = 0;
        if (occ == 0)
        {
            return;
        }

        while (true)
        {
            if (!NextCode(input, ref bp, ref nextdata, ref nextbits, ref bitsLeft, nbits, nbitsmask, out int code))
            {
                throw Short();
            }

            if (code == CodeEoi)
            {
                break;
            }

            if (code == CodeClear)
            {
                freeEnt = CodeFirst;
                nbits = BitsMin;
                nbitsmask = (1 << BitsMin) - 1;
                maxCode = (int)nbitsmask - 1;
                do
                {
                    if (!NextCode(input, ref bp, ref nextdata, ref nextbits, ref bitsLeft, nbits, nbitsmask, out code))
                    {
                        throw Short();
                    }
                }
                while (code == CodeClear);

                if (code == CodeEoi)
                {
                    break;
                }

                if (code > CodeEoi)
                {
                    throw Corrupt();
                }

                output[op++] = (byte)code;
                occ--;
                oldCode = code;
                if (occ == 0)
                {
                    break;
                }

                continue;
            }

            // Add the new entry to the code table.
            ref CodeEntry old = ref tab[oldCode];
            byte value;
            if (code < 256)
            {
                if (code > freeEnt)
                {
                    throw Corrupt();
                }

                value = (byte)code;
            }
            else if (code >= freeEnt)
            {
                if (code != freeEnt)
                {
                    throw Corrupt();
                }

                value = old.FirstChar;
            }
            else
            {
                value = tab[code].FirstChar;
            }

            ref CodeEntry entry = ref tab[freeEnt];
            entry.Value = value;
            entry.Repeated = old.Repeated && old.Value == value;
            entry.Next = oldCode;
            entry.FirstChar = old.FirstChar;
            entry.Length = (ushort)(old.Length + 1);
            if (++freeEnt > maxCode)
            {
                if (++nbits > BitsMax)
                {
                    nbits = BitsMax;
                }

                nbitsmask = (1L << (int)nbits) - 1;
                maxCode = (int)nbitsmask - 1;
                if (freeEnt >= CSize)
                {
                    // Only EOI or CLEAR may follow; another code fails when it would add an entry.
                    freeEnt = -1;
                }
            }

            oldCode = code;
            if (code < 256)
            {
                output[op++] = (byte)code;
                if (--occ == 0)
                {
                    break;
                }

                continue;
            }

            // The code maps to a string, written in reverse.
            ref CodeEntry codep = ref tab[code];
            int len = codep.Length;
            if (len > occ)
            {
                WritePrefix(tab, code, output.Slice(op, occ));
                return;
            }

            if (codep.Repeated)
            {
                output.Slice(op, len).Fill(codep.Value);
            }
            else
            {
                int c = code;
                for (int tp = op + len - 1; tp >= op; tp--)
                {
                    output[tp] = tab[c].Value;
                    c = tab[c].Next;
                }
            }

            op += len;
            occ -= len;
            if (occ == 0)
            {
                break;
            }
        }

        if (occ > 0)
        {
            throw Short();
        }
    }

    // port: libtiff/tif_lzw.c GetNextCodeCompat inside NextCode: a strip that ends without EOI ends at its last code.
    private static int NextCodeCompat(ReadOnlySpan<byte> input, ref int bp, ref ulong nextdata, ref int nextbits, ref ulong bitsLeft, int nbits, int mask)
    {
        if (bitsLeft < (ulong)nbits)
        {
            return CodeEoi;
        }

        nextdata |= (ulong)input[bp++] << nextbits;
        nextbits += 8;
        if (nextbits < nbits)
        {
            nextdata |= (ulong)input[bp++] << nextbits;
            nextbits += 8;
        }

        int code = (int)(nextdata & (ulong)mask);
        nextdata >>= nbits;
        nextbits -= nbits;
        bitsLeft -= (ulong)nbits;
        return code;
    }

    // port: libtiff/tif_lzw.c LZWDecodeCompat, whole strip in one call like DecodeMsb.
    private void DecodeCompat(ReadOnlySpan<byte> input, Span<byte> output)
    {
        CodeEntry[] tab = table;
        int op = 0, occ = output.Length, bp = 0;
        ulong bitsLeft = (ulong)input.Length << 3, nextdata = 0;
        int nbits = BitsMin, nextbits = 0, nbitsmask = (1 << BitsMin) - 1;
        int freeEnt = -1, maxCode = nbitsmask - 1, oldCode = 0;
        while (occ > 0)
        {
            int code = NextCodeCompat(input, ref bp, ref nextdata, ref nextbits, ref bitsLeft, nbits, nbitsmask);
            if (code == CodeEoi)
            {
                break;
            }

            if (code == CodeClear)
            {
                do
                {
                    freeEnt = CodeFirst;
                    Array.Fill(tab, new CodeEntry { Next = -1 }, CodeFirst, CSize - CodeFirst);
                    nbits = BitsMin;
                    nbitsmask = (1 << BitsMin) - 1;
                    maxCode = nbitsmask;
                    code = NextCodeCompat(input, ref bp, ref nextdata, ref nextbits, ref bitsLeft, nbits, nbitsmask);
                }
                while (code == CodeClear);

                if (code == CodeEoi)
                {
                    break;
                }

                if (code > CodeClear)
                {
                    throw TiffDecoder.Error("The TIFF LZW table is corrupt.");
                }

                output[op++] = (byte)code;
                occ--;
                oldCode = code;
                continue;
            }

            // Add the new entry to the code table.
            if (freeEnt < 0 || freeEnt >= CSize)
            {
                throw TiffDecoder.Error("The TIFF LZW table is corrupt.");
            }

            ref CodeEntry entry = ref tab[freeEnt];
            entry.Next = oldCode;
            entry.FirstChar = tab[oldCode].FirstChar;
            entry.Length = (ushort)(tab[oldCode].Length + 1);
            entry.Value = code < freeEnt ? tab[code].FirstChar : entry.FirstChar;
            if (++freeEnt > maxCode)
            {
                if (++nbits > BitsMax)
                {
                    nbits = BitsMax;
                }

                nbitsmask = (1 << nbits) - 1;
                maxCode = nbitsmask;
            }

            oldCode = code;
            if (code < 256)
            {
                output[op++] = (byte)code;
                occ--;
                continue;
            }

            // The code maps to a string, written in reverse.
            int len = tab[code].Length;
            if (len == 0)
            {
                throw TiffDecoder.Error("The TIFF LZW data refers to an empty string.");
            }

            if (len > occ)
            {
                WritePrefix(tab, code, output.Slice(op, occ));
                return;
            }

            int c = code;
            for (int tp = op + len - 1; tp >= op && c >= 0; tp--)
            {
                output[tp] = tab[c].Value;
                c = tab[c].Next;
            }

            op += len;
            occ -= len;
        }

        if (occ > 0)
        {
            throw Short();
        }
    }

    // port: libtiff/tif_lzw.c too_short_buffer: walks back to the prefix that fits and writes it in reverse.
    private static void WritePrefix(CodeEntry[] tab, int code, Span<byte> output)
    {
        int c = code;
        while (c >= 0 && tab[c].Length > output.Length)
        {
            c = tab[c].Next;
        }

        for (int tp = output.Length - 1; tp >= 0; tp--)
        {
            if (c < 0)
            {
                throw TiffDecoder.Error("The TIFF LZW table is corrupt.");
            }

            output[tp] = tab[c].Value;
            c = tab[c].Next;
        }
    }

    private struct CodeEntry
    {
        public int Next;
        public ushort Length;
        public byte FirstChar;
        public byte Value;
        public bool Repeated;
    }
}
