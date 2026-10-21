namespace PictureSharp.Formats.Tiff;

/// <summary>The samples the TIFF encoder writes; every type uses 8 bits per sample.</summary>
public enum TiffColorType
{
    /// <summary>Choose the smallest lossless representation (greyscale, greyscale with alpha, RGB or RGBA).</summary>
    Auto,

    /// <summary>8-bit greyscale (colour is converted to luminance, alpha is dropped).</summary>
    Grayscale,

    /// <summary>8-bit greyscale with unassociated alpha (colour is converted to luminance).</summary>
    GrayscaleAlpha,

    /// <summary>24-bit RGB (alpha is dropped).</summary>
    Rgb,

    /// <summary>32-bit RGB with unassociated alpha.</summary>
    Rgba,
}
