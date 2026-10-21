# Decoding TIFF

Examples assume `TiffFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("scan.tif");                                // first page, straight-alpha Rgba32
ImageInfo info = Image.Identify("scan.tif");                               // size, bit depth and page count
ImageMetadata metadata = Image.ReadMetadata("scan.tif");                   // ICC, EXIF, XMP, resolution
```

`Identify` and `ReadMetadata` read only the directories, never the pixel data.

## Pages

A TIFF file can hold several pages (a fax, a scanned document). The decoder reads one page per call; the first unless
`TiffDecoderOptions.Page` selects another:

```csharp
ImageInfo info = Image.Identify("fax.tif");
for (int page = 0; page < info.ContainerItemCount; page++)
{
    var options = new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = page }] };
    using Image image = Image.Load("fax.tif", options);
    image.Save($"fax-{page + 1}.png");
}
```

- `ContainerItemCount` is the number of full-resolution pages; `FrameCount` is always 1.
- Reduced-resolution pages (thumbnails, previews; NewSubfileType bit 0 or SubfileType 2) are skipped and not counted,
  and SubIFDs (tag 330) are not read.
- `Load`, `Identify` and `ReadMetadata` all describe the selected page, including its own size and metadata.
- A negative page throws `ArgumentOutOfRangeException` when the options are created; a page beyond the last throws
  `ImageFormatException`.

## What is decoded

| | Supported |
| --- | --- |
| Container | classic TIFF and BigTIFF, little- and big-endian; strips or tiles; contiguous or planar samples |
| Compression | none, PackBits, LZW (also the old LSB-first variant), Deflate (8 and 32946), JPEG (7), WebP (50001), CCITT RLE, RLEW, Group 3 (1D and 2D) and Group 4; `FillOrder` 2 |
| Predictor | horizontal (2) and floating point (3), with LZW and Deflate |
| Greyscale | 1, 2, 4, 8 and 16 bits, black or white is zero |
| Palette | 1, 2, 4 and 8 bits |
| RGB, CMYK | 8 and 16 bits |
| YCbCr | 8 bits, with subsampling (port of libtiff `tif_color.c`) |
| Floating point | 32-bit greyscale and RGB |
| Alpha | `ExtraSamples` associated (unpremultiplied) or unassociated, following Pillow's rules |

Not supported, with `NotSupportedException`: old-style JPEG (6), JBIG, LZMA, Zstandard, LERC, JPEG 2000, PixarLog,
LogLuv, CIE Lab, transparency masks, signed samples, floating point other than 32 bits, JPEG other than contiguous 8-bit
greyscale, RGB or YCbCr (so no alpha), and WebP with a photometric interpretation other than RGB. As in libtiff, WebP
other than 3 or 4 contiguous 8-bit samples is an error (`ImageFormatException`). Old-style JPEG, Zstandard and JBIG are discussed under Deferred in the
[plan](https://github.com/art-ix/PictureSharp.Tiff/blob/main/plan.md#deferred).

### Conversion to 8 bits

`Image` holds 8-bit straight-alpha RGBA, so deeper samples are reduced:

- 16-bit samples and 16-bit colour map entries: `round(v / 257)`.
- 32-bit floating point: clamped to [0, 1], then `(int)(v * 255 + 0.5)`.
- Associated alpha is unpremultiplied at the source depth, before the reduction.
- CMYK is converted as Pillow converts it (no colour management); an ICC profile is kept in the metadata.

### JPEG and WebP strips

Each JPEG or WebP strip or tile is a complete stream that the configuration's own JPEG or WebP codec decodes (through
`ImageDecodeContext.DecodeEmbeddedPart`), within the strip's or tile's size. `JPEGTables` shared by the strips are
merged into each stream as libtiff does. JPEG output therefore matches the PictureSharp JPEG decoder exactly and libjpeg
within the usual IDCT tolerance; WebP output matches libwebp exactly, lossy included.

## Options and limits

- `MaxPixels`, `MaxInputBytes` and `MaxMetadataBytes` are checked before allocating; `MaxPixels` covers the image once,
  also for JPEG and WebP strips.
- Cancellation is checked between strips and tiles.
- `TargetSize`, `ApplyExifOrientation`, `ColorConversion` and `IgnoreMetadata` behave as for built-in formats. The
  orientation comes from the Orientation tag (274).
- Decoding is single-threaded; `MaxDegreeOfParallelism` has no effect.

## Errors

- Corrupt or truncated data throws `ImageFormatException`: offsets outside the file, IFD loops, missing required tags,
  short strips. A byte count past the end of the file is cut to the file, as libtiff does for memory-mapped files, and
  data after the end of a compressed strip is ignored.
- Valid files using a feature listed above as not supported throw `NotSupportedException`.
- No other exception type escapes for malformed input; this is tested with mutated and truncated corpus files.

## Speed

A 24 MP RGB photo (5712 x 4284) written by this encoder, read to RGBA on one thread (Intel Core i5-12500H, .NET 10,
best of 5 runs), against libtiff 4.5.1 `TIFFReadRGBAImage` through ctypes:

| Compression | PictureSharp.Tiff | libtiff 4.5.1 |
| --- | ---: | ---: |
| None | 98 ms | 45 ms |
| LZW + predictor | 428 ms | 268 ms |
| Deflate + predictor | 406 ms | 148 ms ¹ |

¹ libtiff as Ubuntu builds it decompresses whole strips with libdeflate; PictureSharp uses the runtime's zlib.

The probes are in [benchmarks](https://github.com/art-ix/PictureSharp.Tiff/tree/main/benchmarks).

## Validation

Every corpus file (written by libtiff 4.5.1 and ImageMagick 6, both byte orders, classic and BigTIFF) decodes exactly
to the pixels of Pillow 10.2.0, of libtiff `TIFFReadRGBAImage` where its conversion is exact, or of the rules above;
every page of the multi-page files is checked. The corpus is regenerated with `tests/assets/generate_corpus.py`.
