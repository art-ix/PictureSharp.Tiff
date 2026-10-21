using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>TIFF predictor 2: each sample is stored as the difference from the same sample of the pixel to its left.</summary>
internal static class HorizontalPredictor
{
    /// <summary>
    /// Undoes the differencing row by row. <paramref name="stride"/> is the number of samples per pixel in the row
    /// (1 for a separate plane).
    /// </summary>
    // port: libtiff/tif_predict.c PredictorDecodeTile with horAcc8, horAcc16, horAcc32, swabHorAcc16 and swabHorAcc32.
    // Samples are summed in the file's byte order instead of swapping the buffer first; the result is the same.
    public static void Decode(Span<byte> chunk, int rowBytes, int stride, int bits, bool bigEndian)
    {
        for (int start = 0; start + rowBytes <= chunk.Length; start += rowBytes)
        {
            Span<byte> row = chunk.Slice(start, rowBytes);
            if (bits == 8)
            {
                for (int i = stride; i < row.Length; i++)
                {
                    row[i] += row[i - stride];
                }
            }
            else if (bits == 32)
            {
                int words = row.Length / 4;
                for (int i = stride; i < words; i++)
                {
                    Span<byte> current = row.Slice(i * 4, 4);
                    ReadOnlySpan<byte> left = row.Slice((i - stride) * 4, 4);
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(current, BinaryPrimitives.ReadUInt32BigEndian(current) + BinaryPrimitives.ReadUInt32BigEndian(left));
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(current, BinaryPrimitives.ReadUInt32LittleEndian(current) + BinaryPrimitives.ReadUInt32LittleEndian(left));
                    }
                }
            }
            else
            {
                int words = row.Length / 2;
                for (int i = stride; i < words; i++)
                {
                    Span<byte> current = row.Slice(i * 2, 2);
                    ReadOnlySpan<byte> left = row.Slice((i - stride) * 2, 2);
                    if (bigEndian)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(current, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(current) + BinaryPrimitives.ReadUInt16BigEndian(left)));
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(current, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(current) + BinaryPrimitives.ReadUInt16LittleEndian(left)));
                    }
                }
            }
        }
    }

    /// <summary>Applies the differencing to 8-bit samples, row by row; the inverse of <see cref="Decode"/>.</summary>
    // port: libtiff/tif_predict.c PredictorEncodeTile with horDiff8. Each sample loses the original sample to its
    // left, so the row is walked from the end.
    public static void Encode8(Span<byte> chunk, int rowBytes, int stride)
    {
        for (int start = 0; start < chunk.Length; start += rowBytes)
        {
            Span<byte> row = chunk.Slice(start, rowBytes);
            for (int i = row.Length - 1; i >= stride; i--)
            {
                row[i] -= row[i - stride];
            }
        }
    }
}
