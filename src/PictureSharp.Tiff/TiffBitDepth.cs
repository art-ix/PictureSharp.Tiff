namespace PictureSharp.Formats.Tiff;

/// <summary>The sample depth the TIFF encoder writes (<see cref="TiffEncoderOptions.BitDepth"/>).</summary>
public enum TiffBitDepth
{
    /// <summary>
    /// 8-bit images keep 8-bit samples (or 1 bit for <see cref="TiffColorType.BlackWhite"/>). A page of a 16-bit image is
    /// written with 16 bits per sample, unless every sample is an exact 8-bit value (a multiple of 257, e.g. a widened
    /// 8-bit image), which is written with 8 bits without loss, or its compression stores only 8-bit samples
    /// (<see cref="TiffCompression.Jpeg"/>, <see cref="TiffCompression.WebP"/>, <see cref="TiffCompression.CcittGroup4"/>),
    /// which rounds it to 8 bits.
    /// </summary>
    Auto,

    /// <summary>At most 8 bits per sample: 16-bit images are rounded to 8 bits, then written as 8-bit images are.</summary>
    UpTo8,

    /// <summary>
    /// 16 bits per sample (greyscale, greyscale with alpha, RGB or RGBA); 8-bit images are widened exactly. Saving fails
    /// with <see cref="ArgumentException"/> for <see cref="TiffColorType.Palette"/>, <see cref="TiffColorType.BlackWhite"/>
    /// and the 8-bit compressions.
    /// </summary>
    Sixteen,
}
