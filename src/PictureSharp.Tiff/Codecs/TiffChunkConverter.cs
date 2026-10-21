using System.Buffers.Binary;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>Turns one decoded strip or tile, every plane of it, into pixels.</summary>
internal abstract class TiffChunkConverter
{
    public static TiffChunkConverter Create(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) =>
        layout.SampleFormat == TiffLayout.FormatFloat ? new FloatChunkConverter(layout, ifd.BigEndian)
        : layout.Photometric == TiffLayout.YCbCr ? new YCbCrChunkConverter(data, ifd, layout)
        : new IntegerChunkConverter(data, ifd, layout);

    /// <summary>
    /// Converts the top-left <paramref name="width"/> x <paramref name="rows"/> pixels of the chunk. Plane
    /// <c>p</c> starts at <c>p * planeBytes</c> in <paramref name="chunk"/>; output row <c>r</c> starts at
    /// <c>r * stride</c> in <paramref name="pixels"/>.
    /// </summary>
    public abstract void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride);
}

/// <summary>Unsigned integer samples of 1 to 16 bits, unpacked per row and converted by <see cref="TiffColorConverter"/>.</summary>
internal sealed class IntegerChunkConverter(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) : TiffChunkConverter
{
    private readonly TiffColorConverter colors = new(layout,
        layout.Photometric == TiffLayout.Palette ? TiffColorConverter.ReadPalette(data, ifd, layout.BitsPerSample) : null);

    private readonly ushort[] samples = new ushort[layout.ChunkWidth * layout.SamplesPerPixel];
    private readonly int rowBytes = (int)layout.RowBytes;
    private readonly bool bigEndian = ifd.BigEndian;

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        int spp = layout.SamplesPerPixel, bits = layout.BitsPerSample;
        for (int r = 0; r < rows; r++)
        {
            if (layout.Separate)
            {
                for (int p = 0; p < spp; p++)
                {
                    Unpack(chunk.Slice(p * planeBytes + r * rowBytes, rowBytes), width, bits, bigEndian, samples, p, spp);
                }
            }
            else
            {
                Unpack(chunk.Slice(r * rowBytes, rowBytes), width * spp, bits, bigEndian, samples, 0, 1);
            }

            colors.Convert(samples.AsSpan(0, width * spp), pixels.Slice(r * stride, width));
        }
    }

    /// <summary>Unpacks <paramref name="count"/> MSB-first samples into every <paramref name="step"/>-th slot from <paramref name="first"/>.</summary>
    private static void Unpack(ReadOnlySpan<byte> row, int count, int bits, bool bigEndian, Span<ushort> samples, int first, int step)
    {
        switch (bits)
        {
            case 8:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    samples[d] = row[i];
                }

                break;
            case 16:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    ReadOnlySpan<byte> pair = row.Slice(i * 2, 2);
                    samples[d] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(pair) : BinaryPrimitives.ReadUInt16LittleEndian(pair);
                }

                break;
            default:
                int mask = (1 << bits) - 1;
                for (int i = 0, d = first, bit = 0; i < count; i++, d += step, bit += bits)
                {
                    samples[d] = (ushort)((row[bit >> 3] >> (8 - bits - (bit & 7))) & mask);
                }

                break;
        }
    }
}

/// <summary>
/// 32-bit IEEE samples, greyscale or RGB with optional alpha. Values are clamped to [0, 1] and become
/// <c>(int)(v * 255 + 0.5)</c> in single precision; NaN becomes 0. Premultiplied colour is divided by alpha first
/// (0 where alpha is not positive). After predictor 3 the samples are big-endian, as the predictor reassembles them.
/// </summary>
internal sealed class FloatChunkConverter(TiffLayout layout, bool fileBigEndian) : TiffChunkConverter
{
    private readonly int rowBytes = (int)layout.RowBytes;
    private readonly bool bigEndian = layout.EffectivePredictor == 3 || fileBigEndian;

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        int spp = layout.SamplesPerPixel, alpha = layout.AlphaSample;
        bool rgb = layout.Photometric == TiffLayout.Rgb, invert = layout.Photometric == TiffLayout.WhiteIsZero;
        for (int r = 0; r < rows; r++)
        {
            Span<Rgba32> row = pixels.Slice(r * stride, width);
            for (int x = 0; x < width; x++)
            {
                float a = alpha >= 0 ? Sample(chunk, planeBytes, r, x, alpha) : 1f;
                if (rgb)
                {
                    row[x] = new Rgba32(Color(Sample(chunk, planeBytes, r, x, 0), a), Color(Sample(chunk, planeBytes, r, x, 1), a),
                        Color(Sample(chunk, planeBytes, r, x, 2), a), To8(a));
                }
                else
                {
                    float v = Sample(chunk, planeBytes, r, x, 0);
                    byte g = Color(invert ? 1f - v : v, a);
                    row[x] = new Rgba32(g, g, g, To8(a));
                }
            }
        }

        float Sample(ReadOnlySpan<byte> c, int plane, int r, int x, int s)
        {
            int at = layout.Separate ? s * plane + r * rowBytes + x * 4 : r * rowBytes + (x * spp + s) * 4;
            ReadOnlySpan<byte> bytes = c.Slice(at, 4);
            return bigEndian ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes);
        }
    }

    private byte Color(float v, float a) =>
        To8(!layout.Associated || a == 1f ? v : a > 0 ? v / a : 0f);

    private static byte To8(float v) => !(v > 0f) ? (byte)0 : v >= 1f ? (byte)255 : (byte)(int)(v * 255f + 0.5f);
}

/// <summary>
/// 8-bit YCbCr through libtiff's fixed-point tables. Packed data holds blocks of Horizontal x Vertical luma samples
/// in row order followed by one Cb and one Cr; each pixel takes the chroma of its block.
/// </summary>
// port: libtiff/tif_getimage.c putcontig8bitYCbCr44tile ... putcontig8bitYCbCr11tile and putseparate8bitYCbCr11tile,
// which all address the blocks this way.
internal sealed class YCbCrChunkConverter(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) : TiffChunkConverter
{
    private readonly YCbCrToRgb tables = new(data, ifd);

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        (int h, int v) = layout.Subsampling;
        int blockSize = h * v + 2;
        int blocksAcross = (layout.ChunkWidth + h - 1) / h;
        int rowBytes = (int)layout.RowBytes;
        for (int r = 0; r < rows; r++)
        {
            Span<Rgba32> row = pixels.Slice(r * stride, width);
            for (int x = 0; x < width; x++)
            {
                if (layout.Separate)
                {
                    int at = r * rowBytes + x;
                    row[x] = tables.Convert(chunk[at], chunk[planeBytes + at], chunk[2 * planeBytes + at]);
                }
                else
                {
                    int block = ((r / v) * blocksAcross + x / h) * blockSize;
                    row[x] = tables.Convert(chunk[block + (r % v) * h + x % h], chunk[block + h * v], chunk[block + h * v + 1]);
                }
            }
        }
    }
}
