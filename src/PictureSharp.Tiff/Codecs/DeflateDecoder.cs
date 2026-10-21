using System.IO.Compression;

namespace PictureSharp.Formats.Tiff.Codecs;

internal static class DeflateDecoder
{
    // port: libtiff/tif_zip.c ZIPDecode with System.IO.Compression's zlib. Inflating stops once the strip or tile is
    // full, so data beyond it (writers that pad the last strip to RowsPerStrip rows) is ignored and never expanded.
    public static void Decode(ReadOnlyMemory<byte> input, Span<byte> output)
    {
        int read;
        try
        {
            using var zlib = new ZLibStream(new ReadOnlyMemoryStream(input), CompressionMode.Decompress);
            read = zlib.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            // Most corrupt streams throw InvalidDataException; zlib reports some as ZLibException, an IOException.
            throw new ImageFormatException(TiffFormat.Descriptor, "The TIFF Deflate data is corrupt.", e);
        }

        if (read < output.Length)
        {
            throw TiffDecoder.Error("The TIFF Deflate data ends before the strip or tile is complete.");
        }
    }
}
