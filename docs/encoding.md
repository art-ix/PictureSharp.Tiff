# Encoding TIFF

```csharp
using Image image = Image.Load("photo.jpg");
image.SaveAsTiff("photo.tif");                                             // LZW + predictor, smallest lossless colour type
image.SaveAsTiff("archive.tif", new TiffEncoderOptions { Compression = TiffCompression.Deflate, TileSize = 256 });
```

`SaveAsTiff`, `SaveAsTiffAsync`, `ToTiff` and `Image.Save("photo.tif")` use `ImageConfiguration.Default` unless the
options set `Configuration`. The stream is left open; a stream that cannot seek gets the same bytes.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Compression` | `Lzw` | `None`, `PackBits`, `Lzw`, `Deflate`, `Jpeg`, `WebP` or `CcittGroup4` |
| `ColorType` | `Auto` | `Grayscale`, `GrayscaleAlpha`, `Rgb`, `Rgba`, `Palette`, `BlackWhite`, or the smallest lossless one |
| `Predictor` | `Auto` | `Horizontal` (2) or `None`; `Auto` is horizontal for LZW and Deflate, except palette and black and white |
| `DeflateLevel` | 6 | 0 (store) to 9 (best), the zlib level |
| `JpegQuality` | 75 | 1–100, as the JPEG encoder |
| `JpegSubsampling` | `Ratio420` | chroma subsampling of colour JPEG, as libtiff |
| `WebPQuality` | 75 | 1–100; for lossless WebP, the compression effort |
| `WebPLossless` | false | lossless WebP, which also keeps the colour of fully transparent pixels |
| `RowsPerStrip` | 0 | rows in each strip; 0 chooses strips of about 64 KiB of uncompressed samples |
| `TileSize` | 0 | square tiles, a positive multiple of 16; 0 writes strips |
| `Container` | `Auto` | `Classic`, `BigTiff`, or BigTIFF only above 4 GiB of uncompressed data |
| `SaveAllPages` | true | every frame becomes a page; false writes the first frame only |
| `MaxDegreeOfParallelism` | -1 | strips or tiles compressed at once; -1 = one per processor; the file is identical for every value |
| `Metadata` | all | ICC, EXIF and XMP blocks to write |

Each option checks its own range when it is set (`ArgumentOutOfRangeException`). Combinations are checked when saving
and throw `ArgumentException`: `RowsPerStrip` with `TileSize`, the horizontal predictor with None, PackBits, Group 4
or black and white, and a colour type the compression cannot hold (below).

## Choosing a compression

| Compression | Lossless | Colour types | Read by | Use for |
| --- | :---: | --- | --- | --- |
| `None` | ✔ | all | every reader | speed, interchange with old software |
| `PackBits` | ✔ | all | every baseline reader | flat graphics; fast, little gain on photos |
| `Lzw` | ✔ | all | almost every reader | the safe default |
| `Deflate` | ✔ | all | libtiff, Photoshop, GIMP, GDAL, Pillow and most current software | smaller than LZW |
| `Jpeg` | | `Grayscale`, `Rgb` | libtiff-based readers, Photoshop, GDAL, Pillow | photos where size matters |
| `WebP` | either | `Rgb`, `Rgba` | libtiff with libwebp, GDAL, Pillow; not Photoshop | small lossy or lossless colour |
| `CcittGroup4` | ✔ | `BlackWhite` | every fax-capable reader | scanned text, faxes |

With `Auto`, JPEG writes grey when every pixel has R = G = B and RGB otherwise; WebP writes RGBA only when a pixel is not
opaque; Group 4 converts to black and white. Any other colour type with these three compressions throws
`ArgumentException`.

## Colour types

| `ColorType` | Samples | From colour pixels |
| --- | --- | --- |
| `Grayscale` | 8-bit grey | luminance (0.299 R + 0.587 G + 0.114 B), alpha dropped |
| `GrayscaleAlpha` | 8-bit grey + unassociated alpha | luminance |
| `Rgb` | 3 x 8 bits | alpha dropped |
| `Rgba` | 4 x 8 bits, unassociated alpha | as is |
| `Palette` | 8-bit indices, 256-entry colour map | alpha dropped; more than 256 colours throws `InvalidOperationException` |
| `BlackWhite` | 1 bit, WhiteIsZero | black when the luminance is below 128; alpha dropped |

`Auto` picks, for each page:

1. black and white when every pixel is opaque black or white (not with an explicit horizontal predictor);
2. otherwise greyscale when every pixel has R = G = B, with alpha only when a pixel is not opaque;
3. otherwise a palette when the image is opaque, has at most 256 colours and more than 768 pixels (smaller images are
   smaller as RGB than with the 1.5 KiB colour map);
4. otherwise RGB, or RGBA when a pixel is not opaque.

Dropped alpha is discarded, not composited over a background.

## Strips, tiles and BigTIFF

- **Strips** hold about 64 KiB of uncompressed samples by default, which compresses better than libtiff's 8 KiB and
  is still small to read. JPEG strips are rounded up to whole MCU rows (8, or 16 with vertical chroma subsampling).
- **Tiles** (`TileSize`) suit large images that are read in parts, such as maps and pyramids viewed in GIS software.
  Tiles at the right and bottom edges are padded by repeating the last column and row. JPEG and WebP tiles are complete
  streams of the full tile.
- **BigTIFF** has 64-bit offsets. `Container = Auto` writes classic TIFF unless the uncompressed samples and
  directories would exceed 4 GiB. Compressed sizes are unknown up front, so a compressed classic file that still
  grows past 4 GiB throws `NotSupportedException`; set `Container = TiffContainer.BigTiff` for it. BigTIFF is read by libtiff 4,
  GDAL, Pillow and tifffile, but not by every reader, so prefer classic TIFF when the file fits.
- **Limits:** JPEG widths (strips) or tiles up to 65535; WebP up to 16383, with strips capped at 16383 rows.

## Pages

```csharp
using var document = Image.Load("page1.png");
using var page2 = Image.Load("page2.png");                                 // the same size as page 1
document.Frames.AddClone(page2.Frames[0]);
document.SaveAsTiff("document.tif", new TiffEncoderOptions { Compression = TiffCompression.CcittGroup4 });
```

- Every frame of the `Image` becomes a page. With more than one, each page gets NewSubfileType 2 (page of a
  multi-page document) and PageNumber (index, count); a single page gets neither.
- Each page chooses its own `Auto` colour type and palette.
- The image's resolution and metadata are written on every page, so any page loads with them.
- Frame durations are ignored. The encoder needs the whole `Image`; pages cannot be written one at a time.

## What is written

- **Layout:** little-endian, the header, the strips or tiles of every page, then the directories of all pages, linked
  in page order. Out-of-line values are word-aligned and tags are sorted.
- **Tags:** the baseline set (size, bits, compression, photometric, strips or tiles, samples per pixel, planar
  configuration 1), the predictor, `ExtraSamples` 2 for alpha, the colour map, YCbCr subsampling and
  ReferenceBlackWhite for colour JPEG, and Software (`PictureSharp.Tiff`, without a version).
- **Resolution:** always, in dots per inch, as the smallest power-of-ten rational that reads back as the same `float`.
- **Metadata:** see [metadata](metadata.md).

## Exactness

Lossless output (all compressions except JPEG and lossy WebP) reads back exactly, in this decoder, libtiff 4.5.1
(`TIFFReadRGBAImage`) and Pillow 10.2.0. LZW, PackBits and Group 4 strips and tiles are byte-identical to what libtiff
writes from the same samples, so their compression ratio is libtiff's. Deflate depends on the runtime's zlib, so its
bytes can differ between platforms; the pixels do not.

The validation writes 62 byte-exact snapshots and the 137 corpus images with every compression and as LZW tiles in
BigTIFF (959 files) and checks them with `tests/assets/verify_written.py`. It also documents two limits of the oracles:
libtiff 4.5.1 misplaces grey-with-alpha pixels in tiles cut by the right edge (its own files too), and Pillow 10.2.0
cannot follow BigTIFF EXIF pointers (type IFD8).

## Speed

A 24 MP RGB photo (5712 x 4284) written as RGB strips, on an Intel Core i5-12500H with 16 threads, .NET 10, best of 5
runs. libtiff 4.5.1 was called through ctypes with the same strips, predictor and Deflate level:

| Compression | 1 thread | 16 threads | libtiff 4.5.1 | Size |
| --- | ---: | ---: | ---: | ---: |
| None | 37 ms | 26 ms | 18 ms | 73.4 MB |
| LZW + predictor | 691 ms | 106 ms | 683 ms | 36.9 MB (same strips as libtiff) |
| Deflate + predictor, level 6 | 2515 ms | 308 ms | 1159 ms ¹ | 34.1 MB (libtiff 33.5 MB) |

¹ libtiff as Ubuntu builds it compresses whole strips with libdeflate. The runtime's zlib takes as long as zlib itself
on the same strips (2572 ms), so the difference is the library, not the plugin.

The table writes RGB. The default `ColorType = Auto` first scans the pixels with vector instructions, on one thread:
about 8 ms for this photo (opacity, grey and black-and-white checked at once; the palette check stops after 257
colours).

Reproduce with the probes in [benchmarks](https://github.com/art-ix/PictureSharp.Tiff/tree/main/benchmarks):

```bash
dotnet run -c Release --project benchmarks/PictureSharp.Tiff.Speed -- encode photo.jpg --out /tmp/tiff
python3 -I benchmarks/libtiff_speed.py /tmp/tiff/photo.none.tif
```
