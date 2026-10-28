using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// 16-bit images: decoding with <see cref="DecoderOptions.PreserveBitDepth"/> to <see cref="PixelType.Rgba64"/>, and
/// writing 16-bit samples (<see cref="TiffBitDepth"/>). Expected samples are computed from the stored values here, and
/// ImageMagick (Q16) reads both the corpus and written files as an independent 16-bit reader when it is installed.
/// </summary>
public sealed class HighBitDepthTests
{
    private static readonly DecoderOptions Deep = TestSupport.Options with { PreserveBitDepth = true };

    public static IEnumerable<string> Files() => TestSupport.Files();

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Corpus_DeepSamplesDecodeToRgba64_WithinOneLevelOfTheEightBitDecode(string name)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        Image eight;
        try
        {
            eight = TestSupport.Load(data);
        }
        catch (Exception e) when (e is NotSupportedException or ImageFormatException)
        {
            await Assert.That(() => TestSupport.Load(data, Deep)).Throws<Exception>();
            return;
        }

        using (eight)
        using (Image deep = TestSupport.Load(data, Deep))
        {
            ImageColorInfo color = TestSupport.Identify(data).Color;
            bool expectDeep = color.BitsPerSample > 8 && color.Model != ImageColorModel.Palette;
            await Assert.That(deep.PixelType).IsEqualTo(expectDeep ? PixelType.Rgba64 : PixelType.Rgba32);
            if (!expectDeep)
            {
                // Sources of up to 8 bits are unchanged by the option.
                await Assert.That(TestSupport.Sha256(deep)).IsEqualTo(TestSupport.Sha256(eight));
                return;
            }

            // One rounding from 16 bits may differ from the direct 8-bit rounding by one level, never more. L*a*b* is the
            // exception: libtiff's 8-bit conversion looks the gamma curve up in 1501 steps of luminance, truncating the
            // index, which near black is up to 12 levels darker than the exact curve the 16-bit conversion evaluates.
            Rgba32[] direct = eight.GetPixelSpan<Rgba32>().ToArray();
            Rgba64[] wide = deep.GetPixelSpan<Rgba64>().ToArray();
            int worst = 0;
            for (int i = 0; i < wide.Length; i++)
            {
                worst = Math.Max(worst, Max(wide[i].ToRgba32(), direct[i]));
            }

            await Assert.That(worst).IsLessThanOrEqualTo(color.Model == ImageColorModel.Lab ? 12 : 1);
            if (color is { BitsPerSample: 16, SampleFormat: ImageSampleFormat.UnsignedInteger, Model: ImageColorModel.Gray or ImageColorModel.Rgb }
                && color.AlphaMode != ImageAlphaMode.Premultiplied)
            {
                // Straight 16-bit integer samples are stored as they are, so the 8-bit decode is their rounding.
                await Assert.That(wide.Select(p => p.ToRgba32())).IsEquivalentTo(direct, CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    public async Task SixteenBitGrayAndRgb_KeepEverySample_InEveryByteOrderAndPlanarLayout()
    {
        const int width = 5, height = 3;
        ushort[] values = [.. Enumerable.Range(0, width * height * 4).Select(i => (ushort)(i * 4099 + 1))];
        foreach (bool bigEndian in new[] { false, true })
        foreach ((int spp, ushort photometric) in new[] { (1, (ushort)1), (2, (ushort)1), (3, (ushort)2), (4, (ushort)2) })
        foreach (bool planar in spp > 1 ? new[] { false, true } : [false])
        {
            byte[] file = Build16(width, height, spp, photometric, values, bigEndian, planar);
            using Image image = TestSupport.Load(file, Deep);
            Rgba64[] pixels = image.GetPixelSpan<Rgba64>().ToArray();
            for (int i = 0; i < width * height; i++)
            {
                ushort[] s = [.. values.Skip(i * spp).Take(spp)];
                Rgba64 expected = spp switch
                {
                    1 => new Rgba64(s[0], s[0], s[0]),
                    2 => new Rgba64(s[0], s[0], s[0], s[1]),
                    3 => new Rgba64(s[0], s[1], s[2]),
                    _ => new Rgba64(s[0], s[1], s[2], s[3]),
                };
                await Assert.That(pixels[i]).IsEqualTo(expected);
            }
        }
    }

    [Test]
    public async Task OtherDepths_ScaleToSixteenBits_WithWhiteAtFullScale()
    {
        // 12-bit grey (packed MSB first), 32-bit grey, MinIsWhite and associated alpha, against the documented rules.
        ushort[] twelve = [0, 1, 2048, 4094, 4095, 77];
        byte[] packed = new byte[twelve.Length * 3 / 2];
        for (int i = 0; i < twelve.Length; i += 2)
        {
            packed[i / 2 * 3] = (byte)(twelve[i] >> 4);
            packed[(i / 2 * 3) + 1] = (byte)((twelve[i] << 4) | (twelve[i + 1] >> 8));
            packed[(i / 2 * 3) + 2] = (byte)twelve[i + 1];
        }

        byte[] gray12 = TiffBuilder.GrayStrip(6, 1, 1, packed).Set(258, TiffBuilder.Short, 12).Build();
        using (Image image = TestSupport.Load(gray12, Deep))
        {
            await Assert.That(image.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R))
                .IsEquivalentTo(twelve.Select(v => (ushort)Math.Round(v * 65535.0 / 4095, MidpointRounding.AwayFromZero)), CollectionOrdering.Matching);
        }

        using (Image white = TestSupport.Load(TiffBuilder.GrayStrip(6, 1, 1, packed).Set(258, TiffBuilder.Short, 12).Set(262, TiffBuilder.Short, 0).Build(), Deep))
        {
            await Assert.That(white.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R))
                .IsEquivalentTo(twelve.Select(v => (ushort)Math.Round((4095 - v) * 65535.0 / 4095, MidpointRounding.AwayFromZero)), CollectionOrdering.Matching);
        }

        uint[] thirtyTwo = [0, 1, 32768, 2147483648, 4294934528, uint.MaxValue];
        byte[] raw32 = new byte[thirtyTwo.Length * 4];
        for (int i = 0; i < thirtyTwo.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(raw32.AsSpan(i * 4), thirtyTwo[i]);
        using (Image image = TestSupport.Load(TiffBuilder.GrayStrip(6, 1, 1, raw32).Set(258, TiffBuilder.Short, 32).Build(), Deep))
        {
            await Assert.That(image.GetPixelSpan<Rgba64>().ToArray().Select(p => p.R))
                .IsEquivalentTo(thirtyTwo.Select(v => (ushort)Math.Round(v / 65537.0, MidpointRounding.AwayFromZero)), CollectionOrdering.Matching);
        }

        // Associated alpha is divided out at the source depth: min(max, v * max / a).
        ushort[] premultiplied = [30000, 60000, 0, 65535, 1, 7, 40000, 50000];
        byte[] assoc = Build16(4, 1, 2, 1, premultiplied, bigEndian: false, planar: false, extra: 1);
        using (Image image = TestSupport.Load(assoc, Deep))
        {
            Rgba64[] pixels = image.GetPixelSpan<Rgba64>().ToArray();
            for (int i = 0; i < 4; i++)
            {
                uint v = premultiplied[2 * i], a = premultiplied[(2 * i) + 1];
                ushort expected = a == 0 ? (ushort)0 : (ushort)Math.Min(65535u, v * 65535u / a);
                await Assert.That(pixels[i]).IsEqualTo(new Rgba64(expected, expected, expected, (ushort)a));
            }
        }
    }

    [Test]
    public async Task Budget_ChargesSixteenBitPixelsTwice()
    {
        byte[] file = Build16(5, 3, 3, 2, [.. Enumerable.Range(0, 45).Select(i => (ushort)(i * 1000))], bigEndian: false, planar: false);
        using (Image fits = TestSupport.Load(file, Deep with { MaxPixels = 30 }))
        {
            await Assert.That(fits.PixelType).IsEqualTo(PixelType.Rgba64);
        }

        await Assert.That(() => TestSupport.Load(file, Deep with { MaxPixels = 29 })).Throws<ImageFormatException>();
        using Image eight = TestSupport.Load(file, TestSupport.Options with { MaxPixels = 15 });
        await Assert.That(eight.PixelType).IsEqualTo(PixelType.Rgba32);
    }

    public static IEnumerable<(TiffCompression Compression, TiffPredictor Predictor, int TileSize)> Layouts() =>
    [
        (TiffCompression.None, TiffPredictor.Auto, 0),
        (TiffCompression.Lzw, TiffPredictor.Auto, 0),
        (TiffCompression.Lzw, TiffPredictor.None, 16),
        (TiffCompression.Deflate, TiffPredictor.Horizontal, 0),
        (TiffCompression.Deflate, TiffPredictor.Auto, 32),
        (TiffCompression.PackBits, TiffPredictor.Auto, 0),
    ];

    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task Encode_SixteenBitImages_RoundTripExactly_ForEveryAutomaticColourType(TiffCompression compression, TiffPredictor predictor, int tileSize)
    {
        foreach ((bool gray, bool alpha, ushort spp) in new[] { (true, false, (ushort)1), (true, true, (ushort)2), (false, false, (ushort)3), (false, true, (ushort)4) })
        {
            using Image source = Pattern16(37, 21, gray, alpha);
            byte[] file = Save(source, new TiffEncoderOptions { Compression = compression, Predictor = predictor, TileSize = tileSize, RowsPerStrip = tileSize == 0 ? 5 : 0 });
            ImageInfo info = TestSupport.Identify(file);
            await Assert.That(info.Color.BitsPerSample).IsEqualTo(16);
            await Assert.That(info.Color.ChannelCount).IsEqualTo(spp);
            using Image back = TestSupport.Load(file, Deep);
            await Assert.That(back.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Encode_FullRamp_SurvivesAllPages()
    {
        // 65536 levels in a 256 x 256 grey ramp, and a second page with another pattern.
        using var ramp = new Image(256, 256, PixelType.Rgba64);
        Span<Rgba64> pixels = ramp.GetPixelSpan<Rgba64>();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Rgba64((ushort)i, (ushort)i, (ushort)i);
        using Image second = Pattern16(256, 256, gray: false, alpha: true);
        using Image pages = Image.CreateAnimation(TimeSpan.Zero, ramp, second);
        byte[] file = Save(pages, new TiffEncoderOptions());
        for (int page = 0; page < 2; page++)
        {
            using Image back = TestSupport.Load(file, TestSupport.Page(page) with { PreserveBitDepth = true });
            await Assert.That(back.GetPixelSpan<Rgba64>().ToArray())
                .IsEquivalentTo((page == 0 ? ramp : second).GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Encode_DepthChoice_FollowsTiffBitDepth()
    {
        using Image eight = EncoderSupport.Pattern(40, 30, alpha: true);
        using Image widened = eight.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        using Image deep = Pattern16(40, 30, gray: false, alpha: true);
        using Image rounded = deep.Clone(x => x.ConvertPixelType(PixelType.Rgba32));

        // Auto: exact 8-bit content is written as the 8-bit image would be; UpTo8 rounds once, like the core's conversion.
        await Assert.That(Save(widened, new TiffEncoderOptions())).IsEquivalentTo(Save(eight, new TiffEncoderOptions()), CollectionOrdering.Matching);
        await Assert.That(Save(deep, new TiffEncoderOptions { BitDepth = TiffBitDepth.UpTo8 }))
            .IsEquivalentTo(Save(rounded, new TiffEncoderOptions()), CollectionOrdering.Matching);

        // Sixteen widens 8-bit images exactly.
        byte[] sixteen = Save(eight, new TiffEncoderOptions { BitDepth = TiffBitDepth.Sixteen });
        await Assert.That(TestSupport.Identify(sixteen).Color.BitsPerSample).IsEqualTo(16);
        using (Image back = TestSupport.Load(sixteen, Deep))
        {
            await Assert.That(back.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(widened.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
        }

        // 8-bit compressions round a 16-bit image under Auto.
        byte[] jpeg = Save(deep, new TiffEncoderOptions { Compression = TiffCompression.Jpeg });
        await Assert.That(TestSupport.Identify(jpeg).Color.BitsPerSample).IsEqualTo(8);
        byte[] webp = Save(deep, new TiffEncoderOptions { Compression = TiffCompression.WebP, WebPLossless = true });
        using (Image back = TestSupport.Load(webp))
        {
            await Assert.That(back.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        }

        // Explicit colour types without 16-bit samples need UpTo8.
        await Assert.That(() => Save(deep, new TiffEncoderOptions { ColorType = TiffColorType.Palette })).Throws<ArgumentException>();
        await Assert.That(() => Save(deep, new TiffEncoderOptions { ColorType = TiffColorType.BlackWhite })).Throws<ArgumentException>();
        foreach (TiffCompression compression in new[] { TiffCompression.Jpeg, TiffCompression.WebP, TiffCompression.CcittGroup4 })
        {
            await Assert.That(() => Save(eight, new TiffEncoderOptions { BitDepth = TiffBitDepth.Sixteen, Compression = compression })).Throws<ArgumentException>();
        }

        await Assert.That(() => Save(eight, new TiffEncoderOptions { BitDepth = TiffBitDepth.Sixteen, ColorType = TiffColorType.Palette })).Throws<ArgumentException>();
        await Assert.That(() => new TiffEncoderOptions { BitDepth = (TiffBitDepth)3 }).Throws<ArgumentOutOfRangeException>();
        using (Image paletted = TestSupport.Load(Save(deep, new TiffEncoderOptions { BitDepth = TiffBitDepth.UpTo8, ColorType = TiffColorType.Grayscale })))
        {
            await Assert.That(paletted.PixelType).IsEqualTo(PixelType.Rgba32);
        }
    }

    [Test]
    public async Task Encode_GrayscaleFromColour_UsesSixteenBitLuminance()
    {
        using Image deep = Pattern16(9, 4, gray: false, alpha: false);
        using Image back = TestSupport.Load(Save(deep, new TiffEncoderOptions { ColorType = TiffColorType.Grayscale }), Deep);
        Rgba64[] source = deep.GetPixelSpan<Rgba64>().ToArray(), pixels = back.GetPixelSpan<Rgba64>().ToArray();
        for (int i = 0; i < source.Length; i++)
        {
            Rgba64 p = source[i];
            var y = (ushort)(((19595u * p.R) + (38470u * p.G) + (7471u * p.B) + 32768u) >> 16);
            await Assert.That(pixels[i]).IsEqualTo(new Rgba64(y, y, y));
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    [MethodDataSource(nameof(Files))]
    public async Task Corpus_DeepGrayAndRgb_MatchImageMagickWithinOneCode(string name)
    {
        Skip.Unless(Magick.Available, "ImageMagick (convert) is not installed.");
        byte[] data = TestSupport.ReadCorpus(name);
        ImageInfo info = TestSupport.Identify(data);
        // CMYK, L*a*b* and associated alpha follow libtiff's and Pillow's conversions, which ImageMagick does not; ImageMagick
        // 6.9.12 also reads planar grey (alpha into G) and 12-bit MinIsWhite differently from libtiff, whose 8-bit results
        // the corpus test above holds these files to.
        if (name.Contains("separate", StringComparison.Ordinal) || name.Contains("miniswhite", StringComparison.Ordinal)
            || info.Color.BitsPerSample <= 8 || info.Color.Model is not (ImageColorModel.Gray or ImageColorModel.Rgb)
            || info.Color.AlphaMode == ImageAlphaMode.Premultiplied || info.Color.SampleFormat == ImageSampleFormat.SignedInteger
            || info.ContainerItemCount > 1)
        {
            return;
        }

        Image deep;
        try
        {
            deep = TestSupport.Load(data, Deep);
        }
        catch (NotSupportedException)
        {
            return;
        }

        using (deep)
        {
            ushort[]? expected = Magick.Read(Path.Combine(AppContext.BaseDirectory, "corpus", name), deep.Width, deep.Height);
            if (expected is null)
            {
                return;
            }

            ReadOnlySpan<ushort> actual = MemoryMarshal.Cast<Rgba64, ushort>(deep.GetPixelSpan<Rgba64>());
            int worst = 0;
            for (int i = 0; i < actual.Length; i++) worst = Math.Max(worst, Math.Abs(actual[i] - expected[i]));
            await Assert.That(worst).IsLessThanOrEqualTo(1);
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task WrittenSixteenBitFiles_ReadIdenticallyByImageMagick()
    {
        Skip.Unless(Magick.Available, "ImageMagick (convert) is not installed.");
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-tiff16-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach ((bool gray, bool alpha) in new[] { (true, false), (true, true), (false, false), (false, true) })
            foreach ((TiffCompression compression, TiffPredictor predictor, int tileSize) in Layouts())
            {
                using Image source = Pattern16(37, 21, gray, alpha);
                string path = Path.Combine(dir, $"{gray}{alpha}{compression}{predictor}{tileSize}.tif");
                File.WriteAllBytes(path, Save(source, new TiffEncoderOptions { Compression = compression, Predictor = predictor, TileSize = tileSize }));
                ushort[]? read = Magick.Read(path, 37, 21);
                await Assert.That(read).IsNotNull();
                await Assert.That(read!).IsEquivalentTo(MemoryMarshal.Cast<Rgba64, ushort>(source.GetPixelSpan<Rgba64>()).ToArray(), CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A deterministic 16-bit pattern with samples that are not multiples of 257.</summary>
    internal static Image Pattern16(int width, int height, bool gray, bool alpha)
    {
        var image = new Image(width, height, PixelType.Rgba64);
        Span<Rgba64> pixels = image.GetPixelSpan<Rgba64>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var r = (ushort)((x * 1777) + (y * 3001) + 3);
                var g = gray ? r : (ushort)((x * 911) + (y * 5003) + 1);
                var b = gray ? r : (ushort)((x * x * 97) + (y * 7));
                var a = alpha ? (ushort)((x * 2903) + (y * 1709) + 11) : ushort.MaxValue;
                pixels[(y * width) + x] = new Rgba64(r, g, b, a);
            }
        }

        return image;
    }

    private static byte[] Save(Image image, TiffEncoderOptions options)
    {
        using var stream = new MemoryStream();
        image.Save(stream, options with { Configuration = TestSupport.Configuration });
        return stream.ToArray();
    }

    /// <summary>An uncompressed 16-bit image in one strip, contiguous or planar, with alpha as extra sample <paramref name="extra"/>.</summary>
    private static byte[] Build16(int width, int height, int spp, ushort photometric, ushort[] values, bool bigEndian, bool planar, uint extra = 2)
    {
        var raw = new byte[width * height * spp * 2];
        for (int i = 0; i < width * height; i++)
        {
            for (int s = 0; s < spp; s++)
            {
                int at = planar ? ((s * width * height) + i) * 2 : ((i * spp) + s) * 2;
                if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(at), values[(i * spp) + s]);
                else BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at), values[(i * spp) + s]);
            }
        }

        TiffBuilder builder = TiffBuilder.GrayStrip(width, height, 1, raw)
            .Set(258, TiffBuilder.Short, [.. Enumerable.Repeat(16u, spp)])
            .Set(262, TiffBuilder.Short, photometric)
            .Set(277, TiffBuilder.Short, (uint)spp);
        if (spp is 2 or 4)
        {
            builder.Set(338, TiffBuilder.Short, extra);
        }

        if (planar)
        {
            uint plane = (uint)(width * height * 2);
            builder.Set(284, TiffBuilder.Short, 2)
                .Set(273, TiffBuilder.Long, [.. Enumerable.Range(0, spp).Select(p => 8 + ((uint)p * plane))])
                .Set(279, TiffBuilder.Long, [.. Enumerable.Repeat(plane, spp)]);
        }

        byte[] file = builder.Build();
        if (bigEndian)
        {
            file = ToBigEndian(file);
        }

        return file;
    }

    /// <summary>Rewrites the little-endian header and IFD of a <see cref="TiffBuilder"/> file as big-endian; the samples are already big-endian.</summary>
    private static byte[] ToBigEndian(byte[] file)
    {
        byte[] result = (byte[])file.Clone();
        "MM"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), 42);
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), ifd);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)ifd));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan((int)ifd), (ushort)count);
        for (int e = 0; e < count; e++)
        {
            int at = (int)ifd + 2 + (e * 12);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at)), type = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2));
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at + 4), n);
            int size = type == TiffBuilder.Short ? 2 : 4;
            int valueAt = n * size <= 4 ? at + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8));
            if (n * size > 4) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at + 8), (uint)valueAt);
            for (int v = 0; v < n * (type == TiffBuilder.Rational ? 2 : 1); v++)
            {
                if (size == 2) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(valueAt + (v * 2)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(valueAt + (v * 2))));
                else BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(valueAt + (v * 4)), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(valueAt + (v * 4))));
            }
        }

        return result;
    }

    private static int Max(Rgba32 a, Rgba32 b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    /// <summary>ImageMagick Q16 as an independent 16-bit TIFF reader: RGBA samples, native byte order.</summary>
    private static class Magick
    {
        public static bool Available { get; } = Run("convert", "-version") is not null;

        public static ushort[]? Read(string path, int width, int height)
        {
            byte[]? raw = Run("convert", $"\"{path}[0]\" -depth 16 -endian LSB rgba:-");
            if (raw is null || raw.Length != width * height * 8)
            {
                return null;
            }

            return [.. MemoryMarshal.Cast<byte, ushort>(raw)];
        }

        private static byte[]? Run(string file, string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(file, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (process is null) return null;
                using var output = new MemoryStream();
                Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
                _ = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                copy.Wait();
                return process.ExitCode == 0 ? output.ToArray() : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
