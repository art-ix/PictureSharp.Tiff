using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// The TIFF format plugin (read only). It decodes the first full-resolution page of a classic TIFF or BigTIFF file:
/// strips or tiles, contiguous or planar samples, no compression, PackBits, LZW or Deflate with predictor 2 or 3,
/// JPEG (through the configuration's JPEG codec) and CCITT RLE, Group 3 and Group 4; greyscale (1-16 bits), palette
/// (1-8 bits), RGB and CMYK (8 or 16 bits), YCbCr (8 bits, subsampled) and 32-bit floating-point colour with optional
/// alpha. Other compressions and colour models throw <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// Identifying an image reports the number of full-resolution pages
/// as <see cref="ImageInfo.ContainerItemCount"/>. Samples deeper than 8 bits become <c>round(v / 257)</c>; floating-point
/// samples are clamped to [0, 1] and scaled to 255. The EXIF, GPS and orientation tags form
/// <see cref="ImageMetadata.Exif"/>. JPEG strips and tiles are decoded by the configuration's JPEG codec, each within
/// its own strip or tile size, so <see cref="DecoderOptions.MaxPixels"/> applies to the image as for other compressions.
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

    /// <summary>Reads ICC, EXIF, XMP and resolution for <c>Image.ReadMetadata</c>; rewriting is not supported.</summary>
    public IImageFormatMetadataCodec MetadataCodec => TiffMetadataCodec.Instance;
}
