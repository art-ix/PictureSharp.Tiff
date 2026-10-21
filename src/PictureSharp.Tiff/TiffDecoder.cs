using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Decodes one full-resolution page of a TIFF file, the first unless <see cref="TiffDecoderOptions"/> selects another:
/// strips or tiles, contiguous or planar samples, every supported compression and colour model.
/// Pixel rounding is described on <see cref="TiffColorConverter"/>.
/// </summary>
internal sealed class TiffDecoder : IImageFormatDecoder
{
    public static TiffDecoder Instance { get; } = new();

    public static bool HasSignature(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4] is [(byte)'I', (byte)'I', 42 or 43, 0] or [(byte)'M', (byte)'M', 0, 42 or 43];

    public static ImageFormatException Error(string message) => new(TiffFormat.Descriptor, message);

    /// <summary>The page selected with <see cref="TiffDecoderOptions"/>, 0 without it.</summary>
    public static int Page(DecoderOptions options) => options.GetFormatOptions<TiffDecoderOptions>()?.Page ?? 0;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, Page(options), countPages: true);
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
        var file = TiffFile.Open(bytes, Page(context.Options), countPages: false);
        TiffLayout layout = TiffLayout.Read(bytes, file.Image);
        layout.EnsureDecodable();
        context.ReservePixels(layout.Width, layout.Height);

        var image = new Image(layout.Width, layout.Height);
        try
        {
            TiffPixelDecoder.Decode(data, file.Image, layout, image.GetPixelMemory(), context);
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
