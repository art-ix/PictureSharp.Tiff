namespace PictureSharp.Formats.Tiff;

// Placeholder until the decoder is ported (see plan.md): the core requires a decoder or an encoder for every module.
internal sealed class TiffDecoder : IImageFormatDecoder
{
    public static TiffDecoder Instance { get; } = new();

    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4] is [(byte)'I', (byte)'I', 42 or 43, 0] or [(byte)'M', (byte)'M', 0, 42 or 43];

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => throw NotImplemented(data.Span);

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => throw NotImplemented(data.Span);

    private static Exception NotImplemented(ReadOnlySpan<byte> data) =>
        HasSignature(data)
            ? new NotSupportedException("TIFF decoding is not implemented yet in this version of PictureSharp.Tiff.")
            : new ImageFormatException(TiffFormat.Descriptor, "Not a TIFF file.");
}
