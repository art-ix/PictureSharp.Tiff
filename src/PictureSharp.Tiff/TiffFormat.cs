using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// The TIFF format plugin. It writes every frame as a page (see <see cref="TiffEncoderOptions"/>) and decodes one full-resolution page of a classic TIFF or BigTIFF file, the first unless
/// <see cref="TiffDecoderOptions"/> selects another: strips or tiles, contiguous or planar samples, no
/// compression, PackBits, LZW or Deflate with predictor 2 or 3, JPEG and WebP (through the configuration's JPEG and WebP
/// codecs) and CCITT RLE, Group 3 and Group 4; greyscale (1-16 bits), palette (1-8 bits), RGB and CMYK (8 or 16 bits), YCbCr (8 bits,
/// subsampled) and 32-bit floating-point colour with optional alpha. Other compressions and colour models throw
/// <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// Identifying an image reports the number of full-resolution pages
/// as <see cref="ImageInfo.ContainerItemCount"/>. Samples deeper than 8 bits become <c>round(v / 257)</c>; floating-point
/// samples are clamped to [0, 1] and scaled to 255. The EXIF, GPS and orientation tags form
/// <see cref="ImageMetadata.Exif"/>. JPEG and WebP strips and tiles are decoded by the configuration's codecs, each
/// within its own strip or tile size, so <see cref="DecoderOptions.MaxPixels"/> applies to the image as for other compressions.
/// </remarks>
/// <example>
/// <code>
/// ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
/// </code>
/// </example>
public sealed class TiffFormat : IImageFormatModule
{
    // One instance per process: ImageFormat compares by reference, so every module shares it.
    internal static readonly ImageFormat Descriptor = ImageFormat.Create("TIFF", "image/tiff", ".tif", ".tiff");

    /// <inheritdoc/>
    public ImageFormat Format => Descriptor;

    /// <inheritdoc/>
    public int HeaderSize => 4;

    /// <summary>Whether the header is a little- or big-endian TIFF (42) or BigTIFF (43) signature.</summary>
    public bool IsSupported(ReadOnlySpan<byte> header) =>
        TiffDecoder.HasSignature(header);

    /// <inheritdoc/>
    public IImageFormatDecoder Decoder => TiffDecoder.Instance;

    /// <summary>Writes every frame as a page, as described on <see cref="TiffEncoderOptions"/>.</summary>
    public IImageFormatEncoder Encoder => TiffEncoder.Instance;

    /// <summary>
    /// Reads ICC, EXIF, XMP and resolution for <c>Image.ReadMetadata</c>. <c>Image.EditMetadata</c> appends a new IFD for
    /// the page with the changed tags and relinks it, so the image data and the other pages stay byte-identical; gamma,
    /// chromaticities and sRGB intent cannot be stored and throw <see cref="NotSupportedException"/>.
    /// </summary>
    public IImageFormatMetadataCodec MetadataCodec => TiffMetadataCodec.Instance;
}
