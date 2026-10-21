namespace PictureSharp.Formats.Tiff.Codecs;

internal static class PackBitsDecoder
{
    // port: libtiff/tif_packbits.c PackBitsDecode. Runs past the end of the output are cut; input that ends before
    // the output is full is an error.
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int bp = 0, op = 0;
        int cc = input.Length, occ = output.Length;
        while (cc > 0 && occ > 0)
        {
            int n = (sbyte)input[bp++];
            cc--;
            if (n < 0)
            {
                // Replicate the next byte -n + 1 times.
                if (n == -128)
                {
                    continue;
                }

                n = -n + 1;
                if (occ < n)
                {
                    n = occ;
                }

                if (cc == 0)
                {
                    break;
                }

                occ -= n;
                byte b = input[bp++];
                cc--;
                output.Slice(op, n).Fill(b);
                op += n;
            }
            else
            {
                // Copy the next n + 1 bytes literally.
                if (occ < n + 1)
                {
                    n = occ - 1;
                }

                if (cc < n + 1)
                {
                    break;
                }

                n++;
                input.Slice(bp, n).CopyTo(output[op..]);
                op += n;
                occ -= n;
                bp += n;
                cc -= n;
            }
        }

        if (occ > 0)
        {
            throw TiffDecoder.Error("The TIFF PackBits data ends before the strip or tile is complete.");
        }
    }
}
