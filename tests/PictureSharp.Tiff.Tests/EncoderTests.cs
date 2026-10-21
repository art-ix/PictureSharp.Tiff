using System.Text;

namespace PictureSharp.Formats.Tiff.Tests;

public sealed class EncoderTests
{
    public static IEnumerable<(TiffColorType, int, int, int)> RoundTripCases()
    {
        TiffColorType[] types = [TiffColorType.Grayscale, TiffColorType.GrayscaleAlpha, TiffColorType.Rgb, TiffColorType.Rgba];
        (int, int)[] sizes = [(1, 1), (7, 3), (13, 1), (33, 17), (1, 40)];
        foreach (TiffColorType type in types)
        {
            foreach ((int width, int height) in sizes)
            {
                foreach (int rows in new[] { 0, 1, 5 })
                {
                    yield return (type, width, height, rows);
                }
            }
        }
    }

    [Test]
    [MethodDataSource(nameof(RoundTripCases))]
    public async Task RoundTrip_ReadsBackTheColorType(TiffColorType colorType, int width, int height, int rowsPerStrip)
    {
        using Image source = EncoderSupport.Pattern(width, height, alpha: true);
        byte[] file = source.ToTiff(EncoderSupport.Options(colorType, rowsPerStrip));
        using Image decoded = TestSupport.Load(file);
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((width, height));
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(EncoderSupport.Expected(source, colorType)));

