namespace PictureSharp.Formats.Tiff.Container;

/// <summary>Reads resolution, orientation, ICC (34675) and XMP (700) from the image IFD.</summary>
internal static class TiffMetadataReader
{
    private const uint UnitNone = 1;
    private const uint UnitCentimeter = 3;

    /// <summary>
    /// The resolution in dots per inch, or (0, 0) when it is missing, invalid or has no unit (ResolutionUnit 1).
    /// The unit defaults to inches; centimetres are multiplied by 2.54.
    /// </summary>
    public static (float Horizontal, float Vertical) Resolution(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        uint unit = ifd.GetUInt(data, TiffTag.ResolutionUnit, 2);
        double x = ifd.GetRational(data, TiffTag.XResolution);
        double y = ifd.GetRational(data, TiffTag.YResolution);
        if (unit == UnitNone || !(x > 0) || !(y > 0))
        {
            return (0, 0);
        }

        double factor = unit == UnitCentimeter ? 2.54 : 1;
        float horizontal = (float)(x * factor), vertical = (float)(y * factor);
        return float.IsFinite(horizontal) && float.IsFinite(vertical) ? (horizontal, vertical) : (0, 0);
    }

    /// <summary>The Orientation tag (1-8), or null when it is missing or out of range.</summary>
    public static ExifOrientation? Orientation(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        if (!ifd.TryGet(TiffTag.Orientation, out TiffEntry entry) || entry.Count == 0 || entry.Type is not (TiffEntry.Short or TiffEntry.Long))
        {
            return null;
        }

        uint value = ifd.GetUInt(data, entry, 0);
        return value is >= 1 and <= 8 ? (ExifOrientation)value : null;
    }

    public static void Read(ReadOnlySpan<byte> data, TiffDirectory ifd, ImageMetadata metadata, ImageDecodeContext context)
    {
        (float horizontal, float vertical) = Resolution(data, ifd);
        if (horizontal > 0)
        {
            metadata.HorizontalResolution = horizontal;
            metadata.VerticalResolution = vertical;
        }

        if (context.Options.IgnoreMetadata)
        {
            return;
        }

        ReadOnlySpan<byte> icc = ifd.GetBytes(data, TiffTag.IccProfile);
        if (!icc.IsEmpty)
        {
            context.ReserveMetadata(icc.Length);
            metadata.IccProfile = icc.ToArray();
        }

        ReadOnlySpan<byte> xmp = ifd.GetBytes(data, TiffTag.Xmp);
        if (!xmp.IsEmpty)
        {
            context.ReserveMetadata(xmp.Length);
            metadata.Xmp = xmp.ToArray();
        }

        // Assigning creates an EXIF block, so the default top-left orientation is left implicit.
        if (Orientation(data, ifd) is { } orientation && orientation != ExifOrientation.TopLeft)
        {
            metadata.Orientation = orientation;
        }
    }
}
