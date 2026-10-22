using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// WebP compression (50001, libtiff's <c>tif_webp.c</c>). Each strip or tile is a complete WebP stream decoded by the
/// configuration's WebP codec through <see cref="ImageDecodeContext.DecodeEmbeddedPart(ReadOnlyMemory{byte}, int, int)"/>; its RGB or RGBA values become
/// the chunk's contiguous 8-bit samples, which the photometric and alpha rules then convert like any other chunk.
/// </summary>
internal static class TiffWebP
{
    /// <summary>The largest strip or tile side libtiff reads or writes, the WebP limit.</summary>
    public const int MaxSide = 16383;

    // port: libtiff/tif_webp.c TWebPPreDecode and TWebPDecode: a strip spans the image width and its own rows (the last
    // one fewer), a tile its full size; the stream decodes into RGB for three samples (alpha dropped) or RGBA for four.
    public static void Decode(ReadOnlyMemory<byte> raw, ImageDecodeContext context, TiffLayout layout, int rows, Span<byte> chunk)
    {
        int segmentHeight = layout.Tiled ? layout.ChunkHeight : rows;
        using Image image = context.DecodeEmbeddedPart(raw, layout.ChunkWidth, segmentHeight);
        if (image.Width < layout.ChunkWidth || image.Height < segmentHeight)
        {
            throw TiffDecoder.Error($"A TIFF WebP strip or tile decodes to {image.Width}x{image.Height}, smaller than its {layout.ChunkWidth}x{segmentHeight} pixels.");
        }

        ReadOnlySpan<Rgba32> decoded = image.GetPixelSpan();
        int spp = layout.SamplesPerPixel, at = 0;
        for (int y = 0; y < segmentHeight; y++)
        {
            foreach (Rgba32 p in decoded.Slice(y * image.Width, layout.ChunkWidth))
            {
                chunk[at] = p.R;
                chunk[at + 1] = p.G;
                chunk[at + 2] = p.B;
                if (spp == 4)
                {
                    chunk[at + 3] = p.A;
                }

                at += spp;
            }
        }
    }
}
