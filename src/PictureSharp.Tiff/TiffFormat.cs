namespace PictureSharp.Formats.Tiff;

/// <summary>
/// The TIFF format plugin (read only). It decodes the first full-resolution page of a classic TIFF file: strips or
/// tiles, contiguous or planar samples, no compression, PackBits, LZW or Deflate with horizontal predictor 2, and
/// greyscale (1-16 bits), palette (1-8 bits) or RGB (8 or 16 bits) colour with optional alpha. Other compressions,
/// colour models and BigTIFF throw <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// Identifying an image reports the number of full-resolution pages
/// as <see cref="ImageInfo.ContainerItemCount"/>. Samples deeper than 8 bits become <c>round(v / 257)</c>.
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
}
