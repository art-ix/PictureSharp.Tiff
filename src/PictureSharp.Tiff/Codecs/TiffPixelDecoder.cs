using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using PictureSharp.Formats.Tiff.Container;

namespace PictureSharp.Formats.Tiff.Codecs;

/// <summary>
/// Decodes strips or tiles one chunk index at a time: every plane of a chunk is decompressed and un-predicted into a
/// pooled buffer, then a <see cref="TiffChunkConverter"/> writes its visible pixels, so planar images need no full copy.
/// Contiguous JPEG chunks are decoded by the configuration's JPEG codec instead (<see cref="TiffJpeg"/>), the planes of
/// planar JPEG chunks by it into the buffer as one-component images, and WebP chunks by its WebP
/// codec into the same buffer (<see cref="TiffWebP"/>). Uncompressed contiguous chunks convert straight from the file.
/// Chunks write disjoint rectangles of the image, so contiguous ranges of them decode in parallel, each thread with its
/// own <see cref="ChunkDecoder"/>; an image in one strip or tile is un-predicted and converted in parallel row bands
/// after it is decompressed. The pixels, and the exception of a corrupt file (the one of the lowest chunk), do not depend
/// on the degree.
/// </summary>
internal static class TiffPixelDecoder
{
    /// <summary>Below this many pixels the automatic degree (-1) decodes on one thread, as the core's parallel loops do.</summary>
    private const long ParallelPixels = 1 << 18;

    private const int RangesPerThread = 4;

    /// <summary>Decodes into the pixels of <paramref name="image"/>, 8- or 16-bit; JPEG and WebP chunks are always 8-bit.</summary>
    public static void Decode(ReadOnlyMemory<byte> data, TiffDirectory ifd, TiffLayout layout, Image image, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        long perPlane = layout.ChunksPerPlane;
        var chunkTable = TiffChunks.Read(bytes, ifd, layout);
        bool wide = image.PixelType == PixelType.Rgba64;
        var job = new Job(data, ifd, layout, chunkTable, wide ? default : image.GetPixelMemory<Rgba32>(), context,
            layout.IsJpeg && !layout.Separate ? null : TiffChunkConverter.Create(bytes, ifd, layout),
            layout.IsJpeg ? new TiffJpeg(bytes, ifd, layout) : null)
        {
            Wide = wide ? image.GetPixelMemory<Rgba64>() : default,
        };
        if (layout.Compression == TiffLayout.CompressionLzw)
        {
            job = job with { LzwCompat = LzwCompat(job) };
        }

        int threads = Threads(layout, context.Options.MaxDegreeOfParallelism);
        int degree = Degree(layout, threads);
        if (perPlane == 1 && !layout.IsJpeg && !layout.IsSubsampledYCbCr)
        {
            job = job with { Bands = Math.Min(threads, layout.Height) };
        }

        if (degree <= 1)
        {
            using var decoder = new ChunkDecoder(job);
            for (long c = 0; c < perPlane; c++)
            {
                context.ThrowIfCancellationRequested();
                decoder.Decode(c);
            }

            return;
        }

        DecodeParallel(job, perPlane, degree);
    }

    /// <summary>The threads <paramref name="requested"/> means for this image: 1 for a small one at the automatic degree.</summary>
    private static int Threads(TiffLayout layout, int requested) =>
        requested == 1 || (requested == -1 && (Environment.ProcessorCount == 1 || (long)layout.Width * layout.Height < ParallelPixels)) ? 1
        : requested == -1 ? Environment.ProcessorCount
        : requested;

