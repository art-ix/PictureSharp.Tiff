using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// JPEG compression (7). Each strip or tile is an abbreviated JPEG stream whose tables may live in the JPEGTables tag;
/// the two are joined into one interchange stream and decoded by the configuration's JPEG codec through
/// <see cref="ImageDecodeContext.DecodeEmbeddedPart(ReadOnlyMemory{byte}, int, int, EmbeddedPartOptions)"/>, which also does the YCbCr conversion and chroma upsampling.
/// A chunk may not decode to more than its strip or tile size and is not charged against the reserved image. CMYK
/// (photometric 5) strips hold ink amounts, as libtiff writes them, so they are decoded with
/// <see cref="JpegCmykStorage.Plain"/> rather than the Adobe polarity standalone JPEG files use.
/// </summary>
internal sealed class TiffJpeg
{
    // An Adobe APP14 segment with transform 0: the three components are RGB, not YCbCr.
    private static readonly byte[] AdobeRgb = [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly EmbeddedPartOptions PlainCmyk = new() { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain } };

    private readonly byte[] tables;
    private readonly bool rgb;
    private readonly EmbeddedPartOptions part;
    private readonly int chunkWidth;
    private readonly int chunkHeight;

    public TiffJpeg(ReadOnlySpan<byte> data, TiffDirectory ifd, TiffLayout layout)
    {
        ReadOnlySpan<byte> stored = ifd.GetBytes(data, TiffTag.JpegTables);
        if (!stored.IsEmpty && (stored.Length < 4 || stored[0] != 0xFF || stored[1] != 0xD8 || stored[^2] != 0xFF || stored[^1] != 0xD9))
        {
            throw TiffDecoder.Error("The TIFF JPEGTables tag is not a JPEG tables-only stream.");
        }

        // The tables stream without its EOI; an empty array when the strips carry their own tables.
        tables = stored.IsEmpty ? [] : stored[..^2].ToArray();
        rgb = layout.Photometric == TiffLayout.Rgb;
        part = layout.Photometric == TiffLayout.Separated ? PlainCmyk : new EmbeddedPartOptions();
        (chunkWidth, chunkHeight) = (layout.ChunkWidth, layout.ChunkHeight);
    }

    // port: libtiff/tif_jpeg.c JPEGPreDecode: the tables are read before the strip's own stream, and RGB photometric
    // data is taken as RGB (libtiff sets jpeg_color_space to JCS_RGB) whatever the stream says. Four components stay
    // as libjpeg decodes them (CMYK, or YCCK converted to CMYK), which TIFF reads as ink amounts.
    public void Decode(ReadOnlyMemory<byte> raw, ImageDecodeContext context, int width, int rows, Span<Rgba32> pixels, int stride)
    {
        ReadOnlySpan<byte> strip = raw.Span;
        if (strip.Length < 4 || strip[0] != 0xFF || strip[1] != 0xD8)
        {
            throw TiffDecoder.Error("A TIFF JPEG strip or tile does not start with a JPEG SOI marker.");
        }

        bool addAdobe = rgb && !HasAdobe(tables) && !HasAdobe(strip);
        ReadOnlyMemory<byte> stream = raw;
        if (tables.Length > 0 || addAdobe)
        {
            ReadOnlySpan<byte> head = tables.Length > 0 ? tables : strip[..2];
            byte[] joined = new byte[head.Length + (addAdobe ? AdobeRgb.Length : 0) + strip.Length - 2];
            head[..2].CopyTo(joined);
            int at = 2;
            if (addAdobe)
            {
                AdobeRgb.CopyTo(joined, at);
                at += AdobeRgb.Length;
            }

            head[2..].CopyTo(joined.AsSpan(at));
            strip[2..].CopyTo(joined.AsSpan(at + head.Length - 2));
            stream = joined;
        }

        using Image image = context.DecodeEmbeddedPart(stream, chunkWidth, chunkHeight, part);
        if (image.Width < width || image.Height < rows)
        {
            throw TiffDecoder.Error($"A TIFF JPEG strip or tile decodes to {image.Width}x{image.Height}, smaller than its {width}x{rows} pixels.");
        }

        ReadOnlySpan<Rgba32> decoded = image.GetPixelSpan();
        for (int r = 0; r < rows; r++)
        {
            decoded.Slice(r * image.Width, width).CopyTo(pixels.Slice(r * stride, width));
        }
    }

    /// <summary>Whether the markers before the first SOS include an Adobe APP14 segment.</summary>
    private static bool HasAdobe(ReadOnlySpan<byte> stream)
    {
        int pos = 2;
        while (pos + 4 <= stream.Length && stream[pos] == 0xFF)
        {
            byte marker = stream[pos + 1];
            if (marker is 0xDA or 0xD9)
            {
                return false;
            }

            int length = (stream[pos + 2] << 8) | stream[pos + 3];
            if (marker == 0xEE && length >= 7 && pos + 9 <= stream.Length && stream.Slice(pos + 4, 5).SequenceEqual("Adobe"u8))
            {
                return true;
            }

            pos += 2 + length;
        }

        return false;
    }
}
