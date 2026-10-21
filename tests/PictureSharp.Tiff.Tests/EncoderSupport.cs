using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>Source images for the encoder tests and the pixels each colour type should read back as.</summary>
internal static class EncoderSupport
{
    public static TiffEncoderOptions Options(TiffColorType colorType = TiffColorType.Auto, int rowsPerStrip = 0) =>
        new() { ColorType = colorType, RowsPerStrip = rowsPerStrip, Configuration = TestSupport.Configuration };

    /// <summary>A deterministic gradient; <paramref name="gray"/> makes R = G = B, <paramref name="alpha"/> varies A.</summary>
    public static Image Pattern(int width, int height, bool gray = false, bool alpha = false)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x * 37) + (y * 11)), g = (byte)((x * 5) + (y * 53)), b = (byte)((x * x) + (y * 3));
                byte a = alpha ? (byte)((x * 29) + (y * 17)) : (byte)255;
                pixels[(y * width) + x] = gray ? new Rgba32(r, r, r, a) : new Rgba32(r, g, b, a);
            }
        }

        return image;
    }

    /// <summary>The pixels a file written with <paramref name="colorType"/> (not Auto) decodes to.</summary>
    public static Rgba32[] Expected(Image source, TiffColorType colorType)
    {
        Rgba32[] pixels = source.GetPixelSpan().ToArray();
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            byte gray = p.R == p.G && p.G == p.B ? p.R : (byte)(((19595 * p.R) + (38470 * p.G) + (7471 * p.B) + 32768) >> 16);
            pixels[i] = colorType switch
            {
                TiffColorType.Grayscale => new Rgba32(gray, gray, gray, 255),
                TiffColorType.GrayscaleAlpha => new Rgba32(gray, gray, gray, p.A),
                TiffColorType.Rgb => p with { A = 255 },
                _ => p,
            };
        }

        return pixels;
    }

    public static string Sha256(ReadOnlySpan<Rgba32> pixels) => Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(pixels)));

    public static string? Sha256(byte[]? block) => block is null ? null : Convert.ToHexStringLower(SHA256.HashData(block));

    /// <summary>The entries of the first IFD of a little-endian TIFF: tag → (type, count, first value or offset).</summary>
    public static Dictionary<ushort, (ushort Type, uint Count, uint Value)> Tags(byte[] file)
    {
        int ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd));
        var tags = new Dictionary<ushort, (ushort, uint, uint)>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = file.AsSpan(ifd + 2 + (i * 12), 12);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint value = type == 3 && n <= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            tags.Add(BinaryPrimitives.ReadUInt16LittleEndian(entry), (type, n, value));
        }

        return tags;
    }
}
