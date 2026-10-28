using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>Tiles, BigTIFF, multi-page output and parallel compression (0.5.0).</summary>
public sealed class LayoutTests
{
    private static readonly TiffCompression[] Lossless =
        [TiffCompression.None, TiffCompression.PackBits, TiffCompression.Lzw, TiffCompression.Deflate];

    public static IEnumerable<(TiffCompression, int, int, int)> TileCases()
    {
        foreach (TiffCompression compression in Lossless)
        {
            yield return (compression, 16, 70, 41);
            yield return (compression, 32, 32, 32);
            yield return (compression, 48, 5, 100);
        }
    }

    [Test]
    [MethodDataSource(nameof(TileCases))]
    public async Task Tiles_RoundTripExactly(TiffCompression compression, int tile, int width, int height)
    {
        foreach ((Func<Image> create, TiffColorType decoded) in Sources(width, height))
        {
            using Image source = create();
            byte[] file = source.ToTiff(EncoderSupport.Options(compression: compression) with { TileSize = tile });
            var tags = EncoderSupport.Tags(file);
            await Assert.That(tags.ContainsKey(322) && !tags.ContainsKey(273) && !tags.ContainsKey(278)).IsTrue();
            await Assert.That(tags[324].Count).IsEqualTo((uint)(((source.Width + tile - 1) / tile) * ((source.Height + tile - 1) / tile)));
            using Image image = TestSupport.Load(file);
            await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, decoded)));
        }
    }

    [Test]
    [Arguments(TiffCompression.CcittGroup4)]
    [Arguments(TiffCompression.Lzw)]
    [Arguments(TiffCompression.None)]
    public async Task BlackWhiteTiles_RoundTripExactly(TiffCompression compression)
    {
        using Image source = EncoderSupport.Bilevel(61, 40, noise: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: compression) with { TileSize = 16 });
        using Image image = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan<Rgba32>()));
        await Assert.That(EncoderSupport.Tags(file)[258].Value).IsEqualTo(1u);
    }

    [Test]
    public async Task LosslessWebPTiles_RoundTripExactly()
    {
        using Image source = EncoderSupport.Pattern(70, 41, alpha: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP) with { TileSize = 32, WebPLossless = true });
        using Image image = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan<Rgba32>()));
        await Assert.That(EncoderSupport.StripsMatch(file, image)).IsTrue();
    }

    [Test]
    [Arguments(TiffCompression.Jpeg, false)]
    [Arguments(TiffCompression.Jpeg, true)]
    [Arguments(TiffCompression.WebP, false)]
    public async Task LossyTiles_AreCompleteStreamsOfTheTile(TiffCompression compression, bool gray)
    {
        using Image source = EncoderSupport.Pattern(70, 41, gray: gray);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: compression) with { TileSize = 32 });
        using Image image = TestSupport.Load(file);
        await Assert.That(EncoderSupport.StripsMatch(file, image)).IsTrue();
        // Padding repeats edge pixels, so tiles lose no more than strips of the same samples.
        using Image strips = TestSupport.Load(source.ToTiff(EncoderSupport.Options(compression: compression)));
        await Assert.That(Psnr(source, image)).IsGreaterThan(Psnr(source, strips) - 1);
        if (compression == TiffCompression.Jpeg)
        {
            await Assert.That(EncoderSupport.Strips(file).All(tile => EncoderSupport.JpegComponents(tile) == (gray ? 1 : 3))).IsTrue();
        }
    }

    [Test]
    public async Task TileEdges_RepeatTheLastColumnAndRow()
    {
        using Image source = EncoderSupport.Pattern(17, 18, gray: true);
        byte[] file = source.ToTiff(EncoderSupport.Options() with { TileSize = 16 });
        List<byte[]> tiles = EncoderSupport.Strips(file);
        // Tile 1 holds column 16 then repeats it; tile 2 holds rows 16 and 17 then repeats row 17.
        await Assert.That(tiles[1][..16].All(v => v == tiles[1][0])).IsTrue();
        await Assert.That(tiles[2].AsSpan(2 * 16, 16).SequenceEqual(tiles[2].AsSpan(15 * 16, 16))).IsTrue();
        await Assert.That(tiles[2].AsSpan(16, 16).SequenceEqual(tiles[2].AsSpan(2 * 16, 16))).IsTrue();
    }

    [Test]
    public async Task TilesAndRowsPerStrip_AreRejected()
    {
        using Image source = EncoderSupport.Pattern(8, 8);
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(rowsPerStrip: 4) with { TileSize = 16 })).Throws<ArgumentException>();
        await Assert.That(() => source.ToTiff(EncoderSupport.Options(compression: TiffCompression.WebP) with { TileSize = 16384 }))
            .Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(TiffCompression.None, 0)]
    [Arguments(TiffCompression.Lzw, 0)]
    [Arguments(TiffCompression.Deflate, 32)]
    [Arguments(TiffCompression.CcittGroup4, 0)]
    public async Task BigTiff_RoundTrips(TiffCompression compression, int tile)
    {
        using Image source = compression == TiffCompression.CcittGroup4 ? EncoderSupport.Bilevel(61, 40) : EncoderSupport.Pattern(70, 41, alpha: true);
        source.Metadata.IccProfile = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        source.Metadata.Exif = ExifSample.Block(bigEndian: false);
        TiffEncoderOptions options = EncoderSupport.Options(rowsPerStrip: tile == 0 ? 7 : 0, compression: compression)
            with { TileSize = tile, Container = TiffContainer.BigTiff };
        byte[] file = source.ToTiff(options);
        await Assert.That(file.AsSpan(0, 8).SequenceEqual(new byte[] { (byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0 })).IsTrue();
        await Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8)) % 2).IsEqualTo(0ul);
        using Image image = TestSupport.Load(file);
        using Image classic = TestSupport.Load(source.ToTiff(options with { Container = TiffContainer.Classic }));
        await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(classic.GetPixelSpan<Rgba32>()));
        await Assert.That(image.Metadata.IccProfile!.SequenceEqual(source.Metadata.IccProfile)).IsTrue();
        await Assert.That(image.Metadata.Exif).IsNotNull();
    }

    [Test]
    public async Task Auto_WritesClassicTiffForSmallImages()
    {
        using Image source = EncoderSupport.Pattern(9, 9);
        byte[] auto = source.ToTiff(EncoderSupport.Options());
        await Assert.That(auto[2]).IsEqualTo((byte)42);
        await Assert.That(auto.SequenceEqual(source.ToTiff(EncoderSupport.Options() with { Container = TiffContainer.Classic }))).IsTrue();
    }

    [Test]
    public async Task EveryFrame_BecomesAPage()
    {
        using Image source = Pages();
        TiffEncoderOptions options = EncoderSupport.Options(compression: TiffCompression.Lzw);
        byte[] file = source.ToTiff(options);
        await Assert.That(TestSupport.Identify(file).ContainerItemCount).IsEqualTo(3);
        TiffColorType[] decoded = [TiffColorType.Rgb, TiffColorType.GrayscaleAlpha, TiffColorType.BlackWhite];
        for (int page = 0; page < 3; page++)
        {
            using Image image = TestSupport.Load(file, TestSupport.Page(page));
            using Image frame = source.Frames.CloneFrame(page);
            await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(frame, decoded[page])));
            await Assert.That(image.Metadata.IccProfile!.SequenceEqual(source.Metadata.IccProfile!)).IsTrue();
            await Assert.That(image.Metadata.HorizontalResolution).IsEqualTo(300f);
        }

        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags[254].Value).IsEqualTo(2u);
        // PageNumber 297 (0x129): SHORT, two values, page 0 of 3.
        await Assert.That(tags[297]).IsEqualTo(((ushort)3, 2u, 0u));
        await Assert.That(file.AsSpan().IndexOf(new byte[] { 0x29, 0x01, 3, 0, 2, 0, 0, 0, 0, 0, 3, 0 })).IsGreaterThan(0);
    }

    [Test]
    public async Task Pages_AreTheSameInEveryVariant()
    {
        using Image source = Pages();
        TiffEncoderOptions options = EncoderSupport.Options(compression: TiffCompression.Lzw);
        byte[] classic = source.ToTiff(options);
        foreach (TiffEncoderOptions variant in new[] { options with { Container = TiffContainer.BigTiff }, options with { TileSize = 16 } })
        {
            byte[] file = source.ToTiff(variant);
            for (int page = 0; page < 3; page++)
            {
                using Image expected = TestSupport.Load(classic, TestSupport.Page(page));
                using Image image = TestSupport.Load(file, TestSupport.Page(page));
                await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan<Rgba32>())).IsEqualTo(EncoderSupport.Sha256(expected.GetPixelSpan<Rgba32>()));
            }
        }
    }

    [Test]
    public async Task NonSeekableStream_GetsTheSameBytes()
    {
        using Image source = Pages();
        foreach (TiffEncoderOptions options in new[]
        {
            EncoderSupport.Options(compression: TiffCompression.Lzw),
            EncoderSupport.Options(compression: TiffCompression.PackBits) with { Container = TiffContainer.BigTiff, TileSize = 16 },
        })
        {
            byte[] seekable = source.ToTiff(options);
            using var sink = new MemoryStream();
            source.SaveAsTiff(new EncoderTests.ForwardOnlyStream(sink), options);
            await Assert.That(sink.ToArray().SequenceEqual(seekable)).IsTrue();

            using var offset = new MemoryStream();
            offset.Write([1, 2, 3]);
            source.SaveAsTiff(offset, options);
            await Assert.That(offset.ToArray()[3..].SequenceEqual(seekable)).IsTrue();
        }
    }

    public static IEnumerable<TiffEncoderOptions> ParallelCases()
    {
        foreach (TiffCompression compression in Enum.GetValues<TiffCompression>())
        {
            yield return EncoderSupport.Options(rowsPerStrip: 16, compression: compression);
            yield return EncoderSupport.Options(compression: compression) with { TileSize = 32 };
        }
    }

    [Test]
    [MethodDataSource(nameof(ParallelCases))]
    public async Task Output_DoesNotDependOnParallelism(TiffEncoderOptions options)
    {
        using Image source = options.Compression == TiffCompression.CcittGroup4 ? EncoderSupport.Bilevel(300, 200, noise: true) : EncoderSupport.Noise(300, 200);
        source.Frames.AddClone(source.Frames[0]);
        byte[] single = source.ToTiff(options with { MaxDegreeOfParallelism = 1 });
        foreach (int degree in new[] { 2, 3, 16, -1 })
        {
            await Assert.That(source.ToTiff(options with { MaxDegreeOfParallelism = degree }).SequenceEqual(single)).IsTrue();
        }
    }

    [Test]
    public async Task CanceledParallelSave_ThrowsOperationCanceled()
    {
        using Image source = EncoderSupport.Noise(300, 300);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        TiffEncoderOptions options = EncoderSupport.Options(rowsPerStrip: 1, compression: TiffCompression.Deflate) with { MaxDegreeOfParallelism = 4 };
        await Assert.That(() => source.Save(new MemoryStream(), options, cancel.Token)).Throws<OperationCanceledException>();
    }

    /// <summary>RGB, grey with alpha and black-and-white frames with ICC and 300 dpi, so each page has its own colour type.</summary>
    private static Image Pages()
    {
        Image image = EncoderSupport.Pattern(33, 17);
        using Image gray = EncoderSupport.Pattern(33, 17, gray: true, alpha: true);
        using Image bilevel = EncoderSupport.Bilevel(33, 17);
        image.Frames.AddClone(gray.Frames[0]);
        image.Frames.AddClone(bilevel.Frames[0]);
        image.Metadata.IccProfile = [.. Enumerable.Range(0, 50).Select(i => (byte)i)];
        image.Metadata.HorizontalResolution = 300;
        image.Metadata.VerticalResolution = 300;
        return image;
    }

    private static IEnumerable<(Func<Image>, TiffColorType)> Sources(int width, int height)
    {
        yield return (() => EncoderSupport.Pattern(width, height), TiffColorType.Rgb);
        yield return (() => EncoderSupport.Pattern(width, height, alpha: true), TiffColorType.Rgba);
        yield return (() => EncoderSupport.Pattern(width, height, gray: true), TiffColorType.Grayscale);
        yield return (() => EncoderSupport.Pattern(width, height, gray: true, alpha: true), TiffColorType.GrayscaleAlpha);
        yield return (() => EncoderSupport.Pattern(Math.Max(width, 40), Math.Max(height, 30), posterize: true), TiffColorType.Palette);
    }

    private static double Psnr(Image a, Image b)
    {
        ReadOnlySpan<Rgba32> x = a.GetPixelSpan<Rgba32>(), y = b.GetPixelSpan<Rgba32>();
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += Math.Pow(x[i].R - y[i].R, 2) + Math.Pow(x[i].G - y[i].G, 2) + Math.Pow(x[i].B - y[i].B, 2);
        }

        double mse = sum / (3.0 * x.Length);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
    }
}
