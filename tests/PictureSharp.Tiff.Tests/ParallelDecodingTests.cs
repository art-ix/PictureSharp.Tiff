using System.Diagnostics;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Parallel strip and tile decoding: every file decodes to the same pixels, and every corrupt file fails with the same
/// exception, on one thread and on several. An explicit degree is used as given, so the small corpus files take the
/// parallel path too (the automatic degree decodes them on one thread).
/// </summary>
public sealed class ParallelDecodingTests
{
    private static readonly int Threads = Math.Max(4, Environment.ProcessorCount);

    public static IEnumerable<string> AllFiles() => RobustnessTests.AllFiles();

    /// <summary>The reference corpus re-encoded in many small strips or tiles.</summary>
    public static IEnumerable<(string Name, string Variant)> Reencoded() =>
        TestSupport.Files().SelectMany(name => Variants.Keys.Select(variant => (name, variant)));

    private static readonly Dictionary<string, TiffEncoderOptions> Variants = new()
    {
        ["none-tiles"] = EncoderSupport.Options() with { TileSize = 16 },
        ["packbits-strips"] = EncoderSupport.Options(rowsPerStrip: 1, compression: TiffCompression.PackBits),
        ["lzw-strips"] = EncoderSupport.Options(rowsPerStrip: 1, compression: TiffCompression.Lzw),
        ["deflate-tiles"] = EncoderSupport.Options(compression: TiffCompression.Deflate) with { TileSize = 16 },
    };

