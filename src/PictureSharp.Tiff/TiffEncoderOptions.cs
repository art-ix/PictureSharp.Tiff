namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Options for the TIFF encoder, which writes every frame as one little-endian TIFF page, in strips or tiles with
/// contiguous 8-bit (or 1-bit black-and-white) samples, by default LZW-compressed with the horizontal predictor.
/// Resolution is always written (in dots per inch). When <see cref="EncoderOptions.Metadata"/> allows them, ICC and XMP
/// are written as tags 34675 and 700, and the EXIF block as the image IFD's descriptive tags and orientation with its
/// EXIF, GPS and interoperability IFDs, on every page; Software always names this encoder, and an EXIF block that is not
/// a TIFF stream is left out. The output does not depend on <see cref="MaxDegreeOfParallelism"/>.
/// </summary>
/// <example>
/// <code>
/// image.SaveAsTiff("scan.tif", new TiffEncoderOptions { ColorType = TiffColorType.Grayscale });
/// </code>
/// </example>
public sealed record TiffEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => TiffFormat.Descriptor;

    /// <summary>The compression of each strip or tile. Default: <see cref="TiffCompression.Lzw"/>.</summary>
    public TiffCompression Compression
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Compression), value, "Unknown TIFF compression.");
    } = TiffCompression.Lzw;

    /// <summary>
    /// The predictor applied before compression. <see cref="TiffPredictor.Horizontal"/> with
    /// <see cref="TiffCompression.None"/>, <see cref="TiffCompression.PackBits"/>,
    /// <see cref="TiffCompression.CcittGroup4"/> or <see cref="TiffColorType.BlackWhite"/> makes saving fail with
    /// <see cref="ArgumentException"/>; with <see cref="TiffColorType.Auto"/> it keeps black-and-white images grey. Default:
    /// <see cref="TiffPredictor.Auto"/>.
    /// </summary>
    public TiffPredictor Predictor
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Predictor), value, "Unknown TIFF predictor.");
    }

    /// <summary>The zlib level for <see cref="TiffCompression.Deflate"/>, 0 (store) to 9 (best). Default: 6.</summary>
    public int DeflateLevel
    {
        get;
        init => field = value is >= 0 and <= 9 ? value : throw new ArgumentOutOfRangeException(nameof(DeflateLevel), value, "The Deflate level must be between 0 and 9.");
    } = 6;

    /// <summary>The quality of <see cref="TiffCompression.Jpeg"/>, 1 to 100, as for the JPEG encoder. Default: 75.</summary>
    public int JpegQuality
    {
        get;
        init => field = value is >= 1 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(JpegQuality), value, "The JPEG quality must be between 1 and 100.");
    } = 75;

    /// <summary>
    /// The chroma subsampling of colour <see cref="TiffCompression.Jpeg"/> strips, written as YCbCrSubSampling (tag
    /// 530). Default: <see cref="Formats.Jpeg.JpegSubsampling.Ratio420"/>, as libtiff.
    /// </summary>
    public Formats.Jpeg.JpegSubsampling JpegSubsampling
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(JpegSubsampling), value, "Unknown JPEG subsampling.");
    } = Formats.Jpeg.JpegSubsampling.Ratio420;

    /// <summary>
    /// The quality of lossy <see cref="TiffCompression.WebP"/>, 1 to 100, as for the WebP encoder; for lossless WebP,
    /// the compression effort. Default: 75, as libtiff.
    /// </summary>
    public int WebPQuality
    {
        get;
        init => field = value is >= 1 and <= 100 ? value : throw new ArgumentOutOfRangeException(nameof(WebPQuality), value, "The WebP quality must be between 1 and 100.");
    } = 75;

    /// <summary>
    /// Whether <see cref="TiffCompression.WebP"/> strips are lossless (VP8L), which keeps every value, including the
    /// colour of fully transparent pixels. Default: false, as libtiff.
    /// </summary>
    public bool WebPLossless { get; init; }

    /// <summary>The samples to write. Default: <see cref="TiffColorType.Auto"/>.</summary>
    public TiffColorType ColorType
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(ColorType), value, "Unknown TIFF colour type.");
    }

    /// <summary>
    /// The number of rows in each strip, or 0 to choose strips of about 64 KiB of uncompressed samples. Values above the
    /// image height mean one strip. Setting it together with <see cref="TileSize"/> makes saving fail with
    /// <see cref="ArgumentException"/>. Default: 0.
    /// </summary>
    public int RowsPerStrip
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(RowsPerStrip), value, "Rows per strip cannot be negative.");
    }

    /// <summary>
    /// The width and height of square tiles, a positive multiple of 16, or 0 to write strips. Tiles at the right and
    /// bottom edges are padded by repeating the last column and row. Default: 0.
    /// </summary>
    public int TileSize
    {
        get;
        init => field = value >= 0 && value % 16 == 0 ? value : throw new ArgumentOutOfRangeException(nameof(TileSize), value, "The tile size must be 0 or a positive multiple of 16.");
    }

    /// <summary>Classic TIFF or BigTIFF. Default: <see cref="TiffContainer.Auto"/>.</summary>
    public TiffContainer Container
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Container), value, "Unknown TIFF container.");
    }

    /// <summary>
    /// Whether every frame becomes a page (NewSubfileType 2 and PageNumber on each page when there are several); false
    /// writes the first frame only. Each page chooses its own <see cref="TiffColorType.Auto"/> colour type. Default: true.
    /// </summary>
    public bool SaveAllPages { get; init; } = true;

    /// <summary>
    /// The maximum number of strips or tiles compressed at once (1 = single-threaded, -1 = one per processor). Default: -1.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value is -1 or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(MaxDegreeOfParallelism), value, "Expected -1 or a positive number.");
    } = -1;
}
