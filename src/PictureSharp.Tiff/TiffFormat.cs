namespace PictureSharp.Formats.Tiff;

/// <summary>
/// The TIFF format plugin. This version only recognizes TIFF and BigTIFF files; decoding is not implemented yet, so
/// identifying or loading one throws <see cref="NotSupportedException"/>.
/// </summary>
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
