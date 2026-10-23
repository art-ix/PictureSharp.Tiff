namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// Findings of the coverage-guided campaign (0.7.2, tools/fuzz.sh), reduced to the case each one exposed. The files
/// the fuzzer found are in tests/assets/fuzz/ and go through the same checks in <see cref="FuzzCorpus_KeepsTheContract"/>.
/// </summary>
public sealed class FuzzRegressionTests
{
    private const ushort StripByteCounts = 279;

    private static readonly byte[] NewXmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();

    public static IEnumerable<string> FuzzFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fuzz")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    [Test]
    public async Task WebPStripHoldingTheFile_ThrowsInsteadOfRecursing()
    {
        // StripOffsets 0 makes the "WebP stream" the TIFF file itself, which the core decoded as TIFF again, and so
        // on until the stack overflowed and took the process down.
        byte[] file = Rgb(TiffBuilder.Gray(4, 3)).Set(259, TiffBuilder.Short, 50001).Set(273, TiffBuilder.Long, 0)
            .Set(StripByteCounts, TiffBuilder.Long, 4096).Build();
        await Assert.That(() => TestSupport.Load(file)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task EditMetadata_WithANextLinkPastTheEnd_AddsNoPage()
    {
        // The link ended the chain; after the append it pointed into the new IFD's values. As the fuzzer found, the
        // high half of a rational's denominator reads as an IFD with no entries, which counts as a page.
        static byte[] Resolution(byte[] file) => Image.EditMetadata(file, metadata => metadata.HorizontalResolution = 150, TestSupport.Options);
        byte[] probe = Resolution(TiffBuilder.Gray(4, 3).Build());
        int link = ValueOffset(probe, BitConverter.ToInt32(probe, 4), 282) + 6;
        byte[] file = TiffBuilder.Gray(4, 3).Build(_ => (uint)link);
        await Assert.That(file.Length).IsLessThan(link);

        byte[] edited = Resolution(file);
        await Assert.That(TestSupport.Identify(edited).ContainerItemCount).IsEqualTo(1);
        await Assert.That(Pixels(edited)).IsEquivalentTo(Pixels(file));
    }

    [Test]
    [Arguments((ushort)1)]
    [Arguments((ushort)32773)]
    public async Task EditMetadata_WithoutByteCounts_StoresTheEstimate(ushort compression)
    {
        // libtiff estimates missing counts, the compressed one from the length of the file, which the edit changes.
        byte[] rows = [.. Enumerable.Range(0, 3).SelectMany(y => new byte[] { 3, (byte)y, 1, 2, 3 })];
        TiffBuilder builder = compression == 1 ? TiffBuilder.Gray(4, 3) : TiffBuilder.GrayStrip(4, 3, compression, rows);
        byte[] file = builder.Remove(StripByteCounts).Build();
        byte[] edited = Edit(file);

        await Assert.That(Pixels(edited)).IsEquivalentTo(Pixels(file));
        uint expected = compression == 1 ? 12u : (uint)(file.Length - 8);
        await Assert.That(StripByteCountsOfFirstIfd(edited)).IsEqualTo(expected);
    }

    [Test]
    public async Task EditMetadata_WhenTheStripHoldsTheHeader_Throws()
    {
        // The new IFD offset goes into the header, which the strip at offset 0 decodes as pixels.
        byte[] file = TiffBuilder.Gray(4, 3).Set(273, TiffBuilder.Long, 0).Build();
        _ = Pixels(file);
        await Assert.That(() => Edit(file)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task EditMetadata_WithAnExifPointerPastTheEnd_AddsNoExif()
    {
        // The pointer was ignored; after the append it would point at the new IFD, read as an EXIF IFD.
        int length = TiffBuilder.Gray(4, 3).Set(34665, TiffBuilder.Long, 0).Build().Length;
        byte[] file = TiffBuilder.Gray(4, 3).Set(34665, TiffBuilder.Long, (uint)(length + 1) & ~1u).Build();
        await Assert.That(Image.ReadMetadata(file, TestSupport.Options).Exif).IsNull();

        byte[] edited = Image.EditMetadata(file, metadata => metadata.Xmp = NewXmp, TestSupport.Options);
        ImageMetadata metadata = Image.ReadMetadata(edited, TestSupport.Options);
        await Assert.That(metadata.Exif).IsNull();
        await Assert.That(metadata.Xmp).IsEquivalentTo(NewXmp);
    }

    /// <summary>
    /// The checks of tools/PictureSharp.Tiff.Fuzz: only the contract exceptions escape, <c>Identify</c> agrees with
    /// <c>Load</c>, and a file <c>EditMetadata</c> accepts decodes as before and carries the edit.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(FuzzFiles))]
    public async Task FuzzCorpus_KeepsTheContract(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fuzz", name));
        byte[]? pixels = Contract(() => Pixels(data));
        ImageInfo? info = Contract(() => TestSupport.Identify(data));
        _ = Contract(() => Image.ReadMetadata(data, TestSupport.Options));
        if (pixels is not null)
        {
            using Image image = TestSupport.Load(data);
            await Assert.That((info?.Width, info?.Height)).IsEqualTo((image.Width, image.Height));
        }

        byte[]? edited = Contract(() => Edit(data));
        if (edited is not null)
        {
            if (pixels is not null)
            {
                await Assert.That(Pixels(edited)).IsEquivalentTo(pixels);
            }

            ImageMetadata metadata = Image.ReadMetadata(edited, TestSupport.Options);
            await Assert.That(metadata.Xmp).IsEquivalentTo(NewXmp);
            await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        }
    }

    private static TiffBuilder Rgb(TiffBuilder builder) =>
        builder.Set(258, TiffBuilder.Short, 8, 8, 8).Set(262, TiffBuilder.Short, 2).Set(277, TiffBuilder.Short, 3);

    private static byte[] Edit(byte[] file) => Image.EditMetadata(file, metadata =>
    {
        metadata.Xmp = NewXmp;
        metadata.Orientation = ExifOrientation.RightTop;
    }, TestSupport.Options);

    private static byte[] Pixels(byte[] file)
    {
        using Image image = TestSupport.Load(file, TestSupport.Options with { IgnoreMetadata = true });
        return image.GetPixelByteSpan().ToArray();
    }

    private static T? Contract<T>(Func<T> action) where T : class
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            return null;
        }
    }

    // The StripByteCounts value of the first IFD of a little-endian classic file with one strip.
    private static uint StripByteCountsOfFirstIfd(byte[] file) => BitConverter.ToUInt32(file, Entry(file, BitConverter.ToInt32(file, 4), StripByteCounts) + 8);

    // Where the value of an entry of a little-endian classic IFD lies, when it does not fit the entry.
    private static int ValueOffset(byte[] file, int ifd, ushort tag) => BitConverter.ToInt32(file, Entry(file, ifd, tag) + 8);

    private static int Entry(byte[] file, int ifd, ushort tag)
    {
        for (int i = 0, count = BitConverter.ToUInt16(file, ifd); i < count; i++)
        {
            int entry = ifd + 2 + (i * 12);
            if (BitConverter.ToUInt16(file, entry) == tag)
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"The IFD at {ifd} has no tag {tag}.");
    }
}
