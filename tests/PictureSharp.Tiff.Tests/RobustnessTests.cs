namespace PictureSharp.Formats.Tiff.Tests;

public sealed class RobustnessTests
{
    public static IEnumerable<string> Files() => TestSupport.Files();

    /// <summary>The reference corpus plus the JPEG and WebP files, whose strips the core codecs decode.</summary>
    public static IEnumerable<string> AllFiles() => Files().Concat(JpegTests.Files()).Concat(WebPTests.Files());

    [Test]
    [Property("Category", "Slow")]
    [MethodDataSource(nameof(AllFiles))]
    public async Task MutatedInput_FailsOnlyWithContractExceptions(string name)
    {
        byte[] original = TestSupport.ReadCorpus(name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) & 0x7fff);
        var unexpected = new List<string>();
        for (int round = 0; round < 300; round++)
        {
            byte[] data = [.. original];
            int changes = 1 + random.Next(8);
            for (int k = 0; k < changes; k++)
            {
                // Bias towards the IFD, where offsets, counts and dimensions live; libtiff writes it after the data.
                int at = random.Next(2) == 0 ? data.Length - 1 - random.Next(Math.Min(data.Length, 256)) : random.Next(data.Length);
                data[at] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                data = data[..random.Next(data.Length)];
            }

            Check(data, unexpected);
        }

        await Assert.That(unexpected).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(AllFiles))]
    public async Task EveryTruncation_FailsOnlyWithContractExceptions(string name)
    {
        byte[] original = TestSupport.ReadCorpus(name);
        var unexpected = new List<string>();
        int step = Math.Max(1, original.Length / 200);
        for (int length = 0; length < original.Length; length += length < 64 ? 1 : step)
        {
            Check(original[..length], unexpected);
        }

        await Assert.That(unexpected).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task MaxPixels_IsEnforcedBeforeDecoding(string name)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        ReferenceEntry entry = TestSupport.Reference[name];
        DecoderOptions options = TestSupport.Options with { MaxPixels = (long)entry.Width * entry.Height - 1 };
        await Assert.That(() => TestSupport.Load(data, options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task CanceledLoad_ThrowsOperationCanceled()
    {
        byte[] data = TestSupport.ReadCorpus("rgb8_lzw_predictor.tif");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.That(() => Image.Load(data, TestSupport.Options, source.Token)).Throws<OperationCanceledException>();
    }

    private static void Check(byte[] data, List<string> unexpected)
    {
        try
        {
            using Image image = Image.Load(data, TestSupport.Options);
            _ = Image.Identify(data, TestSupport.Options);
            _ = Image.ReadMetadata(data, TestSupport.Options);
            _ = Image.EditMetadata(data, metadata => metadata.Xmp = [60, 47, 62], TestSupport.Options);
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
        }
        catch (Exception e)
        {
            unexpected.Add($"{data.Length} bytes: {e}");
        }
    }
}
