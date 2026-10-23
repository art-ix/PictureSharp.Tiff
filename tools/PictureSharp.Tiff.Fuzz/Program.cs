using System.Diagnostics;
using SharpFuzz;

namespace PictureSharp.Formats.Tiff.Fuzz;

/// <summary>
/// <c>fuzz &lt;target&gt;</c> runs under afl-fuzz (persistent mode); <c>replay &lt;target&gt; &lt;file or directory&gt;...</c>
/// runs inputs once each and reports contract violations (one signature line each), time and memory;
/// <c>minimize &lt;target&gt; &lt;input&gt; &lt;output&gt;</c> shrinks an input that keeps the same failure signature.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["fuzz", string name])
        {
            Action<byte[]> target = FuzzTargets.Get(name);
            Fuzzer.Run(stream =>
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                try
                {
                    target(buffer.ToArray());
                }
                catch (Exception e) when (Environment.GetEnvironmentVariable("FUZZ_LOG") is string log)
                {
                    // afl-fuzz hides the harness's output; FUZZ_LOG keeps the exception for debugging the setup.
                    File.AppendAllText(log, e + Environment.NewLine);
                    throw;
                }
            });
            return 0;
        }

        if (args is ["replay", string replayed, .. string[] paths] && paths.Length > 0)
        {
            return Replay(replayed, paths);
        }

        if (args is ["minimize", string minimized, string input, string output])
        {
            return Minimize(minimized, input, output);
        }

        Console.Error.WriteLine("Usage: fuzz <target> | replay <target> <file or directory>... | minimize <target> <input> <output>; "
            + $"targets: {string.Join(", ", FuzzTargets.Names)}");
        return 2;
    }

    private static int Replay(string name, string[] paths)
    {
        Action<byte[]> target = FuzzTargets.Get(name);
        IEnumerable<string> files = paths.SelectMany(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(file => !Path.GetFileName(file).StartsWith('.') && !file.EndsWith(".txt", StringComparison.Ordinal))
            : [path]);
        int count = 0, failures = 0;
        (string File, double Ms) slowest = ("", 0);
        (string File, long Bytes) largest = ("", 0);
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            byte[] data = File.ReadAllBytes(file);
            Statistics.Reset();
            var watch = Stopwatch.StartNew();
            try
            {
                target(data);
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine($"FAIL {file}: {Signature(e)}");
                if (Environment.GetEnvironmentVariable("FUZZ_VERBOSE") is not null)
                {
                    Console.WriteLine(e);
                }
            }

            double ms = watch.Elapsed.TotalMilliseconds;
            count++;
            slowest = ms > slowest.Ms ? (file, ms) : slowest;
            largest = Statistics.MaxAllocated > largest.Bytes ? (file, Statistics.MaxAllocated) : largest;
        }

        Console.WriteLine($"{name}: {count} inputs, {failures} failures, {Statistics.Decoded} pages decoded; slowest {slowest.Ms:F1} ms ({slowest.File}); "
            + $"largest call {largest.Bytes:N0} bytes ({largest.File})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Shrinks <paramref name="input"/> while the target fails with the same <see cref="Signature"/>: removes ever
    /// smaller blocks, then zeroes single bytes. Replaces afl-tmin, which cannot run SharpFuzz targets with AFL++ 4.09c.
    /// </summary>
    private static int Minimize(string name, string input, string output)
    {
        Action<byte[]> target = FuzzTargets.Get(name);
        byte[] data = File.ReadAllBytes(input);
        string? signature = Failure(target, data);
        if (signature is null)
        {
            Console.Error.WriteLine($"{input} does not fail the {name} target.");
            return 1;
        }

        var watch = Stopwatch.StartNew();
        for (int block = Math.Max(1, data.Length / 2); block >= 1 && watch.Elapsed < TimeSpan.FromMinutes(2); block /= 2)
        {
            for (int at = 0; at + block <= data.Length;)
            {
                byte[] shorter = [.. data.AsSpan(0, at), .. data.AsSpan(at + block)];
                if (Failure(target, shorter) == signature)
                {
                    data = shorter;
                }
                else
                {
                    at += block;
                }
            }
        }

        for (int at = 0; at < data.Length && watch.Elapsed < TimeSpan.FromMinutes(4); at++)
        {
            if (data[at] != 0)
            {
                byte kept = data[at];
                data[at] = 0;
                if (Failure(target, data) != signature)
                {
                    data[at] = kept;
                }
            }
        }

        File.WriteAllBytes(output, data);
        Console.WriteLine($"{input}: {new FileInfo(input).Length} -> {data.Length} bytes; {signature}");
        return 0;
    }

    private static string? Failure(Action<byte[]> target, byte[] data)
    {
        try
        {
            target(data);
            return null;
        }
        catch (Exception e)
        {
            return Signature(e);
        }
    }

    /// <summary>The exception type, its message with numbers replaced by #, and the innermost plugin frame.</summary>
    private static string Signature(Exception e)
    {
        string message = System.Text.RegularExpressions.Regex.Replace(e.Message, "[0-9][0-9,.]*", "#");
        string? frame = new StackTrace(e).GetFrames()
            .Select(f => f.GetMethod())
            .FirstOrDefault(m => m?.DeclaringType?.Namespace?.StartsWith("PictureSharp.Formats.Tiff", StringComparison.Ordinal) == true
                && m.DeclaringType.Namespace != typeof(Program).Namespace) is { } method
            ? $"{method.DeclaringType!.Name}.{method.Name}"
            : null;
        return $"{e.GetType().Name}: {message}" + (frame is null ? "" : $" @ {frame}");
    }
}
