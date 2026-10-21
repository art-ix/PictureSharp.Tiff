using PictureSharp.Formats.Tiff.Container;
using static PictureSharp.Formats.Tiff.Codecs.FaxTables;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// CCITT bilevel decoding: Modified Huffman RLE (2, and 32771 word-aligned), Group 3 1D and 2D (3, T4Options bit 0)
/// and Group 4 (4). Rows are decoded to white/black run lengths, then filled as bits, 1 for black. Like libtiff,
/// codes the tables do not expect end the row instead of failing; running out of data fails, except for Group 4
/// strips that already decoded a row.
/// </summary>
// port: libtiff/tif_fax3.c Fax3PreDecode, Fax3Decode1D, Fax3Decode2D, Fax4Decode, Fax3DecodeRLE and
// _TIFFFax3fillruns, with the tif_fax3.h macros as methods; runs are indices into int arrays.
internal sealed class FaxDecoder
{
    private const int Group3Option2D = 1;

    private readonly int compression;
    private readonly bool twoDimensional;
    private readonly bool reverse;
    private readonly int rowBytes;
    private readonly int rowPixels;
    private readonly int nruns;
    private int[] curruns;
    private int[] refruns;

    // port: libtiff/tif_fax3.c Fax3SetupState
    public FaxDecoder(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout)
    {
        compression = layout.Compression;
        uint options = compression switch
        {
            TiffLayout.CompressionCcittFax3 => ifd.GetUInt(data, TiffTag.T4Options, 0),
            TiffLayout.CompressionCcittFax4 => ifd.GetUInt(data, TiffTag.T6Options, 0),
            _ => 0,
        };
        twoDimensional = compression == TiffLayout.CompressionCcittFax4
            || (compression == TiffLayout.CompressionCcittFax3 && (options & Group3Option2D) != 0);

        // The decoder reads bits LSB first; FillOrder 1 data is reversed byte by byte as it is read.
        reverse = !layout.ReverseBits;
        rowBytes = (int)layout.RowBytes;
        rowPixels = layout.ChunkWidth;
        int runs = (int)Math.Min(((long)rowPixels + 1 + 31) / 32 * 32, int.MaxValue / 4);
        nruns = twoDimensional ? runs * 2 : runs;

        // Two spare entries absorb the unchecked writes libtiff makes into the next array of its single allocation.
        curruns = new int[nruns + 2];
        refruns = twoDimensional ? new int[nruns + 2] : [];
    }

    private enum Status
    {
        Done,
        Eof,
        Overflow,
    }

    public void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        output.Clear();

        // Corrupt data may read runs a previous strip left behind; cleared, every strip decodes the same on any thread.
        curruns.AsSpan().Clear();
        refruns.AsSpan().Clear();

        // port: libtiff/tif_fax3.c Fax3PreDecode: the reference line starts all white.
        if (twoDimensional)
        {
            refruns[0] = rowPixels;
            refruns[1] = 0;
        }

