namespace PictureSharp.Formats.Tiff.Container;

// Image.ReadMetadata without decoding pixels: the blocks Decode sets, read from the selected page.
// Rewriting (EditMetadata) is planned for writing, so Rewrite keeps the interface's NotSupportedException.
internal sealed class TiffMetadataCodec : IImageFormatMetadataCodec
{
    public static TiffMetadataCodec Instance { get; } = new();

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, TiffDecoder.Page(context.Options), countPages: false);
        var metadata = new ImageMetadata();
        TiffMetadataReader.Read(bytes, file.Image, metadata, context);
        return metadata;
    }
}
