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
| Predictor | horizontal (2) for 8-, 16- and 32-bit integers and floating point (3), with LZW and Deflate |
| Greyscale, RGB | unsigned 1 to 16 and 32 bits; signed 8, 16 and 32 bits; black or white is zero for greyscale |
| Palette | 1, 2, 4 and 8 bits |
| CMYK | 8 and 16 bits |
| YCbCr | 8 bits, with subsampling (port of libtiff `tif_color.c`) |
| CIE L*a*b* | 8 and 16 bits, three contiguous samples, with the file's `WhitePoint` (port of libtiff `tif_color.c`) |
| Floating point | 16-bit (half), 32-bit and 64-bit greyscale and RGB |
| Alpha | `ExtraSamples` associated (unpremultiplied) or unassociated, following Pillow's rules |

Not supported, with `NotSupportedException`: old-style JPEG (6), JBIG, LZMA, Zstandard, LERC, JPEG 2000, PixarLog,
LogLuv, ICC L*a*b* (9) and ITU L*a*b* (10), CIE L*a*b* with alpha or separate planes, transparency masks, complex samples, integer samples of 17 to 31 or 64 bits, JPEG other than
8-bit greyscale, RGB, YCbCr or CMYK (alpha only with separate planes; planar YCbCr only without subsampling), and WebP with a photometric interpretation other than RGB. As in libtiff, WebP
other than 3 or 4 contiguous 8-bit samples is an error (`ImageFormatException`). Old-style JPEG, Zstandard and JBIG are discussed under Deferred in the
[plan](https://github.com/art-ix/PictureSharp.Tiff/blob/main/plan.md#deferred).

### Conversion to 8 bits

`Image` holds 8-bit straight-alpha RGBA, so deeper samples are reduced:

- Unsigned b-bit samples: `round(v * 255 / (2^b - 1))`. This is exact for 1, 2, 4 and 8 bits, `round(v / 257)` for
  16 bits and `round(v / 16843009)` for 32 bits; no ties occur. Depths other than 8, 16 and 32 are packed MSB first.
- Signed samples (`SampleFormat` 2) are offset by `2^(b - 1)` and then reduced as unsigned samples of the same depth,
  so the whole range maps linearly: the minimum to 0, zero to 128 (for 8 bits) and the maximum to 255. Negative values
  are not clamped to 0. libtiff's `TIFFReadRGBAImage` reinterprets the bits as unsigned instead.
- 16-bit colour map entries: `round(v / 257)`.
- Floating point: clamped to [0, 1], then `(int)(v * 255 + 0.5)`; NaN becomes 0. 16- and 32-bit samples are computed
  in single precision (a half converts to a float exactly), 64-bit samples in double precision.
- Associated alpha is unpremultiplied at the source depth, before the reduction.
- CMYK is converted as Pillow converts it (no colour management); an ICC profile is kept in the metadata.
- CIE L*a*b* is converted exactly as libtiff's `TIFFReadRGBAImage` converts it: to XYZ relative to the `WhitePoint`
  tag (CIE D50 without it, as Photoshop assumes), then with libtiff's sRGB display matrix and a gamma 2.4 table, in
  single precision. There is no chromatic adaptation, so a D50 white comes out slightly yellow (255, 252, 222); a
  D65 white point gives white.

### JPEG and WebP strips

Each JPEG or WebP strip or tile is a complete stream that the configuration's own JPEG or WebP codec decodes (through
`ImageDecodeContext.DecodeEmbeddedPart`), within the strip's or tile's size. `JPEGTables` shared by the strips are
merged into each stream as libtiff does. JPEG output therefore matches the PictureSharp JPEG decoder exactly and libjpeg
within the usual IDCT tolerance; WebP output matches libwebp exactly, lossy included. CMYK JPEG strips (photometric 5)
hold ink amounts, as libtiff writes them, and are decoded with `JpegCmykStorage.Plain` (PictureSharp 0.7.10), not with
the inverted Adobe polarity of standalone CMYK JPEG files; their RGB follows the same Pillow `cmyk2rgb` rule as
uncompressed CMYK, within 1 of libtiff's `TIFFReadRGBAImage`. Planar JPEG (PlanarConfiguration 2) stores every plane
as a one-component JPEG, decoded without colour conversion and then converted like uncompressed planar samples, so a
plane may also hold alpha; a plane with more than one component is an error (`ImageFormatException`), as in libtiff.

## Options and limits

- `MaxPixels`, `MaxInputBytes` and `MaxMetadataBytes` are checked before allocating; `MaxPixels` covers the image once,
  also for JPEG and WebP strips.
- Cancellation is checked between strips and tiles.
- `TargetSize`, `ApplyExifOrientation`, `ColorConversion` and `IgnoreMetadata` behave as for built-in formats. The
  orientation comes from the Orientation tag (274).
- Strips and tiles decode in parallel, up to `MaxDegreeOfParallelism` threads (-1, the default, is one per processor).
  The automatic degree decodes images under 262,144 pixels on one thread; an explicit degree is used as given. The
  thread count is also capped at the number of strips or tiles and so that the threads' buffers stay within the size
  of the image. An image in a single strip or tile is decompressed on one thread, then un-predicted and converted in
  parallel row bands (except packed YCbCr). Pixels, and the exception a corrupt file throws (the one of its first
  failing strip or tile), are the same for every degree.

## Errors

- Corrupt or truncated data throws `ImageFormatException`: offsets outside the file, IFD loops, missing required tags,
  short strips. A byte count past the end of the file is cut to the file, as libtiff does for memory-mapped files, and
  data after the end of a compressed strip is ignored.
- Valid files using a feature listed above as not supported throw `NotSupportedException`.
- No other exception type escapes for malformed input; this is tested with mutated and truncated corpus files.

## Speed

A 24 MP RGB photo (5712 x 4284) written by this encoder, read to RGBA (Intel Core i5-12500H with 16 threads, .NET 10,
best of 5 runs), against libtiff 4.5.1 `TIFFReadRGBAImage` on one thread through ctypes:

| Compression | 1 thread | 16 threads | libtiff 4.5.1 |
| --- | ---: | ---: | ---: |
| None | 25 ms | 10 ms | 45 ms |
| LZW + predictor | 363 ms | 46 ms | 268 ms |
| Deflate + predictor | 338 ms | 39 ms | 148 ms ¹ |

The same photo in a single strip decodes in 52 / 11 ms uncompressed (1 / 16 threads), but 364 / 307 ms with LZW and
342 / 286 ms with Deflate: one compressed stream decompresses on one thread, and only the predictor and the conversion
run in parallel.

¹ libtiff as Ubuntu builds it decompresses whole strips with libdeflate; PictureSharp uses the runtime's zlib.

Reproduce with the probes in [benchmarks](https://github.com/art-ix/PictureSharp.Tiff/tree/main/benchmarks), on the
files the encode probe writes:

```bash
dotnet run -c Release --project benchmarks/PictureSharp.Tiff.Speed -- encode photo.jpg --out /tmp/tiff
dotnet run -c Release --project benchmarks/PictureSharp.Tiff.Speed -- decode /tmp/tiff/photo.*.tif --threads 1,all
python3 -I benchmarks/libtiff_speed.py /tmp/tiff/photo.none.tif
```

## Validation

Every corpus file (written by libtiff 4.5.1 and ImageMagick 6, both byte orders, classic and BigTIFF) decodes exactly
to the pixels of Pillow 10.2.0, of libtiff `TIFFReadRGBAImage` where its conversion is exact, or of the rules above;
every page of the multi-page files is checked. The corpus is regenerated with `tests/assets/generate_corpus.py`.
