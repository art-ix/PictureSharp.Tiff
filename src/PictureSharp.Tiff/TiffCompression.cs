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
}
