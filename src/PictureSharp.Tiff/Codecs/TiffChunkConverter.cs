using System.Buffers;
using System.Buffers.Binary;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>Turns one decoded strip or tile, every plane of it, into pixels. Instances are shared by the decoding threads.</summary>
internal abstract class TiffChunkConverter
{
    public static TiffChunkConverter Create(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) =>
        layout.SampleFormat == TiffLayout.FormatFloat ? new FloatChunkConverter(layout, ifd.BigEndian)
        : layout.Photometric == TiffLayout.YCbCr ? new YCbCrChunkConverter(data, ifd, layout)
        : layout.Photometric == TiffLayout.CieLab ? new CieLabChunkConverter(data, ifd, layout)
        : new IntegerChunkConverter(data, ifd, layout);

    /// <summary>
    /// Converts the top-left <paramref name="width"/> x <paramref name="rows"/> pixels of the chunk. Plane
    /// <c>p</c> starts at <c>p * planeBytes</c> in <paramref name="chunk"/>; output row <c>r</c> starts at
    /// <c>r * stride</c> in <paramref name="pixels"/>.
    /// </summary>
    public abstract void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride);
}

/// <summary>
/// Integer samples of 1 to 16 or 32 bits, unpacked per row (signed ones offset by flipping the sign bit, which adds
/// <c>2^(b - 1)</c>) and converted by <see cref="TiffColorConverter"/>; contiguous unsigned 8-bit samples convert
/// straight from the chunk. Depths other than 8, 16 and 32 are packed MSB first whatever the byte order.
/// </summary>
internal sealed class IntegerChunkConverter(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) : TiffChunkConverter
{
    private readonly TiffColorConverter colors = new(layout,
        layout.Photometric == TiffLayout.Palette ? TiffColorConverter.ReadPalette(data, ifd, layout.BitsPerSample) : null);

    private readonly int rowBytes = (int)layout.RowBytes;
    private readonly bool bigEndian = ifd.BigEndian;
    private readonly uint flip = layout.SampleFormat == TiffLayout.FormatSigned ? 1u << (layout.BitsPerSample - 1) : 0;

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        int spp = layout.SamplesPerPixel, bits = layout.BitsPerSample;
        if (bits == 8 && !layout.Separate && flip == 0)
        {
            for (int r = 0; r < rows; r++)
            {
                colors.Convert(chunk.Slice(r * rowBytes, width * spp), pixels.Slice(r * stride, width));
            }

            return;
        }

        if (bits == 32)
        {
            uint[] wide = ArrayPool<uint>.Shared.Rent(width * spp);
            try
            {
                for (int r = 0; r < rows; r++)
                {
                    for (int p = 0; p < layout.Planes; p++)
                    {
                        int count = layout.Separate ? width : width * spp;
                        Unpack32(chunk.Slice(p * planeBytes + r * rowBytes, rowBytes), count, wide, p, layout.Separate ? spp : 1);
                    }

                    colors.Convert(wide.AsSpan(0, width * spp), pixels.Slice(r * stride, width));
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(wide);
            }

            return;
        }

        ushort[] samples = ArrayPool<ushort>.Shared.Rent(width * spp);
        try
        {
            ConvertRows(chunk, planeBytes, width, rows, pixels, stride, spp, bits, samples);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(samples);
        }
    }

    private void ConvertRows(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride, int spp, int bits,
        ushort[] samples)
    {
        for (int r = 0; r < rows; r++)
        {
            if (layout.Separate)
            {
                for (int p = 0; p < spp; p++)
                {
                    Unpack(chunk.Slice(p * planeBytes + r * rowBytes, rowBytes), width, bits, samples, p, spp);
                }
            }
            else
            {
                Unpack(chunk.Slice(r * rowBytes, rowBytes), width * spp, bits, samples, 0, 1);
            }

            colors.Convert(samples.AsSpan(0, width * spp), pixels.Slice(r * stride, width));
        }
    }

    /// <summary>Unpacks <paramref name="count"/> samples into every <paramref name="step"/>-th slot from <paramref name="first"/>.</summary>
    private void Unpack(ReadOnlySpan<byte> row, int count, int bits, Span<ushort> samples, int first, int step)
    {
        switch (bits)
        {
            case 8:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    samples[d] = (ushort)(row[i] ^ flip);
                }

                break;
            case 16:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    ReadOnlySpan<byte> pair = row.Slice(i * 2, 2);
                    samples[d] = (ushort)((bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(pair) : BinaryPrimitives.ReadUInt16LittleEndian(pair)) ^ flip);
                }

                break;
            case 1 or 2 or 4:
                int mask = (1 << bits) - 1;
                for (int i = 0, d = first, bit = 0; i < count; i++, d += step, bit += bits)
                {
                    samples[d] = (ushort)((row[bit >> 3] >> (8 - bits - (bit & 7))) & mask);
                }

                break;
            default:
                // Other depths may cross byte boundaries; a 24-bit window always holds a sample of up to 15 bits.
                int wideMask = (1 << bits) - 1;
                for (int i = 0, d = first, bit = 0; i < count; i++, d += step, bit += bits)
                {
                    int at = bit >> 3;
                    int window = (row[at] << 16) | (at + 1 < row.Length ? row[at + 1] << 8 : 0) | (at + 2 < row.Length ? row[at + 2] : 0);
                    samples[d] = (ushort)((window >> (24 - bits - (bit & 7))) & wideMask);
                }

                break;
        }
    }

    private void Unpack32(ReadOnlySpan<byte> row, int count, Span<uint> samples, int first, int step)
    {
        for (int i = 0, d = first; i < count; i++, d += step)
        {
            ReadOnlySpan<byte> word = row.Slice(i * 4, 4);
            samples[d] = (bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(word) : BinaryPrimitives.ReadUInt32LittleEndian(word)) ^ flip;
        }
    }
}

