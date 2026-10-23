using PictureSharp.Formats.Jpeg;

namespace PictureSharp.Formats.Tiff.Fuzz;

/// <summary>
/// The fuzzed entry points. Each one returns normally when the plugin kept its contract: the input decoded, or it was
/// refused with <see cref="ImageFormatException"/> or <see cref="NotSupportedException"/>. Anything else escapes as
/// <see cref="ContractViolation"/> or as the original exception, which AFL++ records as a crash.
/// </summary>
internal static class FuzzTargets
{
    /// <summary>
    /// Limits small enough that a corrupt header cannot make one input slow, and that make <see cref="Budget"/> tight;
    /// documented in tools/README.md.
    /// </summary>
    public const long MaxPixels = 1 << 18;

    public const long MaxMetadataBytes = 1 << 20;

    /// <summary>Pages decoded per input by the load target, after the first.</summary>
    private const int ExtraPages = 3;

    private static readonly TiffFormat Format = new();

    /// <summary>
    /// The core formats stay registered: JPEG and WebP strips are decoded by the core codecs. It is also the default,
    /// which <c>ToTiff</c> encodes with.
    /// </summary>
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(Format).Build();

    public static DecoderOptions Options { get; } = new()
    {
        Configuration = Configuration,
        MaxPixels = MaxPixels,
        MaxMetadataBytes = MaxMetadataBytes,
        MaxDegreeOfParallelism = 1,
    };

    private static readonly byte[] Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();

    public static IReadOnlyList<string> Names { get; } = ["load", "identify", "metadata", "edit", "encode"];

    /// <summary>
    /// The target called <paramref name="name"/>. Inputs without a TIFF signature are skipped, so that mutations into
    /// another format do not fuzz the core's decoders.
    /// </summary>
    public static Action<byte[]> Get(string name)
    {
        Action<byte[]> target = name switch
        {
            "load" => Load,
            "identify" => Identify,
            "metadata" => Metadata,
            "edit" => Edit,
            "encode" => Encode,
            "parallel" => Parallel,
            _ => throw new ArgumentException($"Unknown target '{name}'; expected one of {string.Join(", ", Names)}.", nameof(name)),
        };
        int offset = name == "encode" ? 4 : 0;
        return data =>
        {
            if (data.Length >= offset && Format.IsSupported(data.AsSpan(offset)))
            {
                target(data);
            }
        };
    }

    /// <summary>Decodes the first page, then up to <see cref="ExtraPages"/> more if <c>Identify</c> counts them.</summary>
    public static void Load(byte[] data)
    {
        int pages = 1;
        Contract(data.Length, () => pages = Image.Identify(data, Options).ContainerItemCount ?? 1);
        for (int page = 0; page < Math.Min(Math.Max(pages, 1), 1 + ExtraPages); page++)
        {
            DecoderOptions options = page == 0 ? Options : Options with { FormatOptions = [new TiffDecoderOptions { Page = page }] };
            Contract(data.Length, () =>
            {
                using Image image = Image.Load(data, options);
                Statistics.Decoded++;
            });
        }
    }

    /// <summary>
    /// For <c>replay</c> only (threads make coverage nondeterministic): every degree of parallelism decodes the same
    /// pixels, or fails with the same exception (docs/decoding.md).
    /// </summary>
    public static void Parallel(byte[] data)
    {
        string? single = Outcome(data, 1);
        foreach (int degree in (ReadOnlySpan<int>)[2, 4, 16])
        {
            if (Outcome(data, degree) is var outcome && outcome != single)
            {
                throw new ContractViolation($"One thread: {single}; {degree} threads: {outcome}.");
            }
        }
    }

    private static string? Outcome(byte[] data, int degree)
    {
        try
        {
            using Image image = Image.Load(data, Options with { MaxDegreeOfParallelism = degree, IgnoreMetadata = true });
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.GetPixelByteSpan()));
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary><c>Identify</c> must agree with <c>Load</c> on the size of every page it decodes.</summary>
    public static void Identify(byte[] data)
    {
        ImageInfo? info = null;
        Contract(data.Length, () => info = Image.Identify(data, Options));
        if (info is null)
        {
            return;
        }

        Contract(data.Length, () =>
        {
            using Image image = Image.Load(data, Options with { IgnoreMetadata = true });
            if ((image.Width, image.Height) != (info.Width, info.Height))
            {
                throw new ContractViolation($"Identify says {info.Width}x{info.Height}, Load decodes {image.Width}x{image.Height}.");
            }
        });
    }

