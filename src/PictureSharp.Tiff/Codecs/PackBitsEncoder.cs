namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>PackBits compression of a strip, row by row so that no run crosses a row, byte for byte as libtiff writes it.</summary>
internal static class PackBitsEncoder
{
    private enum State
    {
        Base,
        Literal,
        Run,
        LiteralRun,
    }

    /// <summary>The largest output for <paramref name="length"/> input bytes: no byte costs more than two.</summary>
    public static long MaxLength(long length) => 2 * length;

    // port: libtiff/tif_packbits.c PackBitsEncodeChunk
    public static int Encode(ReadOnlySpan<byte> strip, int rowBytes, Span<byte> output)
    {
        int written = 0;
        for (int start = 0; start < strip.Length; start += rowBytes)
        {
            written += EncodeRow(strip.Slice(start, Math.Min(rowBytes, strip.Length - start)), output[written..]);
        }

        return written;
    }

    // port: libtiff/tif_packbits.c PackBitsEncode. The output always has room, so libtiff's buffer flushing is left out;
    // it does not change the bytes.
    private static int EncodeRow(ReadOnlySpan<byte> row, Span<byte> output)
    {
        int bp = 0, op = 0, lastLiteral = 0;
        State state = State.Base;
        while (bp < row.Length)
        {
            // Find the longest string of identical bytes.
            byte b = row[bp++];
            int n = 1;
            for (; bp < row.Length && row[bp] == b; bp++)
            {
                n++;
            }

            bool again;
            do
            {
                again = false;
                switch (state)
                {
                    case State.Base:
                    case State.Run:
                    case State.Literal when n > 1:
                        if (n > 1)
                        {
                            state = state == State.Literal ? State.LiteralRun : State.Run;
                            if (n > 128)
                            {
                                output[op++] = unchecked((byte)-127);
                                output[op++] = b;
                                n -= 128;
                                again = true;
                                break;
                            }

                            output[op++] = (byte)(1 - n);
                            output[op++] = b;
                        }
                        else
                        {
                            lastLiteral = op;
                            output[op++] = 0;
                            output[op++] = b;
                            state = State.Literal;
                        }

                        break;
                    case State.Literal:
                        // Extend the literal.
                        if (++output[lastLiteral] == 127)
                        {
                            state = State.Base;
                        }

                        output[op++] = b;
                        break;
                    case State.LiteralRun:
                        // A two-byte run between literals is cheaper as part of one literal.
                        if (n == 1 && output[op - 2] == 0xFF && output[lastLiteral] < 126)
                        {
                            output[lastLiteral] += 2;
                            state = output[lastLiteral] == 127 ? State.Base : State.Literal;
                            output[op - 2] = output[op - 1];
                        }
                        else
                        {
                            state = State.Run;
                        }

                        again = true;
                        break;
                }
            }
            while (again);
        }

        return op;
    }
}
