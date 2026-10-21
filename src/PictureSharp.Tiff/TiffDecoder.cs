using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Decodes the first full-resolution page of a classic TIFF file: strips or tiles, contiguous or planar samples,
/// None, PackBits, LZW and Deflate compression with predictor 2, greyscale, palette and RGB with optional alpha.
/// Pixel rounding is described on <see cref="TiffColorConverter"/>.
/// </summary>
internal sealed class TiffDecoder : IImageFormatDecoder
{
    public static TiffDecoder Instance { get; } = new();

    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4] is [(byte)'I', (byte)'I', 42 or 43, 0] or [(byte)'M', (byte)'M', 0, 42 or 43];

    public static ImageFormatException Error(string message) => new(TiffFormat.Descriptor, message);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, countPages: true);
        TiffLayout layout = TiffLayout.Read(bytes, file.Image);
        (float horizontal, float vertical) = TiffMetadataReader.Resolution(bytes, file.Image);
        return new ImageInfo
        {
            Format = TiffFormat.Descriptor,
            Width = layout.Width,
            Height = layout.Height,
            HasAlpha = layout.HasAlpha,
            FrameCount = 1,
            ContainerItemCount = file.Pages,
            BitsPerChannel = layout.BitsPerSample,
            Orientation = TiffMetadataReader.Orientation(bytes, file.Image) ?? ExifOrientation.Unknown,
            HorizontalResolution = horizontal > 0 ? horizontal : 96f,
            VerticalResolution = vertical > 0 ? vertical : 96f,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, countPages: false);
        TiffLayout layout = TiffLayout.Read(bytes, file.Image);
        layout.EnsureDecodable();
        context.ReservePixels(layout.Width, layout.Height);

        var image = new Image(layout.Width, layout.Height);
        try
        {
            TiffPixelDecoder.Decode(data, file.Image, layout, image.GetPixelSpan(), context);
            TiffMetadataReader.Read(bytes, file.Image, image.Metadata, context);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
