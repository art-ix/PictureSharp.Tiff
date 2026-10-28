using System.Text;
using System.Text.Json;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// WebP-compressed TIFF (50001). Files written by libtiff must decode strip by strip like the core WebP decode of the
/// same chunk and like Pillow's libwebp decode; the encoder's strips must be complete WebP streams, lossless ones exact.
/// </summary>
public sealed class WebPTests
{
    private static readonly Dictionary<string, WebPEntry> Index =
        JsonSerializer.Deserialize<Dictionary<string, WebPEntry>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "webp.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static IEnumerable<string> Files() => Index.Keys.Order(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_MatchesCoreWebPDecodeOfEachChunk(string name)
    {
        WebPEntry entry = Index[name];
        using Image image = TestSupport.Load(TestSupport.ReadCorpus(name));
        await Assert.That((image.Width, image.Height)).IsEqualTo((entry.Width, entry.Height));
        Rgba32[] pixels = image.GetPixelSpan<Rgba32>().ToArray();
        int mismatches = 0;
        foreach (JsonElement[] chunk in entry.Chunks)
        {
            (string file, int x0, int y0, int w, int h) = (chunk[0].GetString()!, chunk[1].GetInt32(), chunk[2].GetInt32(), chunk[3].GetInt32(), chunk[4].GetInt32());
            using Image webp = TestSupport.Load(TestSupport.ReadCorpus(file));
            Rgba32[] decoded = webp.GetPixelSpan<Rgba32>().ToArray();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    mismatches += pixels[(y0 + y) * entry.Width + x0 + x] == decoded[y * webp.Width + x] ? 0 : 1;
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_MatchesPillow(string name)
    {
        using Image image = TestSupport.Load(TestSupport.ReadCorpus(name));
        using Image pillow = TestSupport.Load(TestSupport.ReadCorpus(Index[name].Pillow));
        await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(pillow.GetPixelSpan<Rgba32>()));
    }

    [Test]
    public async Task MaxPixels_CoversTheImageOnly()
    {
        byte[] data = TestSupport.ReadCorpus("webp_rgba_lossy_tiles.tif");
        using Image image = TestSupport.Load(data, TestSupport.Options with { MaxPixels = 64 * 48, MaxFrames = 1 });
        await Assert.That(image.Width).IsEqualTo(64);
        await Assert.That(() => TestSupport.Load(data, TestSupport.Options with { MaxPixels = 64 * 48 - 1 })).Throws<ImageFormatException>();
    }

    [Test]
    public async Task WebPLargerThanItsStrip_IsRejected()
    {
        byte[] data = Rgb(16, 8, 3, WebP(16, 16, alpha: false)).Build();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => TestSupport.Load(data));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.WebP);
    }

    [Test]
    public async Task WebPSmallerThanItsStrip_IsRejected()
    {
        byte[] data = Rgb(16, 8, 3, WebP(16, 4, alpha: false)).Build();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => TestSupport.Load(data));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.Tiff);
    }

    [Test]
    public async Task ThreeSamples_DropTheStreamsAlpha()
    {
        using Image source = EncoderSupport.Pattern(16, 8, alpha: true);
        byte[] data = Rgb(16, 8, 3, source.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true })).Build();
        using Image image = TestSupport.Load(data);
        await Assert.That(image.GetPixelSpan<Rgba32>().SequenceEqual(EncoderSupport.Expected(source, TiffColorType.Rgb))).IsTrue();
    }

    [Test]
    public async Task FourSamples_WithoutAlphaInTheStream_AreOpaque()
    {
        using Image source = EncoderSupport.Pattern(16, 8);
        byte[] data = Rgb(16, 8, 4, source.ToWebP(new WebPEncoderOptions { Lossless = true })).Set(338, TiffBuilder.Short, 2).Build();
        using Image image = TestSupport.Load(data);
        await Assert.That(image.GetPixelSpan<Rgba32>().SequenceEqual(source.GetPixelSpan<Rgba32>())).IsTrue();
    }

    [Test]
    [Arguments(1, 1, 8)]
    [Arguments(2, 1, 8)]
    [Arguments(3, 2, 8)]
    [Arguments(3, 1, 16)]
    public async Task UnsupportedLayouts_AreRejectedAsLibtiffDoes(int samples, int planar, int bits)
    {
        byte[] data = Rgb(16, 8, samples, WebP(16, 8, alpha: false))
            .Set(258, TiffBuilder.Short, [.. Enumerable.Repeat((uint)bits, samples)])
            .Set(284, TiffBuilder.Short, (uint)planar)
            .Build();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => TestSupport.Load(data));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.Tiff);
    }

    [Test]
    public async Task NonRgbPhotometric_IsNotSupported()
    {
        byte[] data = Rgb(16, 8, 3, WebP(16, 8, alpha: false)).Set(262, TiffBuilder.Short, 1).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task StripTallerThan16383_IsRejected()
    {
        byte[] data = Rgb(1, 16384, 3, WebP(1, 8, alpha: false)).Build();
        await Assert.That(() => TestSupport.Load(data)).Throws<ImageFormatException>();
    }

    public static IEnumerable<(bool Alpha, int Width, int Height, int RowsPerStrip)> LosslessCases()
    {
        yield return (false, 33, 17, 0);
        yield return (false, 64, 48, 5);
        yield return (true, 40, 30, 0);
        yield return (true, 1, 1, 0);
        yield return (true, 70, 41, 16);
    }

    [Test]
    [MethodDataSource(nameof(LosslessCases))]
    public async Task Lossless_RoundTripsExactly_IncludingTransparentColours(bool alpha, int width, int height, int rowsPerStrip)
    {
        using Image source = EncoderSupport.Pattern(width, height, alpha: alpha);
        if (alpha)
        {
            // Fully transparent pixels whose colour must survive (libwebp's "exact").
            source.GetPixelSpan<Rgba32>()[..width].Fill(new Rgba32(200, 100, 50, 0));
        }

        byte[] file = source.ToTiff(EncoderSupport.Options(rowsPerStrip: rowsPerStrip, compression: TiffCompression.WebP) with { WebPLossless = true });
        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags[259].Value).IsEqualTo(50001u);
        await Assert.That(tags[277].Value).IsEqualTo(alpha ? 4u : 3u);
        await Assert.That(tags.ContainsKey(338)).IsEqualTo(alpha);
        using Image image = TestSupport.Load(file);
        await Assert.That(image.GetPixelSpan<Rgba32>().SequenceEqual(source.GetPixelSpan<Rgba32>())).IsTrue();
        await Assert.That(EncoderSupport.StripsMatch(file, image)).IsTrue();
        await Assert.That(EncoderSupport.Strips(file).All(s => Encoding.ASCII.GetString(s, 12, 4) == "VP8L")).IsTrue();
    }

    [Test]
    public async Task Lossy_StripsAreTheCoreWebPOfEachStrip_WithinThePsnrBound()
    {
        // The bound was set before the implementation: 30 dB on a smooth image at the default quality 75.
        using Image source = Smooth(96, 64);
        byte[] file = source.ToTiff(EncoderSupport.Options(rowsPerStrip: 32, compression: TiffCompression.WebP));
        using Image image = TestSupport.Load(file);
        await Assert.That(EncoderSupport.StripsMatch(file, image)).IsTrue();

        using Image top = new(96, 32);
        source.GetPixelSpan<Rgba32>()[..(96 * 32)].CopyTo(top.GetPixelSpan<Rgba32>());
        byte[] webp = top.ToWebP(new WebPEncoderOptions { Quality = 75, Metadata = MetadataKinds.None });
        await Assert.That(EncoderSupport.Strips(file)[0].AsSpan().SequenceEqual(webp)).IsTrue();
        await Assert.That(Psnr(source, image)).IsGreaterThan(30);
    }

    [Test]
    public async Task Lossy_QualityChangesTheSize_AndAlphaStaysExact()
    {
        using Image source = Smooth(64, 64, alpha: true);
        byte[] low = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP) with { WebPQuality = 20 });
        byte[] high = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP) with { WebPQuality = 95 });
        await Assert.That(low.Length).IsLessThan(high.Length);
        using Image image = TestSupport.Load(high);
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray().Select(p => p.A).SequenceEqual(source.GetPixelSpan<Rgba32>().ToArray().Select(p => p.A))).IsTrue();
        await Assert.That(Psnr(source, image)).IsGreaterThan(35);
    }

    [Test]
    public async Task Strips_CarryNoMetadataChunks()
    {
        using Image source = EncoderSupport.Pattern(20, 10);
        source.Metadata.IccProfile = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        source.Metadata.Exif = ExifSample.Block(bigEndian: false);
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP));
        byte[] strip = EncoderSupport.Strips(file)[0];
        await Assert.That(Encoding.ASCII.GetString(strip, 12, 4)).IsEqualTo("VP8 ");
    }

    [Test]
    [Arguments(TiffColorType.Grayscale)]
    [Arguments(TiffColorType.GrayscaleAlpha)]
    [Arguments(TiffColorType.Palette)]
    [Arguments(TiffColorType.BlackWhite)]
    public async Task OtherColourTypes_Throw(TiffColorType colorType)
    {
        using Image source = EncoderSupport.Pattern(8, 8, posterize: true);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(colorType, compression: TiffCompression.WebP))).Throws<ArgumentException>();
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP, predictor: TiffPredictor.Horizontal))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Auto_WritesGreyImagesAsRgb()
    {
        using Image source = EncoderSupport.Pattern(8, 8, gray: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP) with { WebPLossless = true });
        await Assert.That(EncoderSupport.Tags(file)[262].Value).IsEqualTo(2u);
        using Image image = TestSupport.Load(file);
        await Assert.That(image.GetPixelSpan<Rgba32>().SequenceEqual(source.GetPixelSpan<Rgba32>())).IsTrue();
    }

    [Test]
    public async Task WiderThan16383_IsNotSupported()
    {
        using var source = new Image(16384, 1);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task RowsPerStrip_IsLimitedTo16383()
    {
        using var source = new Image(1, 16400, new Rgba32(10, 20, 30, 255));
        byte[] file = source.ToTiff(EncoderSupport.Options(rowsPerStrip: 20000, compression: TiffCompression.WebP) with { WebPLossless = true });
        await Assert.That(EncoderSupport.Tags(file)[278].Value).IsEqualTo(16383u);
        using Image image = TestSupport.Load(file);
        await Assert.That(image.GetPixelSpan<Rgba32>().SequenceEqual(source.GetPixelSpan<Rgba32>())).IsTrue();
    }

    [Test]
    public async Task Options_DefaultAndRange()
    {
        await Assert.That((new TiffEncoderOptions().WebPQuality, new TiffEncoderOptions().WebPLossless)).IsEqualTo((75, false));
        await Assert.That(() => new TiffEncoderOptions { WebPQuality = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { WebPQuality = 101 }).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>A contiguous 8-bit RGB (or more samples) file in one strip of WebP data.</summary>
    private static TiffBuilder Rgb(int width, int height, int samples, byte[] strip) =>
        TiffBuilder.GrayStrip(width, height, 50001, strip)
            .Set(258, TiffBuilder.Short, [.. Enumerable.Repeat(8u, samples)])
            .Set(262, TiffBuilder.Short, 2)
            .Set(277, TiffBuilder.Short, (uint)samples);

    private static byte[] WebP(int width, int height, bool alpha)
    {
        using Image image = EncoderSupport.Pattern(width, height, alpha: alpha);
        return image.ToWebP(new WebPEncoderOptions { Lossless = true });
    }

    private static Image Smooth(int width, int height, bool alpha = false)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgba32((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)(128 + (64 * Math.Sin((x + y) / 9.0))),
                    alpha ? (byte)(x * 4) : (byte)255);
            }
        }

        return image;
    }

    private static double Psnr(Image a, Image b)
    {
        ReadOnlySpan<Rgba32> p = a.GetPixelSpan<Rgba32>(), q = b.GetPixelSpan<Rgba32>();
        double sum = 0;
        for (int i = 0; i < p.Length; i++)
        {
            sum += Math.Pow(p[i].R - q[i].R, 2) + Math.Pow(p[i].G - q[i].G, 2) + Math.Pow(p[i].B - q[i].B, 2);
        }

        return 10 * Math.Log10(255.0 * 255.0 / (sum / (3.0 * p.Length)));
    }

    private sealed record WebPEntry(int Width, int Height, JsonElement[][] Chunks, string Pillow, bool Lossless);
}
