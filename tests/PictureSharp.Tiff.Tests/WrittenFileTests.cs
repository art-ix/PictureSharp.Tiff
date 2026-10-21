using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Byte-exact snapshots of encoder output in <c>tests/assets/written</c>, which <c>verify_written.py</c> checks with
/// libtiff 4.5.1 and Pillow 10.2.0. A mismatch writes the new files to <c>written.received</c> in the output directory:
/// verify them with the script, then copy them over the snapshots. Deflate files are snapshotted for the script but not
/// compared, since their bytes depend on the runtime's zlib. JPEG files come with our decode as a PNG
/// (<c>name.decoded.png</c>), since libjpeg decodes them slightly differently.
/// </summary>
public sealed class WrittenFileTests
{
    private static readonly string Snapshots = Path.Combine(AppContext.BaseDirectory, "written");
    private static readonly string Received = Path.Combine(AppContext.BaseDirectory, "written.received");
    private static readonly string CorpusOutput = Path.Combine(AppContext.BaseDirectory, "written.corpus");

    private static IEnumerable<(string Name, Func<Image> Source, TiffEncoderOptions Options, TiffColorType Decoded)> Cases()
    {
        yield return ("gray_7x3", () => EncoderSupport.Pattern(7, 3, gray: true), EncoderSupport.Options(), TiffColorType.Grayscale);
        yield return ("gray_alpha_33x17", () => EncoderSupport.Pattern(33, 17, gray: true, alpha: true), EncoderSupport.Options(), TiffColorType.GrayscaleAlpha);
        yield return ("rgb_33x17_rows5", () => EncoderSupport.Pattern(33, 17), EncoderSupport.Options(rowsPerStrip: 5), TiffColorType.Rgb);
        yield return ("rgba_13x1", () => EncoderSupport.Pattern(13, 1, alpha: true), EncoderSupport.Options(), TiffColorType.Rgba);
        yield return ("rgb_1x1", () => EncoderSupport.Pattern(1, 1), EncoderSupport.Options(TiffColorType.Rgb), TiffColorType.Rgb);
        yield return ("gray_1x40_rows1", () => EncoderSupport.Pattern(1, 40, gray: true), EncoderSupport.Options(rowsPerStrip: 1), TiffColorType.Grayscale);
        yield return ("gray_from_rgb_16x8", () => EncoderSupport.Pattern(16, 8), EncoderSupport.Options(TiffColorType.Grayscale), TiffColorType.Grayscale);
        yield return ("gray_alpha_from_rgba_16x8", () => EncoderSupport.Pattern(16, 8, alpha: true), EncoderSupport.Options(TiffColorType.GrayscaleAlpha), TiffColorType.GrayscaleAlpha);
        yield return ("rgb_from_rgba_16x8", () => EncoderSupport.Pattern(16, 8, alpha: true), EncoderSupport.Options(TiffColorType.Rgb), TiffColorType.Rgb);
        yield return ("rgba_opaque_8x8", () => EncoderSupport.Pattern(8, 8), EncoderSupport.Options(TiffColorType.Rgba), TiffColorType.Rgba);
        yield return ("rgb_128x200_strips", () => EncoderSupport.Pattern(128, 200), EncoderSupport.Options(), TiffColorType.Rgb);
        yield return ("rgb_metadata_9x9", () =>
        {
            Image image = EncoderSupport.Pattern(9, 9);
            image.Metadata.IccProfile = [.. Enumerable.Range(0, 131).Select(i => (byte)(i * 7))];
            image.Metadata.Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");
            image.Metadata.HorizontalResolution = 300;
            image.Metadata.VerticalResolution = 150;
            return image;
        }, EncoderSupport.Options(), TiffColorType.Rgb);
        yield return ("palette_40x30_lzw", () => EncoderSupport.Pattern(40, 30, posterize: true), EncoderSupport.Options(compression: TiffCompression.Lzw), TiffColorType.Palette);
        yield return ("palette_7x3", () => EncoderSupport.Pattern(7, 3, alpha: true, posterize: true), EncoderSupport.Options(TiffColorType.Palette), TiffColorType.Palette);
        yield return ("palette_40x30_deflate", () => EncoderSupport.Pattern(40, 30, posterize: true), EncoderSupport.Options(compression: TiffCompression.Deflate), TiffColorType.Palette);
        yield return ("gray_alpha_33x17_lzw_predictor", () => EncoderSupport.Pattern(33, 17, gray: true, alpha: true), EncoderSupport.Options(compression: TiffCompression.Lzw, predictor: TiffPredictor.Horizontal), TiffColorType.GrayscaleAlpha);
        yield return ("rgb_33x17_lzw_predictor", () => EncoderSupport.Pattern(33, 17), EncoderSupport.Options(compression: TiffCompression.Lzw, predictor: TiffPredictor.Horizontal), TiffColorType.Rgb);
        yield return ("rgba_33x17_lzw", () => EncoderSupport.Pattern(33, 17, alpha: true), EncoderSupport.Options(compression: TiffCompression.Lzw, predictor: TiffPredictor.None), TiffColorType.Rgba);
        yield return ("rgb_128x200_lzw_predictor", () => EncoderSupport.Pattern(128, 200), EncoderSupport.Options(compression: TiffCompression.Lzw), TiffColorType.Rgb);
        yield return ("gray_noise_128x128_lzw", () => EncoderSupport.Noise(128, 128, gray: true), EncoderSupport.Options(compression: TiffCompression.Lzw), TiffColorType.Grayscale);
        yield return ("rgb_noise_64x64_lzw_rows64", () => EncoderSupport.Noise(64, 64), EncoderSupport.Options(rowsPerStrip: 64, compression: TiffCompression.Lzw, predictor: TiffPredictor.None), TiffColorType.Rgb);
        yield return ("gray_1x40_packbits_rows1", () => EncoderSupport.Pattern(1, 40, gray: true), EncoderSupport.Options(rowsPerStrip: 1, compression: TiffCompression.PackBits), TiffColorType.Grayscale);
        yield return ("rgb_128x200_packbits", () => EncoderSupport.Pattern(128, 200), EncoderSupport.Options(compression: TiffCompression.PackBits), TiffColorType.Rgb);
        yield return ("rgb_posterized_100x60_packbits", () => EncoderSupport.Pattern(100, 60, posterize: true), EncoderSupport.Options(TiffColorType.Rgb, compression: TiffCompression.PackBits), TiffColorType.Rgb);
        yield return ("gray_noise_96x40_packbits", () => EncoderSupport.Noise(96, 40, gray: true), EncoderSupport.Options(compression: TiffCompression.PackBits), TiffColorType.Grayscale);
        yield return ("gray_flat_300x20_packbits", () =>
        {
            var image = new Image(300, 20);
            image.GetPixelSpan().Fill(new Rgba32(77, 77, 77, 255));
            image.GetPixelSpan()[150] = new Rgba32(1, 1, 1, 255);
            return image;
        }, EncoderSupport.Options(compression: TiffCompression.PackBits), TiffColorType.Grayscale);
        yield return ("rgba_33x17_deflate_predictor", () => EncoderSupport.Pattern(33, 17, alpha: true), EncoderSupport.Options(compression: TiffCompression.Deflate), TiffColorType.Rgba);
        yield return ("bw_61x40_g4", () => EncoderSupport.Bilevel(61, 40), EncoderSupport.Options(compression: TiffCompression.CcittGroup4), TiffColorType.BlackWhite);
        yield return ("bw_noise_200x80_g4_rows16", () => EncoderSupport.Bilevel(200, 80, noise: true), EncoderSupport.Options(rowsPerStrip: 16, compression: TiffCompression.CcittGroup4), TiffColorType.BlackWhite);
        yield return ("bw_wide_6000x12_g4", () => EncoderSupport.Bilevel(6000, 12), EncoderSupport.Options(compression: TiffCompression.CcittGroup4), TiffColorType.BlackWhite);
        yield return ("bw_from_rgba_33x17_g4", () => EncoderSupport.Pattern(33, 17, alpha: true), EncoderSupport.Options(compression: TiffCompression.CcittGroup4), TiffColorType.BlackWhite);
        yield return ("bw_61x40", () => EncoderSupport.Bilevel(61, 40), EncoderSupport.Options(), TiffColorType.BlackWhite);
        yield return ("bw_61x40_packbits_rows7", () => EncoderSupport.Bilevel(61, 40), EncoderSupport.Options(rowsPerStrip: 7, compression: TiffCompression.PackBits), TiffColorType.BlackWhite);
        yield return ("bw_61x40_lzw", () => EncoderSupport.Bilevel(61, 40), EncoderSupport.Options(compression: TiffCompression.Lzw), TiffColorType.BlackWhite);
        yield return ("bw_61x40_deflate", () => EncoderSupport.Bilevel(61, 40), EncoderSupport.Options(compression: TiffCompression.Deflate), TiffColorType.BlackWhite);
        yield return ("bw_from_gray_33x17", () => EncoderSupport.Pattern(33, 17, gray: true), EncoderSupport.Options(TiffColorType.BlackWhite), TiffColorType.BlackWhite);
        yield return ("jpeg_rgb_96x64", () => EncoderSupport.Pattern(96, 64), EncoderSupport.Options(compression: TiffCompression.Jpeg), TiffColorType.Rgb);
        yield return ("jpeg_rgb_70x40_444_q90_rows8", () => EncoderSupport.Pattern(70, 40, alpha: true), EncoderSupport.Options(rowsPerStrip: 8, compression: TiffCompression.Jpeg) with { JpegQuality = 90, JpegSubsampling = JpegSubsampling.Ratio444 }, TiffColorType.Rgb);
        yield return ("jpeg_rgb_33x17_422", () => EncoderSupport.Pattern(33, 17), EncoderSupport.Options(compression: TiffCompression.Jpeg) with { JpegSubsampling = JpegSubsampling.Ratio422 }, TiffColorType.Rgb);
        yield return ("jpeg_rgb_33x40_440_rows16", () => EncoderSupport.Pattern(33, 40), EncoderSupport.Options(rowsPerStrip: 16, compression: TiffCompression.Jpeg) with { JpegSubsampling = JpegSubsampling.Ratio440 }, TiffColorType.Rgb);
        yield return ("jpeg_gray_61x30_rows8", () => EncoderSupport.Pattern(61, 30, gray: true), EncoderSupport.Options(rowsPerStrip: 8, compression: TiffCompression.Jpeg), TiffColorType.Grayscale);
        yield return ("jpeg_gray_from_rgb_40x24", () => EncoderSupport.Pattern(40, 24), EncoderSupport.Options(TiffColorType.Grayscale, compression: TiffCompression.Jpeg), TiffColorType.Grayscale);
        yield return ("jpeg_rgb_grey_strip_64x48_rows16", () =>
        {
            Image image = EncoderSupport.Pattern(64, 48);
            Span<Rgba32> pixels = image.GetPixelSpan();
            for (int i = 0; i < 16 * 64; i++)
            {
                pixels[i] = new Rgba32(pixels[i].R, pixels[i].R, pixels[i].R, 255);
            }

            return image;
        }, EncoderSupport.Options(rowsPerStrip: 16, compression: TiffCompression.Jpeg), TiffColorType.Rgb);
        yield return ("rgb_exif_be_9x7_lzw", () =>
        {
            Image image = EncoderSupport.Pattern(9, 7);
            image.Metadata.Exif = ExifSample.Block(bigEndian: true);
            return image;
        }, EncoderSupport.Options(compression: TiffCompression.Lzw), TiffColorType.Rgb);
        yield return ("gray_exif_le_7x3_orientation8", () =>
        {
            Image image = EncoderSupport.Pattern(7, 3, gray: true);
            image.Metadata.Exif = ExifSample.Block(bigEndian: false, orientation: 8);
            return image;
        }, EncoderSupport.Options(), TiffColorType.Grayscale);
        yield return ("gray_7x3_deflate_level0", () => EncoderSupport.Pattern(7, 3, gray: true), EncoderSupport.Options(compression: TiffCompression.Deflate) with { DeflateLevel = 0 }, TiffColorType.Grayscale);
    }

