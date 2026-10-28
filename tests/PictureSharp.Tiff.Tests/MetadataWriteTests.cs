using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PictureSharp.Formats.Jpeg;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>
/// EXIF written by the encoder and metadata rewritten by <c>Image.EditMetadata</c>. The edited corpus goes to
/// <c>written.edited</c> in the output directory, where <c>verify_written.py</c> checks it against libtiff and Pillow.
/// </summary>
public sealed class MetadataWriteTests
{
    // The IFD0 tags the EXIF block carries, without Software, which names the encoder.
    private static readonly ushort[] DescriptiveTags = [270, 271, 272, 274, 306, 315, 33432];
    private static readonly ushort[] BlockTags = [270, 271, 272, 274, 305, 306, 315, 33432];
    private static readonly string EditedOutput = Path.Combine(AppContext.BaseDirectory, "written.edited");
    private static readonly byte[] NewXmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");

    public static IEnumerable<string> Files() => TestSupport.Files();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exif_RoundTripsThroughTheEncoder_InEitherByteOrder(bool bigEndian)
    {
        using Image source = EncoderSupport.Pattern(9, 7);
        source.Metadata.Exif = ExifSample.Block(bigEndian);
        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Lzw));
        using Image decoded = TestSupport.Load(file);
        var tags = EncoderSupport.Tags(file);

        List<string> expected = ExifSample.Entries(source.Metadata.Exif, DescriptiveTags);
        await Assert.That(ExifSample.Entries(decoded.Metadata.Exif, DescriptiveTags)).IsEquivalentTo(expected);
        await Assert.That(expected.Count(e => e.StartsWith("exif ", StringComparison.Ordinal))).IsEqualTo(14);
        await Assert.That(expected.Count(e => e.StartsWith("interop ", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(expected.Count(e => e.StartsWith("gps ", StringComparison.Ordinal))).IsEqualTo(8);
        await Assert.That(ExifSample.Ascii(decoded.Metadata.Exif, 305)).IsEqualTo("PictureSharp.Tiff");
        await Assert.That(decoded.Metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
        await Assert.That(decoded.Metadata.ExifProfile.TryGetValue(ExifTag.Make, out string? make) ? make : null).IsEqualTo("Camera Maker");
        await Assert.That(TestSupport.Sha256(decoded)).IsEqualTo(TestSupport.Sha256(source));
        // Structural tags of the block (YCbCrPositioning, the thumbnail IFD) stay out; the resolution is the image's.
        await Assert.That(tags.ContainsKey(531)).IsFalse();
        await Assert.That(tags[34665].Type).IsEqualTo((ushort)4);
        await Assert.That(tags[274].Value).IsEqualTo(6u);
        await Assert.That(BitConverter.ToUInt32(file, (int)tags[282].Value)).IsEqualTo((uint)source.Metadata.HorizontalResolution);
    }

    [Test]
    [Arguments(MetadataKinds.Icc | MetadataKinds.Xmp)]
    [Arguments(MetadataKinds.None)]
    public async Task Exif_IsLeftOut_WithoutTheExifKind(MetadataKinds kinds)
    {
        using Image source = EncoderSupport.Pattern(4, 4);
        source.Metadata.Exif = ExifSample.Block(bigEndian: true);
        byte[] file = source.ToTiff(EncoderSupport.Options() with { Metadata = kinds });
        var tags = EncoderSupport.Tags(file);
        await Assert.That(tags.Keys.Intersect<ushort>([34665, 34853, 270, 271, 274]).Any()).IsFalse();
        using Image decoded = TestSupport.Load(file);
        await Assert.That(ExifSample.Entries(decoded.Metadata.Exif).Select(e => e[..6])).IsEquivalentTo(["0 305 "]);
    }

    [Test]
    public async Task Orientation_IsHonouredByApplyExifOrientation()
    {
        using Image source = EncoderSupport.Pattern(6, 4);
        source.Metadata.Orientation = ExifOrientation.RightTop;
        byte[] file = source.ToTiff(EncoderSupport.Options());
        await Assert.That(EncoderSupport.Tags(file)[274].Value).IsEqualTo(6u);
        await Assert.That(EncoderSupport.Tags(file).ContainsKey(34665)).IsFalse();
        using Image oriented = TestSupport.Load(file, TestSupport.Options with { ApplyExifOrientation = true });
        await Assert.That((oriented.Width, oriented.Height)).IsEqualTo((4, 6));
        await Assert.That(oriented.GetPixelSpan<Rgba32>()[3]).IsEqualTo(source.GetPixelSpan<Rgba32>()[0]);
    }

    [Test]
    [Arguments(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [Arguments(new byte[] { (byte)'I', (byte)'I', 42, 0, 200, 0, 0, 0 })]
    public async Task UnreadableExif_IsLeftOut(byte[] exif)
    {
        using Image source = EncoderSupport.Pattern(4, 4);
        source.Metadata.Exif = exif;
        byte[] file = source.ToTiff(EncoderSupport.Options());
        await Assert.That(EncoderSupport.Tags(file).ContainsKey(34665)).IsFalse();
        using Image decoded = TestSupport.Load(file);
        await Assert.That(TestSupport.Sha256(decoded)).IsEqualTo(TestSupport.Sha256(source));
    }

    /// <summary>
    /// Every corpus file edited: the orientation set (creating the EXIF block when there is none), a new XMP packet and
    /// resolution. Every page decodes as before, the selected one reads back the edit and the files go to the script.
    /// </summary>
    [Test]
    public async Task EditMetadata_OnTheCorpus_KeepsThePixelsOfEveryPage()
    {
        var manifest = new JsonObject();
        Directory.CreateDirectory(EditedOutput);
        foreach (string name in Files())
        {
            byte[] data = TestSupport.ReadCorpus(name);
            ImageMetadata original = Image.ReadMetadata(data, TestSupport.Options);
            byte[]? expectedExif = null;
            byte[] edited = Image.EditMetadata(data, metadata =>
            {
                metadata.Orientation = ExifOrientation.RightTop;
                metadata.Xmp = NewXmp;
                metadata.HorizontalResolution = 123.5f;
                metadata.VerticalResolution = 77;
                expectedExif = metadata.Exif;
            }, TestSupport.Options);

            // Only the link to the IFD changes; the new IFD follows the original bytes.
            await Assert.That(Enumerable.Range(0, data.Length).Count(i => edited[i] != data[i])).IsBetween(1, 8).Because(name);
            IReadOnlyList<ReferenceEntry> pages = TestSupport.Reference[name].Pages ?? [TestSupport.Reference[name]];
            for (int page = 0; page < pages.Count; page++)
            {
                using Image image = TestSupport.Load(edited, TestSupport.Page(page));
                await Assert.That(TestSupport.Sha256(image)).IsEqualTo(pages[page].Sha256).Because($"{name} page {page}");
            }

            ImageMetadata metadata = Image.ReadMetadata(edited, TestSupport.Options);
            await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.RightTop);
            await Assert.That(ExifSample.Entries(metadata.Exif, BlockTags)).IsEquivalentTo(ExifSample.Entries(expectedExif, BlockTags)).Because(name);
            await Assert.That(EncoderSupport.Sha256(metadata.Xmp)).IsEqualTo(EncoderSupport.Sha256(NewXmp));
            await Assert.That(EncoderSupport.Sha256(metadata.IccProfile)).IsEqualTo(EncoderSupport.Sha256(original.IccProfile));
            await Assert.That((metadata.HorizontalResolution, metadata.VerticalResolution)).IsEqualTo((123.5f, 77f));
            using (Image oriented = TestSupport.Load(edited, TestSupport.Options with { ApplyExifOrientation = true }))
            {
                await Assert.That((oriented.Width, oriented.Height)).IsEqualTo((pages[0].Height, pages[0].Width));
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            File.WriteAllBytes(Path.Combine(EditedOutput, stem + ".tif"), edited);
            File.WriteAllBytes(Path.Combine(EditedOutput, stem + ".original.tif"), data);
            manifest[stem + ".tif"] = new JsonObject
            {
                ["original"] = stem + ".original.tif",
                ["pages"] = pages.Count,
                ["exif"] = ExifReference(expectedExif, EditedOutput, stem),
                ["software"] = true,
                ["dpi"] = new JsonArray(123.5f, 77f),
                ["icc"] = EncoderSupport.Sha256(original.IccProfile),
                ["xmp"] = EncoderSupport.Sha256(NewXmp),
            };
        }

        File.WriteAllText(Path.Combine(EditedOutput, "written.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task EditMetadata_ReplacesTheExifIfds()
    {
        byte[] data = TestSupport.ReadCorpus("exif.tif");
        byte[] block = ExifSample.Block(bigEndian: true, orientation: 3);
        byte[] edited = Image.EditMetadata(data, metadata => metadata.Exif = block, TestSupport.Options);
        ImageMetadata metadata = Image.ReadMetadata(edited, TestSupport.Options);
        await Assert.That(ExifSample.Entries(metadata.Exif, BlockTags)).IsEquivalentTo(ExifSample.Entries(block, BlockTags));
        await Assert.That(ExifSample.Ascii(metadata.Exif, 305)).IsEqualTo("Camera firmware 1.0");
        await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.BottomRight);
        using Image image = TestSupport.Load(edited);
        await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference["exif.tif"].Sha256);
    }

    [Test]
    public async Task EditMetadata_RemovingEverything_RemovesTheTags()
    {
        byte[] data = TestSupport.ReadCorpus("metadata.tif");
        byte[] withExif = Image.EditMetadata(data, metadata => metadata.Exif = ExifSample.Block(bigEndian: false), TestSupport.Options);
        byte[] edited = Image.EditMetadata(withExif, metadata => metadata.Clear(), TestSupport.Options);

        await Assert.That(Image.ReadMetadata(edited, TestSupport.Options).IsEmpty).IsTrue();
        using Image image = TestSupport.Load(edited);
        await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference["metadata.tif"].Sha256);
        foreach (ushort tag in new ushort[] { 270, 271, 274, 305, 700, 34665, 34675, 34853 })
        {
            await Assert.That(TiffTags(edited).Contains(tag)).IsFalse().Because($"tag {tag}");
        }
    }

    [Test]
    public async Task EditMetadata_OfOnePage_LeavesTheOthers()
    {
        byte[] data = TestSupport.ReadCorpus("pages_mixed.tif");
        int pages = TestSupport.Reference["pages_mixed.tif"].Pages!.Count;
        byte[] edited = Image.EditMetadata(data, metadata => metadata.Xmp = NewXmp, TestSupport.Page(1));
        for (int page = 0; page < pages; page++)
        {
            ImageMetadata before = Image.ReadMetadata(data, TestSupport.Page(page));
            ImageMetadata after = Image.ReadMetadata(edited, TestSupport.Page(page));
            byte[]? xmp = page == 1 ? NewXmp : before.Xmp;
            await Assert.That(EncoderSupport.Sha256(after.Xmp)).IsEqualTo(EncoderSupport.Sha256(xmp));
            await Assert.That(EncoderSupport.Sha256(after.Exif)).IsEqualTo(EncoderSupport.Sha256(before.Exif));
            using Image image = TestSupport.Load(edited, TestSupport.Page(page));
            await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference["pages_mixed.tif"].Pages![page].Sha256);
        }
    }

    [Test]
    public async Task EditMetadata_Twice_AppendsAgain()
    {
        byte[] data = TestSupport.ReadCorpus("rgb8_none_be.tif");
        byte[] once = Image.EditMetadata(data, metadata => metadata.Orientation = ExifOrientation.LeftBottom, TestSupport.Options);
        byte[] twice = Image.EditMetadata(once, metadata => metadata.Orientation = ExifOrientation.TopRight, TestSupport.Options);
        await Assert.That(twice.Length).IsGreaterThan(once.Length);
        await Assert.That(Image.ReadMetadata(twice, TestSupport.Options).Orientation).IsEqualTo(ExifOrientation.TopRight);
        using Image image = TestSupport.Load(twice);
        await Assert.That(TestSupport.Sha256(image)).IsEqualTo(TestSupport.Reference["rgb8_none_be.tif"].Sha256);
    }

    [Test]
    public async Task EditMetadata_WithoutChanges_ReturnsTheSameBytes()
    {
        byte[] data = TestSupport.ReadCorpus("exif.tif");
        await Assert.That(Image.EditMetadata(data, _ => { }, TestSupport.Options).AsSpan().SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task EditMetadata_OfTheColourDescription_IsNotSupported()
    {
        byte[] data = TestSupport.ReadCorpus("rgb8_none.tif");
        await Assert.That(() => Image.EditMetadata(data, metadata => metadata.Gamma = 0.45f, TestSupport.Options)).Throws<NotSupportedException>();
    }

    /// <summary>
    /// A JPEG carrying <paramref name="exif"/>, whose EXIF Pillow reads to compare with the TIFF's; returns its name.
    /// </summary>
    internal static string? ExifReference(byte[]? exif, string directory, string name)
    {
        if (exif is null)
        {
            return null;
        }

        using var image = new Image(8, 8);
        image.Metadata.Exif = exif;
        string file = name + ".exif.jpg";
        File.WriteAllBytes(Path.Combine(directory, file), image.ToJpeg(new JpegEncoderOptions { Configuration = TestSupport.Configuration }));
        return file;
    }

    /// <summary>The tags of the first IFD of a TIFF in either byte order.</summary>
    private static HashSet<ushort> TiffTags(byte[] file)
    {
        bool be = file[0] == 'M';
        ushort U16(int pos) => be ? (ushort)((file[pos] << 8) | file[pos + 1]) : BitConverter.ToUInt16(file, pos);
        int ifd = be ? (file[4] << 24) | (file[5] << 16) | (file[6] << 8) | file[7] : BitConverter.ToInt32(file, 4);
        return [.. Enumerable.Range(0, U16(ifd)).Select(i => U16(ifd + 2 + (i * 12)))];
    }

    [Test]
    [Arguments(1, 0)]
    [Arguments(2, 0)]
    [Arguments(2, 1)]
    public async Task EditMetadata_OnAnIfdLoop_KeepsThePages(int pages, int page)
    {
        // As libtiff's test_ifd_loop_to_self and test_ifd_loop_to_first: the last IFD links back to the first.
        using Image source = EncoderSupport.Pattern(9, 7);
        if (pages == 2)
        {
            using Image second = EncoderSupport.Pattern(9, 7, gray: true);
            source.Frames.AddClone(second.Frames[0]);
        }

        byte[] file = source.ToTiff(EncoderSupport.Options(compression: TiffCompression.Lzw));
        List<(int Offset, int Link)> chain = Chain(file);
        BitConverter.TryWriteBytes(file.AsSpan(chain[^1].Link), chain[0].Offset);
        string[] before = [.. Enumerable.Range(0, pages).Select(p => Hash(file, p))];

        byte[] edited = Image.EditMetadata(file, metadata => metadata.Orientation = ExifOrientation.RightTop, TestSupport.Options with
        {
            FormatOptions = [new TiffDecoderOptions { Page = page }],
        });

        // The old IFD of the page is no longer reachable: the chain holds the same number of IFDs, the new one in its
        // place, and still loops.
        List<(int Offset, int Link)> after = Chain(edited);
        await Assert.That(after.Count).IsEqualTo(pages);
        await Assert.That(after[page].Offset).IsGreaterThanOrEqualTo(file.Length);
        await Assert.That(BitConverter.ToInt32(edited, after[^1].Link)).IsEqualTo(after[0].Offset);
        await Assert.That(TestSupport.Identify(edited).ContainerItemCount).IsEqualTo(pages);
        await Assert.That(Enumerable.Range(0, pages).Select(p => Hash(edited, p))).IsEquivalentTo(before);
        await Assert.That(Image.ReadMetadata(edited, TestSupport.Options with { FormatOptions = [new TiffDecoderOptions { Page = page }] }).Orientation)
            .IsEqualTo(ExifOrientation.RightTop);
    }

    private static string Hash(byte[] file, int page)
    {
        using Image image = TestSupport.Load(file, TestSupport.Page(page));
        return TestSupport.Sha256(image);
    }

    // The IFDs of a little-endian classic file and the position of each one's next-IFD offset, up to a repeat.
    private static List<(int Offset, int Link)> Chain(byte[] file)
    {
        var chain = new List<(int Offset, int Link)>();
        for (int ifd = BitConverter.ToInt32(file, 4); ifd != 0 && chain.TrueForAll(c => c.Offset != ifd);)
        {
            int link = ifd + 2 + (BitConverter.ToUInt16(file, ifd) * 12);
            chain.Add((ifd, link));
            ifd = BitConverter.ToInt32(file, link);
        }

        return chain;
    }
}
