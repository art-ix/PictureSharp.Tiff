using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Image.ReadMetadata without decoding pixels: the blocks Decode sets, read from the selected page. Image.EditMetadata
/// appends a new IFD for that page and relinks it, leaving the image data and the other pages as they are.
/// </summary>
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

    /// <summary>
    /// Copies the file and appends a copy of the page's IFD in which the changed blocks replace their tags, then points
    /// the link to the old IFD at it. Unchanged tags keep their values, so the strips, tiles and other pages stay valid.
    /// </summary>
    // port: libtiff/tif_dirwrite.c TIFFRewriteDirectory: the rewritten directory goes at the end of the file and the
    // link that pointed to the old one (in the header or the previous directory) is patched; the old one is left unused.
    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context)
    {
        if (metadata.Gamma != original.Gamma || metadata.Chromaticities != original.Chromaticities
            || metadata.SrgbRenderingIntent != original.SrgbRenderingIntent)
        {
            throw new NotSupportedException("TIFF stores colours only as an ICC profile; gamma, chromaticities and sRGB intent are PNG chunks.");
        }

        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, TiffDecoder.Page(context.Options), countPages: false);
        TiffDirectory image = file.Image;
        TiffHeader header = image.Header;
        byte[]? exif = metadata.Exif, icc = metadata.IccProfile, xmp = metadata.Xmp;
        bool exifChanged = !Same(original.Exif, exif);
        bool iccChanged = !Same(original.IccProfile, icc);
        bool xmpChanged = !Same(original.Xmp, xmp);
        bool resolutionChanged = metadata.HorizontalResolution != original.HorizontalResolution
            || metadata.VerticalResolution != original.VerticalResolution;

        var ifd = new TiffIfdWriter(header.BigEndian, header.BigTiff);
        foreach (TiffEntry e in image.Entries)
        {
            bool replaced = (exifChanged && (TiffExifBuilder.Ifd0Tags.Contains(e.Tag) || e.Tag is TiffTag.ExifIfd or TiffTag.GpsIfd))
                || (iccChanged && e.Tag == TiffTag.IccProfile)
                || (xmpChanged && e.Tag == TiffTag.Xmp)
                || (resolutionChanged && e.Tag is TiffTag.XResolution or TiffTag.YResolution or TiffTag.ResolutionUnit);
            if (!replaced)
            {
                ifd.AddCopy(e.Tag, e.Type, e.Count, bytes.Slice(e.ValueOffset, (int)e.Count * e.Size(header.BigTiff)), header.BigEndian);
            }
        }

        if (exifChanged && exif is { Length: > 0 })
        {
            TiffExifWriter.AddTo(ifd, exif, software: true);
        }

        if (iccChanged && icc is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.IccProfile, TiffEntry.Undefined, icc);
        }

        if (xmpChanged && xmp is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.Xmp, TiffEntry.Byte, xmp);
        }

        if (resolutionChanged)
        {
            ifd.AddResolution(metadata.HorizontalResolution, metadata.VerticalResolution);
        }

        context.ThrowIfCancellationRequested();
        long offset = (bytes.Length + 1L) & ~1L;
        long length = offset + ifd.Size;
        if (!header.BigTiff && length > uint.MaxValue)
        {
            throw new NotSupportedException("The TIFF file would exceed 4 GiB, which classic TIFF cannot address.");
        }

        if (length > Array.MaxLength)
        {
            throw new NotSupportedException("The rewritten TIFF file would exceed the largest array.");
        }

        byte[] result = new byte[length];
        bytes.CopyTo(result);
        ifd.ToArray((ulong)offset, image.Next).CopyTo(result.AsSpan((int)offset));
        Span<byte> link = result.AsSpan((int)file.Link, header.OffsetSize);
        if (header.BigTiff)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(link, (ulong)offset);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(link, (uint)offset);
        }

        if (header.BigEndian)
        {
            link.Reverse();
        }

        return result;
    }

    private static bool Same(byte[]? a, byte[]? b) => (a is null) == (b is null) && (a is null || a.AsSpan().SequenceEqual(b));
}
