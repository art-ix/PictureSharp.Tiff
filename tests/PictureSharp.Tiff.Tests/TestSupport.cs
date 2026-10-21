using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PictureSharp.Formats.Tiff.Tests;

internal static class TestSupport
{
    public static ImageConfiguration Configuration { get; } = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();

    public static DecoderOptions Options { get; } = new() { Configuration = Configuration };

    public static IReadOnlyDictionary<string, ReferenceEntry> Reference { get; } =
        JsonSerializer.Deserialize<Dictionary<string, ReferenceEntry>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    public static IEnumerable<string> Files() => Reference.Keys.Order(StringComparer.Ordinal);

    public static byte[] ReadCorpus(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", name));

    public static Image Load(byte[] data, DecoderOptions? options = null) => Image.Load(data, options ?? Options);

    public static ImageInfo Identify(byte[] data) => Image.Identify(data, Options);

    public static string Sha256(Image image) =>
        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(image.GetPixelSpan())));
}

internal sealed record ReferenceEntry(int Width, int Height, string Oracle, string Sha256);
