using System.Diagnostics;
using System.Globalization;
using PictureSharp;
using PictureSharp.Formats.Tiff;

// Wall-clock speed probe for TIFF encoding and decoding (best of N runs, after a warm-up run).
//   encode <image>... [--runs 5] [--threads 1,all] [--out dir]   None, LZW and Deflate (predictor 2, level 6) strips
//   decode <file.tif>... [--runs 5] [--threads 1,all]           the first page
// The image is loaded with any registered format and written as RGB; --out keeps the files for libtiff_speed.py.
// Prints one TSV row per measurement: operation, file, compression, threads, best ms, allocated MB per run, bytes.
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
string mode = args.Length > 0 ? args[0] : "";
var files = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
int runs = Option("--runs", 5);
string[] threads = (OptionText("--threads") ?? "1,all").Split(',');
string? outDir = OptionText("--out");

Console.WriteLine("operation\tfile\tcompression\tthreads\tms\talloc_mb\tbytes");
switch (mode)
{
    case "encode":
        foreach (string file in files)
        {
            using Image image = Image.Load(file);
            image.Metadata.Clear();
            foreach (TiffCompression compression in new[] { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate })
            {
                foreach (string t in threads)
                {
                    var options = new TiffEncoderOptions
                    {
                        Compression = compression,
                        ColorType = TiffColorType.Rgb,
                        MaxDegreeOfParallelism = t == "all" ? -1 : int.Parse(t, CultureInfo.InvariantCulture),
                    };
                    byte[] output = [];
                    (double ms, double mb) = Measure(() => output = image.ToTiff(options));
                    Row("encode", file, compression, t, ms, mb, output.Length);
                    if (outDir is not null && t == threads[0])
                    {
                        Directory.CreateDirectory(outDir);
                        File.WriteAllBytes(Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(file)}.{compression.ToString().ToLowerInvariant()}.tif"), output);
                    }
                }
            }
        }

        break;
    case "decode":
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            foreach (string t in threads)
            {
                var options = new DecoderOptions { MaxDegreeOfParallelism = t == "all" ? -1 : int.Parse(t, CultureInfo.InvariantCulture) };
                (double ms, double mb) = Measure(() => Image.Load(bytes, options).Dispose());
                Row("decode", file, null, t, ms, mb, bytes.Length);
            }
        }

        break;
    default:
        Console.Error.WriteLine("Usage: encode <image>... | decode <file.tif>... [--runs N] [--threads 1,all] [--out dir]");
        return 1;
}

return 0;

(double Ms, double Mb) Measure(Action action)
{
    action();
    double best = double.MaxValue, allocated = 0;
    for (int i = 0; i < runs; i++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        action();
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / 1048576.0;
    }

    return (best, allocated);
}

void Row(string operation, string file, TiffCompression? compression, string t, double ms, double mb, long bytes) =>
    Console.WriteLine(string.Join('\t', operation, Path.GetFileName(file), compression?.ToString() ?? "-", t,
        ms.ToString("F1", CultureInfo.InvariantCulture), mb.ToString("F1", CultureInfo.InvariantCulture), bytes));

int Option(string name, int fallback) => OptionText(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

string? OptionText(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
