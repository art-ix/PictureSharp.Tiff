using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Writes the first frame as a little-endian classic TIFF: the header, the strips (each packed, differenced and
/// compressed in turn), then the IFD. A seekable stream gets
/// the header's IFD offset patched after the data; any other stream gets the strips spooled first, with the same bytes.
/// </summary>
internal sealed class TiffEncoder : IImageFormatEncoder<TiffEncoderOptions>
{
    private const int StripTarget = 64 * 1024;
    private const string Software = "PictureSharp.Tiff";
    private const ushort PhotometricMinIsWhite = 0;
    private const ushort PhotometricMinIsBlack = 1;
    private const ushort PhotometricRgb = 2;
    private const ushort PhotometricPalette = 3;
    private const ushort PhotometricYCbCr = 6;
    private const int JpegMaxWidth = 65535;
    private const ushort PredictorHorizontal = 2;
    private const int PaletteMinPixels = 768;
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

        TiffCompression compression = options.Compression;
        if (options.Predictor == TiffPredictor.Horizontal && compression is not (TiffCompression.Lzw or TiffCompression.Deflate))
        {
            throw new ArgumentException("The horizontal predictor applies only to LZW and Deflate compression.", nameof(options));
        }

        if (compression == TiffCompression.CcittGroup4 && options.ColorType is not (TiffColorType.Auto or TiffColorType.BlackWhite))
        {
            throw new ArgumentException("CCITT Group 4 compression writes only black-and-white images.", nameof(options));
        }

        if (compression == TiffCompression.Jpeg && options.ColorType is not (TiffColorType.Auto or TiffColorType.Grayscale or TiffColorType.Rgb))
        {
            throw new ArgumentException("JPEG compression writes only greyscale or RGB images, without alpha.", nameof(options));
        }

        if (compression == TiffCompression.Jpeg && image.Width > JpegMaxWidth)
        {
            throw new NotSupportedException($"JPEG compression supports widths up to {JpegMaxWidth}.");
        }

        if (options.Predictor == TiffPredictor.Horizontal && options.ColorType == TiffColorType.BlackWhite)
        {
            throw new ArgumentException("The horizontal predictor applies only to 8-bit samples, not black and white.", nameof(options));
        }

        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
        Dictionary<uint, byte>? palette = null;
        TiffColorType colorType = options.ColorType != TiffColorType.Auto ? options.ColorType
            : compression == TiffCompression.CcittGroup4 ? TiffColorType.BlackWhite
            : compression == TiffCompression.Jpeg ? (IsGray(pixels) ? TiffColorType.Grayscale : TiffColorType.Rgb)
            : Choose(pixels, bilevel: options.Predictor != TiffPredictor.Horizontal, out palette);
        if (colorType == TiffColorType.Palette && palette is null)
        {
            palette = TiffSamplePacker.Palette(pixels)
                ?? throw new InvalidOperationException("The image has more than 256 colours and cannot be saved as a palette TIFF.");
        }

        int samplesPerPixel = colorType switch
        {
            TiffColorType.Grayscale or TiffColorType.Palette or TiffColorType.BlackWhite => 1,
            TiffColorType.GrayscaleAlpha => 2,
            TiffColorType.Rgb => 3,
            _ => 4,
        };
        bool horizontal = options.Predictor == TiffPredictor.Horizontal
            || (options.Predictor == TiffPredictor.Auto && compression is TiffCompression.Lzw or TiffCompression.Deflate
                && colorType is not (TiffColorType.Palette or TiffColorType.BlackWhite));

        int width = image.Width, height = image.Height;
        long rowBytes = colorType == TiffColorType.BlackWhite ? (width + 7L) / 8 : (long)width * samplesPerPixel;
        // JPEG strips other than the last hold whole MCU rows (libtiff requires it).
        int block = compression != TiffCompression.Jpeg ? 1
            : colorType == TiffColorType.Rgb && options.JpegSubsampling is JpegSubsampling.Ratio420 or JpegSubsampling.Ratio440 ? 16
            : 8;
        int rowsPerStrip = options.RowsPerStrip > 0
            ? (int)Math.Min((options.RowsPerStrip + (long)block - 1) / block * block, height)
            : (int)Math.Min(Math.Max(StripTarget / rowBytes / block, 1) * block, height);
        int strips = (int)(((long)height + rowsPerStrip - 1) / rowsPerStrip);
        var offsets = new uint[strips];
        var counts = new uint[strips];
        var layout = new Layout(colorType, samplesPerPixel, compression, horizontal, rowsPerStrip, options.JpegSubsampling, palette,
            (options.Metadata & MetadataKinds.Icc) != 0 ? image.Metadata.IccProfile : null,
            (options.Metadata & MetadataKinds.Xmp) != 0 ? image.Metadata.Xmp : null);

        // Classic TIFF addresses 4 GiB. The IFD size does not depend on the offsets, so uncompressed output is checked
        // before any output; compressed strips are checked as they are written.
        long stripBytes = rowBytes * rowsPerStrip;
        long bound = compression switch
        {
            TiffCompression.PackBits => PackBitsEncoder.MaxLength(stripBytes),
            TiffCompression.Lzw => LzwEncoder.MaxLength(stripBytes),
            _ => stripBytes,
        };
        int ifdSize = Ifd(image, layout, offsets, counts).Size;
        if (bound > Array.MaxLength
            || (compression == TiffCompression.None && Align(8 + (rowBytes * height)) + ifdSize > uint.MaxValue))
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

        byte[] strip = ArrayPool<byte>.Shared.Rent((int)stripBytes);
        byte[]? packed = compression is TiffCompression.PackBits or TiffCompression.Lzw ? ArrayPool<byte>.Shared.Rent((int)bound) : null;
        LzwEncoder? lzw = compression == TiffCompression.Lzw ? new LzwEncoder() : null;
        Fax4Encoder? fax4 = compression == TiffCompression.CcittGroup4 ? new Fax4Encoder() : null;
        using MemoryStream? jpeg = compression == TiffCompression.Jpeg ? new MemoryStream() : null;
        JpegEncoderOptions? jpegOptions = jpeg is null ? null : new JpegEncoderOptions
        {
            Quality = options.JpegQuality,
            // A single component must have sampling factors 1x1, which libtiff checks (JPEGPreDecode).
            Subsampling = colorType == TiffColorType.Grayscale ? JpegSubsampling.Ratio444 : options.JpegSubsampling,
            ColorMode = colorType == TiffColorType.Grayscale ? JpegColorMode.Grayscale : JpegColorMode.Color,
            Metadata = MetadataKinds.None,
            Configuration = options.Configuration,
        };
        using DeflateEncoder? deflate = compression == TiffCompression.Deflate ? new DeflateEncoder(options.DeflateLevel) : null;
        long position = 8;
        try
        {
            for (int s = 0; s < strips; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y0 = s * rowsPerStrip, rows = Math.Min(rowsPerStrip, height - y0);
                Span<byte> samples = strip.AsSpan(0, (int)(rows * rowBytes));
                for (int y = 0; y < (jpeg is null ? rows : 0); y++)
                {
                    ReadOnlySpan<Rgba32> row = pixels.Slice((y0 + y) * width, width);
                    Span<byte> target = samples.Slice((int)(y * rowBytes), (int)rowBytes);
                    if (palette is not null)
                    {
                        TiffSamplePacker.PackIndices(row, target, palette);
                    }
                    else if (colorType == TiffColorType.BlackWhite)
                    {
                        TiffSamplePacker.PackBlackWhite(row, target);
                    }
                    else
                    {
                        TiffSamplePacker.Pack(row, target, samplesPerPixel);
                    }
                }

                if (horizontal)
                {
                    HorizontalPredictor.Encode8(samples, (int)rowBytes, samplesPerPixel);
                }

                ReadOnlySpan<byte> data = compression switch
                {
                    TiffCompression.PackBits => packed.AsSpan(0, PackBitsEncoder.Encode(samples, (int)rowBytes, packed)),
                    TiffCompression.Lzw => packed.AsSpan(0, lzw!.Encode(samples, packed)),
                    TiffCompression.Deflate => deflate!.Encode(samples),
                    TiffCompression.CcittGroup4 => fax4!.Encode(samples, (int)rowBytes, width),
                    TiffCompression.Jpeg => EncodeJpeg(pixels.Slice(y0 * width, rows * width), width, rows, jpeg!, jpegOptions!, cancellationToken),
                    _ => samples,
                };
                if (Align(position + data.Length) + ifdSize > uint.MaxValue)
                {
                    throw new NotSupportedException("The TIFF file would exceed 4 GiB, which classic TIFF cannot address.");
                }

                if (spool is null)
                {
                    output.Write(data);
                }
                else
                {
                    spool.Write(data);
                }

                offsets[s] = (uint)position;
                counts[s] = (uint)data.Length;
                position += data.Length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(strip);
            if (packed is not null)
            {
                ArrayPool<byte>.Shared.Return(packed);
            }
        }

        uint ifdOffset = (uint)Align(position);
        byte[] ifd = Ifd(image, layout, offsets, counts).ToArray(ifdOffset);
        if (spool is not null)
        {
            output.Write(Header(ifdOffset));
            spool.CopyTo(output);
        }

        if (ifdOffset != position)
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

    /// <summary>
    /// The smallest lossless type: black and white when <paramref name="bilevel"/> allows it and every pixel is opaque
    /// black or white, grey when every pixel has
    /// R = G = B, else a palette when the image is opaque, has at
    /// most 256 colours and enough pixels to outweigh the colour map, else RGB; alpha only when a pixel is not opaque.
    /// </summary>
    private static TiffColorType Choose(ReadOnlySpan<Rgba32> pixels, bool bilevel, out Dictionary<uint, byte>? palette)
    {
        palette = null;
        bool alpha = false, gray = true;
        foreach (Rgba32 p in pixels)
        {
            alpha |= p.A != 255;
            gray &= p.R == p.G && p.G == p.B;
            bilevel &= p.R is 0 or 255;
        }

        if (gray && bilevel && !alpha)
        {
            return TiffColorType.BlackWhite;
        }

        if (!gray && !alpha && pixels.Length > PaletteMinPixels)
        {
            palette = TiffSamplePacker.Palette(pixels);
        }

        return (gray, alpha) switch
        {
            (true, false) => TiffColorType.Grayscale,
            (true, true) => TiffColorType.GrayscaleAlpha,
            (false, false) => palette is null ? TiffColorType.Rgb : TiffColorType.Palette,
            _ => TiffColorType.Rgba,
        };
    }

    /// <summary>Whether every pixel has R = G = B.</summary>
    private static bool IsGray(ReadOnlySpan<Rgba32> pixels)
    {
        foreach (Rgba32 p in pixels)
        {
            if (p.R != p.G || p.G != p.B)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One strip as a complete JPEG stream. Alpha is dropped rather than composited, as for the other compressions, and
    /// the colour mode is fixed so that a grey strip of a colour image still has the three components the IFD declares.
    /// </summary>
    // port: libtiff/tif_jpeg.c JPEGSetupEncode and JPEGEncode write one interchange stream per strip when no JPEGTables
    // are shared; libtiff itself shares the tables, which TTN2 leaves optional.
    private static ReadOnlySpan<byte> EncodeJpeg(ReadOnlySpan<Rgba32> pixels, int width, int rows, MemoryStream output,
        JpegEncoderOptions options, CancellationToken cancellationToken)
    {
        using var part = new Image(width, rows);
        Span<Rgba32> target = part.GetPixelSpan();
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = pixels[i] with { A = 255 };
        }

        output.SetLength(0);
        part.Save(output, options, cancellationToken);
        return output.GetBuffer().AsSpan(0, (int)output.Length);
    }

    private static TiffIfdWriter Ifd(Image image, Layout layout, uint[] offsets, uint[] counts)
    {
        var ifd = new TiffIfdWriter();
        ifd.AddLong(TiffTag.ImageWidth, (uint)image.Width);
        ifd.AddLong(TiffTag.ImageLength, (uint)image.Height);
        Span<ushort> bits = [8, 8, 8, 8];
        ifd.AddShort(TiffTag.BitsPerSample, layout.ColorType == TiffColorType.BlackWhite ? [1] : bits[..layout.SamplesPerPixel]);
        ifd.AddShort(TiffTag.Compression, layout.Compression switch
        {
            TiffCompression.PackBits => (ushort)32773,
            TiffCompression.Lzw => (ushort)5,
            TiffCompression.Deflate => (ushort)8,
            TiffCompression.Jpeg => (ushort)7,
            TiffCompression.CcittGroup4 => (ushort)4,
            _ => (ushort)1,
        });
        ifd.AddShort(TiffTag.Photometric, layout.ColorType switch
        {
            TiffColorType.BlackWhite => PhotometricMinIsWhite,
            TiffColorType.Palette => PhotometricPalette,
            TiffColorType.Rgb when layout.Compression == TiffCompression.Jpeg => PhotometricYCbCr,
            TiffColorType.Rgb or TiffColorType.Rgba => PhotometricRgb,
            _ => PhotometricMinIsBlack,
        });
        ifd.AddLong(TiffTag.StripOffsets, offsets);
        ifd.AddShort(TiffTag.SamplesPerPixel, (ushort)layout.SamplesPerPixel);
        ifd.AddLong(TiffTag.RowsPerStrip, (uint)layout.RowsPerStrip);
        ifd.AddLong(TiffTag.StripByteCounts, counts);
        (uint x, uint xDenominator) = Rational(image.Metadata.HorizontalResolution);
        (uint y, uint yDenominator) = Rational(image.Metadata.VerticalResolution);
        ifd.AddRational(TiffTag.XResolution, x, xDenominator);
        ifd.AddRational(TiffTag.YResolution, y, yDenominator);
        ifd.AddShort(TiffTag.PlanarConfiguration, 1);
        ifd.AddShort(TiffTag.ResolutionUnit, ResolutionUnitInch);
        ifd.AddAscii(TiffTag.Software, Software);
        if (layout.Horizontal)
        {
            ifd.AddShort(TiffTag.Predictor, PredictorHorizontal);
        }

        if (layout.Compression == TiffCompression.Jpeg && layout.ColorType == TiffColorType.Rgb)
        {
            // port: libtiff/tif_jpeg.c JPEGSetupEncode: ReferenceBlackWhite is written because its default does not fit
            // YCbCr.
            (ushort h, ushort v) = layout.JpegSubsampling switch
            {
                JpegSubsampling.Ratio444 => ((ushort)1, (ushort)1),
                JpegSubsampling.Ratio422 => ((ushort)2, (ushort)1),
                JpegSubsampling.Ratio440 => ((ushort)1, (ushort)2),
                _ => ((ushort)2, (ushort)2),
            };
            ifd.AddShort(TiffTag.YCbCrSubsampling, h, v);
            ifd.AddRational(TiffTag.ReferenceBlackWhite, 0, 1, 255, 1, 128, 1, 255, 1, 128, 1, 255, 1);
        }

        if (layout.Palette is not null)
        {
            ifd.AddShort(TiffTag.ColorMap, ColorMap(layout.Palette));
        }

        if (layout.ColorType is TiffColorType.GrayscaleAlpha or TiffColorType.Rgba)
        {
            ifd.AddShort(TiffTag.ExtraSamples, UnassociatedAlpha);
        }

        if (layout.Xmp is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.Xmp, TiffEntry.Byte, layout.Xmp);
        }

        if (layout.Icc is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.IccProfile, TiffEntry.Undefined, layout.Icc);
        }

        return ifd;
    }

    /// <summary>The 256 red, then green, then blue values of the palette, scaled to 16 bits; unused entries are black.</summary>
    private static ushort[] ColorMap(Dictionary<uint, byte> palette)
    {
        var map = new ushort[3 * 256];
        foreach ((uint packed, byte index) in palette)
        {
            Rgba32 color = MemoryMarshal.Cast<uint, Rgba32>(new ReadOnlySpan<uint>(in packed))[0];
            map[index] = (ushort)(color.R * 257);
            map[256 + index] = (ushort)(color.G * 257);
            map[512 + index] = (ushort)(color.B * 257);
        }

        return map;
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

    private sealed record Layout(TiffColorType ColorType, int SamplesPerPixel, TiffCompression Compression, bool Horizontal,
        int RowsPerStrip, JpegSubsampling JpegSubsampling, Dictionary<uint, byte>? Palette, byte[]? Icc, byte[]? Xmp);
}
