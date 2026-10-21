using System.Buffers;
using System.Buffers.Binary;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Decodes strips or tiles one at a time: every plane of a chunk is decompressed and un-predicted into a pooled
/// buffer, then each visible row is unpacked to samples and converted to pixels, so planar images need no full copy.
/// </summary>
internal static class TiffPixelDecoder
{
    public static void Decode(ReadOnlyMemory<byte> data, TiffDirectory ifd, TiffLayout layout, Span<Rgba32> pixels, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        long perPlane = layout.ChunksPerPlane;
        long chunks = perPlane * layout.Planes;
        ushort offsetsTag = layout.Tiled ? TiffTag.TileOffsets : TiffTag.StripOffsets;
        ushort countsTag = layout.Tiled ? TiffTag.TileByteCounts : TiffTag.StripByteCounts;
        if (!ifd.TryGet(offsetsTag, out TiffEntry offsets) || offsets.Count < chunks)
        {
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {(layout.Tiled ? "tile" : "strip")} offsets.");
        }

        // port: libtiff/tif_dirread.c EstimateStripByteCounts, for uncompressed data only: each chunk is assumed full.
        bool hasCounts = ifd.TryGet(countsTag, out TiffEntry counts);
        if (hasCounts ? counts.Count < chunks : layout.Compression != TiffLayout.CompressionNone)
        {
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {(layout.Tiled ? "tile" : "strip")} byte counts.");
        }

        Rgba32[]? palette = layout.Photometric == TiffLayout.Palette ? TiffColorConverter.ReadPalette(bytes, ifd, layout.BitsPerSample) : null;
        var converter = new TiffColorConverter(layout, palette);
        LzwDecoder? lzw = layout.Compression == TiffLayout.CompressionLzw ? new LzwDecoder() : null;
        int rowBytes = (int)layout.RowBytes;
        int chunkBytes = rowBytes * layout.ChunkHeight;
        int planes = layout.Planes, spp = layout.SamplesPerPixel, width = layout.Width;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkBytes * planes);
        ushort[] samples = ArrayPool<ushort>.Shared.Rent(layout.ChunkWidth * spp);
        try
        {
            for (long c = 0; c < perPlane; c++)
            {
                context.ThrowIfCancellationRequested();
                int cx = (int)(c % layout.ChunksAcross) * layout.ChunkWidth;
                int cy = (int)(c / layout.ChunksAcross) * layout.ChunkHeight;
                int rows = layout.Tiled ? layout.ChunkHeight : Math.Min(layout.ChunkHeight, layout.Height - cy);
                for (int p = 0; p < planes; p++)
                {
                    int index = (int)(p * perPlane + c);
                    Span<byte> output = buffer.AsSpan(p * chunkBytes, rows * rowBytes);
                    uint offset = ifd.GetUInt(bytes, offsets, index);
                    uint count = hasCounts ? ifd.GetUInt(bytes, counts, index) : (uint)output.Length;
                    Decompress(data, offset, count, layout, lzw, output);
                    if (layout.Predictor == 2)
                    {
                        HorizontalPredictor.Decode(output, rowBytes, layout.Separate ? 1 : spp, layout.BitsPerSample, ifd.BigEndian);
                    }
                }

                int visibleWidth = Math.Min(layout.ChunkWidth, width - cx);
                int visibleRows = Math.Min(rows, layout.Height - cy);
                for (int r = 0; r < visibleRows; r++)
                {
                    for (int p = 0; p < planes; p++)
                    {
                        ReadOnlySpan<byte> row = buffer.AsSpan(p * chunkBytes + r * rowBytes, rowBytes);
                        if (layout.Separate)
                        {
                            Unpack(row, visibleWidth, layout.BitsPerSample, ifd.BigEndian, samples, p, spp);
                        }
                        else
                        {
                            Unpack(row, visibleWidth * spp, layout.BitsPerSample, ifd.BigEndian, samples, 0, 1);
                        }
                    }

                    converter.Convert(samples.AsSpan(0, visibleWidth * spp), pixels.Slice((cy + r) * width + cx, visibleWidth));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<ushort>.Shared.Return(samples);
        }
    }

    private static void Decompress(ReadOnlyMemory<byte> data, uint offset, uint count, TiffLayout layout, LzwDecoder? lzw, Span<byte> output)
    {
        if (offset > data.Length)
        {
            throw TiffDecoder.Error($"A TIFF strip or tile starts at offset {offset}, outside the file.");
        }

        // As libtiff does for memory-mapped files, a byte count past the end of the file is cut to the file.
        ReadOnlyMemory<byte> raw = data.Slice((int)offset, (int)Math.Min(count, (uint)(data.Length - (int)offset)));
        byte[]? reversed = null;
        try
        {
            if (layout.ReverseBits)
            {
                reversed = ArrayPool<byte>.Shared.Rent(raw.Length);
                ReverseBits(raw.Span, reversed);
                raw = reversed.AsMemory(0, raw.Length);
            }

            switch (layout.Compression)
            {
                case TiffLayout.CompressionNone:
                    if (raw.Length < output.Length)
                    {
                        throw TiffDecoder.Error("A TIFF strip or tile is truncated.");
                    }

                    raw.Span[..output.Length].CopyTo(output);
                    break;
                case TiffLayout.CompressionPackBits:
                    PackBitsDecoder.Decode(raw.Span, output);
                    break;
                case TiffLayout.CompressionLzw:
                    lzw!.Decode(raw.Span, output);
                    break;
                default:
                    DeflateDecoder.Decode(raw, output);
                    break;
            }
        }
        finally
        {
            if (reversed is not null)
            {
                ArrayPool<byte>.Shared.Return(reversed);
            }
        }
    }

    // port: libtiff/tif_swab.c TIFFReverseBits for FillOrder 2, applied to the raw data before decompression.
    private static void ReverseBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint b = source[i];
            b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4);
            b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2);
            destination[i] = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        }
    }

    /// <summary>Unpacks <paramref name="count"/> MSB-first samples into every <paramref name="step"/>-th slot from <paramref name="first"/>.</summary>
    private static void Unpack(ReadOnlySpan<byte> row, int count, int bits, bool bigEndian, Span<ushort> samples, int first, int step)
    {
        switch (bits)
        {
            case 8:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    samples[d] = row[i];
                }

                break;
            case 16:
                for (int i = 0, d = first; i < count; i++, d += step)
                {
                    ReadOnlySpan<byte> pair = row.Slice(i * 2, 2);
                    samples[d] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(pair) : BinaryPrimitives.ReadUInt16LittleEndian(pair);
                }

                break;
            default:
                int mask = (1 << bits) - 1;
                for (int i = 0, d = first, bit = 0; i < count; i++, d += step, bit += bits)
                {
                    samples[d] = (ushort)((row[bit >> 3] >> (8 - bits - (bit & 7))) & mask);
                }

                break;
        }
    }
}
