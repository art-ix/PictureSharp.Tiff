namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>NeXT 2-bit greyscale compression (32766): each row is literal, a literal span on white, or runs.</summary>
internal static class NeXTDecoder
{
    private const int LiteralRow = 0x00;
    private const int LiteralSpan = 0x40;

    // port: libtiff/tif_next.c NeXTDecode. Rows start white (0xFF); rows after the end of the data stay white, as in
    // libtiff, but a row cut short is an error. Runs are cut at the row's width in pixels.
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output, int rowBytes, int width)
    {
        output.Fill(0xFF);
        int bp = 0, cc = input.Length;
        for (int row = 0; cc > 0 && row + rowBytes <= output.Length; row += rowBytes)
        {
            int n = input[bp++];
            cc--;
            if (n == LiteralRow)
            {
                if (cc < rowBytes)
                {
                    throw NotEnoughData();
                }

                input.Slice(bp, rowBytes).CopyTo(output[row..]);
                bp += rowBytes;
                cc -= rowBytes;
            }
            else if (n == LiteralSpan)
            {
                if (cc < 4)
                {
                    throw NotEnoughData();
                }

                int offset = (input[bp] << 8) | input[bp + 1];
                n = (input[bp + 2] << 8) | input[bp + 3];
                if (cc < 4 + n || offset + n > rowBytes)
                {
                    throw NotEnoughData();
                }

                input.Slice(bp + 4, n).CopyTo(output[(row + offset)..]);
                bp += 4 + n;
                cc -= 4 + n;
            }
            else
            {
                // Runs of <grey:2><count:6> until the row is full.
                int pixels = 0, op = row;
                while (true)
                {
                    int grey = (n >> 6) & 3;
                    n &= 0x3F;
                    while (n-- > 0 && pixels < width && op < row + rowBytes)
                    {
                        int shift = 6 - ((pixels & 3) * 2);
                        output[op] = (byte)((pixels & 3) == 0 ? grey << 6 : output[op] | (grey << shift));
                        op += (pixels & 3) == 3 ? 1 : 0;
                        pixels++;
                    }

                    if (pixels >= width)
                    {
                        break;
                    }

                    if (op >= row + rowBytes)
                    {
                        throw TiffDecoder.Error("The TIFF NeXT data is invalid.");
                    }

                    if (cc == 0)
                    {
                        throw NotEnoughData();
                    }

                    n = input[bp++];
                    cc--;
                }
            }
        }
    }

    private static ImageFormatException NotEnoughData() => TiffDecoder.Error("The TIFF NeXT data ends before the row is complete.");
}
