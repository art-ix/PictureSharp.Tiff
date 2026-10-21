namespace PictureSharp.Formats.Tiff;

/// <summary>The compression the TIFF encoder applies to each strip.</summary>
public enum TiffCompression
{
    /// <summary>Uncompressed samples (Compression 1).</summary>
    None,

    /// <summary>PackBits run-length encoding (Compression 32773), row by row.</summary>
    PackBits,

    /// <summary>LZW (Compression 5), the most widely read lossless TIFF compression.</summary>
    Lzw,

    /// <summary>Deflate in a zlib stream (Adobe Deflate, Compression 8); smaller than LZW, read by most current software.</summary>
    Deflate,

    /// <summary>
    /// Baseline JPEG (Compression 7), lossy, through the PictureSharp JPEG encoder: each strip is a complete JPEG stream
    /// with its own tables, colour images as YCbCr with <see cref="TiffEncoderOptions.JpegSubsampling"/> and grey images
    /// as one component. It writes <see cref="TiffColorType.Grayscale"/> or <see cref="TiffColorType.Rgb"/> (alpha is
    /// dropped): <see cref="TiffColorType.Auto"/> chooses grey when every pixel has R = G = B, and any other colour type
    /// makes saving fail with <see cref="ArgumentException"/>. Rows per strip are rounded up to a multiple of the JPEG
    /// block height (8, or 16 with vertical chroma subsampling), and the width cannot exceed 65535.
    /// </summary>
    Jpeg,

    /// <summary>
    /// CCITT Group 4 fax coding (T.6, Compression 4), the most compact lossless coding of black-and-white images. It
    /// writes only <see cref="TiffColorType.BlackWhite"/>: with <see cref="TiffColorType.Auto"/> the image is converted
    /// to black and white, and any other colour type makes saving fail with <see cref="ArgumentException"/>.
    /// </summary>
    CcittGroup4,
}
