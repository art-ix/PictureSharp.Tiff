namespace PictureSharp.Formats.Tiff.Tests;

public sealed class TiffFormatTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();

    [Test]
    [Arguments(new byte[] { (byte)'I', (byte)'I', 42, 0 })]
    [Arguments(new byte[] { (byte)'M', (byte)'M', 0, 42 })]
    [Arguments(new byte[] { (byte)'I', (byte)'I', 43, 0 })]   // BigTIFF
    [Arguments(new byte[] { (byte)'M', (byte)'M', 0, 43 })]
    public async Task IsSupported_TiffSignatures_ReturnsTrue(byte[] header)
    {
        await Assert.That(new TiffFormat().IsSupported(header)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { (byte)'I', (byte)'I', 42 })]
    [Arguments(new byte[] { (byte)'I', (byte)'I', 0, 42 })]
    [Arguments(new byte[] { (byte)'M', (byte)'M', 42, 0 })]
    [Arguments(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })]
    public async Task IsSupported_OtherHeaders_ReturnsFalse(byte[] header)
    {
        await Assert.That(new TiffFormat().IsSupported(header)).IsFalse();
    }

    [Test]
    public async Task Registration_RecognizesTiff()
    {
        await Assert.That(new TiffFormat().Format).IsSameReferenceAs(ImageFormat.Tiff);
        await Assert.That(Configuration.FromExtension("scan.TIF")).IsSameReferenceAs(ImageFormat.Tiff);
        await Assert.That(Configuration.FromMimeType("image/tiff")).IsSameReferenceAs(ImageFormat.Tiff);
        await Assert.That(Configuration.CanDecode(ImageFormat.Tiff)).IsTrue();
        await Assert.That(Configuration.CanEncode(ImageFormat.Tiff)).IsFalse();
    }

    [Test]
    public async Task Load_ThrowsNotSupported_UntilDecodingIsImplemented()
    {
        byte[] data = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var options = new DecoderOptions { Configuration = Configuration };
        await Assert.That(() => Image.Load(data, options)).Throws<NotSupportedException>();
        await Assert.That(() => Image.Identify(data, options)).Throws<NotSupportedException>();
    }
}
