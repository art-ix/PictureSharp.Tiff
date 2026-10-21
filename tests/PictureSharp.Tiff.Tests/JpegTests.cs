using System.Text.Json;
using PictureSharp.Formats.Jpeg;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// JPEG-compressed TIFF: each strip or tile must equal the core JPEG decode of the same data, joined to the
/// JPEGTables by the corpus generator, and the whole image must stay close to Pillow's libjpeg decode.
/// </summary>
public sealed class JpegTests
{
    private static readonly Dictionary<string, JpegEntry> Index =
        JsonSerializer.Deserialize<Dictionary<string, JpegEntry>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "jpeg.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static IEnumerable<string> Files() => Index.Keys.Order(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_MatchesCoreJpegDecodeOfEachChunk(string name)
    {
        JpegEntry entry = Index[name];
        using Image image = TestSupport.Load(TestSupport.ReadCorpus(name));
        await Assert.That((image.Width, image.Height)).IsEqualTo((entry.Width, entry.Height));
        Rgba32[] pixels = image.GetPixelSpan().ToArray();
        int mismatches = 0;
        foreach (JsonElement[] chunk in entry.Chunks)
        {
            (string file, int x0, int y0, int w, int h) = (chunk[0].GetString()!, chunk[1].GetInt32(), chunk[2].GetInt32(), chunk[3].GetInt32(), chunk[4].GetInt32());
            using Image jpeg = TestSupport.Load(TestSupport.ReadCorpus(file));
            Rgba32[] decoded = jpeg.GetPixelSpan().ToArray();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    mismatches += pixels[(y0 + y) * entry.Width + x0 + x] == decoded[y * jpeg.Width + x] ? 0 : 1;
                }
            }
        }

        await Assert.That(mismatches).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_IsCloseToPillow(string name)
    {
        JpegEntry entry = Index[name];
        using Image image = TestSupport.Load(TestSupport.ReadCorpus(name));
        using Image pillow = TestSupport.Load(TestSupport.ReadCorpus(entry.Pillow));
        ReadOnlySpan<Rgba32> a = image.GetPixelSpan(), b = pillow.GetPixelSpan();
        long total = 0;
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)), Math.Abs(a[i].B - b[i].B));
            total += d;
            worst = Math.Max(worst, d);
        }

        // Different IDCTs and chroma upsampling, not different colours: small on average, bounded everywhere.
        await Assert.That((double)total / a.Length).IsLessThan(1.5);
        await Assert.That(worst).IsLessThanOrEqualTo(24);
    }

    [Test]
    [Arguments("jpeg_ycbcr_strips.tif")]
    [Arguments("jpeg_ycbcr_tiles.tif")]
    public async Task MaxPixels_CoversTheImageOnly(string name)
    {
        // DecodeEmbeddedPart gives each strip or tile its own allowance instead of charging it next to the image.
        byte[] data = TestSupport.ReadCorpus(name);
        using Image image = TestSupport.Load(data, TestSupport.Options with { MaxPixels = 64 * 48, MaxFrames = 1 });
        await Assert.That(image.Width).IsEqualTo(64);
        await Assert.That(() => TestSupport.Load(data, TestSupport.Options with { MaxPixels = 64 * 48 - 1 })).Throws<ImageFormatException>();
    }

    [Test]
    public async Task JpegLargerThanItsStrip_IsRejected()
    {
        byte[] jpeg;
        using (var source = new Image(16, 16, new Rgba32(90, 90, 90, 255)))
        {
            jpeg = source.ToJpeg();
        }

        byte[] data = TiffBuilder.GrayStrip(16, 8, 7, jpeg).Build();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => TestSupport.Load(data));
        await Assert.That(error.Format).IsSameReferenceAs(ImageFormat.Jpeg);
    }

    private sealed record JpegEntry(int Width, int Height, JsonElement[][] Chunks, string Pillow);
}
