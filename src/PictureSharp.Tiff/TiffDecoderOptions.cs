namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Selects the TIFF page to decode, passed in <see cref="DecoderOptions.FormatOptions"/>. Without it the decoder reads
/// the first page. Load, <c>Image.Identify</c> and <c>Image.ReadMetadata</c> all describe the selected page.
/// </summary>
/// <example>
/// <code>
/// using var second = Image.Load("fax.tif", new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 1 }] });
/// </code>
/// </example>
public sealed record TiffDecoderOptions : FormatDecoderOptions
{
    /// <summary>
    /// The zero-based index of the page among the full-resolution pages, which <c>Image.Identify</c> counts as
    /// <see cref="ImageInfo.ContainerItemCount"/>. Reduced-resolution pages such as thumbnails are not counted. A page
    /// beyond the last one throws <see cref="ImageFormatException"/>. Default: 0.
    /// </summary>
    public int Page
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Page), value, "TIFF pages are numbered from 0.");
    }
}
