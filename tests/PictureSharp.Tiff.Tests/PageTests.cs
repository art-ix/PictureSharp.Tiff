namespace PictureSharp.Formats.Tiff.Tests;

public sealed class PageTests
{
    public static IEnumerable<string> MultiPageFiles() => TestSupport.Files().Where(name => TestSupport.Reference[name].Pages is not null);

    [Test]
    [MethodDataSource(nameof(MultiPageFiles))]
    public async Task EveryPage_MatchesReferencePixels(string name)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        IReadOnlyList<ReferenceEntry> pages = TestSupport.Reference[name].Pages!;
        for (int page = 0; page < pages.Count; page++)
        {
            DecoderOptions options = TestSupport.Page(page);
            using Image image = TestSupport.Load(data, options);
            ImageInfo info = TestSupport.Identify(data, options);
            await Assert.That((image.Width, image.Height)).IsEqualTo((pages[page].Width, pages[page].Height));
            await Assert.That(TestSupport.Sha256(image)).IsEqualTo(pages[page].Sha256);
            await Assert.That((info.Width, info.Height)).IsEqualTo((image.Width, image.Height));
            await Assert.That(info.ContainerItemCount).IsEqualTo(pages.Count);
            await Assert.That(Image.ReadMetadata(data, options).IsEmpty).IsEqualTo(image.Metadata.IsEmpty);
        }
    }

    [Test]
    [MethodDataSource(nameof(MultiPageFiles))]
    public async Task FirstPage_IsTheDefault(string name)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        using Image image = TestSupport.Load(data);
        await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference[name].Pages![0].Sha256);
    }

    [Test]
    [Arguments("pages_mixed.tif", 5)]
    [Arguments("pages_thumbnail_first.tif", 2)]
    [Arguments("rgb8_lzw_predictor.tif", 1)]
    public async Task PageBeyondTheLast_ThrowsImageFormatException(string name, int page)
    {
        byte[] data = TestSupport.ReadCorpus(name);
        DecoderOptions options = TestSupport.Page(page);
        await Assert.That(() => TestSupport.Load(data, options)).Throws<ImageFormatException>();
        await Assert.That(() => TestSupport.Identify(data, options)).Throws<ImageFormatException>();
        await Assert.That(() => Image.ReadMetadata(data, options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task NegativePage_IsRejected() =>
        await Assert.That(() => new TiffDecoderOptions { Page = -1 }).Throws<ArgumentOutOfRangeException>();

    [Test]
    public async Task ReducedResolutionPages_AreNotCounted()
    {
        // pages_mixed: IFD 1 is a thumbnail, so page 1 is IFD 2 (palette, 19x11).
        using Image image = TestSupport.Load(TestSupport.ReadCorpus("pages_mixed.tif"), TestSupport.Page(1));
        await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference["pages_mixed.tif"].Pages![1].Sha256);
        await Assert.That((image.Width, image.Height)).IsEqualTo((19, 11));
    }
}
