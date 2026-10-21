namespace PictureSharp.Formats.Tiff;

/// <summary>The file variant the TIFF encoder writes.</summary>
public enum TiffContainer
{
    /// <summary>
    /// Classic TIFF unless the uncompressed samples and directories would exceed 4 GiB, then BigTIFF. Compressed sizes
    /// are unknown up front, so a compressed file that still exceeds 4 GiB as classic TIFF makes saving fail with
    /// <see cref="NotSupportedException"/>; choose <see cref="BigTiff"/> for it.
    /// </summary>
    Auto,

    /// <summary>Classic TIFF (version 42) with 32-bit offsets, read by every TIFF reader; at most 4 GiB.</summary>
    Classic,

    /// <summary>BigTIFF (version 43) with 64-bit offsets, read by libtiff 4, GDAL, Pillow and tifffile, but not by older readers.</summary>
    BigTiff,
}
