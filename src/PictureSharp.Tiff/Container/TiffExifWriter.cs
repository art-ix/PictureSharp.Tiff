namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Writes an EXIF block (<see cref="ImageMetadata.Exif"/>, a TIFF stream) into an image IFD, the inverse of
/// <see cref="TiffExifBuilder"/>: the descriptive tags and the orientation of the block's IFD0 become entries of the
/// image IFD, and its EXIF, GPS and interoperability IFDs become child IFDs. Structural tags of IFD0 (offsets, sizes,
/// compression, resolution) and the thumbnail IFD are never copied; values are converted to the output byte order.
/// </summary>
internal static class TiffExifWriter
{
    /// <summary>
    /// Adds the block's entries to <paramref name="ifd"/>; <paramref name="software"/> includes its Software tag. A block
    /// that is not a TIFF stream adds nothing, and sub-IFDs or entries outside it are dropped, as the reader does.
    /// </summary>
    // port: libtiff/tif_dir.c TIFFCreateEXIFDirectory and TIFFWriteCustomDirectory write the EXIF and GPS IFDs as
    // custom directories that the image IFD points to.
    public static void AddTo(TiffIfdWriter ifd, ReadOnlySpan<byte> exif, bool software)
    {
        TiffDirectory root;
        try
        {
            TiffHeader header = TiffHeader.Read(exif);
            root = TiffDirectory.Parse(exif, header, header.FirstIfd);
        }
        catch (ImageFormatException)
        {
            return;
        }

        foreach (TiffEntry e in TiffExifBuilder.Copyable(root))
        {
            if (TiffExifBuilder.Ifd0Tags.Contains(e.Tag) && (software || e.Tag != TiffTag.Software) && !ifd.Contains(e.Tag))
            {
                Copy(ifd, exif, root, e);
            }
        }

        TiffDirectory? exifIfd = root.TryParseSubdirectory(exif, TiffTag.ExifIfd);
        TiffDirectory? gps = root.TryParseSubdirectory(exif, TiffTag.GpsIfd);
        TiffDirectory? interop = exifIfd?.TryParseSubdirectory(exif, TiffTag.InteropIfd);
        TiffIfdWriter? exifChild = Child(ifd, TiffTag.ExifIfd, exif, exifIfd);
        Child(ifd, TiffTag.GpsIfd, exif, gps);
        if (exifChild is not null)
        {
            Child(exifChild, TiffTag.InteropIfd, exif, interop);
        }
    }

    private static TiffIfdWriter? Child(TiffIfdWriter parent, ushort tag, ReadOnlySpan<byte> exif, TiffDirectory? source)
    {
        if (source is null)
        {
            return null;
        }

        TiffIfdWriter child = parent.CreateChild();
        foreach (TiffEntry e in TiffExifBuilder.Copyable(source))
        {
            Copy(child, exif, source, e);
        }

        parent.AddChild(tag, child);
        return child;
    }

    private static void Copy(TiffIfdWriter ifd, ReadOnlySpan<byte> data, TiffDirectory source, TiffEntry e) =>
        ifd.AddCopy(e.Tag, e.Type, e.Count, data.Slice(e.ValueOffset, (int)e.Count * e.Size(source.Header.BigTiff)), source.BigEndian);
}
