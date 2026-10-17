namespace PictureSharp.Formats.Tiff;

/// <summary>TIFF members of <see cref="ImageFormat"/>.</summary>
public static class TiffImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>TIFF (<c>image/tiff</c>), recognized once <see cref="TiffFormat"/> is registered.</summary>
        public static ImageFormat Tiff => TiffFormat.Descriptor;
    }
}
