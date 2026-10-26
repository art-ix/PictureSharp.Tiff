namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// <c>ImageInfo.Color</c> and <c>Compression</c> (PictureSharp 0.8.5): what the IFD declares, also for layouts
/// <c>Load</c> rejects.
/// </summary>
public sealed class TiffIdentifyColorTests
{
    [Test]
    [Arguments("gray8_none.tif", ImageColorModel.Gray, 1, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("bilevel_g4.tif", ImageColorModel.Gray, 1, 1, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("gray8_miniswhite_lzw.tif", ImageColorModel.Gray, 1, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("graya8_straight.tif", ImageColorModel.Gray, 2, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Straight, ImageCompression.Lossless)]
    [Arguments("palette4_none.tif", ImageColorModel.Palette, 1, 4, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("rgb8_none.tif", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("rgb16_none.tif", ImageColorModel.Rgb, 3, 16, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("rgba8_straight.tif", ImageColorModel.Rgb, 4, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Straight, ImageCompression.Lossless)]
    [Arguments("rgba8_assoc.tif", ImageColorModel.Rgb, 4, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Premultiplied, ImageCompression.Lossless)]
    [Arguments("rgbx8_unspecified.tif", ImageColorModel.Rgb, 4, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("ycbcr_22.tif", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("jpeg_ycbcr_strips.tif", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossy)]
    [Arguments("cmyk8_none.tif", ImageColorModel.Cmyk, 4, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("cmyka8_straight.tif", ImageColorModel.Cmyk, 5, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.Straight, ImageCompression.Lossless)]
    [Arguments("lab8_none.tif", ImageColorModel.Lab, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("int8_gray_none.tif", ImageColorModel.Gray, 1, 8, ImageSampleFormat.SignedInteger, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("float16_gray_none.tif", ImageColorModel.Gray, 1, 16, ImageSampleFormat.Float, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("float32_rgba_assoc.tif", ImageColorModel.Rgb, 4, 32, ImageSampleFormat.Float, ImageAlphaMode.Premultiplied, ImageCompression.Lossless)]
    [Arguments("float64_gray_none.tif", ImageColorModel.Gray, 1, 64, ImageSampleFormat.Float, ImageAlphaMode.None, ImageCompression.Lossless)]
    [Arguments("webp_rgb_lossy_strips.tif", ImageColorModel.Rgb, 3, 8, ImageSampleFormat.UnsignedInteger, ImageAlphaMode.None, ImageCompression.Unknown)]
    public async Task Identify_ReportsTheDeclaredLayout(string name, ImageColorModel model, int channels, int bits,
        ImageSampleFormat sample, ImageAlphaMode alpha, ImageCompression compression)
    {
        ImageInfo info = TestSupport.Identify(TestSupport.ReadCorpus(name));
        await Assert.That(info.Color.Model).IsEqualTo(model);
        await Assert.That(info.Color.ChannelCount).IsEqualTo(channels);
        await Assert.That(info.Color.BitsPerSample).IsEqualTo(bits);
        await Assert.That(info.Color.SampleFormat).IsEqualTo(sample);
        await Assert.That(info.Color.AlphaMode).IsEqualTo(alpha);
        await Assert.That(info.Compression).IsEqualTo(compression);
        await Assert.That(info.Color.Cicp).IsNull();
    }

    [Test]
    public async Task Identify_DetectsIccProfile_AndResolution()
    {
        ImageInfo tagged = TestSupport.Identify(TestSupport.ReadCorpus("metadata.tif"), TestSupport.Options with { IgnoreMetadata = true });
        await Assert.That(tagged.Color.HasIccProfile).IsTrue();
        ImageInfo plain = TestSupport.Identify(TestSupport.ReadCorpus("rgb8_none.tif"));
        await Assert.That(plain.Color.HasIccProfile).IsFalse();
        await Assert.That(plain.Resolution).IsNull();
        await Assert.That(TestSupport.Identify(TestSupport.ReadCorpus("metadata_cm.tif")).Resolution)
            .IsEqualTo(new ImageResolution(254f, 127f));
    }
}
