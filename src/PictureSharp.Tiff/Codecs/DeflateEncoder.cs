using System.IO.Compression;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>Deflate (Adobe, Compression 8) strips through System.IO.Compression's zlib; the bytes depend on the runtime.</summary>
internal sealed class DeflateEncoder(int level) : IDisposable
{
    private readonly MemoryStream buffer = new();

    // port: libtiff/tif_zip.c ZIPEncode and ZIPPostEncode: one zlib stream per strip.
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> data)
    {
        buffer.SetLength(0);
        using (var zlib = new ZLibStream(buffer, new ZLibCompressionOptions { CompressionLevel = level }, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
    }

    public void Dispose() => buffer.Dispose();
}
