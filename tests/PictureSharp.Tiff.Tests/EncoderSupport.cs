using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PictureSharp.Formats.Tiff.Tests;

/// <summary>Source images for the encoder tests and the pixels each colour type should read back as.</summary>
internal static class EncoderSupport
{
    /// <summary>Encoder options; unlike the encoder's default, uncompressed unless <paramref name="compression"/> says otherwise.</summary>
    public static TiffEncoderOptions Options(TiffColorType colorType = TiffColorType.Auto, int rowsPerStrip = 0,
        TiffCompression compression = TiffCompression.None, TiffPredictor predictor = TiffPredictor.Auto) =>
        new()
        {
            ColorType = colorType,
            RowsPerStrip = rowsPerStrip,
            Compression = compression,
            Predictor = predictor,
            Configuration = TestSupport.Configuration,
        };

    /// <summary>
    /// A deterministic gradient; <paramref name="gray"/> makes R = G = B, <paramref name="alpha"/> varies A and
    /// <paramref name="posterize"/> keeps the top 3, 3 and 2 bits of R, G and B (at most 256 colours).
    /// </summary>
    public static Image Pattern(int width, int height, bool gray = false, bool alpha = false, bool posterize = false)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)((x * 37) + (y * 11)), g = (byte)((x * 5) + (y * 53)), b = (byte)((x * x) + (y * 3));
                byte a = alpha ? (byte)((x * 29) + (y * 17)) : (byte)255;
                if (posterize)
                {
                    (r, g, b) = ((byte)(r & 0xE0), (byte)(g & 0xE0), (byte)(b & 0xC0));
                }

                pixels[(y * width) + x] = gray ? new Rgba32(r, r, r, a) : new Rgba32(r, g, b, a);
            }
        }

        return image;
    }

    /// <summary>
    /// Opaque black-and-white shapes: rings, a diagonal band, a long white run and, with <paramref name="noise"/>, random
    /// pixels in the bottom half (the worst case for fax coding).
    /// </summary>
    public static Image Bilevel(int width, int height, bool noise = false)
    {
        var image = new Image(width, height);
        var random = new Random(width + height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx = x - (width / 2), dy = y - (height / 2);
                bool black = (((dx * dx) + (dy * dy)) / 50 % 3 == 0) || Math.Abs(x - (2 * y)) < 3;
                if (y == height / 3)
                {
                    black = x == width - 1;
                }
                else if (noise && y > height / 2)
                {
                    black = random.Next(2) == 0;
                }

                pixels[(y * width) + x] = black ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255);
            }
        }

        return image;
    }

    /// <summary>Opaque random pixels from a fixed seed; <paramref name="gray"/> makes R = G = B.</summary>
    public static Image Noise(int width, int height, bool gray = false)
    {
        var image = new Image(width, height);
        var random = new Random(width * height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            byte r = (byte)random.Next(256), g = (byte)random.Next(256), b = (byte)random.Next(256);
            pixels[i] = gray ? new Rgba32(r, r, r, 255) : new Rgba32(r, g, b, 255);
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
                TiffColorType.Rgb or TiffColorType.Palette => p with { A = 255 },
                TiffColorType.BlackWhite => gray < 128 ? new Rgba32(0, 0, 0, 255) : new Rgba32(255, 255, 255, 255),
                _ => p,
            };
        }

        return pixels;
    }

    /// <summary>The raw strips of the first IFD of a little-endian TIFF.</summary>
    public static List<byte[]> Strips(byte[] file)
    {
        var tags = Tags(file);
        (_, uint count, uint offsets) = tags[273];
        uint counts = tags[279].Value;
        var strips = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            uint offset = count == 1 ? offsets : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)offsets + (4 * i)));
            uint length = count == 1 ? counts : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)counts + (4 * i)));
            strips.Add(file[(int)offset..(int)(offset + length)]);
        }

        return strips;
    }

    /// <summary>
    /// Whether each JPEG strip of <paramref name="file"/> is a complete stream with <paramref name="components"/>
    /// components that the core JPEG decoder decodes to the same pixels as <paramref name="image"/>.
    /// </summary>
    public static bool JpegStripsMatch(byte[] file, Image image, int components) =>
        Strips(file).All(strip => JpegComponents(strip) == components) && StripsMatch(file, image);

    /// <summary>
    /// Whether each strip of <paramref name="file"/> is a complete image stream (JPEG or WebP) of the strip's own rows
    /// that the core decodes to the same pixels as <paramref name="image"/>.
    /// </summary>
    public static bool StripsMatch(byte[] file, Image image)
    {
        int rowsPerStrip = (int)Tags(file)[278].Value, y0 = 0;
        foreach (byte[] strip in Strips(file))
        {
            using Image part = Image.Load(strip, TestSupport.Options);
            if (part.Width != image.Width || part.Height != Math.Min(rowsPerStrip, image.Height - y0)
                || !part.GetPixelSpan().SequenceEqual(image.GetPixelSpan().Slice(y0 * image.Width, part.Width * part.Height)))
            {
                return false;
            }

            y0 += rowsPerStrip;
        }

        return y0 >= image.Height;
    }

    /// <summary>The component count of the first SOF segment of a JPEG stream.</summary>
    public static int JpegComponents(byte[] jpeg)
    {
        for (int i = 2; i + 9 < jpeg.Length; i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]))
        {
            if (jpeg[i + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                // A single component must be sampled 1x1 for libtiff.
                return jpeg[i + 9] == 1 && jpeg[i + 11] != 0x11 ? -1 : jpeg[i + 9];
            }
        }

        return 0;
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