    /// <summary><c>ReadMetadata</c> must return the blocks <c>Load</c> attaches to the image.</summary>
    public static void Metadata(byte[] data)
    {
        ImageMetadata? read = null;
        Contract(data.Length, () => read = Image.ReadMetadata(data, Options));
        if (read is null)
        {
            return;
        }

        Contract(data.Length, () =>
        {
            using Image image = Image.Load(data, Options);
            ImageMetadata loaded = image.Metadata;
            Same("ICC", read.IccProfile, loaded.IccProfile);
            Same("EXIF", read.Exif, loaded.Exif);
            Same("XMP", read.Xmp, loaded.Xmp);
        });
    }

    /// <summary>
    /// <c>EditMetadata</c> either refuses the file or writes one whose first page decodes to the same pixels as before
    /// (when it decoded before) and whose metadata carries the edit.
    /// </summary>
    public static void Edit(byte[] data)
    {
        byte[]? edited = null;
        Contract(data.Length, () => edited = Image.EditMetadata(data, metadata =>
        {
            metadata.Xmp = Xmp;
            metadata.Orientation = ExifOrientation.RightTop;
            metadata.HorizontalResolution = 150;
        }, Options));
        if (edited is null)
        {
            return;
        }

        byte[]? before = null;
        Contract(data.Length, () => before = Pixels(data));
        byte[]? after = null;
        Exception? failure = null;
        try
        {
            after = Pixels(edited);
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            failure = e;
        }

        if (before is not null && (after is null || !before.AsSpan().SequenceEqual(after)))
        {
            throw new ContractViolation(after is null
                ? $"The edited file no longer decodes: {failure?.Message}"
                : "The edited file decodes to different pixels.");
        }

        ImageMetadata metadata = Image.ReadMetadata(edited, Options);
        if (!metadata.Xmp.AsSpan().SequenceEqual(Xmp) || metadata.Orientation != ExifOrientation.RightTop)
        {
            throw new ContractViolation("The edited file does not carry the new XMP and orientation.");
        }
    }

    /// <summary>
    /// The first four bytes choose the encoder options, the rest is a TIFF file. A page that decodes is encoded, which
    /// may only fail for an option the image cannot use; the output must decode, and to the same pixels for lossless
    /// options.
    /// </summary>
    public static void Encode(byte[] data)
    {
        if (data.Length < 4)
        {
            return;
        }

        Image? image = null;
        Contract(data.Length, () => image = Image.Load(data.AsMemory(4), Options with { IgnoreMetadata = (data[3] & 0x80) != 0 }));
        if (image is null)
        {
            return;
        }

        using (image)
        {
            TiffEncoderOptions options = EncoderOptions(data);
            byte[] encoded;
            try
            {
                encoded = image.ToTiff(options);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException
                || (e is InvalidOperationException && options.ColorType == TiffColorType.Palette))
            {
                // The documented refusals of an option combination (TiffEncoder.Validate, palette over 256 colours).
                return;
            }

            // The image's own size suffices, except that a JPEG or WebP tile is checked against MaxPixels on its own
            // (ImageDecodeContext.DecodeEmbeddedPart) and may overhang a small image (docs/decoding.md).
            long pixels = Math.Max(image.Width * (long)image.Height, (long)options.TileSize * options.TileSize);
            using Image decoded = Image.Load(encoded, Options with { MaxPixels = pixels });
            if ((decoded.Width, decoded.Height) != (image.Width, image.Height))
            {
                throw new ContractViolation($"Encoded {image.Width}x{image.Height}, decoded {decoded.Width}x{decoded.Height} ({options}).");
            }

            if (IsLossless(options) && !decoded.GetPixelByteSpan().SequenceEqual(image.GetPixelByteSpan()))
            {
                throw new ContractViolation($"Lossless encoding changed the pixels ({options}).");
            }

            Statistics.Decoded++;
        }
    }

