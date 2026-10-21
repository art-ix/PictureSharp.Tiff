using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Byte-exact snapshots of encoder output in <c>tests/assets/written</c>, which <c>verify_written.py</c> checks with
/// libtiff 4.5.1 and Pillow 10.2.0. A mismatch writes the new files to <c>written.received</c> in the output directory:
/// verify them with the script, then copy them over the snapshots.
/// </summary>
public sealed class WrittenFileTests
{
    private static readonly string Snapshots = Path.Combine(AppContext.BaseDirectory, "written");
    private static readonly string Received = Path.Combine(AppContext.BaseDirectory, "written.received");

    private static IEnumerable<(string Name, Func<Image> Source, TiffEncoderOptions Options, TiffColorType Decoded)> Cases()
    {
        yield return ("gray_7x3", () => EncoderSupport.Pattern(7, 3, gray: true), EncoderSupport.Options(), TiffColorType.Grayscale);
        yield return ("gray_alpha_33x17", () => EncoderSupport.Pattern(33, 17, gray: true, alpha: true), EncoderSupport.Options(), TiffColorType.GrayscaleAlpha);
        yield return ("rgb_33x17_rows5", () => EncoderSupport.Pattern(33, 17), EncoderSupport.Options(rowsPerStrip: 5), TiffColorType.Rgb);
        yield return ("rgba_13x1", () => EncoderSupport.Pattern(13, 1, alpha: true), EncoderSupport.Options(), TiffColorType.Rgba);
        yield return ("rgb_1x1", () => EncoderSupport.Pattern(1, 1), EncoderSupport.Options(), TiffColorType.Rgb);
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
            await Assert.That(EncoderSupport.Sha256(image.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, decoded)));
            manifest[name + ".tif"] = new JsonObject
            {
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["sha256"] = EncoderSupport.Sha256(image.GetPixelSpan()),
                ["dpi"] = new JsonArray(image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution),
                ["icc"] = EncoderSupport.Sha256(image.Metadata.IccProfile),
                ["xmp"] = EncoderSupport.Sha256(image.Metadata.Xmp),
            };

            string snapshot = Path.Combine(Snapshots, name + ".tif");
            File.WriteAllBytes(Path.Combine(Received, name + ".tif"), file);
            if (!File.Exists(snapshot) || !File.ReadAllBytes(snapshot).AsSpan().SequenceEqual(file))
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
}
