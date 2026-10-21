namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// The image an IFD describes: size, samples, colour model and how the pixels are cut into strips or tiles.
/// <see cref="Read"/> rejects corrupt values; <see cref="EnsureDecodable"/> rejects valid ones this version cannot decode.
/// </summary>
internal sealed class TiffLayout
{
    public const int WhiteIsZero = 0;
    public const int BlackIsZero = 1;
    public const int Rgb = 2;
    public const int Palette = 3;

    public const int CompressionNone = 1;
    public const int CompressionLzw = 5;
    public const int CompressionDeflate = 8;
    public const int CompressionPackBits = 32773;
    public const int CompressionDeflateOld = 32946;

    // Larger sample counts would multiply the planar buffers; no supported colour model needs them.
    private const int MaxSamplesPerPixel = 16;

    // Tiles may overhang the image, but never by more than this beyond the image's own sample bytes.
    private const long ChunkSlackBytes = 16 << 20;

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int SamplesPerPixel { get; init; }
    public required int BitsPerSample { get; init; }
    public required int Compression { get; init; }
    public required int Photometric { get; init; }
    public required int Predictor { get; init; }
    public required int SampleFormat { get; init; }
    public required bool Separate { get; init; }
    public required bool ReverseBits { get; init; }

    /// <summary>The sample that holds alpha, or -1.</summary>
    public required int AlphaSample { get; init; }

    /// <summary>Whether the colour samples are premultiplied by <see cref="AlphaSample"/> (ExtraSamples 1).</summary>
    public required bool Associated { get; init; }

    public required bool Tiled { get; init; }

    /// <summary>Strip or tile width in pixels; strips span the image width.</summary>
    public required int ChunkWidth { get; init; }

    /// <summary>Rows per strip (at most the image height) or tile length.</summary>
    public required int ChunkHeight { get; init; }

    public int ChunksAcross => (int)(((long)Width + ChunkWidth - 1) / ChunkWidth);

    public int ChunksDown => (int)(((long)Height + ChunkHeight - 1) / ChunkHeight);

    public long ChunksPerPlane => (long)ChunksAcross * ChunksDown;

    public int Planes => Separate ? SamplesPerPixel : 1;

    public int ColorSamples => Photometric == Rgb ? 3 : 1;

    public bool HasAlpha => AlphaSample >= 0;

    /// <summary>Bytes in one row of a strip or tile of one plane, padded to a whole byte.</summary>
    public long RowBytes => (ChunkWidth * (long)(Separate ? 1 : SamplesPerPixel) * BitsPerSample + 7) / 8;

    public static TiffLayout Read(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        uint width = ifd.GetUInt(data, TiffTag.ImageWidth, 0);
        uint height = ifd.GetUInt(data, TiffTag.ImageLength, 0);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw TiffDecoder.Error($"The TIFF image size {width}x{height} is invalid.");
        }

        uint samples = ifd.GetUInt(data, TiffTag.SamplesPerPixel, 1);
        if (samples is 0 or > ushort.MaxValue)
        {
            throw TiffDecoder.Error($"The TIFF SamplesPerPixel value {samples} is invalid.");
        }

        int bits = SameForEverySample(data, ifd, TiffTag.BitsPerSample, 1, "BitsPerSample");
        int format = SameForEverySample(data, ifd, TiffTag.SampleFormat, 1, "SampleFormat");
        if (bits is 0 or > 64)
        {
            throw TiffDecoder.Error($"The TIFF BitsPerSample value {bits} is invalid.");
        }

        // Without the tag, libtiff guesses from the sample count; so do we.
        int photometric = (int)ifd.GetUInt(data, TiffTag.Photometric, samples >= 3 ? (uint)Rgb : BlackIsZero);
        int planar = (int)ifd.GetUInt(data, TiffTag.PlanarConfiguration, 1);
        if (planar is not (1 or 2))
        {
            throw TiffDecoder.Error($"The TIFF PlanarConfiguration value {planar} is invalid.");
        }

        (int alpha, bool associated) = Alpha(data, ifd, photometric, (int)samples);
        bool tiled = ifd.TryGet(TiffTag.TileWidth, out _) || ifd.TryGet(TiffTag.TileLength, out _);
        uint chunkWidth = width, chunkHeight;
        if (tiled)
        {
            chunkWidth = ifd.GetUInt(data, TiffTag.TileWidth, 0);
            chunkHeight = ifd.GetUInt(data, TiffTag.TileLength, 0);
            if (chunkWidth is 0 or > int.MaxValue || chunkHeight is 0 or > int.MaxValue)
            {
                throw TiffDecoder.Error($"The TIFF tile size {chunkWidth}x{chunkHeight} is invalid.");
            }
        }
        else
        {
            chunkHeight = Math.Min(ifd.GetUInt(data, TiffTag.RowsPerStrip, uint.MaxValue), height);
            if (chunkHeight == 0)
            {
                throw TiffDecoder.Error("The TIFF RowsPerStrip value is zero.");
            }
        }

