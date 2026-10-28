using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Writes each frame as a page of a little-endian classic TIFF or BigTIFF: the header, the strips or tiles of every page
/// (each packed, differenced and compressed, possibly in parallel, and written in order), then the IFDs of all pages,
/// linked in page order. A seekable stream gets the header's IFD offset patched after the data; any other stream gets
/// the data spooled first, with the same bytes.
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
    private const uint SubfilePage = 2;
    // Chunks compressed per parallel batch, per thread: enough to keep threads busy, few enough to bound the memory.
    private const int BatchPerThread = 4;

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

        Validate(image, options);
        int pageCount = options.SaveAllPages ? image.FrameCount : 1;
        var pages = new Page[pageCount];
        // Pages written at another depth than the image stores read a converted copy of their frame.
        var copies = new List<Image>();
        try
        {
            for (int i = 0; i < pageCount; i++)
            {
                pages[i] = Plan(image, image.Frames[i], options, pageCount > 1 ? i : -1, pageCount, copies);
            }

            Write(image, pages, output, options, cancellationToken);
        }
        finally
        {
            foreach (Image copy in copies)
            {
                copy.Dispose();
            }
        }
    }

    public bool SupportsPixelType(PixelType pixelType) => pixelType is PixelType.Rgba32 or PixelType.Rgba64;

    private static void Write(Image image, Page[] pages, Stream output, TiffEncoderOptions options, CancellationToken cancellationToken)
    {
        int pageCount = pages.Length;
        // The IFD sizes do not depend on the offsets, so the size of uncompressed output is known before any output;
        // compressed chunks are checked against classic TIFF's 4 GiB as they are written.
        long classicIfds = pages.Sum(page => Ifd(image, page, bigTiff: false).Size);
        long uncompressed = pages.Sum(page => page.UncompressedBytes);
        bool bigTiff = options.Container == TiffContainer.BigTiff
            || (options.Container == TiffContainer.Auto && Align(8 + uncompressed) + classicIfds > uint.MaxValue);
        long ifdBytes = bigTiff ? pages.Sum(page => Ifd(image, page, bigTiff: true).Size) : classicIfds;
        if (pages.Any(page => page.Bound > Array.MaxLength)
            || (!bigTiff && options.Compression == TiffCompression.None && Align(8 + uncompressed) + ifdBytes > uint.MaxValue))
        {
            throw TooLarge();
        }

        bool seekable = output.CanSeek;
        long start = seekable ? output.Position : 0;
        using TiffSpool? spool = seekable ? null : new TiffSpool();
        if (seekable)
        {
            output.Write(Header(0, bigTiff));
        }

        var sink = new Sink(output, spool, bigTiff ? 16 : 8, bigTiff ? long.MaxValue : uint.MaxValue - ifdBytes);
        int degree = options.MaxDegreeOfParallelism == -1 ? Environment.ProcessorCount : options.MaxDegreeOfParallelism;
        foreach (Page page in pages)
        {
            EncodeChunks(page, options, degree, sink, cancellationToken);
        }

        var ifdOffsets = new ulong[pageCount];
        long position = Align(sink.Position);
        var ifds = new TiffIfdWriter[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            ifds[i] = Ifd(image, pages[i], bigTiff);
            ifdOffsets[i] = (ulong)position;
            position += ifds[i].Size;
        }

        if (spool is not null)
        {
            output.Write(Header(ifdOffsets[0], bigTiff));
            spool.CopyTo(output);
        }

        if (Align(sink.Position) != sink.Position)
        {
            output.WriteByte(0);
        }

        for (int i = 0; i < pageCount; i++)
        {
            output.Write(ifds[i].ToArray(ifdOffsets[i], i + 1 < pageCount ? ifdOffsets[i + 1] : 0));
        }

        if (seekable)
        {
            long end = output.Position;
            output.Position = start + (bigTiff ? 8 : 4);
            Span<byte> link = stackalloc byte[8];
            if (bigTiff)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(link, ifdOffsets[0]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(link, (uint)ifdOffsets[0]);
            }

            output.Write(link[..(bigTiff ? 8 : 4)]);
            output.Position = end;
        }
    }

    private static void Validate(Image image, TiffEncoderOptions options)
    {
        TiffCompression compression = options.Compression;
        bool tiled = options.TileSize > 0;
        if (tiled && options.RowsPerStrip > 0)
        {
            throw new ArgumentException("Rows per strip and the tile size cannot both be set.", nameof(options));
        }

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

        if (compression == TiffCompression.Jpeg && (tiled ? options.TileSize : image.Width) > JpegMaxWidth)
        {
            throw new NotSupportedException($"JPEG compression supports widths up to {JpegMaxWidth}.");
        }

        if (compression == TiffCompression.WebP && options.ColorType is not (TiffColorType.Auto or TiffColorType.Rgb or TiffColorType.Rgba))
        {
            throw new ArgumentException("WebP compression writes only RGB or RGBA images.", nameof(options));
        }

        if (compression == TiffCompression.WebP && (tiled ? options.TileSize : image.Width) > TiffWebP.MaxSide)
        {
            throw new NotSupportedException($"WebP compression supports {(tiled ? "tiles" : "widths")} up to {TiffWebP.MaxSide}.");
        }

        if (options.Predictor == TiffPredictor.Horizontal && options.ColorType == TiffColorType.BlackWhite)
        {
            throw new ArgumentException("The horizontal predictor applies only to 8- or 16-bit samples, not black and white.", nameof(options));
        }

        if (options.BitDepth == TiffBitDepth.Sixteen && compression is TiffCompression.Jpeg or TiffCompression.WebP or TiffCompression.CcittGroup4)
        {
            throw new ArgumentException($"{compression} compression stores 8-bit samples only; use TiffBitDepth.Auto or UpTo8.", nameof(options));
        }

        if (options.BitDepth == TiffBitDepth.Sixteen && options.ColorType is TiffColorType.Palette or TiffColorType.BlackWhite)
        {
            throw new ArgumentException($"A {options.ColorType} TIFF has no 16-bit samples; use TiffBitDepth.Auto or UpTo8.", nameof(options));
        }
    }

    /// <summary>The sample depth of a page, 8 or 16 bits (see <see cref="TiffBitDepth"/>).</summary>
    private static int Bits(ImageFrame frame, TiffEncoderOptions options) => options.BitDepth switch
    {
        TiffBitDepth.Sixteen => 16,
        TiffBitDepth.UpTo8 => 8,
        _ => frame.PixelType == PixelType.Rgba64
            && options.Compression is not (TiffCompression.Jpeg or TiffCompression.WebP or TiffCompression.CcittGroup4)
            && !TiffPixelScan.IsExact8(frame.GetPixelSpan<Rgba64>()) ? 16 : 8,
    };

    /// <summary>A copy of <paramref name="frame"/> at <paramref name="pixelType"/>: rounded to 8 bits, or widened exactly.</summary>
    private static ImageFrame Converted(ImageFrame frame, PixelType pixelType, List<Image> copies)
    {
        var copy = new Image(frame.Width, frame.Height, pixelType);
        copies.Add(copy);
        if (pixelType == PixelType.Rgba32)
        {
            frame.CopyPixelDataTo(copy.GetPixelByteSpan(), PixelFormat.Rgba32);
        }
        else
        {
            ReadOnlySpan<Rgba32> source = frame.GetPixelSpan<Rgba32>();
            Span<Rgba64> target = copy.GetPixelSpan<Rgba64>();
            for (int i = 0; i < source.Length; i++)
            {
                target[i] = Rgba64.FromRgba32(source[i]);
            }
        }

        return copy.Frames.RootFrame;
    }

    /// <summary>
    /// The depth, colour type, palette and chunk layout of one page; <paramref name="index"/> is -1 for a single page. A
    /// page at another depth than the frame stores reads a copy added to <paramref name="copies"/>.
    /// </summary>
    private static Page Plan(Image image, ImageFrame frame, TiffEncoderOptions options, int index, int count, List<Image> copies)
    {
        TiffCompression compression = options.Compression;
        int bits = Bits(frame, options);
        if ((bits == 16) != (frame.PixelType == PixelType.Rgba64))
        {
            frame = Converted(frame, bits == 16 ? PixelType.Rgba64 : PixelType.Rgba32, copies);
        }

        Dictionary<uint, byte>? palette = null;
        TiffColorType colorType;
        if (bits == 16)
        {
            if (options.ColorType is TiffColorType.Palette or TiffColorType.BlackWhite)
            {
                throw new ArgumentException($"A {options.ColorType} TIFF has no 16-bit samples; use TiffBitDepth.UpTo8 to round the image.", nameof(options));
            }

            (bool opaque, bool gray) = options.ColorType == TiffColorType.Auto ? TiffPixelScan.Scan(frame.GetPixelSpan<Rgba64>()) : default;
            colorType = options.ColorType != TiffColorType.Auto ? options.ColorType
                : (gray, opaque) switch
                {
                    (true, true) => TiffColorType.Grayscale,
                    (true, false) => TiffColorType.GrayscaleAlpha,
                    (false, true) => TiffColorType.Rgb,
                    _ => TiffColorType.Rgba,
                };
        }
        else
        {
            colorType = Choose8(frame.GetPixelSpan<Rgba32>(), options, out palette);
        }

        return Plan(image, frame, options, index, count, bits, colorType, palette);
    }

    private static TiffColorType Choose8(ReadOnlySpan<Rgba32> pixels, TiffEncoderOptions options, out Dictionary<uint, byte>? palette)
    {
        TiffCompression compression = options.Compression;
        palette = null;
        TiffColorType colorType = options.ColorType != TiffColorType.Auto ? options.ColorType
            : compression == TiffCompression.CcittGroup4 ? TiffColorType.BlackWhite
            : compression == TiffCompression.Jpeg ? (IsGray(pixels) ? TiffColorType.Grayscale : TiffColorType.Rgb)
            : compression == TiffCompression.WebP ? (IsOpaque(pixels) ? TiffColorType.Rgb : TiffColorType.Rgba)
            : Choose(pixels, bilevel: options.Predictor != TiffPredictor.Horizontal, out palette);
        if (colorType == TiffColorType.Palette && palette is null)
        {
            palette = TiffSamplePacker.Palette(pixels)
                ?? throw new InvalidOperationException("The image has more than 256 colours and cannot be saved as a palette TIFF.");
        }

        return colorType;
    }

    private static Page Plan(Image image, ImageFrame frame, TiffEncoderOptions options, int index, int count, int bits, TiffColorType colorType,
        Dictionary<uint, byte>? palette)
    {
        TiffCompression compression = options.Compression;
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

        int width = frame.Width, height = frame.Height;
        bool tiled = options.TileSize > 0;
        int chunkWidth = tiled ? options.TileSize : width;
        long rowBytes = colorType == TiffColorType.BlackWhite ? (chunkWidth + 7L) / 8 : (long)chunkWidth * samplesPerPixel * (bits / 8);
        int chunkHeight;
        if (tiled)
        {
            chunkHeight = options.TileSize;
        }
        else
        {
            // JPEG strips other than the last hold whole MCU rows (libtiff requires it).
            int block = compression != TiffCompression.Jpeg ? 1
                : colorType == TiffColorType.Rgb && options.JpegSubsampling is JpegSubsampling.Ratio420 or JpegSubsampling.Ratio440 ? 16
                : 8;
            chunkHeight = options.RowsPerStrip > 0
                ? (int)Math.Min((options.RowsPerStrip + (long)block - 1) / block * block, height)
                : (int)Math.Min(Math.Max(StripTarget / rowBytes / block, 1) * block, height);
            if (compression == TiffCompression.WebP)
            {
                chunkHeight = Math.Min(chunkHeight, TiffWebP.MaxSide);
            }
        }

        return new Page(frame, colorType, samplesPerPixel, compression, horizontal, tiled, chunkWidth, chunkHeight, rowBytes,
            options.JpegSubsampling, palette, index, count,
            (options.Metadata & MetadataKinds.Icc) != 0 ? image.Metadata.IccProfile : null,
            (options.Metadata & MetadataKinds.Xmp) != 0 ? image.Metadata.Xmp : null,
            (options.Metadata & MetadataKinds.Exif) != 0 ? image.Metadata.Exif : null)
        {
            Bits = bits,
        };
    }

    /// <summary>
    /// Compresses the chunks of a page and writes them in order: one at a time, or in batches compressed in parallel,
    /// each thread with its own <see cref="ChunkEncoder"/>, so the bytes do not depend on <paramref name="degree"/>.
    /// </summary>
    private static void EncodeChunks(Page page, TiffEncoderOptions options, int degree, Sink sink, CancellationToken cancellationToken)
    {
        degree = Math.Min(degree, page.Chunks);
        if (degree <= 1)
        {
            using var encoder = new ChunkEncoder(page, options, options.MaxDegreeOfParallelism);
            for (int c = 0; c < page.Chunks; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink.Write(page, c, encoder.Encode(c, cancellationToken));
            }

            return;
        }

        var encoders = new ConcurrentBag<ChunkEncoder>();
        var results = new (byte[]? Data, int Length)[degree * BatchPerThread];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken };
        try
        {
            for (int first = 0; first < page.Chunks; first += results.Length)
            {
                int batch = Math.Min(results.Length, page.Chunks - first);
                try
                {
                    Parallel.For(0, batch, parallel,
                        () => encoders.TryTake(out ChunkEncoder? encoder) ? encoder : new ChunkEncoder(page, options, 1),
                        (i, _, encoder) =>
                        {
                            ReadOnlySpan<byte> data = encoder.Encode(first + i, cancellationToken);
                            byte[] copy = ArrayPool<byte>.Shared.Rent(data.Length);
                            data.CopyTo(copy);
                            results[i] = (copy, data.Length);
                            return encoder;
                        },
                        encoders.Add);
                }
                catch (AggregateException e) when (e.InnerExceptions.Count == 1)
                {
                    ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
                }

                for (int i = 0; i < batch; i++)
                {
                    sink.Write(page, first + i, results[i].Data.AsSpan(0, results[i].Length));
                    ArrayPool<byte>.Shared.Return(results[i].Data!);
                    results[i] = default;
                }
            }
        }
        finally
        {
            foreach ((byte[]? data, _) in results)
            {
                if (data is not null)
                {
                    ArrayPool<byte>.Shared.Return(data);
                }
            }

            foreach (ChunkEncoder encoder in encoders)
            {
                encoder.Dispose();
            }
        }
    }

    private static NotSupportedException TooLarge() =>
        new("The TIFF file would exceed 4 GiB, which classic TIFF cannot address; use TiffContainer.BigTiff.");

    /// <summary>
    /// The smallest lossless type: black and white when <paramref name="bilevel"/> allows it and every pixel is opaque
    /// black or white, grey when every pixel has
    /// R = G = B, else a palette when the image is opaque, has at
    /// most 256 colours and enough pixels to outweigh the colour map, else RGB; alpha only when a pixel is not opaque.
    /// </summary>
    private static TiffColorType Choose(ReadOnlySpan<Rgba32> pixels, bool bilevel, out Dictionary<uint, byte>? palette)
    {
        palette = null;
        // The scan may stop once a pixel has alpha and one is not grey: RGBA whatever the rest holds.
        (bool opaque, bool gray, bool allBilevel) = TiffPixelScan.Scan(pixels, opaque: true, gray: true);
        bool alpha = !opaque;
        bilevel &= allBilevel;
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
    private static bool IsGray(ReadOnlySpan<Rgba32> pixels) => TiffPixelScan.Scan(pixels, opaque: false, gray: true).Gray;

    /// <summary>Whether every pixel is opaque.</summary>
    private static bool IsOpaque(ReadOnlySpan<Rgba32> pixels) => TiffPixelScan.Scan(pixels, opaque: true, gray: false).Opaque;

    private static TiffIfdWriter Ifd(Image image, Page layout, bool bigTiff)
    {
        var ifd = new TiffIfdWriter(bigTiff: bigTiff);
        if (layout.Index >= 0)
        {
            ifd.AddLong(TiffTag.NewSubfileType, SubfilePage);
        }

        ifd.AddLong(TiffTag.ImageWidth, (uint)layout.Frame.Width);
        ifd.AddLong(TiffTag.ImageLength, (uint)layout.Frame.Height);
        ushort depth = (ushort)layout.Bits;
        Span<ushort> bits = [depth, depth, depth, depth];
        ifd.AddShort(TiffTag.BitsPerSample, layout.ColorType == TiffColorType.BlackWhite ? [1] : bits[..layout.SamplesPerPixel]);
        ifd.AddShort(TiffTag.Compression, layout.Compression switch
        {
            TiffCompression.PackBits => (ushort)32773,
            TiffCompression.Lzw => (ushort)5,
            TiffCompression.Deflate => (ushort)8,
            TiffCompression.Jpeg => (ushort)7,
            TiffCompression.CcittGroup4 => (ushort)4,
            TiffCompression.WebP => (ushort)50001,
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
        ifd.AddShort(TiffTag.SamplesPerPixel, (ushort)layout.SamplesPerPixel);
        if (layout.Tiled)
        {
            ifd.AddLong(TiffTag.TileWidth, (uint)layout.ChunkWidth);
            ifd.AddLong(TiffTag.TileLength, (uint)layout.ChunkHeight);
            ifd.AddOffsets(TiffTag.TileOffsets, layout.Offsets);
            ifd.AddOffsets(TiffTag.TileByteCounts, layout.Counts);
        }
        else
        {
            ifd.AddOffsets(TiffTag.StripOffsets, layout.Offsets);
            ifd.AddLong(TiffTag.RowsPerStrip, (uint)layout.ChunkHeight);
            ifd.AddOffsets(TiffTag.StripByteCounts, layout.Counts);
        }

        ifd.AddResolution(image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution);
        ifd.AddShort(TiffTag.PlanarConfiguration, 1);
        if (layout.Index >= 0)
        {
            ifd.AddShort(TiffTag.PageNumber, (ushort)layout.Index, (ushort)layout.Count);
        }

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

        if (layout.Exif is { Length: > 0 })
        {
            // Software names this encoder, not the program the block came from.
            TiffExifWriter.AddTo(ifd, layout.Exif, software: false);
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

    private static byte[] Header(ulong ifdOffset, bool bigTiff)
    {
        if (!bigTiff)
        {
            byte[] header = [(byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)ifdOffset);
            return header;
        }

        // BigTIFF: version 43, 8-byte offsets, a reserved zero, then the first IFD offset.
        byte[] big = [(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt64LittleEndian(big.AsSpan(8), ifdOffset);
        return big;
    }

    private static long Align(long offset) => (offset + 1) & ~1L;

    /// <summary>
    /// One page: its frame, samples and chunk grid (strips are chunks as wide as the image), and the offsets and byte
    /// counts of its chunks once written. <see cref="Index"/> is -1 when the file has one page.
    /// </summary>
    private sealed record Page(ImageFrame Frame, TiffColorType ColorType, int SamplesPerPixel, TiffCompression Compression,
        bool Horizontal, bool Tiled, int ChunkWidth, int ChunkHeight, long RowBytes, JpegSubsampling JpegSubsampling,
        Dictionary<uint, byte>? Palette, int Index, int Count, byte[]? Icc, byte[]? Xmp, byte[]? Exif)
    {
        /// <summary>The bits of each sample, 8 or 16; black and white has 1 whatever this says.</summary>
        public int Bits { get; init; } = 8;

        public int Across => Tiled ? (Frame.Width + ChunkWidth - 1) / ChunkWidth : 1;

        public int Chunks => Across * (int)(((long)Frame.Height + ChunkHeight - 1) / ChunkHeight);

        public ulong[] Offsets => field ??= new ulong[Chunks];

        public ulong[] Counts => field ??= new ulong[Chunks];

        /// <summary>The samples of a full chunk.</summary>
        public long ChunkBytes => RowBytes * ChunkHeight;

        /// <summary>The largest compressed chunk.</summary>
        public long Bound => Compression switch
        {
            TiffCompression.PackBits => PackBitsEncoder.MaxLength(ChunkBytes),
            TiffCompression.Lzw => LzwEncoder.MaxLength(ChunkBytes),
            _ => ChunkBytes,
        };

        /// <summary>The samples of every chunk; tiles are full at the edges, the last strip is not.</summary>
        public long UncompressedBytes => Tiled ? ChunkBytes * Chunks : RowBytes * Frame.Height;

        /// <summary>The position and visible size of chunk <paramref name="chunk"/>.</summary>
        public (int X, int Y, int Width, int Rows) Region(int chunk)
        {
            int x = chunk % Across * ChunkWidth, y = chunk / Across * ChunkHeight;
            return (x, y, Math.Min(ChunkWidth, Frame.Width - x), Math.Min(ChunkHeight, Frame.Height - y));
        }
    }

    /// <summary>
    /// The output after the header, written directly or spooled; it records each chunk's offset and byte count and fails
    /// before a chunk would end past <paramref name="limit"/> (where the IFDs no longer fit classic TIFF).
    /// </summary>
    private sealed class Sink(Stream output, TiffSpool? spool, long position, long limit)
    {
        public long Position { get; private set; } = position;

        public void Write(Page page, int chunk, ReadOnlySpan<byte> data)
        {
            if (Align(Position + data.Length) > limit)
            {
                throw TooLarge();
            }

            if (spool is null)
            {
                output.Write(data);
            }
            else
            {
                spool.Write(data);
            }

            page.Offsets[chunk] = (ulong)Position;
            page.Counts[chunk] = (ulong)data.Length;
            Position += data.Length;
        }
    }

    /// <summary>
    /// Packs, differences and compresses one chunk at a time with buffers and codec state of its own, so encoders on
    /// different threads share nothing but the page's pixels. A tile past the image edge repeats the last column and row.
    /// </summary>
    private sealed class ChunkEncoder : IDisposable
    {
        private readonly Page _page;
        private readonly byte[] _samples;
        private readonly byte[]? _packed;
        private readonly Rgba32[]? _row;
        private readonly Rgba64[]? _wideRow;
        private readonly LzwEncoder? _lzw;
        private readonly Fax4Encoder? _fax4;
        private readonly DeflateEncoder? _deflate;
        private readonly MemoryStream? _part;
        private readonly EncoderOptions? _partOptions;

        public ChunkEncoder(Page page, TiffEncoderOptions options, int partParallelism)
        {
            _page = page;
            TiffCompression compression = page.Compression;
            bool image = compression is TiffCompression.Jpeg or TiffCompression.WebP;
            _samples = image ? [] : ArrayPool<byte>.Shared.Rent((int)page.ChunkBytes);
            _packed = compression is TiffCompression.PackBits or TiffCompression.Lzw ? ArrayPool<byte>.Shared.Rent((int)page.Bound) : null;
            _row = page.Tiled && !image && page.Bits == 8 ? ArrayPool<Rgba32>.Shared.Rent(page.ChunkWidth) : null;
            _wideRow = page.Tiled && page.Bits == 16 ? ArrayPool<Rgba64>.Shared.Rent(page.ChunkWidth) : null;
            _lzw = compression == TiffCompression.Lzw ? new LzwEncoder() : null;
            _fax4 = compression == TiffCompression.CcittGroup4 ? new Fax4Encoder() : null;
            _deflate = compression == TiffCompression.Deflate ? new DeflateEncoder(options.DeflateLevel) : null;
            _part = image ? new MemoryStream() : null;
            _partOptions = compression switch
            {
                TiffCompression.Jpeg => new JpegEncoderOptions
                {
                    Quality = options.JpegQuality,
                    // A single component must have sampling factors 1x1, which libtiff checks (JPEGPreDecode).
                    Subsampling = page.ColorType == TiffColorType.Grayscale ? JpegSubsampling.Ratio444 : options.JpegSubsampling,
                    ColorMode = page.ColorType == TiffColorType.Grayscale ? JpegColorMode.Grayscale : JpegColorMode.Color,
                    Metadata = MetadataKinds.None,
                    MaxDegreeOfParallelism = partParallelism,
                    Configuration = options.Configuration,
                },
                // port: libtiff/tif_webp.c TWebPSetupEncode: the default preset at the given quality; lossless keeps the
                // colour under transparent pixels (libtiff leaves that to libwebp's default).
                TiffCompression.WebP => new WebPEncoderOptions
                {
                    Quality = options.WebPQuality,
                    Lossless = options.WebPLossless,
                    Exact = options.WebPLossless,
                    Metadata = MetadataKinds.None,
                    MaxDegreeOfParallelism = partParallelism,
                    Configuration = options.Configuration,
                },
                _ => null,
            };
        }

        /// <summary>The compressed bytes of chunk <paramref name="chunk"/>, valid until the next call.</summary>
        public ReadOnlySpan<byte> Encode(int chunk, CancellationToken cancellationToken)
        {
            Page page = _page;
            (int x0, int y0, int width, int visibleRows) = page.Region(chunk);
            // Tiles are always full; strips have their own rows.
            int rows = page.Tiled ? page.ChunkHeight : visibleRows;
            if (_part is not null)
            {
                return EncodePart(x0, y0, width, visibleRows, rows, cancellationToken);
            }

            int rowBytes = (int)page.RowBytes;
            Span<byte> samples = _samples.AsSpan(0, rows * rowBytes);
            if (page.Bits == 16)
            {
                PackWide(samples, x0, y0, width, visibleRows, rows);
            }
            else
            {
                Pack(samples, x0, y0, width, visibleRows, rows);
            }

            return page.Compression switch
            {
                TiffCompression.PackBits => _packed.AsSpan(0, PackBitsEncoder.Encode(samples, rowBytes, _packed)),
                TiffCompression.Lzw => _packed.AsSpan(0, _lzw!.Encode(samples, _packed)),
                TiffCompression.Deflate => _deflate!.Encode(samples),
                TiffCompression.CcittGroup4 => _fax4!.Encode(samples, rowBytes, page.ChunkWidth),
                _ => samples,
            };
        }

        private void PackWide(Span<byte> samples, int x0, int y0, int width, int visibleRows, int rows)
        {
            Page page = _page;
            int rowBytes = (int)page.RowBytes;
            ReadOnlySpan<Rgba64> pixels = page.Frame.GetPixelSpan<Rgba64>();
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<Rgba64> source = pixels.Slice((y0 + Math.Min(y, visibleRows - 1)) * page.Frame.Width + x0, width);
                ReadOnlySpan<Rgba64> row = _wideRow is null ? source : Pad(source, _wideRow.AsSpan(0, page.ChunkWidth));
                TiffSamplePacker.Pack(row, samples.Slice(y * rowBytes, rowBytes), page.SamplesPerPixel);
            }

            if (page.Horizontal)
            {
                HorizontalPredictor.Encode16(samples, rowBytes, page.SamplesPerPixel);
            }
        }

        private void Pack(Span<byte> samples, int x0, int y0, int width, int visibleRows, int rows)
        {
            Page page = _page;
            int rowBytes = (int)page.RowBytes;
            ReadOnlySpan<Rgba32> pixels = page.Frame.GetPixelSpan<Rgba32>();
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<Rgba32> source = pixels.Slice((y0 + Math.Min(y, visibleRows - 1)) * page.Frame.Width + x0, width);
                ReadOnlySpan<Rgba32> row = _row is null ? source : Pad(source, _row.AsSpan(0, page.ChunkWidth));
                Span<byte> target = samples.Slice(y * rowBytes, rowBytes);
                if (page.Palette is not null)
                {
                    TiffSamplePacker.PackIndices(row, target, page.Palette);
                }
                else if (page.ColorType == TiffColorType.BlackWhite)
                {
                    TiffSamplePacker.PackBlackWhite(row, target);
                }
                else
                {
                    TiffSamplePacker.Pack(row, target, page.SamplesPerPixel);
                }
            }

            if (page.Horizontal)
            {
                HorizontalPredictor.Encode8(samples, rowBytes, page.SamplesPerPixel);
            }
        }

        public void Dispose()
        {
            if (_samples.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_samples);
            }

            if (_packed is not null)
            {
                ArrayPool<byte>.Shared.Return(_packed);
            }

            if (_row is not null)
            {
                ArrayPool<Rgba32>.Shared.Return(_row);
            }

            if (_wideRow is not null)
            {
                ArrayPool<Rgba64>.Shared.Return(_wideRow);
            }

            _deflate?.Dispose();
            _part?.Dispose();
        }

        /// <summary>
        /// One chunk as a complete JPEG or WebP stream of <paramref name="rows"/> rows. Without alpha (RGBA WebP),
        /// alpha is dropped rather than composited, as for the other compressions; for JPEG the colour mode is fixed so
        /// that a grey chunk of a colour image still has the three components the IFD declares.
        /// </summary>
        // port: libtiff/tif_jpeg.c JPEGSetupEncode and JPEGEncode write one interchange stream per strip or tile when no
        // JPEGTables are shared; libtiff itself shares the tables, which TTN2 leaves optional. libtiff/tif_webp.c
        // TWebPPostEncode encodes each strip or tile as one WebP stream of the chunk's own size.
        private ReadOnlySpan<byte> EncodePart(int x0, int y0, int width, int visibleRows, int rows, CancellationToken cancellationToken)
        {
            Page page = _page;
            bool alpha = page.ColorType == TiffColorType.Rgba;
            int partWidth = page.Tiled ? page.ChunkWidth : width;
            ReadOnlySpan<Rgba32> pixels = page.Frame.GetPixelSpan<Rgba32>();
            using var part = new Image(partWidth, rows);
            Span<Rgba32> target = part.GetPixelSpan<Rgba32>();
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<Rgba32> source = pixels.Slice((y0 + Math.Min(y, visibleRows - 1)) * page.Frame.Width + x0, width);
                Span<Rgba32> row = target.Slice(y * partWidth, partWidth);
                Pad(source, row);
                if (!alpha)
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        row[i].A = 255;
                    }
                }
            }

            _part!.SetLength(0);
            part.Save(_part, _partOptions!, cancellationToken);
            return _part.GetBuffer().AsSpan(0, (int)_part.Length);
        }

        /// <summary>Copies <paramref name="source"/> into <paramref name="row"/> and repeats its last pixel to the end.</summary>
        private static ReadOnlySpan<T> Pad<T>(ReadOnlySpan<T> source, Span<T> row)
        {
            source.CopyTo(row);
            row[source.Length..].Fill(source[^1]);
            return row;
        }
    }
}