    /// <summary>
    /// The threads decoding chunks: at most one per chunk, and few enough that their buffers (a chunk of every plane,
    /// plus a decoded part for JPEG and WebP) together stay within the size of the image itself.
    /// </summary>
    private static int Degree(TiffLayout layout, int threads)
    {
        long perThread = ((layout.IsJpeg && !layout.Separate) || InPlace(layout) ? 0 : layout.ChunkBytes(layout.ChunkHeight) * layout.Planes)
            + (layout.IsJpeg || layout.Compression == TiffLayout.CompressionWebP ? (long)layout.ChunkWidth * layout.ChunkHeight * 4 : 0);
        long degree = Math.Min(threads, layout.ChunksPerPlane);
        return (int)Math.Clamp(Math.Min(degree, (long)layout.Width * layout.Height * 4 / Math.Max(1, perThread)), 1, int.MaxValue);
    }

    /// <summary>Whether chunks are converted where they are in the file: uncompressed, contiguous, in MSB-first fill order.</summary>
    private static bool InPlace(TiffLayout layout) =>
        layout.Compression == TiffLayout.CompressionNone && !layout.ReverseBits && layout.Planes == 1;

    /// <summary>
    /// Decodes about <see cref="RangesPerThread"/> contiguous ranges of chunks per thread. A range stops at its first
    /// failure, and ranges past the lowest failing chunk known so far stop early; the ranges below it still finish, so
    /// the exception thrown is the one a single thread would meet first. Cancellation stops every range.
    /// </summary>
    private static void DecodeParallel(Job job, long chunks, int degree)
    {
        int ranges = (int)Math.Min(chunks, (long)degree * RangesPerThread);
        var failures = new (long Chunk, ExceptionDispatchInfo Error)?[ranges];
        long lowest = long.MaxValue;
        var decoders = new ConcurrentBag<ChunkDecoder>();
        try
        {
            Parallel.For(0, ranges, new ParallelOptions { MaxDegreeOfParallelism = degree },
                () => decoders.TryTake(out ChunkDecoder? decoder) ? decoder : new ChunkDecoder(job),
                (range, _, decoder) =>
                {
                    long end = chunks * (range + 1) / ranges;
                    for (long c = chunks * range / ranges; c < end && c < Volatile.Read(ref lowest); c++)
                    {
                        try
                        {
                            job.Context.ThrowIfCancellationRequested();
                            decoder.Decode(c);
                        }
                        catch (Exception e)
                        {
                            failures[range] = (c, ExceptionDispatchInfo.Capture(e));
                            for (long seen = Volatile.Read(ref lowest); c < seen; seen = Volatile.Read(ref lowest))
                            {
                                if (Interlocked.CompareExchange(ref lowest, c, seen) == seen)
                                {
                                    break;
                                }
                            }

                            break;
                        }
                    }

                    return decoder;
                },
                decoders.Add);
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
        }
        finally
        {
            foreach (ChunkDecoder decoder in decoders)
            {
                decoder.Dispose();
            }
        }

        (long Chunk, ExceptionDispatchInfo Error)? first = null;
        foreach ((long Chunk, ExceptionDispatchInfo Error)? failure in failures)
        {
            if (failure is { Error.SourceException: OperationCanceledException })
            {
                failure.Value.Error.Throw();
            }

            if (failure is not null && (first is null || failure.Value.Chunk < first.Value.Chunk))
            {
                first = failure;
            }
        }

        first?.Error.Throw();
    }