/// <summary>
/// IEEE samples of 16, 32 or 64 bits, greyscale or RGB with optional alpha. Values are clamped to [0, 1] and become
/// <c>(int)(v * 255 + 0.5)</c>, in single precision for 16- and 32-bit samples (a half converts to a float exactly)
/// and in double precision for 64-bit ones; NaN becomes 0. Premultiplied colour is divided by alpha first (0 where
/// alpha is not positive). After predictor 3 the samples are big-endian, as the predictor reassembles them.
/// </summary>
internal sealed class FloatChunkConverter(TiffLayout layout, bool fileBigEndian) : TiffChunkConverter
{
    private readonly int rowBytes = (int)layout.RowBytes;
    private readonly int size = layout.BitsPerSample / 8;
    private readonly bool bigEndian = layout.EffectivePredictor == 3 || fileBigEndian;

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        int alpha = layout.AlphaSample;
        bool rgb = layout.Photometric == TiffLayout.Rgb, invert = layout.Photometric == TiffLayout.WhiteIsZero;
        for (int r = 0; r < rows; r++)
        {
            Span<Rgba32> row = pixels.Slice(r * stride, width);
            for (int x = 0; x < width; x++)
            {
                if (size == 8)
                {
                    double a = alpha >= 0 ? Wide(chunk, planeBytes, r, x, alpha) : 1d;
                    if (rgb)
                    {
                        row[x] = new Rgba32(Color(Wide(chunk, planeBytes, r, x, 0), a), Color(Wide(chunk, planeBytes, r, x, 1), a),
                            Color(Wide(chunk, planeBytes, r, x, 2), a), To8(a));
                    }
                    else
                    {
                        double v = Wide(chunk, planeBytes, r, x, 0);
                        byte g = Color(invert ? 1d - v : v, a);
                        row[x] = new Rgba32(g, g, g, To8(a));
                    }
                }
                else
                {
                    float a = alpha >= 0 ? Narrow(chunk, planeBytes, r, x, alpha) : 1f;
                    if (rgb)
                    {
                        row[x] = new Rgba32(Color(Narrow(chunk, planeBytes, r, x, 0), a), Color(Narrow(chunk, planeBytes, r, x, 1), a),
                            Color(Narrow(chunk, planeBytes, r, x, 2), a), To8(a));
                    }
                    else
                    {
                        float v = Narrow(chunk, planeBytes, r, x, 0);
                        byte g = Color(invert ? 1f - v : v, a);
                        row[x] = new Rgba32(g, g, g, To8(a));
                    }
                }
            }
        }
    }

    private ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> chunk, int planeBytes, int r, int x, int s) =>
        chunk.Slice(layout.Separate ? s * planeBytes + r * rowBytes + x * size : r * rowBytes + (x * layout.SamplesPerPixel + s) * size, size);

    private float Narrow(ReadOnlySpan<byte> chunk, int planeBytes, int r, int x, int s)
    {
        ReadOnlySpan<byte> bytes = Bytes(chunk, planeBytes, r, x, s);
        return size == 2
            ? (float)(bigEndian ? BinaryPrimitives.ReadHalfBigEndian(bytes) : BinaryPrimitives.ReadHalfLittleEndian(bytes))
            : bigEndian ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    private double Wide(ReadOnlySpan<byte> chunk, int planeBytes, int r, int x, int s)
    {
        ReadOnlySpan<byte> bytes = Bytes(chunk, planeBytes, r, x, s);
        return bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    private byte Color(float v, float a) =>
        To8(!layout.Associated || a == 1f ? v : a > 0 ? v / a : 0f);

    private byte Color(double v, double a) =>
        To8(!layout.Associated || a == 1d ? v : a > 0 ? v / a : 0d);

    private static byte To8(float v) => !(v > 0f) ? (byte)0 : v >= 1f ? (byte)255 : (byte)(int)(v * 255f + 0.5f);

    private static byte To8(double v) => !(v > 0d) ? (byte)0 : v >= 1d ? (byte)255 : (byte)(int)(v * 255d + 0.5d);
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

/// <summary>8- or 16-bit contiguous CIE L*a*b* (photometric 8) through libtiff's conversion, <see cref="CieLabToRgb"/>.</summary>
// port: libtiff/tif_getimage.c putcontig8bitCIELab8 and putcontig8bitCIELab16.
internal sealed class CieLabChunkConverter(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout) : TiffChunkConverter
{
    private readonly CieLabToRgb lab = new(data, ifd);
    private readonly int rowBytes = (int)layout.RowBytes;
    private readonly bool bigEndian = ifd.BigEndian;

    public override void Convert(ReadOnlySpan<byte> chunk, int planeBytes, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        for (int r = 0; r < rows; r++)
        {
            ReadOnlySpan<byte> row = chunk.Slice(r * rowBytes, rowBytes);
            Span<Rgba32> output = pixels.Slice(r * stride, width);
            if (layout.BitsPerSample == 8)
            {
                for (int x = 0, s = 0; x < width; x++, s += 3)
                {
                    output[x] = lab.Convert8(row[s], (sbyte)row[s + 1], (sbyte)row[s + 2]);
                }
            }
            else
            {
                for (int x = 0, s = 0; x < width; x++, s += 6)
                {
                    output[x] = lab.Convert16(Read(row[s..]), (short)Read(row[(s + 2)..]), (short)Read(row[(s + 4)..]));
                }
            }
        }
    }

    private ushort Read(ReadOnlySpan<byte> bytes) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
}
