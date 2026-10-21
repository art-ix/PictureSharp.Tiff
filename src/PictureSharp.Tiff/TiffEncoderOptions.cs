namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Options for the TIFF encoder, which writes the first frame as one little-endian classic TIFF page in strips with
/// contiguous 8-bit samples, by default LZW-compressed with the horizontal predictor. Resolution is always written (in dots per inch); ICC and XMP blocks are written as tags
/// 34675 and 700 when <see cref="EncoderOptions.Metadata"/> allows them.
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

    /// <summary>The compression of each strip. Default: <see cref="TiffCompression.Lzw"/>.</summary>
    public TiffCompression Compression
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Compression), value, "Unknown TIFF compression.");
    } = TiffCompression.Lzw;

    /// <summary>
    /// The predictor applied before compression. <see cref="TiffPredictor.Horizontal"/> with
    /// <see cref="TiffCompression.None"/> or <see cref="TiffCompression.PackBits"/> makes saving fail with
    /// <see cref="ArgumentException"/>. Default: <see cref="TiffPredictor.Auto"/>.
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

    /// <summary>The samples to write. Default: <see cref="TiffColorType.Auto"/>.</summary>
    public TiffColorType ColorType
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(ColorType), value, "Unknown TIFF colour type.");
    }

    /// <summary>
    /// The number of rows in each strip, or 0 to choose strips of about 64 KiB of uncompressed samples. Values above the
    /// image height mean one strip. Default: 0.
    /// </summary>
    public int RowsPerStrip
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(RowsPerStrip), value, "Rows per strip cannot be negative.");
    }
}
