using System.Buffers;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Decodes strips or tiles one at a time: every plane of a chunk is decompressed and un-predicted into a pooled
/// buffer, then a <see cref="TiffChunkConverter"/> writes its visible pixels, so planar images need no full copy.
/// JPEG chunks are decoded by the configuration's JPEG codec instead (<see cref="TiffJpeg"/>).
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
        if (!ifd.TryGet(offsetsTag, out TiffEntry offsets) || offsets.Count < chunks || !TiffEntry.IsInteger(offsets.Type))
        {
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {(layout.Tiled ? "tile" : "strip")} offsets.");
        }

        // port: libtiff/tif_dirread.c EstimateStripByteCounts, for uncompressed data only: each chunk is assumed full.
        bool hasCounts = ifd.TryGet(countsTag, out TiffEntry counts) && TiffEntry.IsInteger(counts.Type);
        if (hasCounts ? counts.Count < chunks : layout.Compression != TiffLayout.CompressionNone)
        {
            throw TiffDecoder.Error($"The TIFF image needs {chunks} {(layout.Tiled ? "tile" : "strip")} byte counts.");
        }

        int width = layout.Width;
        if (layout.IsJpeg)
        {
            var jpeg = new TiffJpeg(bytes, ifd, layout);
            for (long c = 0; c < perPlane; c++)
            {
                context.ThrowIfCancellationRequested();
                (int cx, int cy, int visibleWidth, int visibleRows) = Region(layout, c);
                ReadOnlyMemory<byte> raw = Raw(data, ifd.GetULong(bytes, offsets, (int)c), ifd.GetULong(bytes, counts, (int)c));
                jpeg.Decode(raw, context, visibleWidth, visibleRows, pixels[(cy * width + cx)..], width);
            }

            return;
        }

        TiffChunkConverter converter = TiffChunkConverter.Create(bytes, ifd, layout);
        LzwDecoder? lzw = layout.Compression == TiffLayout.CompressionLzw ? new LzwDecoder() : null;
        FaxDecoder? fax = layout.IsCcitt ? new FaxDecoder(bytes, ifd, layout) : null;
        int rowBytes = (int)layout.RowBytes;
        int planeBytes = (int)layout.ChunkBytes(layout.ChunkHeight);
        int planes = layout.Planes, spp = layout.SamplesPerPixel;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(planeBytes * planes);
        try
        {
            for (long c = 0; c < perPlane; c++)
            {
                context.ThrowIfCancellationRequested();
                (int cx, int cy, int visibleWidth, int visibleRows) = Region(layout, c);
                int rows = layout.Tiled ? layout.ChunkHeight : visibleRows;
                for (int p = 0; p < planes; p++)
                {
                    int index = (int)(p * perPlane + c);
                    Span<byte> output = buffer.AsSpan(p * planeBytes, (int)layout.ChunkBytes(rows));
                    ulong offset = ifd.GetULong(bytes, offsets, index);
                    ulong count = hasCounts ? ifd.GetULong(bytes, counts, index) : (ulong)output.Length;
                    Decompress(Raw(data, offset, count), layout, lzw, fax, output);
                    if (layout.EffectivePredictor == 2)
                    {
                        HorizontalPredictor.Decode(output, rowBytes, layout.Separate ? 1 : spp, layout.BitsPerSample, ifd.BigEndian);
                    }
                    else if (layout.EffectivePredictor == 3)
                    {
                        FloatingPointPredictor.Decode(output, rowBytes, layout.Separate ? 1 : spp, layout.BitsPerSample / 8);
                    }
                }

                converter.Convert(buffer, planeBytes, visibleWidth, visibleRows, pixels[(cy * width + cx)..], width);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (int X, int Y, int Width, int Rows) Region(TiffLayout layout, long chunk)
    {
        int cx = (int)(chunk % layout.ChunksAcross) * layout.ChunkWidth;
        int cy = (int)(chunk / layout.ChunksAcross) * layout.ChunkHeight;
        return (cx, cy, Math.Min(layout.ChunkWidth, layout.Width - cx), Math.Min(layout.ChunkHeight, layout.Height - cy));
    }

    // As libtiff does for memory-mapped files, a byte count past the end of the file is cut to the file.
    private static ReadOnlyMemory<byte> Raw(ReadOnlyMemory<byte> data, ulong offset, ulong count)
    {
        if (offset > (ulong)data.Length)
        {
            throw TiffDecoder.Error($"A TIFF strip or tile starts at offset {offset}, outside the file.");
        }

        ulong available = (ulong)data.Length - offset;
        return data.Slice((int)offset, (int)Math.Min(count, available));
    }

    private static void Decompress(ReadOnlyMemory<byte> raw, TiffLayout layout, LzwDecoder? lzw, FaxDecoder? fax, Span<byte> output)
    {
        byte[]? reversed = null;
        try
        {
            // CCITT reads the fill order itself, as libtiff sets TIFF_NOBITREV for it.
            if (layout.ReverseBits && fax is null)
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
                case TiffLayout.CompressionDeflate or TiffLayout.CompressionDeflateOld:
                    DeflateDecoder.Decode(raw, output);
                    break;
                default:
                    fax!.Decode(raw.Span, output);
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
    public static void ReverseBits(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint b = source[i];
            b = ((b & 0xF0) >> 4) | ((b & 0x0F) << 4);
            b = ((b & 0xCC) >> 2) | ((b & 0x33) << 2);
            destination[i] = (byte)(((b & 0xAA) >> 1) | ((b & 0x55) << 1));
        }
    }
}
