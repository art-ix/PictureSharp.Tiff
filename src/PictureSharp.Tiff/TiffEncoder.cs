using System.Buffers;
using System.Buffers.Binary;
using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Writes the first frame as a little-endian classic TIFF: the header, the strips, then the IFD. A seekable stream gets
/// the header's IFD offset patched after the data; any other stream gets the strips spooled first, with the same bytes.
/// </summary>
internal sealed class TiffEncoder : IImageFormatEncoder<TiffEncoderOptions>
{
    private const int StripTarget = 64 * 1024;
    private const string Software = "PictureSharp.Tiff";
    private const ushort PhotometricMinIsBlack = 1;
    private const ushort PhotometricRgb = 2;
    private const ushort UnassociatedAlpha = 2;
    private const ushort ResolutionUnitInch = 2;

    public static TiffEncoder Instance { get; } = new();

    public void Encode(Image image, Stream output, TiffEncoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
        TiffColorType colorType = options.ColorType == TiffColorType.Auto ? Choose(pixels) : options.ColorType;
        int samplesPerPixel = colorType switch
        {
            TiffColorType.Grayscale => 1,
            TiffColorType.GrayscaleAlpha => 2,
            TiffColorType.Rgb => 3,
            _ => 4,
        };

        int width = image.Width, height = image.Height;
        long rowBytes = (long)width * samplesPerPixel;
        int rowsPerStrip = options.RowsPerStrip > 0
            ? Math.Min(options.RowsPerStrip, height)
            : (int)Math.Clamp(StripTarget / rowBytes, 1, height);
        int strips = (int)(((long)height + rowsPerStrip - 1) / rowsPerStrip);
        var offsets = new uint[strips];
        var counts = new uint[strips];
        byte[]? icc = (options.Metadata & MetadataKinds.Icc) != 0 ? image.Metadata.IccProfile : null;
        byte[]? xmp = (options.Metadata & MetadataKinds.Xmp) != 0 ? image.Metadata.Xmp : null;

        // Classic TIFF addresses 4 GiB; the IFD size does not depend on the offsets, so the check precedes any output.
        long dataEnd = 8 + (rowBytes * height);
        long fileSize = Align(dataEnd) + Ifd(image, colorType, samplesPerPixel, rowsPerStrip, offsets, counts, icc, xmp).Size;
        if (rowBytes * rowsPerStrip > Array.MaxLength || fileSize > uint.MaxValue)
        {
            throw new NotSupportedException("The TIFF file would exceed 4 GiB, which classic TIFF cannot address.");
        }

        bool seekable = output.CanSeek;
        long start = seekable ? output.Position : 0;
        using TiffSpool? spool = seekable ? null : new TiffSpool();
        if (seekable)
        {
            output.Write(Header(0));
        }

        byte[] strip = ArrayPool<byte>.Shared.Rent((int)(rowBytes * rowsPerStrip));
        try
        {
            uint position = 8;
            for (int s = 0; s < strips; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y0 = s * rowsPerStrip, rows = Math.Min(rowsPerStrip, height - y0);
                for (int y = 0; y < rows; y++)
                {
                    TiffSamplePacker.Pack(pixels.Slice((y0 + y) * width, width), strip.AsSpan((int)(y * rowBytes), (int)rowBytes), samplesPerPixel);
                }

                ReadOnlySpan<byte> data = strip.AsSpan(0, (int)(rows * rowBytes));
                if (spool is null)
                {
                    output.Write(data);
                }
                else
                {
                    spool.Write(data);
                }

                offsets[s] = position;
                counts[s] = (uint)data.Length;
                position += (uint)data.Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(strip);
        }

        uint ifdOffset = (uint)Align(dataEnd);
        byte[] ifd = Ifd(image, colorType, samplesPerPixel, rowsPerStrip, offsets, counts, icc, xmp).ToArray(ifdOffset);
        if (spool is not null)
        {
            output.Write(Header(ifdOffset));
            spool.CopyTo(output);
        }

        if (ifdOffset != dataEnd)
        {
            output.WriteByte(0);
        }

        output.Write(ifd);
        if (seekable)
        {
            long end = output.Position;
            output.Position = start + 4;
            Span<byte> link = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(link, ifdOffset);
            output.Write(link);
            output.Position = end;
        }
    }

    /// <summary>The smallest lossless type: alpha only when a pixel is not opaque, grey when every pixel has R = G = B.</summary>
    private static TiffColorType Choose(ReadOnlySpan<Rgba32> pixels)
    {
        bool alpha = false, gray = true;
        foreach (Rgba32 p in pixels)
        {
            alpha |= p.A != 255;
            gray &= p.R == p.G && p.G == p.B;
        }

        return (gray, alpha) switch
        {
            (true, false) => TiffColorType.Grayscale,
            (true, true) => TiffColorType.GrayscaleAlpha,
            (false, false) => TiffColorType.Rgb,
            _ => TiffColorType.Rgba,
        };
    }

    private static TiffIfdWriter Ifd(Image image, TiffColorType colorType, int samplesPerPixel, int rowsPerStrip,
        uint[] offsets, uint[] counts, byte[]? icc, byte[]? xmp)
    {
        var ifd = new TiffIfdWriter();
        ifd.AddLong(TiffTag.ImageWidth, (uint)image.Width);
        ifd.AddLong(TiffTag.ImageLength, (uint)image.Height);
        Span<ushort> bits = [8, 8, 8, 8];
        ifd.AddShort(TiffTag.BitsPerSample, bits[..samplesPerPixel]);
        ifd.AddShort(TiffTag.Compression, 1);
        ifd.AddShort(TiffTag.Photometric, samplesPerPixel <= 2 ? PhotometricMinIsBlack : PhotometricRgb);
        ifd.AddLong(TiffTag.StripOffsets, offsets);
        ifd.AddShort(TiffTag.SamplesPerPixel, (ushort)samplesPerPixel);
        ifd.AddLong(TiffTag.RowsPerStrip, (uint)rowsPerStrip);
        ifd.AddLong(TiffTag.StripByteCounts, counts);
        (uint x, uint xDenominator) = Rational(image.Metadata.HorizontalResolution);
        (uint y, uint yDenominator) = Rational(image.Metadata.VerticalResolution);
        ifd.AddRational(TiffTag.XResolution, x, xDenominator);
        ifd.AddRational(TiffTag.YResolution, y, yDenominator);
        ifd.AddShort(TiffTag.PlanarConfiguration, 1);
        ifd.AddShort(TiffTag.ResolutionUnit, ResolutionUnitInch);
        ifd.AddAscii(TiffTag.Software, Software);
        if (colorType is TiffColorType.GrayscaleAlpha or TiffColorType.Rgba)
        {
            ifd.AddShort(TiffTag.ExtraSamples, UnassociatedAlpha);
        }

        if (xmp is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.Xmp, TiffEntry.Byte, xmp);
        }

        if (icc is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.IccProfile, TiffEntry.Undefined, icc);
        }

        return ifd;
    }

    private static byte[] Header(uint ifdOffset)
    {
        byte[] header = [(byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ifdOffset);
        return header;
    }

    /// <summary>
    /// The resolution as a rational that reads back as the same <see cref="float"/>: the smallest power-of-ten
    /// denominator up to 10⁶ that is exact, or the finest one that fits.
    /// </summary>
    private static (uint Numerator, uint Denominator) Rational(float value)
    {
        (uint, uint) best = ((uint)Math.Min(Math.Round((double)value), uint.MaxValue), 1);
        for (uint denominator = 1; denominator <= 1_000_000; denominator *= 10)
        {
            double numerator = Math.Round(value * (double)denominator);
            if (numerator > uint.MaxValue)
            {
                break;
            }

            best = ((uint)numerator, denominator);
            if ((float)(numerator / denominator) == value)
            {
                break;
            }
        }

        return best;
    }

    private static long Align(long offset) => (offset + 1) & ~1L;
}
