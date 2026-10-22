using System.Security.Cryptography;
using System.Text.Json;
using PictureSharp;
using PictureSharp.Formats.Tiff;

// Decodes every corpus file listed in reference.json, on one thread and in parallel, and compares the pixels with the
// reference hashes: the trimmed, ahead-of-time compiled plugin must decode exactly as the JIT tests do.
// Usage: AotConsumer <corpus directory> <reference.json>
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
using JsonDocument reference = JsonDocument.Parse(File.ReadAllBytes(args[1]));
int checkedPages = 0, mismatches = 0;
foreach (JsonProperty file in reference.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
{
    byte[] data = File.ReadAllBytes(Path.Combine(args[0], file.Name));
    JsonElement[] pages = file.Value.TryGetProperty("pages", out JsonElement list) ? [.. list.EnumerateArray()] : [file.Value];
    for (int page = 0; page < pages.Length; page++)
    {
        foreach (int degree in new[] { 1, -1 })
        {
            var options = new DecoderOptions { MaxDegreeOfParallelism = degree, FormatOptions = [new TiffDecoderOptions { Page = page }] };
            using Image image = Image.Load(data, options);
            string sha = Convert.ToHexStringLower(SHA256.HashData(image.ToPixelData(PixelFormat.Rgba32)));
            checkedPages++;
            if (sha != pages[page].GetProperty("sha256").GetString())
            {
                mismatches++;
                Console.WriteLine($"MISMATCH {file.Name} page {page} degree {degree}");
            }
        }
    }
}

Console.WriteLine($"{checkedPages} decodes checked, {mismatches} mismatches");
return mismatches == 0 ? 0 : 1;