        var reader = new BitReader(input, reverse);
        int result = compression switch
        {
            TiffLayout.CompressionCcittRle or TiffLayout.CompressionCcittRlew => DecodeRle(ref reader, output),
            TiffLayout.CompressionCcittFax4 => Decode4(ref reader, output),
            _ => twoDimensional ? Decode2D(ref reader, output) : Decode1D(ref reader, output),
        };
        if (result < 0)
        {
            throw TiffDecoder.Error("The TIFF CCITT data is truncated or corrupt.");
        }
    }

    private int Decode1D(ref BitReader r, Span<byte> buf)
    {
        int eolcnt = 0;
        for (int at = 0; at + rowBytes <= buf.Length; at += rowBytes)
        {
            var row = new Row(curruns, refruns, rowPixels, nruns);
            if (!SyncEol(ref r, ref eolcnt))
            {
                if (Cleanup(ref row) == Status.Overflow)
                {
                    return -1;
                }

                Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
                return -1;
            }

            Status status = Expand1D(ref r, ref row, ref eolcnt);
            if (status == Status.Overflow)
            {
                return -1;
            }

            Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
            if (status == Status.Eof)
            {
                return -1;
            }
        }

        return 1;
    }

    private int Decode2D(ref BitReader r, Span<byte> buf)
    {
        int eolcnt = 0;
        for (int at = 0; at + rowBytes <= buf.Length; at += rowBytes)
        {
            var row = new Row(curruns, refruns, rowPixels, nruns);
            if (!SyncEol(ref r, ref eolcnt) || !r.Need8(1))
            {
                if (Cleanup(ref row) == Status.Overflow)
                {
                    return -1;
                }

                Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
                return -1;
            }

            bool is1D = r.Get(1) != 0;
            r.Clear(1);
            row.B1 = refruns[row.Pb++];
            Status status = is1D ? Expand1D(ref r, ref row, ref eolcnt) : Expand2D(ref r, ref row, ref eolcnt);
            if (status == Status.Overflow)
            {
                return -1;
            }

            Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
            if (status == Status.Eof)
            {
                return -1;
            }

            if (row.Pa < nruns && !row.SetValue(0))
            {
                return -1;
            }

            (curruns, refruns) = (refruns, curruns);
        }

        return 1;
    }

    private int Decode4(ref BitReader r, Span<byte> buf)
    {
        int eolcnt = 0, line = 0;
        for (int at = 0; at + rowBytes <= buf.Length; at += rowBytes)
        {
            var row = new Row(curruns, refruns, rowPixels, nruns);
            row.B1 = refruns[row.Pb++];
            Status status = Expand2D(ref r, ref row, ref eolcnt);
            if (status == Status.Overflow)
            {
                return -1;
            }

            if (((rowPixels + 7) >> 3) > buf.Length - at)
            {
                return -1;
            }

            Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
            if (status == Status.Eof || eolcnt != 0)
            {
                // The end-of-facsimile block, or data that stops early: keep the rows decoded so far.
                return line > 0 ? 1 : -1;
            }

            if (!row.SetValue(0))
            {
                return -1;
            }

            (curruns, refruns) = (refruns, curruns);
            line++;
        }

        return 1;
    }

    private int DecodeRle(ref BitReader r, Span<byte> buf)
    {
        int eolcnt = 0;
        for (int at = 0; at + rowBytes <= buf.Length; at += rowBytes)
        {
            var row = new Row(curruns, refruns, rowPixels, nruns);
            Status status = Expand1D(ref r, ref row, ref eolcnt);
            if (status == Status.Overflow)
            {
                return -1;
            }

            Fill(buf.Slice(at, rowBytes), curruns, row.Pa, rowPixels);
            if (status == Status.Eof)
            {
                return -1;
            }

            // Rows start on a byte (2) or 16-bit word (32771) boundary of the strip.
            if (compression == TiffLayout.CompressionCcittRle)
            {
                r.Clear(r.BitsAvail & 7);
            }
            else
            {
                r.Clear(r.BitsAvail & 15);
                if (r.BitsAvail == 0 && (r.Position & 1) != 0)
                {
                    r.Position++;
                }
            }
        }

        return 1;
    }

    // port: libtiff/tif_fax3.h SYNC_EOL: skips to the end of the next EOL code. Returns false at the end of the data.
    private static bool SyncEol(ref BitReader r, ref int eolcnt)
    {
        if (eolcnt == 0)
        {
            while (true)
            {
                if (!r.Need16(11))
                {
                    return false;
                }

                if (r.Get(11) == 0)
                {
                    break;
                }

                r.Clear(1);
            }
        }

        while (true)
        {
            if (!r.Need8(8))
            {
                return false;
            }

            if (r.Get(8) != 0)
            {
                break;
            }

            r.Clear(8);
        }

        while (r.Get(1) == 0)
        {
            r.Clear(1);
        }

        r.Clear(1);
        eolcnt = 0;
        return true;
    }

    // port: libtiff/tif_fax3.h EXPAND1D
    private static Status Expand1D(ref BitReader r, ref Row row, ref int eolcnt)
    {
        while (true)
        {
            Entry e;
            while (true)
            {
                if (!r.Lookup16(12, White, out e))
                {
                    goto Eof;
                }

                switch (e.State)
                {
                    case Eol:
                        eolcnt = 1;
                        goto Done;
                    case TermW:
                        if (!row.SetValue(e.Param))
                        {
                            return Status.Overflow;
                        }

                        goto DoneWhite;
                    case MakeUpW or MakeUp:
                        row.A0 += e.Param;
                        row.RunLength += e.Param;
                        break;
                    default:
                        goto Done;
                }
            }

        DoneWhite:
            if (row.A0 >= row.LastX)
            {
                goto Done;
            }

            while (true)
            {
                if (!r.Lookup16(13, Black, out e))
                {
                    goto Eof;
                }

                switch (e.State)
                {
                    case Eol:
                        eolcnt = 1;
                        goto Done;
                    case TermB:
                        if (!row.SetValue(e.Param))
                        {
                            return Status.Overflow;
                        }

                        goto DoneBlack;
                    case MakeUpB or MakeUp:
                        row.A0 += e.Param;
                        row.RunLength += e.Param;
                        break;
                    default:
                        goto Done;
                }
            }

        DoneBlack:
            if (row.A0 >= row.LastX)
            {
                goto Done;
            }

            if (row.ThisRun[row.Pa - 1] == 0 && row.ThisRun[row.Pa - 2] == 0)
            {
                row.Pa -= 2;
            }
        }

    Eof:
        return Cleanup(ref row) == Status.Overflow ? Status.Overflow : Status.Eof;
    Done:
        return Cleanup(ref row);
    }

    // port: libtiff/tif_fax3.h EXPAND2D with CHECK_b1
    private static Status Expand2D(ref BitReader r, ref Row row, ref int eolcnt)
    {
        int[] refs = row.RefRuns;
        Entry e;
        while (row.A0 < row.LastX)
        {
            if (row.Pa >= row.NRuns)
            {
                return Status.Overflow;
            }

            if (!r.Lookup8(7, Main, out e))
            {
                goto Eof;
            }

            switch (e.State)
            {
                case Pass:
                    if (!row.CheckB1())
                    {
                        return Status.Overflow;
                    }

                    if (row.Pb + 1 >= row.NRuns)
                    {
                        return Status.Overflow;
                    }

                    row.B1 += refs[row.Pb++];
                    row.RunLength += row.B1 - row.A0;
                    row.A0 = row.B1;
                    row.B1 += refs[row.Pb++];
                    break;
                case Horiz:
                    bool blackFirst = (row.Pa & 1) != 0;
                    for (int run = 0; run < 2; run++)
                    {
                        bool black = blackFirst == (run == 0);
                        while (true)
                        {
                            if (!r.Lookup16(black ? 13 : 12, black ? Black : White, out e))
                            {
                                goto Eof;
                            }

                            if (e.State == (black ? TermB : TermW))
                            {
                                if (!row.SetValue(e.Param))
                                {
                                    return Status.Overflow;
                                }

                                break;
                            }

                            if (e.State == (black ? MakeUpB : MakeUpW) || e.State == MakeUp)
                            {
                                row.A0 += e.Param;
                                row.RunLength += e.Param;
                                continue;
                            }

                            goto Eol2D;
                        }
                    }

                    if (!row.CheckB1())
                    {
                        return Status.Overflow;
                    }

                    break;
                case V0 or VR:
                    if (!row.CheckB1() || !row.SetValue(row.B1 - row.A0 + (e.State == VR ? e.Param : 0)) || row.Pb >= row.NRuns)
                    {
                        return Status.Overflow;
                    }

                    row.B1 += refs[row.Pb++];
                    break;
                case VL:
                    if (!row.CheckB1())
                    {
                        return Status.Overflow;
                    }

                    if (row.B1 < row.A0 + e.Param)
                    {
                        goto Eol2D;
                    }

                    if (!row.SetValue(row.B1 - row.A0 - e.Param) || row.Pb == 0)
                    {
                        return Status.Overflow;
                    }

                    row.B1 -= refs[--row.Pb];
                    break;
                case Ext:
                    row.ThisRun[row.Pa++] = row.LastX - row.A0;
                    goto Eol2D;
                case Eol:
                    row.ThisRun[row.Pa++] = row.LastX - row.A0;
                    if (!r.Need8(4))
                    {
                        goto Eof;
                    }

                    r.Clear(4);
                    eolcnt = 1;
                    goto Eol2D;
                default:
                    goto Eol2D;
            }
        }

        if (row.RunLength != 0)
        {
            if (row.RunLength + row.A0 < row.LastX)
            {
                // A final V0 is expected.
                if (!r.Need8(1))
                {
                    goto Eof;
                }

                if (r.Get(1) == 0)
                {
                    goto Eol2D;
                }

                r.Clear(1);
            }

            if (!row.SetValue(0))
            {
                return Status.Overflow;
            }
        }

    Eol2D:
        return Cleanup(ref row);
    Eof:
        return Cleanup(ref row) == Status.Overflow ? Status.Overflow : Status.Eof;
    }

    // port: libtiff/tif_fax3.h CLEANUP_RUNS: makes the runs add up to the row width.
    private static Status Cleanup(ref Row row)
    {
        if (row.RunLength != 0 && !row.SetValue(0))
        {
            return Status.Overflow;
        }

        if (row.A0 != row.LastX)
        {
            while (row.A0 > row.LastX && row.Pa > 0)
            {
                row.A0 -= row.ThisRun[--row.Pa];
            }

            if (row.A0 < row.LastX)
            {
                if (row.A0 < 0)
                {
                    row.A0 = 0;
                }

                if ((row.Pa & 1) != 0 && !row.SetValue(0))
                {
                    return Status.Overflow;
                }

                if (!row.SetValue(row.LastX - row.A0))
                {
                    return Status.Overflow;
                }
            }
            else if (row.A0 > row.LastX && (!row.SetValue(row.LastX) || !row.SetValue(0)))
            {
                return Status.Overflow;
            }
        }

        return Status.Done;
    }

    // port: libtiff/tif_fax3.c _TIFFFax3fillruns: white then black runs; black bits are set, runs past the row are
    // cut in the run array too, which the next row reads as its reference.
    private static void Fill(Span<byte> buf, int[] runs, int erun, int lastx)
    {
        if ((erun & 1) != 0)
        {
            runs[erun++] = 0;
        }

        uint x = 0, last = (uint)lastx;
        for (int i = 0; i < erun; i += 2)
        {
            uint run = (uint)runs[i];
            if (x + run > last || run > last)
            {
                run = last - x;
                runs[i] = (int)run;
            }

            x += run;
            run = (uint)runs[i + 1];
            if (x + run > last || run > last)
            {
                run = last - x;
                runs[i + 1] = (int)run;
            }

            for (uint end = x + run; x < end; x++)
            {
                buf[(int)(x >> 3)] |= (byte)(0x80 >> (int)(x & 7));
            }
        }
    }

    /// <summary>The run arrays and positions of the row being decoded (libtiff's a0, RunLength, pa, pb and b1).</summary>
    private ref struct Row(int[] thisRun, int[] refRuns, int lastX, int nruns)
    {
        public readonly int[] ThisRun = thisRun;
        public readonly int[] RefRuns = refRuns;
        public readonly int LastX = lastX;
        public readonly int NRuns = nruns;
        public int A0;
        public int RunLength;
        public int Pa;
        public int Pb;
        public int B1;

        // port: libtiff/tif_fax3.h SETVALUE; false at libtiff's buffer overflow error.
        public bool SetValue(int x)
        {
            if (Pa >= NRuns)
            {
                return false;
            }

            ThisRun[Pa++] = RunLength + x;
            A0 += x;
            RunLength = 0;
            return true;
        }

        // port: libtiff/tif_fax3.h CHECK_b1
        public bool CheckB1()
        {
            if (Pa != 0)
            {
                while (B1 <= A0 && B1 < LastX)
                {
                    if (Pb + 1 >= NRuns)
                    {
                        return false;
                    }

                    B1 += RefRuns[Pb] + RefRuns[Pb + 1];
                    Pb += 2;
                }
            }

            return true;
        }
    }

    /// <summary>libtiff's BitAcc/BitsAvail input: bits are taken LSB first, padding the last byte with zeros.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> input, bool reverse)
    {
        private readonly ReadOnlySpan<byte> input = input;
        private uint acc;

        public int BitsAvail;
        public int Position;

        public readonly uint Get(int n) => acc & ((1u << n) - 1);

        public void Clear(int n)
        {
            BitsAvail -= n;
            acc >>= n;
        }

        // port: libtiff/tif_fax3.h NeedBits8
        public bool Need8(int n)
        {
            if (BitsAvail < n)
            {
                if (Position >= input.Length)
                {
                    if (BitsAvail == 0)
                    {
                        return false;
                    }

                    BitsAvail = n;
                }
                else
                {
                    acc |= (uint)Next() << BitsAvail;
                    BitsAvail += 8;
                }
            }

            return true;
        }

        // port: libtiff/tif_fax3.h NeedBits16
        public bool Need16(int n)
        {
            if (BitsAvail < n)
            {
                if (Position >= input.Length)
                {
                    if (BitsAvail == 0)
                    {
                        return false;
                    }

                    BitsAvail = n;
                }
                else
                {
                    acc |= (uint)Next() << BitsAvail;
                    if ((BitsAvail += 8) < n)
                    {
                        if (Position >= input.Length)
                        {
                            BitsAvail = n;
                        }
                        else
                        {
                            acc |= (uint)Next() << BitsAvail;
                            BitsAvail += 8;
                        }
                    }
                }
            }

            return true;
        }

        public bool Lookup8(int width, Entry[] table, out Entry entry) => Lookup(Need8(width), width, table, out entry);

        public bool Lookup16(int width, Entry[] table, out Entry entry) => Lookup(Need16(width), width, table, out entry);

        private bool Lookup(bool available, int width, Entry[] table, out Entry entry)
        {
            if (!available)
            {
                entry = default;
                return false;
            }

            entry = table[Get(width)];
            Clear(entry.Width);
            return true;
        }

        private byte Next()
        {
            byte b = input[Position++];
            if (!reverse)
            {
                return b;
            }

            uint v = b;
            v = ((v & 0xF0) >> 4) | ((v & 0x0F) << 4);
            v = ((v & 0xCC) >> 2) | ((v & 0x33) << 2);
            return (byte)(((v & 0xAA) >> 1) | ((v & 0x55) << 1));
        }
    }
}