    // port: libtiff/tif_lzw.c LZWPreDecode picks the old-style decoder once per image, from the first strip it decodes:
    // chunk 0 of the first plane. Chosen up front, so that every thread's decoder uses the same one.
    private static bool LzwCompat(Job job)
    {
        try
        {
            ReadOnlyMemory<byte> raw = Raw(job.Data, job.Chunks.Offset(job.Data.Span, 0), job.Chunks.Count(job.Data.Span, 0, (int)job.Layout.ChunkBytes(job.Layout.ChunkHeight)));
            Span<byte> head = stackalloc byte[Math.Min(raw.Length, 2)];
            if (job.Layout.ReverseBits)
            {
                ReverseBits(raw.Span[..head.Length], head);
            }
            else
            {
                raw.Span[..head.Length].CopyTo(head);
            }

            return LzwDecoder.IsCompat(head);
        }
        catch (ImageFormatException)
        {
            // Chunk 0 cannot be read, so it fails first whatever decoder the others use.
            return false;
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

    private static void Decompress(ReadOnlyMemory<byte> raw, TiffLayout layout, LzwDecoder? lzw, FaxDecoder? fax, ImageDecodeContext context,
        int rows, Span<byte> output)
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
                    CheckLength(raw, output.Length);
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
                case TiffLayout.CompressionNeXT:
                    NeXTDecoder.Decode(raw.Span, output, (int)layout.RowBytes, layout.ChunkWidth);
                    break;
                case TiffLayout.CompressionThunderScan:
                    ThunderScanDecoder.Decode(raw.Span, output, (int)layout.RowBytes, layout.ChunkWidth);
                    break;
                case TiffLayout.CompressionWebP:
                    TiffWebP.Decode(raw, context, layout, rows, output);
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

    private static void CheckLength(ReadOnlyMemory<byte> raw, int length)
    {
        if (raw.Length < length)
        {
            throw TiffDecoder.Error("A TIFF strip or tile is truncated.");
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

    /// <summary>
    /// What every thread shares: the file, the image and the read-only converter or JPEG tables. A 16-bit image has its
    /// pixels in <see cref="Wide"/> and none in <see cref="Pixels"/>.
    /// </summary>
    private sealed record Job(ReadOnlyMemory<byte> Data, TiffDirectory Ifd, TiffLayout Layout, TiffChunks Chunks,
        Memory<Rgba32> Pixels, ImageDecodeContext Context, TiffChunkConverter? Converter, TiffJpeg? Jpeg)
    {
        public Memory<Rgba64> Wide { get; init; }

        public bool LzwCompat { get; init; }

        /// <summary>The threads converting row bands of an image in a single strip or tile; 1 otherwise.</summary>
        public int Bands { get; init; } = 1;
    }

    /// <summary>The state one thread needs: the chunk buffer and the decompressors that keep tables between calls.</summary>
    private sealed class ChunkDecoder : IDisposable
    {
        private readonly Job job;
        private readonly byte[] buffer;
        private readonly LzwDecoder? lzw;
        private readonly FaxDecoder? fax;
        private readonly int planeBytes;

        public ChunkDecoder(Job job)
        {
            this.job = job;
            TiffLayout layout = job.Layout;
            planeBytes = (int)layout.ChunkBytes(layout.ChunkHeight);
            buffer = (layout.IsJpeg && !layout.Separate) || InPlace(layout) ? [] : ArrayPool<byte>.Shared.Rent(planeBytes * layout.Planes);
            lzw = layout.Compression == TiffLayout.CompressionLzw ? new LzwDecoder(job.LzwCompat) : null;
            fax = layout.IsCcitt ? new FaxDecoder(job.Data.Span, job.Ifd, layout) : null;
        }

        /// <summary>Decodes chunk <paramref name="c"/> of every plane into its rectangle of the image.</summary>
        public void Decode(long c)
        {
            (ReadOnlyMemory<byte> data, TiffDirectory ifd, TiffLayout layout) = (job.Data, job.Ifd, job.Layout);
            ReadOnlySpan<byte> bytes = data.Span;
            int width = layout.Width;
            (int cx, int cy, int visibleWidth, int visibleRows) = Region(layout, c);
            if (job.Jpeg is not null && !layout.Separate)
            {
                ReadOnlyMemory<byte> raw = Raw(data, job.Chunks.Offset(bytes, c), job.Chunks.Count(bytes, c, (int)layout.ChunkBytes(layout.ChunkHeight)));
                job.Jpeg.Decode(raw, job.Context, visibleWidth, visibleRows, job.Pixels.Span[(cy * width + cx)..], width);
                return;
            }

            int rows = layout.Tiled ? layout.ChunkHeight : visibleRows;
            int length = (int)layout.ChunkBytes(rows);
            ReadOnlyMemory<byte> chunk = buffer;
            if (InPlace(layout))
            {
                // Read in place: no predictor applies to uncompressed data and the converter only reads.
                chunk = Raw(data, job.Chunks.Offset(bytes, c), job.Chunks.Count(bytes, c, length));
                CheckLength(chunk, length);
            }
            else
            {
                for (int p = 0; p < layout.Planes; p++)
                {
                    int index = (int)(p * layout.ChunksPerPlane + c);
                    Span<byte> output = buffer.AsSpan(p * planeBytes, length);
                    ulong offset = job.Chunks.Offset(bytes, index);
                    ulong count = job.Chunks.Count(bytes, index, output.Length);
                    if (job.Jpeg is not null)
                    {
                        job.Jpeg.DecodePlane(Raw(data, offset, count), job.Context, visibleWidth, visibleRows, output);
                    }
                    else
                    {
                        Decompress(Raw(data, offset, count), layout, lzw, fax, job.Context, rows, output);
                    }
                }
            }

            if (job.Bands > 1)
            {
                DecodeBands(chunk, cx, cy, visibleWidth, visibleRows);
                return;
            }

            for (int p = 0; p < layout.Planes && buffer.Length > 0; p++)
            {
                Unpredict(buffer.AsSpan(p * planeBytes, length));
            }

            Convert(chunk.Span, cx, cy, visibleWidth, visibleRows);
        }

        /// <summary>Converts <paramref name="rows"/> rows of a chunk into the image at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        private void Convert(ReadOnlySpan<byte> chunk, int x, int y, int visibleWidth, int rows)
        {
            int width = job.Layout.Width, at = y * width + x;
            if (job.Wide.IsEmpty)
            {
                job.Converter!.Convert(chunk, planeBytes, visibleWidth, rows, job.Pixels.Span[at..], width);
            }
            else
            {
                job.Converter!.Convert(chunk, planeBytes, visibleWidth, rows, job.Wide.Span[at..], width);
            }
        }

        /// <summary>
        /// Un-predicts and converts the visible rows of the only chunk in about <see cref="RangesPerThread"/> bands per
        /// thread. Both work row by row, and packed YCbCr, whose blocks span rows, never comes here.
        /// </summary>
        private void DecodeBands(ReadOnlyMemory<byte> chunk, int cx, int cy, int visibleWidth, int visibleRows)
        {
            TiffLayout layout = job.Layout;
            int rowBytes = (int)layout.RowBytes, width = layout.Width;
            int bands = Math.Min(visibleRows, job.Bands * RangesPerThread);
            try
            {
                Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = job.Bands }, band =>
                {
                    job.Context.ThrowIfCancellationRequested();
                    int first = (int)((long)visibleRows * band / bands);
                    int rows = (int)((long)visibleRows * (band + 1) / bands) - first;
                    for (int p = 0; p < layout.Planes && buffer.Length > 0; p++)
                    {
                        Unpredict(buffer.AsSpan(p * planeBytes + first * rowBytes, rows * rowBytes));
                    }

                    Convert(chunk.Span[(first * rowBytes)..], cx, cy + first, visibleWidth, rows);
                });
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
            }
        }

        private void Unpredict(Span<byte> plane)
        {
            TiffLayout layout = job.Layout;
            int rowBytes = (int)layout.RowBytes, stride = layout.Separate ? 1 : layout.SamplesPerPixel;
            if (layout.EffectivePredictor == 2)
            {
                HorizontalPredictor.Decode(plane, rowBytes, stride, layout.BitsPerSample, job.Ifd.BigEndian);
            }
            else if (layout.EffectivePredictor == 3)
            {
                FloatingPointPredictor.Decode(plane, rowBytes, stride, layout.BitsPerSample / 8);
            }
        }

        public void Dispose()
        {
            if (buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