        var tags = EncoderSupport.Tags(file);
        int expectedRows = rowsPerStrip == 0 ? height : Math.Min(rowsPerStrip, height);
        await Assert.That(tags[278].Value).IsEqualTo((uint)expectedRows);
        await Assert.That(tags[273].Count).IsEqualTo((uint)((height + expectedRows - 1) / expectedRows));
    }

    [Test]
    [Arguments(false, false, 3, 2)]
    [Arguments(true, false, 1, 1)]
    [Arguments(true, true, 2, 1)]
    [Arguments(false, true, 4, 2)]
    public async Task Auto_ChoosesTheSmallestLosslessType(bool gray, bool alpha, int samples, int photometric)
    {
        using Image source = EncoderSupport.Pattern(9, 5, gray, alpha);
        byte[] file = source.ToTiff(EncoderSupport.Options());
        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags[277].Value).IsEqualTo((uint)samples);
        await Assert.That(tags[262].Value).IsEqualTo((uint)photometric);
        await Assert.That(tags.ContainsKey(338)).IsEqualTo(alpha);
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
    }

    [Test]
    public async Task DefaultStrips_HoldAbout64KiB()
    {
        using Image source = EncoderSupport.Pattern(300, 200);
        var tags = EncoderSupport.Tags(source.ToTiff(EncoderSupport.Options(TiffColorType.Rgb)));
        await Assert.That(tags[278].Value).IsEqualTo(65536u / 900);
        await Assert.That(tags[273].Count).IsEqualTo(3u);
    }

    [Test]
    [Arguments(96f, 96f)]
    [Arguments(300f, 150f)]
    [Arguments(72.5f, 0.25f)]
    [Arguments(299.9994f, 118.11f)]
    public async Task Resolution_RoundTrips(float horizontal, float vertical)
    {
        using Image source = EncoderSupport.Pattern(4, 4);
        source.Metadata.HorizontalResolution = horizontal;
        source.Metadata.VerticalResolution = vertical;
        byte[] file = source.ToTiff(EncoderSupport.Options());
        using Image decoded = TestSupport.Load(file);
        await Assert.That((decoded.Metadata.HorizontalResolution, decoded.Metadata.VerticalResolution)).IsEqualTo((horizontal, vertical));
        await Assert.That(EncoderSupport.Tags(file)[296].Value).IsEqualTo(2u);
    }

    [Test]
    [Arguments(MetadataKinds.All, true, true)]
    [Arguments(MetadataKinds.Icc, true, false)]
    [Arguments(MetadataKinds.Xmp, false, true)]
    [Arguments(MetadataKinds.None, false, false)]
    public async Task IccAndXmp_FollowEncoderMetadata(MetadataKinds kinds, bool icc, bool xmp)
    {
        using Image source = EncoderSupport.Pattern(5, 3);
        source.Metadata.IccProfile = [.. Enumerable.Range(0, 301).Select(i => (byte)i)];
        source.Metadata.Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        byte[] file = source.ToTiff(EncoderSupport.Options() with { Metadata = kinds });
        using Image decoded = TestSupport.Load(file);
        await Assert.That(EncoderSupport.Sha256(decoded.Metadata.IccProfile)).IsEqualTo(icc ? EncoderSupport.Sha256(source.Metadata.IccProfile) : null);
        await Assert.That(EncoderSupport.Sha256(decoded.Metadata.Xmp)).IsEqualTo(xmp ? EncoderSupport.Sha256(source.Metadata.Xmp) : null);
        ImageMetadata metadata = Image.ReadMetadata(file, TestSupport.Options);
        await Assert.That(EncoderSupport.Sha256(metadata.IccProfile)).IsEqualTo(EncoderSupport.Sha256(decoded.Metadata.IccProfile));
    }

    [Test]
    public async Task OddValueLengths_AreWordAligned()
    {
        using Image source = EncoderSupport.Pattern(3, 3, gray: true);
        source.Metadata.IccProfile = [1, 2, 3, 4, 5];
        source.Metadata.Xmp = [60, 120, 47];
        byte[] file = source.ToTiff(EncoderSupport.Options());
        var tags = EncoderSupport.Tags(file);
        await Assert.That(BitConverter.ToUInt32(file, 4) % 2).IsEqualTo(0u);
        await Assert.That(tags.Values.Where(t => t.Count * (t.Type is 3 ? 2u : t.Type is 4 ? 4u : t.Type is 5 ? 8u : 1u) > 4).All(t => t.Value % 2 == 0)).IsTrue();
        await Assert.That(tags.Keys.SequenceEqual(tags.Keys.Order())).IsTrue();
    }

    [Test]
    public async Task NonSeekableStream_GetsTheSameBytes()
    {
        using Image source = EncoderSupport.Pattern(200, 900, alpha: true);
        source.Metadata.IccProfile = [9, 8, 7];
        TiffEncoderOptions options = EncoderSupport.Options(rowsPerStrip: 7);
        byte[] seekable = source.ToTiff(options);
        using var sink = new MemoryStream();
        source.SaveAsTiff(new ForwardOnlyStream(sink), options);
        await Assert.That(sink.ToArray().SequenceEqual(seekable)).IsTrue();
    }

    [Test]
    public async Task OffsetsAreRelativeToTheStartPosition()
    {
        using Image source = EncoderSupport.Pattern(10, 10);
        byte[] expected = source.ToTiff(EncoderSupport.Options());
        using var stream = new MemoryStream();
        stream.Write([1, 2, 3]);
        source.SaveAsTiff(stream, EncoderSupport.Options());
        await Assert.That(stream.Position).IsEqualTo(3L + expected.Length);
        await Assert.That(stream.ToArray()[3..].SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task CanceledSave_ThrowsOperationCanceled()
    {
        using Image source = EncoderSupport.Pattern(8, 8);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.That(() => source.Save(new MemoryStream(), EncoderSupport.Options(), cancel.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task InvalidOptions_AreRejected()
    {
        await Assert.That(() => new TiffEncoderOptions { RowsPerStrip = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { ColorType = (TiffColorType)99 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new TiffEncoderOptions { Compression = (TiffCompression)99 }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AnimatedImage_WritesTheFirstFrame()
    {
        using Image source = EncoderSupport.Pattern(6, 4);
        using Image second = EncoderSupport.Pattern(6, 4, gray: true);
        source.Frames.AddClone(second.Frames[0]);
        using Image decoded = TestSupport.Load(source.ToTiff(EncoderSupport.Options()));
        await Assert.That(EncoderSupport.Sha256(decoded.GetPixelSpan())).IsEqualTo(EncoderSupport.Sha256(source.GetPixelSpan()));
    }

    [Test]
    [NotInParallel(nameof(ImageConfiguration))]
    public async Task SaveByExtension_UsesTheTiffEncoder()
    {
        ImageConfiguration previous = ImageConfiguration.Default;
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-tiff-{Guid.NewGuid():N}.tif");
        try
        {
            ImageConfiguration.Default = TestSupport.Configuration;
            using Image source = EncoderSupport.Pattern(5, 5);
            source.Save(path);
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(source.ToTiff())).IsTrue();
            await Assert.That(source.Encode(ImageFormat.Tiff).SequenceEqual(source.ToTiff())).IsTrue();
        }
        finally
        {
            ImageConfiguration.Default = previous;
            File.Delete(path);
        }
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