    [Test]
    public async Task Output_MatchesVerifiedSnapshots()
    {
        var manifest = new JsonObject();
        var mismatches = new List<string>();
        Directory.CreateDirectory(Received);
        foreach ((string name, Func<Image> create, TiffEncoderOptions options, TiffColorType decoded) in Cases())
        {
            using Image source = create();
            byte[] file = source.ToTiff(options);
            using Image image = TestSupport.Load(file);
            if (options.Compression == TiffCompression.Jpeg)
            {
                await Assert.That(EncoderSupport.JpegStripsMatch(file, image, decoded == TiffColorType.Grayscale ? 1 : 3)).IsTrue();
            }
            else
            {
                await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, decoded)));
            }

            manifest[name + ".tif"] = Entry(image, options.Compression == TiffCompression.Jpeg ? Decoded(image, Received, name) : null,
                MetadataWriteTests.ExifReference(source.Metadata.Exif, Received, name));

            string snapshot = Path.Combine(Snapshots, name + ".tif");
            File.WriteAllBytes(Path.Combine(Received, name + ".tif"), file);
            if (!File.Exists(snapshot)
                || (options.Compression != TiffCompression.Deflate && !File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(file)))
            {
                mismatches.Add(name);
            }
        }

        string json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(Path.Combine(Received, "written.json"), json);
        string manifestPath = Path.Combine(Snapshots, "written.json");
        string expected = File.Exists(manifestPath) ? File.ReadAllText(manifestPath).Replace("\r\n", "\n") : "";
        await Assert.That(mismatches).IsEmpty();
        await Assert.That(json).IsEqualTo(expected);
    }

    /// <summary>
    /// Every corpus image written with each lossless compression must read back as the same pixels, and with CCITT
    /// Group 4 as its black-and-white threshold. The files go to <c>written.corpus</c> in the output directory, where
    /// <c>verify_written.py</c> checks them against libtiff and Pillow and compares the LZW, PackBits and Group 4 strips
    /// with libtiff's on the same samples.
    /// </summary>
    [Test]
    public async Task Corpus_RoundTripsWithEveryCompression()
    {
        var manifest = new JsonObject();
        Directory.CreateDirectory(CorpusOutput);
        foreach (string name in TestSupport.Files())
        {
            using Image source = TestSupport.Load(TestSupport.ReadCorpus(name));
            foreach (TiffCompression compression in new[] { TiffCompression.PackBits, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.CcittGroup4, TiffCompression.Jpeg })
            {
                byte[] file = source.ToTiff(EncoderSupport.Options(compression: compression));
                using Image image = TestSupport.Load(file);
                if (compression == TiffCompression.Jpeg)
                {
                    await Assert.That(EncoderSupport.JpegStripsMatch(file, image, EncoderSupport.Tags(file)[277].Value == 1 ? 1 : 3)).IsTrue();
                    Record(name, compression, file, source, image, manifest);
                    continue;
                }

                string expected = compression == TiffCompression.CcittGroup4
                    ? EncoderSupport.Sha256(EncoderSupport.Expected(source, TiffColorType.BlackWhite))
                    : EncoderSupport.Sha256(source.GetPixelSpan());
                await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan())).IsEqualTo(expected);
                Record(name, compression, file, source, image, manifest);
            }
        }

        File.WriteAllText(Path.Combine(CorpusOutput, "written.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Record(string name, TiffCompression compression, byte[] file, Image source, Image image, JsonObject manifest)
    {
        string stem = $"{Path.GetFileNameWithoutExtension(name)}.{compression.ToString().ToLowerInvariant()}";
        File.WriteAllBytes(Path.Combine(CorpusOutput, stem + ".tif"), file);
        manifest[stem + ".tif"] = Entry(image, compression == TiffCompression.Jpeg ? Decoded(image, CorpusOutput, stem) : null,
            MetadataWriteTests.ExifReference(source.Metadata.Exif, CorpusOutput, stem));
    }

    /// <summary>
    /// Our decode of a lossy file as a PNG next to it, which the script compares with Pillow and libtiff within the
    /// tolerance of <see cref="JpegTests"/>; returns its name.
    /// </summary>
    private static string Decoded(Image image, string directory, string name)
    {
        string file = name + ".decoded.png";
        File.WriteAllBytes(Path.Combine(directory, file), image.ToPng());
        return file;
    }

    private static JsonObject Entry(Image image, string? decoded, string? exif) => new()
    {
        ["decoded"] = decoded,
        ["exif"] = exif,
        ["width"] = image.Width,
        ["height"] = image.Height,
        ["sha256"] = EncoderSupport.Sha256(image.GetPixelSpan()),
        ["dpi"] = new JsonArray(image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution),
        ["icc"] = EncoderSupport.Sha256(image.Metadata.IccProfile),
        ["xmp"] = EncoderSupport.Sha256(image.Metadata.Xmp),
    };
}
