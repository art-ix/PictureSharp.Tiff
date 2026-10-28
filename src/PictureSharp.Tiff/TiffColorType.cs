namespace PictureSharp.Formats.Tiff;

/// <summary>
/// The samples the TIFF encoder writes: 8 bits per sample (16 with <see cref="TiffBitDepth.Sixteen"/> or a 16-bit
/// image), or 1 bit for <see cref="BlackWhite"/>.
/// </summary>
public enum TiffColorType
{
    /// <summary>
    /// Choose the smallest lossless representation: black and white when every pixel is opaque black or white;
    /// greyscale (with alpha only when a pixel is not opaque) when every pixel has R = G = B; otherwise a palette when the image is opaque, has at most 256 colours and more than 768
    /// pixels (a smaller image is smaller as RGB than with the 1.5 KiB colour map); otherwise RGB or RGBA. With
    /// <see cref="TiffCompression.CcittGroup4"/>, always <see cref="BlackWhite"/>. With 16-bit samples, greyscale or RGB,
    /// with alpha only when a pixel is not opaque.
    /// </summary>
    Auto,

    /// <summary>8-bit greyscale (colour is converted to luminance, alpha is dropped).</summary>
    Grayscale,

    /// <summary>8-bit greyscale with unassociated alpha (colour is converted to luminance).</summary>
    GrayscaleAlpha,

    /// <summary>24-bit RGB (alpha is dropped).</summary>
    Rgb,

    /// <summary>32-bit RGB with unassociated alpha.</summary>
    Rgba,

    /// <summary>
    /// 8-bit indices into a colour map of up to 256 colours (alpha is dropped). Saving fails with
    /// <see cref="InvalidOperationException"/> when the image has more than 256 distinct colours.
    /// </summary>
    Palette,

    /// <summary>
    /// 1-bit black and white (Photometric WhiteIsZero, alpha is dropped): a pixel is black when its luminance, as for
    /// <see cref="Grayscale"/>, is below 128.
    /// </summary>
    BlackWhite,
}
