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
    public const int Separated = 5;
    public const int YCbCr = 6;

    public const int CompressionNone = 1;
    public const int CompressionCcittRle = 2;
    public const int CompressionCcittFax3 = 3;
    public const int CompressionCcittFax4 = 4;
    public const int CompressionLzw = 5;
    public const int CompressionJpeg = 7;
    public const int CompressionDeflate = 8;
    public const int CompressionCcittRlew = 32771;
    public const int CompressionPackBits = 32773;
    public const int CompressionDeflateOld = 32946;
    public const int CompressionWebP = 50001;

    public const int FormatUnsigned = 1;
    public const int FormatSigned = 2;
    public const int FormatFloat = 3;

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

    /// <summary>The InkSet tag of separated images; 1 is CMYK.</summary>
    public required int InkSet { get; init; }

    /// <summary>Horizontal and vertical chroma subsampling of YCbCr images (default 2, 2); 1, 1 otherwise.</summary>
    public required (int Horizontal, int Vertical) Subsampling { get; init; }

    public required bool Tiled { get; init; }

    /// <summary>Strip or tile width in pixels; strips span the image width.</summary>
    public required int ChunkWidth { get; init; }

    /// <summary>Rows per strip (at most the image height) or tile length.</summary>
    public required int ChunkHeight { get; init; }

    public int ChunksAcross => (int)(((long)Width + ChunkWidth - 1) / ChunkWidth);

    public int ChunksDown => (int)(((long)Height + ChunkHeight - 1) / ChunkHeight);

    public long ChunksPerPlane => (long)ChunksAcross * ChunksDown;

    public int Planes => Separate ? SamplesPerPixel : 1;

    public int ColorSamples => Photometric switch
    {
        Rgb or YCbCr => 3,
        Separated => 4,
        _ => 1,
    };

    public bool HasAlpha => AlphaSample >= 0;

    public bool IsJpeg => Compression == CompressionJpeg;

    public bool IsCcitt => Compression is CompressionCcittRle or CompressionCcittFax3 or CompressionCcittFax4 or CompressionCcittRlew;

    /// <summary>
    /// The predictor that applies: libtiff only runs it with the codecs that support one (LZW and Deflate here) and
    /// ignores the tag otherwise.
    /// </summary>
    public int EffectivePredictor => Compression is CompressionLzw or CompressionDeflate or CompressionDeflateOld ? Predictor : 1;

    /// <summary>Whether the samples are packed YCbCr blocks: one Cb and Cr per Horizontal x Vertical luma samples.</summary>
    public bool IsSubsampledYCbCr => Photometric == YCbCr && !IsJpeg && Subsampling != (1, 1);

    /// <summary>Bytes in one row of a strip or tile of one plane, padded to a whole byte.</summary>
    public long RowBytes => (ChunkWidth * (long)(Separate ? 1 : SamplesPerPixel) * BitsPerSample + 7) / 8;

    /// <summary>
    /// The decoded size of <paramref name="rows"/> rows of one plane of a strip or tile.
    /// </summary>
    // port: libtiff/tif_strip.c TIFFVStripSize64: packed YCbCr rounds the width and the rows up to whole blocks.
    public long ChunkBytes(int rows)
    {
        if (!IsSubsampledYCbCr)
        {
            return RowBytes * rows;
        }

        (int h, int v) = Subsampling;
        long blocksAcross = ((long)ChunkWidth + h - 1) / h;
        long blocksDown = ((long)rows + v - 1) / v;
        return blocksAcross * (h * v + 2) * blocksDown;
    }

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
        int format = SameForEverySample(data, ifd, TiffTag.SampleFormat, FormatUnsigned, "SampleFormat");
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

        (int Horizontal, int Vertical) subsampling = (1, 1);
        if (photometric == YCbCr)
        {
            // port: libtiff/tif_dir.c TIFFDefaultDirectory: YCbCrSubsampling defaults to 2, 2.
            subsampling = ifd.TryGet(TiffTag.YCbCrSubsampling, out TiffEntry entry) && entry.Count >= 2 && TiffEntry.IsInteger(entry.Type)
                ? ((int)Math.Min(ifd.GetUInt(data, entry, 0), 8), (int)Math.Min(ifd.GetUInt(data, entry, 1), 8))
                : (2, 2);
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
            InkSet = (int)ifd.GetUInt(data, TiffTag.InkSet, 1),
            Subsampling = subsampling,
            Tiled = tiled,
            ChunkWidth = (int)chunkWidth,
            ChunkHeight = (int)chunkHeight,
        };
    }

    /// <summary>Throws <see cref="NotSupportedException"/> for valid features this version cannot decode.</summary>
    public void EnsureDecodable()
    {
        EnsureCompression();
        EnsureSamples();
        if (SamplesPerPixel < ColorSamples)
        {
            throw TiffDecoder.Error($"The TIFF image has {SamplesPerPixel} samples per pixel, fewer than its colour model needs.");
        }

        if (SamplesPerPixel > MaxSamplesPerPixel || (Photometric == Palette && SamplesPerPixel != 1))
        {
            throw new NotSupportedException($"TIFF images with {SamplesPerPixel} samples per pixel are not supported for the photometric interpretation {Photometric}.");
        }

        bool predictorFits = EffectivePredictor switch
        {
            1 => true,
            // port: libtiff/tif_predict.c PredictorSetupDecode: horAcc8, horAcc16 and horAcc32 for integer samples.
            2 => SampleFormat is FormatUnsigned or FormatSigned && BitsPerSample is 8 or 16 or 32 && !IsSubsampledYCbCr,
            3 => SampleFormat == FormatFloat,
            _ => false,
        };
        if (!predictorFits)
        {
            throw new NotSupportedException($"The TIFF predictor {Predictor} is not supported with {BitsPerSample}-bit samples of format {SampleFormat}.");
        }

        long imageBytes = ((long)Width * (Separate ? 1 : SamplesPerPixel) * BitsPerSample + 7) / 8 * Height;
        long chunkBytes = ChunkBytes(ChunkHeight);
        if (chunkBytes > imageBytes + ChunkSlackBytes)
        {
            throw TiffDecoder.Error($"The TIFF tile size {ChunkWidth}x{ChunkHeight} is far larger than the image.");
        }

        if (chunkBytes * Planes > Array.MaxLength)
        {
            throw new NotSupportedException($"TIFF strips or tiles of {chunkBytes} bytes are not supported; the file needs smaller ones.");
        }
    }

    private void EnsureCompression()
    {
        string? name = Compression switch
        {
            CompressionNone or CompressionCcittRle or CompressionCcittFax3 or CompressionCcittFax4 or CompressionLzw or CompressionJpeg
                or CompressionDeflate or CompressionCcittRlew or CompressionPackBits or CompressionDeflateOld or CompressionWebP => null,
            6 => "old-style JPEG (6)",
            9 or 10 or 34661 => "JBIG",
            32809 => "ThunderScan",
            32908 or 32909 => "PixarLog",
            34676 or 34677 => "SGILog (LogLuv)",
            34712 => "JPEG 2000",
            34887 => "LERC",
            34925 => "LZMA",
            50000 => "Zstandard",
            _ => $"{Compression}",
        };
        if (name is not null)
        {
            throw new NotSupportedException($"The TIFF compression {name} is not supported.");
        }

        if (IsCcitt && (BitsPerSample != 1 || SamplesPerPixel != 1 || Photometric is not (WhiteIsZero or BlackIsZero)))
        {
            throw new NotSupportedException("CCITT compression is only supported for 1-bit bilevel TIFF images.");
        }

        if (IsJpeg && (BitsPerSample != 8 || SampleFormat != FormatUnsigned || Separate || SamplesPerPixel != ColorSamples
            || Photometric is not (BlackIsZero or Rgb or YCbCr)))
        {
            throw new NotSupportedException($"JPEG-compressed TIFF images are only supported as contiguous 8-bit greyscale, RGB or YCbCr, not photometric {Photometric} with {SamplesPerPixel} samples.");
        }

        // port: libtiff/tif_webp.c TWebPSetupDecode, TWebPFixupTags and TWebPPreDecode reject these as errors.
        if (Compression == CompressionWebP)
        {
            if (SamplesPerPixel is not (3 or 4) || BitsPerSample != 8 || SampleFormat != FormatUnsigned || Separate)
            {
                throw TiffDecoder.Error($"A WebP-compressed TIFF image must have 3 or 4 contiguous 8-bit unsigned samples, not {SamplesPerPixel} of {BitsPerSample} bits.");
            }

            if (ChunkWidth > Codecs.TiffWebP.MaxSide || ChunkHeight > Codecs.TiffWebP.MaxSide)
            {
                throw TiffDecoder.Error($"The TIFF WebP strip or tile size {ChunkWidth}x{ChunkHeight} exceeds the WebP limit of {Codecs.TiffWebP.MaxSide}.");
            }

            if (Photometric != Rgb)
            {
                throw new NotSupportedException($"WebP-compressed TIFF images are only supported as RGB, not photometric {Photometric}.");
            }
        }
    }

    private void EnsureSamples()
    {
        if (SampleFormat == FormatFloat)
        {
            if (BitsPerSample is not (16 or 32 or 64) || Photometric is not (WhiteIsZero or BlackIsZero or Rgb))
            {
                throw new NotSupportedException($"{BitsPerSample}-bit floating-point TIFF samples are only supported as 16-, 32- or 64-bit greyscale or RGB.");
            }

            return;
        }

        if (SampleFormat == FormatSigned)
        {
            if (BitsPerSample is not (8 or 16 or 32) || Photometric is not (WhiteIsZero or BlackIsZero or Rgb))
            {
                throw new NotSupportedException($"{BitsPerSample}-bit signed TIFF samples are only supported as 8-, 16- or 32-bit greyscale or RGB.");
            }

            return;
        }

        if (SampleFormat != FormatUnsigned)
        {
            throw new NotSupportedException($"The TIFF sample format {SampleFormat} (complex or undefined samples) is not supported.");
        }

        bool supportedBits = Photometric switch
        {
            WhiteIsZero or BlackIsZero or Rgb => BitsPerSample is <= 16 or 32,
            Separated => BitsPerSample is 8 or 16,
            Palette => BitsPerSample is 1 or 2 or 4 or 8,
            YCbCr => BitsPerSample == 8,
            _ => throw new NotSupportedException($"The TIFF photometric interpretation {Photometric} is not supported."),
        };
        if (!supportedBits)
        {
            throw new NotSupportedException($"{BitsPerSample}-bit samples are not supported for the TIFF photometric interpretation {Photometric}.");
        }

        if (Photometric == Separated && (InkSet != 1 || Associated))
        {
            throw new NotSupportedException("Separated TIFF images are only supported as CMYK (InkSet 1) without premultiplied alpha.");
        }

        // port: libtiff/tif_getimage.c PickContigCase and PickSeparateCase: the subsamplings libtiff converts.
        if (Photometric == YCbCr && !IsJpeg
            && (Subsampling is not ((4, 4) or (4, 2) or (4, 1) or (2, 2) or (2, 1) or (1, 2) or (1, 1)) || (Separate && Subsampling != (1, 1))))
        {
            throw new NotSupportedException($"TIFF YCbCr subsampling {Subsampling.Horizontal}x{Subsampling.Vertical} is not supported{(Separate ? " with separate planes" : "")}.");
        }
    }

    // port: Pillow 10.2.0 TiffImagePlugin OPEN_INFO: ExtraSamples 1 is premultiplied alpha, 2 and unknown values such
    // as Corel's 999 straight alpha, 0 an ignored sample; without the tag the sample after the colour samples is
    // straight alpha when it is the only extra one.
    private static (int Sample, bool Associated) Alpha(ReadOnlySpan<byte> data, TiffDirectory ifd, int photometric, int samples)
    {
        int color = photometric switch
        {
            Rgb => 3,
            Separated => 4,
            _ => 1,
        };
        if (photometric is not (WhiteIsZero or BlackIsZero or Rgb or Separated) || samples <= color)
        {
            return (-1, false);
        }

        if (!ifd.TryGet(TiffTag.ExtraSamples, out TiffEntry extra) || extra.Count == 0 || !TiffEntry.IsInteger(extra.Type))
        {
            return samples == color + 1 && photometric != Separated ? (color, false) : (-1, false);
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

        if (!TiffEntry.IsInteger(entry.Type))
        {
            throw TiffDecoder.Error($"The TIFF {name} tag has the non-integer type {entry.Type}.");
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