    [Test]
    [MethodDataSource(nameof(AllFiles))]
    public async Task Decode_DoesNotDependOnDegree(string name)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        string expected = Outcome(data, 1);
        await Assert.That(expected).DoesNotStartWith("!");
        await Assert.That(Outcome(data, Threads)).IsEqualTo(expected);
        await Assert.That(Outcome(data, 2)).IsEqualTo(expected);
    }

    [Test]
    [MethodDataSource(nameof(Reencoded))]
    public async Task Reencoded_DecodesTheSameOnEveryDegree(string name, string variant)
    {
        using Image source = TestSupport.Load(TestSupport.ReadCorpus(name));
        byte[] data = source.ToTiff(Variants[variant]);
        string expected = Outcome(data, 1);
        await Assert.That(expected).DoesNotStartWith("!");
        await Assert.That(Outcome(data, Threads)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(TiffCompression.None, false)]
    [Arguments(TiffCompression.None, true)]
    [Arguments(TiffCompression.Lzw, false)]
    [Arguments(TiffCompression.Deflate, true)]
    public async Task OneLargeStrip_DecodesTheSameInRowBands(TiffCompression compression, bool gray)
    {
        // Above the automatic threshold, in a single strip: decompressed once, then un-predicted and converted in bands.
        using Image source = EncoderSupport.Pattern(777, 555, gray: gray, alpha: !gray);
        byte[] data = source.ToTiff(EncoderSupport.Options(rowsPerStrip: 555, compression: compression));
        string expected = Outcome(data, 1);
        await Assert.That(Outcome(data, -1)).IsEqualTo(expected);
        await Assert.That(Outcome(data, Threads)).IsEqualTo(expected);
        using Image decoded = Image.Load(data, TestSupport.Options with { MaxDegreeOfParallelism = -1 });
        await Assert.That(decoded.GetPixelSpan<Rgba32>().SequenceEqual(source.GetPixelSpan<Rgba32>())).IsTrue();
    }

    [Test]
    [Property("Category", "Slow")]
    [MethodDataSource(nameof(AllFiles))]
    public async Task MutatedInput_FailsTheSameOnEveryDegree(string name)
    {
        byte[] original = TestSupport.ReadCorpus(name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) & 0x7fff);
        var different = new List<string>();
        for (int round = 0; round < 150; round++)
        {
            byte[] data = [.. original];
            int changes = 1 + random.Next(8);
            for (int k = 0; k < changes; k++)
            {
                // Mostly in the strips, so that several chunks fail in different ways.
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            Compare(data, different);
        }

        await Assert.That(different).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(AllFiles))]
    public async Task EveryTruncation_FailsTheSameOnEveryDegree(string name)
    {
        byte[] original = TestSupport.ReadCorpus(name);
        var different = new List<string>();
        int step = Math.Max(1, original.Length / 200);
        for (int length = 0; length < original.Length; length += length < 64 ? 1 : step)
        {
            Compare(original[..length], different);
        }

        await Assert.That(different).IsEmpty();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(-1)]
    public async Task EveryStripCorrupt_ReportsTheFirstStrip(int degree)
    {
        // 600 x 600 is above the automatic threshold; every strip starts at its own offset past the end of the file.
        const int Size = 600;
        uint[] offsets = [.. Enumerable.Range(0, Size).Select(i => (uint)(10_000_000 + i))];
        byte[] data = TiffBuilder.Gray(Size, Size).Set(278, TiffBuilder.Long, 1).Set(273, TiffBuilder.Long, offsets)
            .Set(279, TiffBuilder.Long, [.. Enumerable.Repeat((uint)Size, Size)]).Build();
        DecoderOptions options = TestSupport.Options with { MaxDegreeOfParallelism = degree };
        await Assert.That(() => Image.Load(data, options)).Throws<ImageFormatException>()
            .WithMessageContaining("offset 10000000,");
    }

    [Test]
    [Retry(2)]
    [Arguments(1)]
    [Arguments(-1)]
    public async Task Cancel_StopsWithinAStripPerThread(int degree)
    {
        using Image source = EncoderSupport.Pattern(3072, 3072);
        byte[] data = source.ToTiff(EncoderSupport.Options(rowsPerStrip: 2, compression: TiffCompression.Lzw));
        DecoderOptions options = TestSupport.Options with { MaxDegreeOfParallelism = degree };
        TimeSpan full = TimeSpan.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var timer = Stopwatch.StartNew();
            using Image image = Image.Load(data, options);
            full = TimeSpan.FromTicks(Math.Min(full.Ticks, timer.Elapsed.Ticks));
        }

        var watch = Stopwatch.StartNew();
        TimeSpan canceledAt = TimeSpan.Zero;
        using var cancel = new CancellationTokenSource();
        cancel.Token.Register(() => canceledAt = watch.Elapsed);
        // A dedicated thread that spins on the clock, not CancelAfter or a sleep: the timer callback runs on the thread
        // pool, which the parallel decode keeps busy, and waking a sleeping thread while every core decodes can take
        // longer than the decode on a small CI runner. A running thread gets its time slices without that delay.
        var canceler = new Thread(() =>
        {
            while (watch.Elapsed < full / 3)
            {
                Thread.SpinWait(100);
            }

            cancel.Cancel();
        }) { IsBackground = true };
        canceler.Start();
        try
        {
            await Assert.That(() => Image.Load(data, options, cancel.Token)).Throws<OperationCanceledException>();
        }
        finally
        {
            // Before the source is disposed, also when the assertion fails (Retry runs the test again).
            canceler.Join();
        }

        // 1536 strips: stopping after the strip each thread is on takes a small part of the whole decode.
        await Assert.That(watch.Elapsed - canceledAt).IsLessThan(full / 4);
    }

    private static void Compare(byte[] data, List<string> different)
    {
        string single = Outcome(data, 1), parallel = Outcome(data, Threads);
        if (single != parallel)
        {
            different.Add($"{data.Length} bytes: {single} | {parallel}");
        }
    }

    /// <summary>The size and pixel hash, or "!" and the exception type and message.</summary>
    private static string Outcome(byte[] data, int degree)
    {
        try
        {
            using Image image = Image.Load(data, TestSupport.Options with { MaxDegreeOfParallelism = degree });
            return $"{image.Width}x{image.Height} {TestSupport.Sha256(image)}";
        }
        catch (Exception e)
        {
            return $"!{e.GetType().Name}: {e.Message}";
        }
    }
}
