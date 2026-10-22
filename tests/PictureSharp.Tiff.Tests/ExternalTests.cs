using System.Text.Json;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Real-world files in tests/assets/external/ (not committed; fetch-external.sh, then external_oracle.py): every page
/// libtiff converts must match its TIFFReadRGBAImage raster (floating point: the documented rule applied to libtiff's
/// samples), and nothing may throw other than
/// <see cref="ImageFormatException"/> or <see cref="NotSupportedException"/>. Each file also gets a line in
/// external/results/, which external_triage.py collects. Files rewritten by <c>Image.EditMetadata</c> go to
/// external/edited/, where <c>external_oracle.py --edited</c> checks that libtiff still decodes every IFD as before.
/// Produces no cases without the files.
/// </summary>
[Property("Category", "Local")]
public sealed class ExternalTests
{
    private static readonly string? Dir = FindExternal();

    private static readonly Dictionary<string, ExternalEntry> Index = Dir is not null && File.Exists(Path.Combine(Dir, "external.json"))
        ? JsonSerializer.Deserialize<Dictionary<string, ExternalEntry>>(File.ReadAllText(Path.Combine(Dir, "external.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
        : [];

    public static IEnumerable<string> Files() => Index.Keys.Order(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Decode_MatchesLibtiff(string name)
    {
        ExternalEntry entry = Index[name];
        byte[] data = File.ReadAllBytes(Path.Combine(Dir!, name));
        var lines = new List<string>();
        var failures = new List<string>();

        (int? pages, string? identify) = Guard(() => TestSupport.Identify(data).ContainerItemCount);
        int expectedPages = entry.Pages.Count(p => !p.Reduced);
        lines.Add($"{name}\t-\tidentify\t{identify ?? $"{pages} pages, libtiff {expectedPages}"}");
        if (identify is not null && !Allowed(identify))
        {
            failures.Add($"Identify: {identify}");
        }

        int page = 0;
        foreach (ExternalPage ifd in entry.Pages.Where(p => !p.Reduced))
        {
            string layout = $"c{ifd.Compression} p{ifd.Photometric} {ifd.Bits}x{ifd.Samples} f{ifd.Format} pl{ifd.Planar} x[{string.Join(',', ifd.Extra ?? [])}]";
            (string outcome, bool failed) = DecodePage(data, page, ifd);
            lines.Add($"{name}\t{page}\t{layout}\t{outcome}");
            if (failed)
            {
                failures.Add($"page {page} ({layout}): {outcome}");
            }

            page++;
        }

        (_, string? metadata) = Guard(() => Image.ReadMetadata(data, TestSupport.Options));
        if (metadata is not null && !Allowed(metadata))
        {
            failures.Add($"ReadMetadata: {metadata}");
        }

        string results = Path.Combine(Dir!, "results", name + ".tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(results)!);
        File.WriteAllLines(results, lines);
        await Assert.That(string.Join(Environment.NewLine, failures)).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task EditMetadata_KeepsThePixelsOfEveryPage(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Dir!, name));
        (byte[]? edited, string? error) = Guard(() => Image.EditMetadata(data, metadata =>
        {
            metadata.Orientation = ExifOrientation.RightTop;
            metadata.HorizontalResolution = 123.5f;
            metadata.VerticalResolution = 77;
        }, TestSupport.Options));
        if (edited is null)
        {
            await Assert.That(Allowed(error!)).IsTrue().Because(error!);
            return;
        }

        string output = Path.Combine(Dir!, "edited", name);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, edited);
        var failures = new List<string>();
        for (int page = 0; page < Index[name].Pages.Count(p => !p.Reduced); page++)
        {
            (string? before, string? beforeError) = Guard(() => Hash(data, page));
            (string? after, string? afterError) = Guard(() => Hash(edited, page));
            if ((before, beforeError) != (after, afterError))
            {
                failures.Add($"page {page}: {before ?? beforeError} before, {after ?? afterError} after");
            }
        }

        await Assert.That(string.Join(Environment.NewLine, failures)).IsEmpty();
        await Assert.That(Image.ReadMetadata(edited, TestSupport.Options).Orientation).IsEqualTo(ExifOrientation.RightTop);
    }

    private static string Hash(byte[] data, int page)
    {
        using Image image = TestSupport.Load(data, TestSupport.Page(page));
        return TestSupport.Sha256(image);
    }

    private static (string Outcome, bool Failed) DecodePage(byte[] data, int page, ExternalPage ifd)
    {
        Image? image = null;
        string? error;
        try
        {
            (image, error) = Guard(() => TestSupport.Load(data, TestSupport.Page(page)));
            if (error is not null)
            {
                // A file libtiff converts and the plugin declares unsupported is a scope question, not a failure.
                string libtiff = ifd.Rgba is null ? "libtiff rejects too" : ifd.Warnings is not null ? "libtiff converts with errors" : "libtiff converts";
                return ($"{libtiff}; {error}", !Allowed(error));
            }

            if (ifd.Rgba is null)
            {
                return ($"decoded; libtiff: {ifd.Libtiff}", false);
            }

            if ((image!.Width, image.Height) != (ifd.Width, ifd.Height))
            {
                return ($"size {image.Width}x{image.Height}, libtiff {ifd.Width}x{ifd.Height}", true);
            }

            return Compare(image.GetPixelSpan(), File.ReadAllBytes(Path.Combine(Dir!, ifd.Rgba)), ifd);
        }
        finally
        {
            image?.Dispose();
        }
    }

    // What TIFFRGBAImage makes of the extra sample (tif_getimage.c): a palette ignores it, greyscale keeps it as stored
    // (putagreytile); for colour, ExtraSamples 1, or 0 with more than three samples, is kept as stored (associated),
    // 2 is premultiplied, anything else is no alpha. The plugin follows Pillow: 0 is ignored, 2 and unknown values
    // (and a lone extra sample without the tag) are straight alpha. Our pixels are brought into libtiff's form before
    // comparing; where one side ignores the alpha sample, only the colour is compared. Stored colour above its
    // associated alpha (invalid) is compared clamped to the alpha, as the plugin unpremultiplies it.
    // Rounding only: 16-bit samples and colour maps (libtiff takes the high byte, the plugin rounds), CMYK and alpha.
    // Signed samples: libtiff reads the bits as unsigned, the plugin offsets them by 2^(b-1) (documented), so
    // libtiff's bytes are compared with the sign bit flipped. JPEG goes through another IDCT (libjpeg-turbo) and is
    // held to the bounds of JpegTests. A model raster (external_oracle.py) already follows the plugin's rules and
    // must match exactly.
    private static (string, bool) Compare(ReadOnlySpan<Rgba32> ours, byte[] libtiff, ExternalPage ifd)
    {
        // Without the tag the plugin assumes RGB for three or more samples, MinIsBlack otherwise, which is what
        // TIFFReadRGBAImage gives too.
        int photometric = ifd.Photometric >= 0 ? ifd.Photometric : ifd.Samples >= 3 ? 2 : 1;
        bool grey = photometric is 0 or 1;
        int colours = photometric switch { 0 or 1 or 3 => 1, 5 => 4, _ => 3 };
        int first = ifd.Extra is { Count: > 0 } extra ? extra[0] : -1;
        bool loneExtra = first == -1 && ifd.Samples == colours + 1;
        // TIFFRGBAImageBegin also takes a fourth RGB sample without ExtraSamples as associated alpha.
        string theirs = photometric == 3 ? "none" : grey ? (first >= 0 ? "stored" : "none")
            : first == 1 || (first == 0 && ifd.Samples > 3) || (first == -1 && photometric == 2 && ifd.Samples == 4) ? "stored"
            : first == 2 ? "premultiplied" : "none";
        string mine = first == 1 ? "associated" : first == 0 || (first == -1 && !loneExtra) ? "none" : "straight";
        if (first == -1 && !loneExtra || ifd.Samples <= colours)
        {
            mine = "none";
        }

        bool model = ifd.Oracle == "model";
        bool premultiply = !model && (theirs == "premultiplied" || (theirs == "stored" && mine == "associated"));
        bool clamp = !model && theirs == "stored" && mine == "associated";
        bool alphaCompared = model || (theirs == "none") == (mine == "none");
        bool signed = !model && ifd.Format == 2;

        int worst = 0, differing = 0;
        long total = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            Rgba32 p = ours[i];
            int a = p.A, la = libtiff[i * 4 + 3];
            int d = 0;
            for (int c = 0; c < (alphaCompared ? 4 : 3); c++)
            {
                int v = c switch { 0 => p.R, 1 => p.G, 2 => p.B, _ => a };
                int l = libtiff[i * 4 + c];
                if (c < 3)
                {
                    v = premultiply ? ((v * a) + 127) / 255 : v;
                    l = clamp ? Math.Min(l, la) : signed ? l ^ 0x80 : l;
                }

                d = Math.Max(d, Math.Abs(v - l));
            }

            worst = Math.Max(worst, d);
            total += d;
            differing += d == 0 ? 0 : 1;
        }

        double mean = (double)total / Math.Max(1, ours.Length);
        string notes = string.Concat(
            premultiply ? ", premultiplied" : "",
            clamp ? ", clamped to alpha" : "",
            alphaCompared ? "" : $", alpha not compared (libtiff {theirs}, plugin {mine})",
            signed ? ", sign bit flipped" : "",
            ifd.Photometric < 0 ? ", no Photometric tag" : "",
            ifd.Oracle == "model" ? " (model)" : "");
        string outcome = worst == 0 ? $"exact{notes}" : $"max {worst}, mean {mean:0.###}, {differing} of {ours.Length} pixels{notes}";
        bool jpeg = ifd.Compression is 7 or 6;
        bool rounding = !model && (ifd.Bits == 16 || photometric is 3 or 5 || theirs != "none" || mine != "none" || signed);
        bool failed = jpeg ? mean >= 1.5 || worst > 24 : worst > (rounding ? 1 : 0);
        return (outcome, failed);
    }

    private static (T? Value, string? Error) Guard<T>(Func<T> action)
    {
        try
        {
            return (action(), null);
        }
        catch (Exception e)
        {
            return (default, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static bool Allowed(string error) =>
        error.StartsWith(nameof(ImageFormatException) + ":", StringComparison.Ordinal)
        || error.StartsWith(nameof(NotSupportedException) + ":", StringComparison.Ordinal);

    private static string? FindExternal()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tests", "assets", "external");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

internal sealed record ExternalEntry(string Sha256, IReadOnlyList<ExternalPage> Pages, string? Libtiff = null);

internal sealed record ExternalPage(
    int Ifd, long Width, long Height, int Bits, int Compression, int Photometric, int Samples, int Planar, int Format,
    IReadOnlyList<int>? Extra, bool Reduced, string? Rgba = null, string? Oracle = null, string? Libtiff = null, string? Warnings = null);
