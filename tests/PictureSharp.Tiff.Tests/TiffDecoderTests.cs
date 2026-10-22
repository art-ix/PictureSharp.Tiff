using System.Text;

namespace PictureSharp.Formats.Tiff.Tests;

public sealed class TiffDecoderTests
{
    [Test]
    public async Task Pages_SkipReducedResolution_AndCountFullPages()
    {
        byte[] data = TestSupport.ReadCorpus("pages_thumbnail_first.tif");
        ImageInfo info = TestSupport.Identify(data);
        using Image image = TestSupport.Load(data);
        await Assert.That(info.ContainerItemCount).IsEqualTo(2);
        await Assert.That(info.FrameCount).IsEqualTo(1);
        await Assert.That((info.Width, info.Height)).IsEqualTo((37, 23));
        await Assert.That((image.Width, image.Height)).IsEqualTo((37, 23));
    }

    [Test]
    public async Task Metadata_IsRead()
    {
        byte[] data = TestSupport.ReadCorpus("metadata.tif");
        ImageInfo info = TestSupport.Identify(data);
        using Image image = TestSupport.Load(data);
        await Assert.That(info.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That((info.HorizontalResolution, info.VerticalResolution)).IsEqualTo((300f, 150f));
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That((image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution)).IsEqualTo((300f, 150f));
        await Assert.That(image.Metadata.IccProfile).IsEquivalentTo(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ICC-PROFILE-BYTES", 3))));
        await Assert.That(Encoding.UTF8.GetString(image.Metadata.Xmp!)).IsEqualTo("<x:xmpmeta xmlns:x='adobe:ns:meta/'/>");
    }

    [Test]
    public async Task Metadata_IgnoreMetadata_KeepsOnlyResolution()
    {
        DecoderOptions options = TestSupport.Options with { IgnoreMetadata = true };
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("metadata.tif"), options);
        await Assert.That(image.Metadata.IccProfile).IsNull();
        await Assert.That(image.Metadata.Xmp).IsNull();
        await Assert.That(image.Metadata.HorizontalResolution).IsEqualTo(300f);
    }

    [Test]
    public async Task Orientation_IsApplied_WhenRequested()
    {
        DecoderOptions options = TestSupport.Options with { ApplyExifOrientation = true };
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("metadata.tif"), options);
        await Assert.That((image.Width, image.Height)).IsEqualTo((23, 37));
    }

