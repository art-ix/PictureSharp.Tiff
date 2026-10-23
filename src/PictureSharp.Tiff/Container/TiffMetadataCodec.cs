using System.Buffers.Binary;

namespace PictureSharp.Formats.Tiff.Container;

/// <summary>
/// Image.ReadMetadata without decoding pixels: the blocks Decode sets, read from the selected page. Image.EditMetadata
/// appends a new IFD for that page and relinks it, leaving the image data and the other pages as they are.
/// </summary>
internal sealed class TiffMetadataCodec : IImageFormatMetadataCodec
{
    public static TiffMetadataCodec Instance { get; } = new();

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> bytes = data.Span;
        var file = TiffFile.Open(bytes, TiffDecoder.Page(context.Options), countPages: false);
        var metadata = new ImageMetadata();
        TiffMetadataReader.Read(bytes, file.Image, metadata, context);
        return metadata;
    }

    /// <summary>
    /// Copies the file and appends a copy of the page's IFD in which the changed blocks replace their tags, then points
    /// the link to the old IFD at it. Unchanged tags keep their values, so the strips, tiles and other pages stay valid.
    /// Whatever the file reaches past its end would land in the appended bytes, so it is made explicit or cut off:
    /// estimated byte counts are written out, EXIF and GPS IFDs that are not fully in the file are rewritten from the
    /// block read, and IFD links that lead outside the file end the chain.
    /// </summary>
    // port: libtiff/tif_dirwrite.c TIFFRewriteDirectory: the rewritten directory goes at the end of the file and the
    // link that pointed to the old one (in the header or the previous directory) is patched; the old one is left unused.
    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context)
    {
        if (metadata.Gamma != original.Gamma || metadata.Chromaticities != original.Chromaticities
            || metadata.SrgbRenderingIntent != original.SrgbRenderingIntent)
        {
            throw new NotSupportedException("TIFF stores colours only as an ICC profile; gamma, chromaticities and sRGB intent are PNG chunks.");
        }

        ReadOnlySpan<byte> bytes = data.Span;
        int page = TiffDecoder.Page(context.Options);
        var file = TiffFile.Open(bytes, page, countPages: true);
        TiffDirectory image = file.Image;
        TiffHeader header = image.Header;
        byte[]? exif = metadata.Exif, icc = metadata.IccProfile, xmp = metadata.Xmp;
        bool exifChanged = !Same(original.Exif, exif) || SubdirectoriesOutsideFile(bytes, image);
        bool iccChanged = !Same(original.IccProfile, icc);
        bool xmpChanged = !Same(original.Xmp, xmp);
        bool resolutionChanged = metadata.HorizontalResolution != original.HorizontalResolution
            || metadata.VerticalResolution != original.VerticalResolution;

        // The new IFD goes after the end of the file: data that should lie there would become the new IFD's bytes,
        // and entries already outside the file could not be copied.
        if (image.OutsideFile > 0)
        {
            throw TiffDecoder.Error("The TIFF page has tags whose values lie outside the file, so its metadata cannot be rewritten.");
        }

        (TiffLayout Layout, (ulong Offset, ulong Count)[] Ranges, bool Estimated)? chunks = Chunks(bytes, image);
        ulong fileLength = (ulong)bytes.Length;
        if (chunks?.Ranges is { } ranges && Array.Exists(ranges, r => r.Offset > fileLength || (r.Count > 0 && r.Count > fileLength - r.Offset)))
        {
            throw TiffDecoder.Error("The TIFF page has strips or tiles that extend past the end of the file, so its metadata cannot be rewritten.");
        }

        // Estimated byte counts depend on the length of the file, which the new IFD changes; the copy stores them.
        bool countsReplaced = chunks?.Estimated == true;
        var ifd = new TiffIfdWriter(header.BigEndian, header.BigTiff);
        foreach (TiffEntry e in image.Entries)
        {
            bool replaced = (exifChanged && (TiffExifBuilder.Ifd0Tags.Contains(e.Tag) || e.Tag is TiffTag.ExifIfd or TiffTag.GpsIfd))
                || (countsReplaced && e.Tag is TiffTag.StripByteCounts or TiffTag.TileByteCounts)
                || (iccChanged && e.Tag == TiffTag.IccProfile)
                || (xmpChanged && e.Tag == TiffTag.Xmp)
                || (resolutionChanged && e.Tag is TiffTag.XResolution or TiffTag.YResolution or TiffTag.ResolutionUnit);
            if (!replaced)
            {
                ifd.AddCopy(e.Tag, e.Type, e.Count, bytes.Slice(e.ValueOffset, (int)e.Count * e.Size(header.BigTiff)), header.BigEndian);
            }
        }

        if (countsReplaced)
        {
            ifd.AddOffsets(chunks!.Value.Layout.Tiled ? TiffTag.TileByteCounts : TiffTag.StripByteCounts, [.. chunks.Value.Ranges.Select(r => r.Count)]);
        }

        if (exifChanged && exif is { Length: > 0 })
        {
            TiffExifWriter.AddTo(ifd, exif, software: true);
        }

        if (iccChanged && icc is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.IccProfile, TiffEntry.Undefined, icc);
        }

        if (xmpChanged && xmp is { Length: > 0 })
        {
            ifd.AddBytes(TiffTag.Xmp, TiffEntry.Byte, xmp);
        }

        if (resolutionChanged)
        {
            ifd.AddResolution(metadata.HorizontalResolution, metadata.VerticalResolution);
        }

        context.ThrowIfCancellationRequested();
        long offset = (bytes.Length + 1L) & ~1L;
        long length = offset + ifd.Size;
        if (!header.BigTiff && length > uint.MaxValue)
        {
            throw new NotSupportedException("The TIFF file would exceed 4 GiB, which classic TIFF cannot address.");
        }

        if (length > Array.MaxLength)
        {
            throw new NotSupportedException("The rewritten TIFF file would exceed the largest array.");
        }

        byte[] result = new byte[length];
        bytes.CopyTo(result);
        ifd.ToArray((ulong)offset, Leads(bytes, header, image.Next) ? image.Next : 0).CopyTo(result.AsSpan((int)offset));
        WriteOffset(result, file.Link, (ulong)offset, header);
        EndLinksOutsideFile(bytes, result, header);

        // A chain that loops back to the old IFD (libtiff's test_ifd_loop_* files) would reach it again after the new
        // one and show the page twice; such a link follows the new IFD instead, so the chain keeps its pages.
        var visited = new HashSet<ulong>();
        for (ulong at = (ulong)offset; at != 0 && visited.Count < TiffFile.MaxDirectories && visited.Add(at);)
        {
            if (!TiffDirectory.TryScan(result, header, at, out _, out ulong next, out long nextLink))
            {
                break;
            }

            if (next == file.ImageOffset)
            {
                WriteOffset(result, nextLink, (ulong)offset, header);
                break;
            }

            at = next;
        }

        // The strips and tiles must read the same bytes as before: the same ranges, none of them holding a patched link.
        if (chunks is { } kept && !SameChunks(bytes, result, kept.Ranges, Chunks(result, TiffDirectory.Parse(result, header, (ulong)offset))?.Ranges))
        {
            throw TiffDecoder.Error("The TIFF page's strips or tiles overlap the links its metadata rewrite has to change.");
        }

        // A link stored where another one lies (an IFD that overlaps the header) could have been overwritten: the page
        // must now be the new IFD, in a chain of as many pages as before.
        if (!Relinked(result, page, (ulong)offset, file.Pages))
        {
            throw TiffDecoder.Error("The TIFF file's IFD links overlap the ones its metadata rewrite has to change.");
        }

        return result;
    }

    private static bool Relinked(byte[] result, int page, ulong offset, int pages)
    {
        try
        {
            var file = TiffFile.Open(result, page, countPages: true);
            return file.ImageOffset == offset && file.Pages == pages;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }

    private static bool SameChunks(ReadOnlySpan<byte> data, byte[] result, (ulong Offset, ulong Count)[] before, (ulong Offset, ulong Count)[]? after)
    {
        if (after is null || !before.AsSpan().SequenceEqual(after))
        {
            return false;
        }

        foreach ((ulong offset, ulong count) in before)
        {
            int length = (int)Math.Min(count, (ulong)data.Length - offset);
            if (!data.Slice((int)offset, length).SequenceEqual(result.AsSpan((int)offset, length)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The strips or tiles as the decoder reads them, or null when the page has none it can read.</summary>
    private static (TiffLayout Layout, (ulong Offset, ulong Count)[] Ranges, bool Estimated)? Chunks(ReadOnlySpan<byte> data, TiffDirectory ifd)
    {
        try
        {
            TiffLayout layout = TiffLayout.Read(data, ifd);
            return (layout, TiffChunks.Ranges(data, ifd, layout, out bool estimated), estimated);
        }
        catch (Exception e) when (e is ImageFormatException or NotSupportedException)
        {
            // The page does not decode before the rewrite and, with the same tags, not after it either.
            return null;
        }
    }

    /// <summary>Whether an EXIF, GPS or Interoperability IFD the page points to is not entirely inside the file.</summary>
    private static bool SubdirectoriesOutsideFile(ReadOnlySpan<byte> data, TiffDirectory image)
    {
        foreach (ushort tag in (ReadOnlySpan<ushort>)[TiffTag.ExifIfd, TiffTag.GpsIfd])
        {
            if (!image.TryGet(tag, out _))
            {
                continue;
            }

            if (image.TryParseSubdirectory(data, tag) is not { OutsideFile: 0 } child
                || (tag == TiffTag.ExifIfd && child.TryGet(TiffTag.InteropIfd, out _)
                    && child.TryParseSubdirectory(data, TiffTag.InteropIfd) is not { OutsideFile: 0 }))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="next"/> is an IFD that lies entirely inside <paramref name="data"/>.</summary>
    private static bool Leads(ReadOnlySpan<byte> data, TiffHeader header, ulong next) =>
        next != 0 && TiffDirectory.TryScan(data, header, next, out _, out _, out _);

    /// <summary>
    /// Ends the chain of <paramref name="result"/> where the original's leads outside the original file: such a link
    /// ended it there, but could reach into the appended IFD now.
    /// </summary>
    private static void EndLinksOutsideFile(ReadOnlySpan<byte> data, byte[] result, TiffHeader header)
    {
        var visited = new HashSet<ulong>();
        for (ulong at = header.FirstIfd; visited.Count < TiffFile.MaxDirectories && visited.Add(at);)
        {
            if (!TiffDirectory.TryScan(data, header, at, out _, out ulong next, out long nextLink) || next == 0)
            {
                break;
            }

            if (!Leads(data, header, next))
            {
                WriteOffset(result, nextLink, 0, header);
                break;
            }

            at = next;
        }
    }

    private static void WriteOffset(byte[] file, long position, ulong value, TiffHeader header)
    {
        Span<byte> link = file.AsSpan((int)position, header.OffsetSize);
        if (header.BigTiff)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(link, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(link, (uint)value);
        }

        if (header.BigEndian)
        {
            link.Reverse();
        }
    }

    private static bool Same(byte[]? a, byte[]? b) => (a is null) == (b is null) && (a is null || a.AsSpan().SequenceEqual(b));
}
