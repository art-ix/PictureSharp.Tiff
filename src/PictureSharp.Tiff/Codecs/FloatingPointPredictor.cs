using System.Buffers;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// TIFF predictor 3: each row stores the bytes of its samples split into planes, most significant first, and
/// differenced byte by byte.
/// </summary>
internal static class FloatingPointPredictor
{
    // port: libtiff/tif_predict.c fpAcc, row by row as PredictorDecodeTile calls it. The bytes are reassembled most
    // significant first (the WORDS_BIGENDIAN branch), so the samples come out big-endian whatever the file order.
    public static void Decode(Span<byte> chunk, int rowBytes, int stride, int bytesPerSample)
    {
        byte[] tmp = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            int words = rowBytes / bytesPerSample;
            for (int start = 0; start + rowBytes <= chunk.Length; start += rowBytes)
            {
                Span<byte> row = chunk.Slice(start, rowBytes);
                for (int i = stride; i < row.Length; i++)
                {
                    row[i] += row[i - stride];
                }

                row.CopyTo(tmp);
                for (int count = 0; count < words; count++)
                {
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        row[bytesPerSample * count + b] = tmp[b * words + count];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }
}