        return new TiffLayout
        {
            Width = (int)width,
            Height = (int)height,
            SamplesPerPixel = (int)samples,
            BitsPerSample = bits,
            Compression = (int)ifd.GetUInt(data, TiffTag.Compression, CompressionNone),
            Photometric = photometric,
            Predictor = (int)ifd.GetUInt(data, TiffTag.Predictor, 1),
            SampleFormat = format,
            Separate = planar == 2 && samples > 1,
            ReverseBits = ifd.GetUInt(data, TiffTag.FillOrder, 1) == 2,
            AlphaSample = alpha,
            Associated = associated,
            Tiled = tiled,
            ChunkWidth = (int)chunkWidth,
            ChunkHeight = (int)chunkHeight,
        };
    }

    /// <summary>Throws <see cref="NotSupportedException"/> for valid features outside TIFF baseline plus LZW, Deflate and predictor 2.</summary>
    public void EnsureDecodable()
    {
        if (Compression is not (CompressionNone or CompressionLzw or CompressionDeflate or CompressionDeflateOld or CompressionPackBits))
        {
            throw new NotSupportedException($"The TIFF compression {Compression} is not supported.");
        }

        if (SampleFormat != 1)
        {
            throw new NotSupportedException($"The TIFF sample format {SampleFormat} (signed or floating-point samples) is not supported.");
        }

        bool supportedBits = Photometric switch
        {
            WhiteIsZero or BlackIsZero => BitsPerSample is 1 or 2 or 4 or 8 or 16,
            Rgb => BitsPerSample is 8 or 16,
            Palette => BitsPerSample is 1 or 2 or 4 or 8,
            _ => throw new NotSupportedException($"The TIFF photometric interpretation {Photometric} is not supported."),
        };
        if (!supportedBits)
        {
            throw new NotSupportedException($"{BitsPerSample}-bit samples are not supported for the TIFF photometric interpretation {Photometric}.");
        }

        if (SamplesPerPixel < ColorSamples)
        {
            throw TiffDecoder.Error($"The TIFF image has {SamplesPerPixel} samples per pixel, fewer than its colour model needs.");
        }

        if (SamplesPerPixel > MaxSamplesPerPixel || (Photometric == Palette && SamplesPerPixel != 1))
        {
            throw new NotSupportedException($"TIFF images with {SamplesPerPixel} samples per pixel are not supported for the photometric interpretation {Photometric}.");
        }

        if (Predictor is not (1 or 2) || (Predictor == 2 && BitsPerSample is not (8 or 16)))
        {
            throw new NotSupportedException($"The TIFF predictor {Predictor} is not supported with {BitsPerSample}-bit samples.");
        }

        long imageBytes = ((long)Width * (Separate ? 1 : SamplesPerPixel) * BitsPerSample + 7) / 8 * Height;
        long chunkBytes = RowBytes * ChunkHeight;
        if (chunkBytes > imageBytes + ChunkSlackBytes)
        {
            throw TiffDecoder.Error($"The TIFF tile size {ChunkWidth}x{ChunkHeight} is far larger than the image.");
        }

        if (chunkBytes * Planes > Array.MaxLength)
        {
            throw new NotSupportedException($"TIFF strips or tiles of {chunkBytes} bytes are not supported; the file needs smaller ones.");
        }
    }

    // port: Pillow 10.2.0 TiffImagePlugin OPEN_INFO: ExtraSamples 1 is premultiplied alpha, 2 and unknown values such
    // as Corel's 999 straight alpha, 0 an ignored sample; without the tag a fourth RGB sample is straight alpha. The
    // same rule applies to a second greyscale sample.
    private static (int Sample, bool Associated) Alpha(ReadOnlySpan<byte> data, TiffDirectory ifd, int photometric, int samples)
    {
        int color = photometric == Rgb ? 3 : 1;
        if (photometric is not (WhiteIsZero or BlackIsZero or Rgb) || samples <= color)
        {
            return (-1, false);
        }

        if (!ifd.TryGet(TiffTag.ExtraSamples, out TiffEntry extra) || extra.Count == 0)
        {
            return samples == color + 1 ? (color, false) : (-1, false);
        }

        return ifd.GetUInt(data, extra, 0) switch
        {
            0 => (-1, false),
            1 => (color, true),
            _ => (color, false),
        };
    }

    private static int SameForEverySample(ReadOnlySpan<byte> data, TiffDirectory ifd, ushort tag, int fallback, string name)
    {
        if (!ifd.TryGet(tag, out TiffEntry entry) || entry.Count == 0)
        {
            return fallback;
        }

        uint first = ifd.GetUInt(data, entry, 0);
        int count = (int)Math.Min(entry.Count, MaxSamplesPerPixel);
        for (int i = 1; i < count; i++)
        {
            if (ifd.GetUInt(data, entry, i) != first)
            {
                throw new NotSupportedException($"TIFF images with a different {name} for each sample are not supported.");
            }
        }

        return (int)Math.Min(first, int.MaxValue);
    }
}