    [Test]
    public async Task Exif_IfdsBecomeTheExifBlock()
    {
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("exif.tif"));
        ExifProfile exif = image.Metadata.ExifProfile;
        await Assert.That(image.Metadata.Orientation).IsEqualTo(ExifOrientation.LeftBottom);
        await Assert.That(exif.TryGetValue(ExifTag.Make, out string? make) ? make : null).IsEqualTo("Maker");
        await Assert.That(exif.TryGetValue(ExifTag.DateTimeOriginal, out DateTime taken) ? taken : default)
            .IsEqualTo(new DateTime(2026, 10, 21, 9, 30, 0));
        await Assert.That(exif.TryGetGpsLocation(out GpsLocation location)).IsTrue();
        await Assert.That(Math.Abs(location.Latitude - (52 + 13 / 60.0 + 36 / 3600.0))).IsLessThan(1e-6);
        await Assert.That(location.Longitude).IsEqualTo(21.0);
        await Assert.That(TestSupport.Identify(TestSupport.ReadCorpus("exif.tif")).Orientation).IsEqualTo(ExifOrientation.LeftBottom);
    }

    [Test]
    public async Task Exif_IsSkipped_WithIgnoreMetadata()
    {
        DecoderOptions options = TestSupport.Options with { IgnoreMetadata = true };
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("exif.tif"), options);
        await Assert.That(image.Metadata.Exif).IsNull();
    }

    [Test]
    public async Task BigTiff_CountsPages()
    {
        ImageInfo info = TestSupport.Identify(TestSupport.ReadCorpus("bigtiff_pages.tif"));
        await Assert.That(info.ContainerItemCount).IsEqualTo(1);
        await Assert.That((info.Width, info.Height)).IsEqualTo((37, 23));
    }

    [Test]
    public async Task Resolution_InCentimetres_IsConvertedToInches()
    {
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("metadata_cm.tif"));
        await Assert.That((image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution)).IsEqualTo((254f, 127f));
    }

    [Test]
    [Arguments("rgba16_assoc.tif", true, 16)]
    [Arguments("rgbx8_unspecified.tif", false, 8)]
    [Arguments("rgba8_no_extrasamples.tif", true, 8)]
    [Arguments("gray4_lzw.tif", false, 4)]
    public async Task Identify_ReportsAlphaAndDepth(string name, bool alpha, int bits)
    {
        ImageInfo info = TestSupport.Identify(TestSupport.ReadCorpus(name));
        await Assert.That(info.HasAlpha).IsEqualTo(alpha);
        await Assert.That(info.BitsPerChannel).IsEqualTo(bits);
        await Assert.That(info.ContainerItemCount).IsEqualTo(1);
    }

    [Test]
    [Arguments((ushort)259, (uint)6)]       // old-style JPEG
    [Arguments((ushort)259, (uint)34925)]   // LZMA
    [Arguments((ushort)259, (uint)50000)]   // Zstandard
    [Arguments((ushort)262, (uint)9)]       // ICC L*a*b*
    [Arguments((ushort)262, (uint)4)]       // transparency mask
    [Arguments((ushort)339, (uint)5)]       // complex integer samples
    [Arguments((ushort)339, (uint)3)]       // 8-bit floating-point samples
    [Arguments((ushort)258, (uint)24)]      // 24-bit samples
    public async Task UnsupportedFeatures_ThrowNotSupported_ButIdentify(ushort tag, uint value)
    {
        byte[] data = TiffBuilder.Gray(8, 4).Set(tag, TiffBuilder.Short, value).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<NotSupportedException>();
        await Assert.That(TestSupport.Identify(data).Width).IsEqualTo(8);
    }

    [Test]
    public async Task CieLab_WithAZeroWhitePointY_IsCorrupt()
    {
        // As libtiff's initCIELabConversion: the white point's y divides, so 0 is invalid.
        byte[] data = TiffBuilder.GrayStrip(4, 2, 1, new byte[4 * 2 * 3]).Set(277, TiffBuilder.Short, 3).Set(262, TiffBuilder.Short, 8)
            .Set(258, TiffBuilder.Short, 8, 8, 8).Set(318, TiffBuilder.Rational, 3127, 10000, 0, 1).Build();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => TestSupport.Load(data));
        await Assert.That(error.Message).Contains("WhitePoint");

        // L* = 100, a* = b* = 0 is the file's white; libtiff maps it to sRGB without chromatic adaptation, so the default
        // D50 white turns slightly yellow and only a D65 white point gives white (values from TIFFReadRGBAImage).
        TiffBuilder white = TiffBuilder.GrayStrip(1, 1, 1, [255, 0, 0]).Set(277, TiffBuilder.Short, 3).Set(262, TiffBuilder.Short, 8)
            .Set(258, TiffBuilder.Short, 8, 8, 8);
        using Image d50 = TestSupport.Load(white.Build());
        await Assert.That(d50[0, 0]).IsEqualTo(new Rgba32(255, 252, 222, 255));
        using Image d65 = TestSupport.Load(white.Set(318, TiffBuilder.Rational, 3127, 10000, 3290, 10000).Build());
        await Assert.That(d65[0, 0]).IsEqualTo(new Rgba32(255, 255, 255, 255));
    }

    [Test]
    public async Task SignedSamples_MapTheWholeRangeLinearly()
    {
        // Two's complement bytes: -128 becomes 0, -1 becomes 127, 0 becomes 128 and 127 becomes 255.
        using Image image = TestSupport.Load(TiffBuilder.Gray(8, 4).Set(339, TiffBuilder.Short, 2).Build());
        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            byte stored = (byte)(i * 7);
            if (pixels[i] != new Rgba32((byte)(stored ^ 0x80), (byte)(stored ^ 0x80), (byte)(stored ^ 0x80), 255))
            {
                throw new InvalidOperationException($"Pixel {i}: {pixels[i]}");
            }
        }

        await Assert.That(pixels[0].R).IsEqualTo((byte)128);
    }

    [Test]
    public async Task FloatingPointPredictor_OnIntegerLzwSamples_ThrowsNotSupported()
    {
        byte[] data = TiffBuilder.Gray(8, 4).Set(259, TiffBuilder.Short, 5).Set(317, TiffBuilder.Short, 3).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Predictor_IsIgnored_WithoutLzwOrDeflate()
    {
        // libtiff applies the predictor only in the codecs that support it.
        byte[] plain = TiffBuilder.Gray(8, 4).Build();
        byte[] tagged = TiffBuilder.Gray(8, 4).Set(317, TiffBuilder.Short, 2).Build();
        using Image a = TestSupport.Load(plain);
        using Image b = TestSupport.Load(tagged);
        await Assert.That(TestSupport.Sha256(b)).IsEqualTo(TestSupport.Sha256(a));
    }

    [Test]
    public async Task DirectoryLoop_EndsTheChain()
    {
        byte[] data = TiffBuilder.Gray(8, 4).Build(next: self => self);
        using Image image = TestSupport.Load(data);
        await Assert.That(TestSupport.Identify(data).ContainerItemCount).IsEqualTo(1);
        await Assert.That(image.GetPixelSpan()[9]).IsEqualTo(new Rgba32(63, 63, 63));
    }

    [Test]
    public async Task BrokenNextDirectory_EndsTheChain()
    {
        byte[] data = TiffBuilder.Gray(8, 4).Build(next: _ => 0x7FFF_FFF0);
        await Assert.That(TestSupport.Identify(data).ContainerItemCount).IsEqualTo(1);
    }

    [Test]
    public async Task HugeStripByteCount_IsCutToTheFile()
    {
        byte[] data = TiffBuilder.Gray(8, 4).Set(279, TiffBuilder.Long, 0xFFFF_FFF0).Build();
        using Image image = TestSupport.Load(data);
        await Assert.That(image.GetPixelSpan()[31]).IsEqualTo(new Rgba32(217, 217, 217));
    }

    [Test]
    [Arguments((ushort)273, (uint)0x7FFF_FFF0)]   // strip outside the file
    [Arguments((ushort)278, (uint)0)]             // zero rows per strip
    [Arguments((ushort)278, (uint)1)]             // four strips, one offset
    [Arguments((ushort)256, (uint)0)]             // zero width
    public async Task CorruptLayout_ThrowsImageFormatException(ushort tag, uint value)
    {
        byte[] data = TiffBuilder.Gray(8, 4).Set(tag, TiffBuilder.Long, value).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task NegativeStripOffset_ThrowsImageFormatException()
    {
        // libtiff reads strip arrays of signed types too, but rejects a negative value.
        byte[] data = TiffBuilder.Gray(8, 4).Set(273, 9, 0xFFFF_FFF8).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task MissingImageWidth_ThrowsImageFormatException()
    {
        byte[] data = TiffBuilder.Gray(8, 4).Remove(256).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
        await Assert.That(() => TestSupport.Identify(data)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task MissingStripOffsets_ThrowsImageFormatException()
    {
        byte[] data = ReplaceTag(TiffBuilder.Gray(8, 4).Build(), 273, 999);
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task OversizedTiles_ThrowImageFormatException()
    {
        byte[] data = TiffBuilder.Gray(1, 1).Remove(278).Remove(279)
            .Set(322, TiffBuilder.Long, 1 << 20).Set(323, TiffBuilder.Long, 1 << 20)
            .Set(324, TiffBuilder.Long, 8).Set(325, TiffBuilder.Long, 1)
            .Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
    }

    private static byte[] ReplaceTag(byte[] data, ushort tag, ushort replacement)
    {
        uint ifd = BitConverter.ToUInt32(data, 4);
        int count = BitConverter.ToUInt16(data, (int)ifd);
        for (int i = 0; i < count; i++)
        {
            int at = (int)ifd + 2 + 12 * i;
            if (BitConverter.ToUInt16(data, at) == tag)
            {
                BitConverter.TryWriteBytes(data.AsSpan(at), replacement);
            }
        }

        return data;
    }
}
