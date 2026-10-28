namespace PictureSharp.Formats.Tiff;

/// <summary>The predictor the TIFF encoder applies to samples before LZW or Deflate compression (tag 317).</summary>
public enum TiffPredictor
{
    /// <summary>
    /// <see cref="Horizontal"/> for LZW and Deflate, except for palette images, whose indices do not vary smoothly, and
    /// black-and-white images; <see cref="None"/> otherwise.
    /// </summary>
    Auto,

    /// <summary>Samples are compressed as they are (Predictor 1).</summary>
    None,

    /// <summary>
    /// Each sample is stored as the difference from the same sample of the pixel to its left (Predictor 2), which makes
    /// photographs and gradients compress better. Only valid with LZW and Deflate and 8- or 16-bit samples (not
    /// <see cref="TiffColorType.BlackWhite"/>).
    /// </summary>
    Horizontal,
}
