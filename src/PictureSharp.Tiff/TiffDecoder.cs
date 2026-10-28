using PictureSharp.Formats.Tiff.Codecs;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff;

/// <summary>
/// Decodes one full-resolution page of a TIFF file, the first unless <see cref="TiffDecoderOptions"/> selects another:
/// strips or tiles, contiguous or planar samples, every supported compression and colour model.
/// Pixel rounding is described on <see cref="TiffColorConverter"/>. With <see cref="DecoderOptions.PreserveBitDepth"/>,
/// samples of more than 8 bits (<see cref="TiffLayout.HasDeepSamples"/>) decode to <see cref="PixelType.Rgba64"/>.
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
            Color = Color(layout, file.Image.TryGet(TiffTag.IccProfile, out _)),
            Compression = layout.Compression switch
            {
                TiffLayout.CompressionNone or TiffLayout.CompressionLzw or TiffLayout.CompressionDeflate
                    or TiffLayout.CompressionDeflateOld or TiffLayout.CompressionPackBits or TiffLayout.CompressionNeXT
                    or TiffLayout.CompressionThunderScan => ImageCompression.Lossless,
                _ when layout.IsCcitt => ImageCompression.Lossless,
                6 or TiffLayout.CompressionJpeg => ImageCompression.Lossy,   // 6: old-style JPEG
                _ => ImageCompression.Unknown,                               // WebP can be either; unknown schemes
            },
            FrameCount = 1,
            ContainerItemCount = file.Pages,
            Orientation = TiffMetadataReader.Orientation(bytes, file.Image) ?? ExifOrientation.Unknown,
            Resolution = horizontal > 0 && vertical > 0 ? new ImageResolution(horizontal, vertical) : null,
        };
    }

    /// <summary>
    /// The colour layout as the IFD declares it, also for layouts <see cref="TiffLayout.EnsureDecodable"/> rejects
    /// (signed or float samples, LogLuv): only what the tags state, with <see cref="ImageColorModel.Other"/> for the rest.
    /// </summary>
    private static ImageColorInfo Color(TiffLayout layout, bool icc) => new()
    {
        Model = layout.Photometric switch
        {
            TiffLayout.WhiteIsZero or TiffLayout.BlackIsZero => ImageColorModel.Gray,
            TiffLayout.Rgb or TiffLayout.YCbCr => ImageColorModel.Rgb,
            TiffLayout.Palette => ImageColorModel.Palette,
            TiffLayout.Separated when layout.InkSet == 1 && layout.SamplesPerPixel - (layout.HasAlpha ? 1 : 0) >= 4 => ImageColorModel.Cmyk,
            TiffLayout.CieLab or 9 or 10 => ImageColorModel.Lab,   // 9 ICCLab, 10 ITULab
            _ => ImageColorModel.Other,                            // transparency mask, other ink sets, CFA, LogL/LogLuv, LinearRaw
        },
        ChannelCount = layout.SamplesPerPixel,
        BitsPerSample = layout.BitsPerSample,
        SampleFormat = layout.SampleFormat switch
        {
            TiffLayout.FormatSigned => ImageSampleFormat.SignedInteger,
            TiffLayout.FormatFloat => ImageSampleFormat.Float,
            5 or 6 => ImageSampleFormat.Other,                     // complex integer and complex float
            _ => ImageSampleFormat.UnsignedInteger,                // 1, and 4 (void) read as unsigned like libtiff
        },
        AlphaMode = !layout.HasAlpha ? ImageAlphaMode.None : layout.Associated ? ImageAlphaMode.Premultiplied : ImageAlphaMode.Straight,
        HasIccProfile = icc,
    };

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, Page(context.Options), countPages: false);
        TiffLayout layout = TiffLayout.Read(bytes, file.Image);
        layout.EnsureDecodable();
        PixelType pixelType = context.Options.PreserveBitDepth && layout.HasDeepSamples ? PixelType.Rgba64 : PixelType.Rgba32;
        context.ReservePixels(layout.Width, layout.Height, pixelType);

        var image = new Image(layout.Width, layout.Height, pixelType);
        try
        {
            TiffPixelDecoder.Decode(data, file.Image, layout, image, context);
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
