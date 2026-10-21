using System.Text;
using PictureSharp.Formats.Jpeg;

namespace PictureSharp.Formats.Tiff.Tests;

public sealed class EncoderTests
{
    private static readonly (TiffCompression, TiffPredictor)[] Compressions =
    [
        (TiffCompression.None, TiffPredictor.None),
        (TiffCompression.PackBits, TiffPredictor.None),
        (TiffCompression.Lzw, TiffPredictor.None),
        (TiffCompression.Lzw, TiffPredictor.Horizontal),
        (TiffCompression.Deflate, TiffPredictor.None),
        (TiffCompression.Deflate, TiffPredictor.Horizontal),
        (TiffCompression.CcittGroup4, TiffPredictor.None),
    ];

    public static IEnumerable<(TiffColorType, int, int, int, TiffCompression, TiffPredictor)> RoundTripCases()
    {
        TiffColorType[] types = [TiffColorType.Grayscale, TiffColorType.GrayscaleAlpha, TiffColorType.Rgb, TiffColorType.Rgba, TiffColorType.Palette, TiffColorType.BlackWhite];
        (int, int)[] sizes = [(1, 1), (7, 3), (13, 1), (33, 17), (1, 40), (16, 9)];
        foreach (TiffColorType type in types)
        {
            foreach ((int width, int height) in sizes)
            {
                foreach (int rows in new[] { 0, 1, 5 })
                {
                    foreach ((TiffCompression compression, TiffPredictor predictor) in Compressions)
                    {
                        bool bilevel = type == TiffColorType.BlackWhite;
                        if ((compression == TiffCompression.CcittGroup4 && !bilevel) || (bilevel && predictor == TiffPredictor.Horizontal))
                        {
                            continue;
                        }

                        yield return (type, width, height, rows, compression, predictor);
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_ReadsBackTheColorType(TiffColorType colorType, int width, int height, int rowsPerStrip,
        TiffCompression compression, TiffPredictor predictor)
    {
        using Image source = EncoderSupport.Pattern(width, height, alpha: true, posterize: colorType == TiffColorType.Palette);
        byte[] file = source.ToTiff(EncoderSupport.Options(colorType, rowsPerStrip, compression, predictor));
        using Image decoded = TestSupport.Load(file);
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((width, height));
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, colorType)));

        var tags = EncoderSupport.Tags(file);
        int expectedRows = rowsPerStrip == 0 ? height : Math.Min(rowsPerStrip, height);
        await Assert.That(tags[278].Value).IsEqualTo((uint)expectedRows);
        await Assert.That(tags[273].Count).IsEqualTo((uint)((height + expectedRows - 1) / expectedRows));
        await Assert.That(tags.ContainsKey(317)).IsEqualTo(predictor == TiffPredictor.Horizontal);
    }

    [Test]
    [Arguments(TiffCompression.PackBits)]
    [Arguments(TiffCompression.Lzw)]
    [Arguments(TiffCompression.Deflate)]
    public async Task WorstCaseInputs_RoundTrip(TiffCompression compression)
    {
        // Noise fills the LZW table many times over and forces resets; a single colour gives the longest runs.
        using var noise = new Image(1000, 300);
        var random = new Random(42);
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(noise.GetPixelSpan()));
        using var flat = new Image(3000, 300);
        flat.GetPixelSpan().Fill(new Rgba32(77, 77, 77, 77));
        foreach (Image source in new[] { noise, flat })
        {
            TiffEncoderOptions options = EncoderSupport.Options(TiffColorType.Rgba, compression: compression, predictor: TiffPredictor.None) with { RowsPerStrip = 300 };
            byte[] file = source.ToTiff(options);
            using Image decoded = TestSupport.Load(file);
            await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
            long raw = source.Width * source.Height * 4L;
            long stored = EncoderSupport.Tags(file)[279].Value;
            await Assert.That(stored).IsLessThan(source == flat ? raw / 50 : raw + (raw / 2));
        }
    }

    [Test]
    [Arguments(TiffCompression.None)]
    [Arguments(TiffCompression.PackBits)]
    [Arguments(TiffCompression.Lzw)]
    [Arguments(TiffCompression.Deflate)]
    [Arguments(TiffCompression.CcittGroup4)]
    public async Task BlackWhite_WorstCaseAndLongRuns_RoundTrip(TiffCompression compression)
    {
        // Random pixels change colour at every other pixel; a 6000-pixel row needs make-up codes beyond 2560.
        using Image noise = EncoderSupport.Bilevel(1001, 300, noise: true);
        using Image wide = EncoderSupport.Bilevel(6000, 40);
        foreach (Image source in new[] { noise, wide })
        {
            byte[] file = source.ToTiff(EncoderSupport.Options(TiffColorType.BlackWhite, compression: compression));
            using Image decoded = TestSupport.Load(file);
            await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
            var tags = EncoderSupport.Tags(file);
            await Assert.That((tags[258].Value, tags[262].Value)).IsEqualTo((1u, 0u));
        }
    }

    [Test]
    public async Task Auto_ChoosesBlackWhiteForBilevelImages()
    {
        using Image source = EncoderSupport.Bilevel(40, 30);
        var tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Lzw)));
        await Assert.That((tags[258].Value, tags[262].Value, tags.ContainsKey(317))).IsEqualTo((1u, 0u, false));

        // An explicit predictor needs 8-bit samples, so the image stays grey.
        tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Lzw, predictor: TiffPredictor.Horizontal)));
        await Assert.That((tags[258].Value, tags[262].Value, tags[317].Value)).IsEqualTo((8u, 1u, 2u));

        // One grey pixel keeps it grey.
        source.GetPixelSpan()[5] = new Rgba32(128, 128, 128, 255);
        tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options()));
        await Assert.That(tags[258].Value).IsEqualTo(8u);
    }

    [Test]
    public async Task Group4_WithAutoColour_ThresholdsTheLuminance()
    {
        using Image source = EncoderSupport.Pattern(33, 17, alpha: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.CcittGroup4));
        var tags = EncoderSupport.Tags(file);
        await Assert.That((tags[258].Value, tags[259].Value, tags[262].Value, tags.ContainsKey(338))).IsEqualTo((1u, 4u, 0u, false));
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, TiffColorType.BlackWhite)));
    }

    [Test]
    [Arguments(TiffColorType.Grayscale, TiffCompression.CcittGroup4, TiffPredictor.Auto)]
    [Arguments(TiffColorType.Rgba, TiffCompression.CcittGroup4, TiffPredictor.Auto)]
    [Arguments(TiffColorType.BlackWhite, TiffCompression.CcittGroup4, TiffPredictor.Horizontal)]
    [Arguments(TiffColorType.BlackWhite, TiffCompression.Lzw, TiffPredictor.Horizontal)]
    [Arguments(TiffColorType.BlackWhite, TiffCompression.Deflate, TiffPredictor.Horizontal)]
    public async Task InvalidBilevelCombinations_Throw(TiffColorType colorType, TiffCompression compression, TiffPredictor predictor)
    {
        using Image source = EncoderSupport.Bilevel(8, 8);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(colorType, compression: compression, predictor: predictor))).Throws<ArgumentException>();
    }

    public static IEnumerable<(bool, int, int, int, JpegSubsampling)> JpegCases()
    {
        foreach (bool gray in new[] { false, true })
        {
            foreach ((int width, int height) in new[] { (1, 1), (13, 7), (33, 40), (70, 17) })
            {
                foreach (int rows in new[] { 0, 1, 9 })
                {
                    foreach (JpegSubsampling subsampling in Enum.GetValues<JpegSubsampling>())
                    {
                        if (!gray || subsampling == JpegSubsampling.Ratio420)
                        {
                            yield return (gray, width, height, rows, subsampling);
                        }
                    }
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(JpegCases))]
    public async Task Jpeg_StripsAreCompleteStreams_DecodedAsTheCoreDecodesThem(bool gray, int width, int height, int rowsPerStrip, JpegSubsampling subsampling)
    {
        // The colour image has a grey top half: its strips must still have three components.
        using Image source = EncoderSupport.Pattern(width, height, gray, alpha: true);
        TiffColorType colorType = gray ? TiffColorType.Auto : TiffColorType.Rgb;
        if (!gray)
        {
            Span<Rgba32> pixels = source.GetPixelSpan();
            for (int i = 0; i < (height / 2) * width; i++)
            {
                pixels[i] = pixels[i] with { G = pixels[i].R, B = pixels[i].R };
            }
        }

        byte[] file = source.ToTiff(EncoderSupport.Options(colorType, rowsPerStrip, TiffCompression.Jpeg) with { JpegSubsampling = subsampling });
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.JpegStripsMatch(file, decoded, gray ? 1 : 3)).IsTrue();
        await Assert.That(decoded.GetPixelSpan().ToArray().All(p => p.A == 255)).IsTrue();

        var tags = EncoderSupport.Tags(file);
        int block = !gray && subsampling is JpegSubsampling.Ratio420 or JpegSubsampling.Ratio440 ? 16 : 8;
        int expectedRows = rowsPerStrip == 0 ? height : Math.Min((rowsPerStrip + block - 1) / block * block, height);
        await Assert.That((tags[259].Value, tags[262].Value, tags[277].Value, tags[278].Value)).IsEqualTo((7u, gray ? 1u : 6u, gray ? 1u : 3u, (uint)expectedRows));
        await Assert.That(tags.ContainsKey(530)).IsEqualTo(!gray);
        await Assert.That(tags.ContainsKey(532)).IsEqualTo(!gray);
    }

    [Test]
    public async Task Jpeg_OneStrip_EqualsTheCoreJpegOfTheImage_AndQualityChangesTheSize()
    {
        using Image source = EncoderSupport.Pattern(96, 64);
        byte[] low = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Jpeg) with { JpegQuality = 30 });
        byte[] high = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Jpeg) with { JpegQuality = 95, JpegSubsampling = JpegSubsampling.Ratio444 });
        await Assert.That(high.Length).IsGreaterThan(low.Length);
        byte[] jpeg = source.ToJpeg(new JpegEncoderOptions { Quality = 95, Subsampling = JpegSubsampling.Ratio444, Metadata = MetadataKinds.None });
        await Assert.That(EncoderSupport.Strips(high).Single().SequenceEqual(jpeg)).IsTrue();
    }

    [Test]
    [Arguments(TiffColorType.GrayscaleAlpha)]
    [Arguments(TiffColorType.Rgba)]
    [Arguments(TiffColorType.Palette)]
    [Arguments(TiffColorType.BlackWhite)]
    public async Task Jpeg_WithAnotherColourType_Throws(TiffColorType colorType)
    {
        using Image source = EncoderSupport.Pattern(8, 8, posterize: true);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(colorType, compression: TiffCompression.Jpeg))).Throws<ArgumentException>();
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Jpeg, predictor: TiffPredictor.Horizontal))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Jpeg_WiderThan65535_IsNotSupported()
    {
        using var source = new Image(65536, 1);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Jpeg))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task Defaults_AreLzwWithTheHorizontalPredictor()
    {
        using Image source = EncoderSupport.Pattern(40, 30);
        var tags = EncoderSupport.Tags(source.ToTiff(new TiffEncoderOptions { Configuration = TestSupport.Configuration }));
        await Assert.That((tags[259].Value, tags[317].Value, tags[262].Value)).IsEqualTo((5u, 2u, 2u));
        await Assert.That(new TiffEncoderOptions().DeflateLevel).IsEqualTo(6);
        await Assert.That((new TiffEncoderOptions().JpegQuality, new TiffEncoderOptions().JpegSubsampling)).IsEqualTo((75, JpegSubsampling.Ratio420));
    }

    [Test]
    [Arguments(769, true)]
    [Arguments(768, false)]
    public async Task Auto_ChoosesAPaletteForEnoughPixels(int pixels, bool palette)
    {
        using Image source = EncoderSupport.Pattern(pixels, 1, posterize: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Lzw));
        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags[262].Value).IsEqualTo(palette ? 3u : 2u);
        await Assert.That(tags.ContainsKey(320)).IsEqualTo(palette);
        await Assert.That(tags.ContainsKey(317)).IsEqualTo(!palette);
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
    }

    [Test]
    public async Task Auto_KeepsRgbForMoreThan256Colours()
    {
        using Image source = EncoderSupport.Pattern(300, 4);
        source.GetPixelSpan()[0] = new Rgba32(1, 2, 3, 255);
        var tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options()));
        await Assert.That(tags[262].Value).IsEqualTo(2u);
    }

    [Test]
    public async Task Palette_WithMoreThan256Colours_Throws()
    {
        using Image source = EncoderSupport.Pattern(64, 64);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(TiffColorType.Palette))).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(TiffCompression.None)]
    [Arguments(TiffCompression.PackBits)]
    public async Task HorizontalPredictor_WithoutLzwOrDeflate_Throws(TiffCompression compression)
    {
        using Image source = EncoderSupport.Pattern(4, 4);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: compression, predictor: TiffPredictor.Horizontal))).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(false, false, 3, 2)]
    [Arguments(true, false, 1, 1)]
    [Arguments(true, true, 2, 1)]
    [Arguments(false, true, 4, 2)]
    public async Task Auto_ChoosesTheSmallestLosslessType(bool gray, bool alpha, int samples, int photometric)
    {
        using Image source = EncoderSupport.Pattern(9, 5, gray, alpha);
        byte[] file = source.ToTiff(EncoderSupport.Options());
        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags[277].Value).IsEqualTo((uint)samples);
        await Assert.That(tags[262].Value).IsEqualTo((uint)photometric);
        await Assert.That(tags.ContainsKey(338)).IsEqualTo(alpha);
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
    }

    [Test]
    public async Task DefaultStrips_HoldAbout64KiB()
    {
        using Image source = EncoderSupport.Pattern(300, 200);
        var tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options(TiffColorType.Rgb)));
        await Assert.That(tags[278].Value).IsEqualTo(65536u / 900);
        await Assert.That(tags[273].Count).IsEqualTo(3u);
    }

    [Test]
    [Arguments(96f, 96f)]
    [Arguments(300f, 150f)]
    [Arguments(72.5f, 0.25f)]
    [Arguments(299.9994f, 118.11f)]
    public async Task Resolution_RoundTrips(float horizontal, float vertical)
    {
        using Image source = EncoderSupport.Pattern(4, 4);
        source.Metadata.HorizontalResolution = horizontal;
        source.Metadata.VerticalResolution = vertical;
        byte[] file = source.ToTiff(EncoderSupport.Options());
        using Image decoded = TestSupport.Load(file);
        await Assert.That((decoded.Metadata.HorizontalResolution, decoded.Metadata.VerticalResolution)).IsEqualTo((horizontal, vertical));
        await Assert.That(EncoderSupport.Tags(file)[296].Value).IsEqualTo(2u);
    }

    [Test]
    [Arguments(MetadataKinds.All, true, true)]
    [Arguments(MetadataKinds.Icc, true, false)]
    [Arguments(MetadataKinds.Xmp, false, true)]
    [Arguments(MetadataKinds.None, false, false)]
    public async Task IccAndXmp_FollowEncoderMetadata(MetadataKinds kinds, bool icc, bool xmp)
    {
        using Image source = EncoderSupport.Pattern(5, 3);
        source.Metadata.IccProfile = [.. Enumerable.Range(0, 301).Select(i => (byte)i)];
        source.Metadata.Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        byte[] file = source.ToTiff(EncoderSupport.Options() with { Metadata = kinds });
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.Metadata.IccProfile)).IsEqualTo(icc ? EncoderSupport.Sha256(source.Metadata.IccProfile) : null);
        await Assert.That(EncoderSupport.Sha256(decoded.Metadata.Xmp)).IsEqualTo(xmp ? EncoderSupport.Sha256(source.Metadata.Xmp) : null);
        ImageMetadata metadata = Image.ReadMetadata(file, TestSupport.Options);
        await Assert.That(EncoderSupport.Sha256(metadata.IccProfile)).IsEqualTo(EncoderSupport.Sha256(decoded.Metadata.IccProfile));
    }

    [Test]
    public async Task OddValueLengths_AreWordAligned()
    {
        using Image source = EncoderSupport.Pattern(3, 3, gray: true);
        source.Metadata.IccProfile = [1, 2, 3, 4, 5];
        source.Metadata.Xmp = [60, 120, 47];
        byte[] file = source.ToTiff(EncoderSupport.Options());
        var tags = EncoderSupport.Tags(file);
        await Assert.That(BitConverter.ToUInt32(file, 4) % 2).IsEqualTo(0u);
        await Assert.That(tags.Values.Where(t => t.Count * (t.Type is 3 ? 2u : t.Type is 4 ? 4u : t.Type is 5 ? 8u : 1u) > 4).All(t => t.Value % 2 == 0)).IsTrue();
        await Assert.That(tags.Keys.SequenceEqual(tags.Keys.Order())).IsTrue();
    }

    [Test]
    public async Task NonSeekableStream_GetsTheSameBytes()
    {
        using Image source = EncoderSupport.Pattern(200, 900, alpha: true);
        source.Metadata.IccProfile = [9, 8, 7];
        TiffEncoderOptions options = EncoderSupport.Options(rowsPerStrip: 7, compression: TiffCompression.Lzw);
        byte[] seekable = source.ToTiff(options);
        using var sink = new MemoryStream();
        source.SaveAsTiff(new ForwardOnlyStream(sink), options);
        await Assert.That(sink.ToArray().SequenceEqual(seekable)).IsTrue();
    }

    [Test]
    public async Task OffsetsAreRelativeToTheStartPosition()
    {
        using Image source = EncoderSupport.Pattern(10, 10);
        byte[] expected = source.ToTiff(EncoderSupport.Options());
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3]);
        source.SaveAsTiff(stream, EncoderSupport.Options());
        await Assert.That(stream.Position).IsEqualTo(3L + expected.Length);
        await Assert.That(stream.ToArray()[3..].SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task CanceledSave_ThrowsOperationCanceled()
    {
        using Image source = EncoderSupport.Pattern(8, 8);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.That(() => source.Save(new MemoryStream(), EncoderSupport.Options(), cancel.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InvalidOptions_AreRejected()
    {
        await Assert.That(() => new TiffEncoderOptions { RowsPerStrip = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { ColorType = (TiffColorType)99 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { Compression = (TiffCompression)99 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { Predictor = (TiffPredictor)99 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { DeflateLevel = 10 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { DeflateLevel = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { JpegQuality = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { JpegQuality = 101 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { JpegSubsampling = (JpegSubsampling)9 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { TileSize = 8 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { TileSize = -16 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { Container = (TiffContainer)9 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { MaxDegreeOfParallelism = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { MaxDegreeOfParallelism = -2 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SaveAllPagesFalse_WritesTheFirstFrame()
    {
        using Image source = EncoderSupport.Pattern(6, 4);
        using Image second = EncoderSupport.Pattern(6, 4, gray: true);
        source.Frames.AddClone(second.Frames[0]);
        byte[] file = source.ToTiff(EncoderSupport.Options() with { SaveAllPages = false });
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
        await Assert.That(TestSupport.Identify(file).ContainerItemCount).IsEqualTo(1);
        using Image first = EncoderSupport.Pattern(6, 4);
        await Assert.That(file.AsSpan().SequenceEqual(first.ToTiff(EncoderSupport.Options()))).IsTrue();
    }

    [Test]
    [NotInParallel(nameof(ImageConfiguration))]
    public async Task SaveByExtension_UsesTheTiffEncoder()
    {
        ImageConfiguration previous = ImageConfiguration.Default;
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-tiff-{Guid.NewGuid():N}.tif");
        try
        {
            ImageConfiguration.Default = TestSupport.Configuration;
            using Image source = EncoderSupport.Pattern(5, 5);
            source.Save(path);
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(source.ToTiff())).IsTrue();
            await Assert.That(source.Encode(ImageFormat.Tiff).SequenceEqual(source.ToTiff())).IsTrue();
        }
        finally
        {
            ImageConfiguration.Default = previous;
            File.Delete(path);
        }
    }

    internal sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
