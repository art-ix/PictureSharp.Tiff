namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Options for the TIFF encoder, which writes the first frame as one little-endian classic TIFF page in strips with
/// contiguous 8-bit samples. Resolution is always written (in dots per inch); ICC and XMP blocks are written as tags
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

    /// <summary>The compression of each strip. Default: <see cref="TiffCompression.None"/>.</summary>
    public TiffCompression Compression
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Compression), value, "Unknown TIFF compression.");
    }

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