    public static TiffEncoderOptions EncoderOptions(ReadOnlySpan<byte> data)
    {
        byte a = data[0], b = data[1], c = data[2], d = data[3];
        var compression = (TiffCompression)(a % 7);
        int layout = c & 0x7f;
        return new TiffEncoderOptions
        {
            Compression = compression,
            Predictor = (TiffPredictor)((a >> 3) % 3),
            Container = (TiffContainer)((a >> 5) % 3),
            ColorType = (TiffColorType)(b % 7),
            WebPLossless = (b & 0x08) != 0,
            JpegSubsampling = ((b >> 4) & 3) switch { 0 => JpegSubsampling.Ratio420, 1 => JpegSubsampling.Ratio422, 2 => JpegSubsampling.Ratio440, _ => JpegSubsampling.Ratio444 },
            RowsPerStrip = (c & 0x80) == 0 ? layout : 0,
            TileSize = (c & 0x80) != 0 ? 16 * (1 + (layout & 15)) : 0,
            DeflateLevel = d % 10,
            JpegQuality = 1 + (d & 0x7f) % 100,
            WebPQuality = 1 + (d & 0x7f) % 100,
            MaxDegreeOfParallelism = 1,
        };
    }

    private static bool IsLossless(TiffEncoderOptions options) =>
        options.ColorType is TiffColorType.Auto or TiffColorType.Rgba
        && (options.Compression is TiffCompression.None or TiffCompression.PackBits or TiffCompression.Lzw or TiffCompression.Deflate
            || (options.Compression == TiffCompression.WebP && options.WebPLossless));

    private static byte[] Pixels(byte[] data)
    {
        using Image image = Image.Load(data, Options with { IgnoreMetadata = true });
        return image.GetPixelByteSpan().ToArray();
    }

    private static void Same(string block, byte[]? read, byte[]? loaded)
    {
        if (!read.AsSpan().SequenceEqual(loaded))
        {
            throw new ContractViolation($"ReadMetadata and Load differ in the {block} block ({read?.Length} and {loaded?.Length} bytes).");
        }
    }

    /// <summary>
    /// The managed memory one call may allocate for an input of <paramref name="inputLength"/> bytes, after
    /// docs/decoding.md: the image and a copy (4 bytes per pixel each), the raw samples of a strip or tile and a
    /// second such buffer (up to 16 samples of 64 bits, 128 bytes per pixel, plus the 16 MiB a tile may overhang), the
    /// metadata twice and the input four times. The total allocated is measured, an upper bound of the peak; it catches
    /// allocations sized by values in the file rather than by the limits.
    /// </summary>
    public static long Budget(long inputLength) =>
        ((2 * 4) + (2 * 128)) * MaxPixels + (2 * (16 << 20)) + (2 * MaxMetadataBytes) + (4 * inputLength) + (16 << 20);

    /// <summary>
    /// Runs <paramref name="action"/>, swallowing the exceptions the plugin may throw for any input, and checks that it
    /// stayed within <see cref="Budget"/>.
    /// </summary>
    private static void Contract(int inputLength, Action action)
    {
        long start = GC.GetTotalAllocatedBytes(precise: true);
        try
        {
            action();
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
        }
        finally
        {
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - start;
            Statistics.Record(allocated);
            if (allocated > Budget(inputLength))
            {
                throw new ContractViolation($"One call allocated {allocated:N0} bytes for {inputLength:N0} input bytes; the budget is {Budget(inputLength):N0}.");
            }
        }
    }
}

/// <summary>What <c>replay</c> reports.</summary>
internal static class Statistics
{
    public static long MaxAllocated { get; private set; }

    /// <summary>Pages the load target decoded, or images the encode target wrote and read back.</summary>
    public static int Decoded { get; set; }

    public static void Record(long allocated) => MaxAllocated = Math.Max(MaxAllocated, allocated);

    public static void Reset() => MaxAllocated = 0;
}

/// <summary>A broken guarantee other than an unexpected exception type.</summary>
internal sealed class ContractViolation(string message) : Exception(message);
