# PictureSharp.Tiff: plan

TIFF plugin for PictureSharp, split out of `PictureSharp.Formats.Extra` (user decision, 2026-10-17). Separate
repository and SemVer, like `PictureSharp.Heif`. Depends only on the public `PictureSharp` package (minimum 0.7.8) and
the BCL; no core code is copied. `System.IO.Compression` is allowed for decompression (2026-10-17) and compression
(2026-10-21).

**Status: 0.3.1 implemented, not released yet (0.3.0 released 2026-10-21, decoder only). Next: 0.4.0, then writing in
0.4.x, released as 0.5.0.**

## Versions

| Version | Scope | Status |
| --- | --- | --- |
| 0.1.0–0.2.0 | Skeleton; baseline decoder | Not released separately |
| 0.3.0 | Extended decoder | Released 2026-10-21 |
| 0.3.1 | `Image.ReadMetadata` for TIFF (metadata codec, read only) | Implemented, not released |
| 0.4.0 | Page selection (`TiffDecoderOptions`) | Planned |
| 0.4.1 | Writer foundation: uncompressed 8-bit strips, ICC, XMP, resolution | Planned, local |
| 0.4.2 | Lossless compression: PackBits, LZW, Deflate, predictor 2; palette and automatic colour type | Planned, local |
| 0.4.3 | JPEG and CCITT Group 4 compression; bilevel output | Planned, local |
| 0.4.4 | EXIF/GPS and orientation on write; `Image.EditMetadata` | Planned, local |
| 0.5.0 | Multi-page output, tiles, BigTIFF, parallel compression; release with writing | Planned |

0.4.1–0.4.4 are internal milestones, each with its own DoD and commit, but not published to NuGet: the encoder is
public API and is released once, complete, in 0.5.0 (unless the user decides otherwise).

## Implemented (0.2.0–0.3.0)

- **Container:** classic TIFF and BigTIFF, both byte orders; bounded IFD chain (loops, duplicate and out-of-file
  offsets); strips and tiles, planar 1 and 2. First full-resolution page (reduced-resolution IFDs skipped); `Identify`
  reports pages as `ContainerItemCount`, `FrameCount` = 1.
- **Compression:** None, PackBits, LZW (plus old LSB-first LZW), Deflate (`ZLibStream`, output capped at the strip
  size, trailing data ignored as in libtiff), JPEG (7) through `ImageDecodeContext.DecodeEmbeddedPart`, CCITT RLE,
  RLEW, G3 1D/2D and G4 (port of `tif_fax3.c`), `FillOrder` 2. Predictors 2 and 3 apply only to LZW and Deflate.
- **Colour:** gray 1–16 bit, palette 1–8, RGB(A) and CMYK 8/16, YCbCr 8-bit with subsampling (port of `tif_color.c`),
  float32. Rounding: 16 bit and colour maps `round(v / 257)`, float clamped to [0, 1] then `(int)(v · 255 + 0.5)`,
  associated alpha unpremultiplied at source depth; `ExtraSamples` rules follow Pillow.
- **Metadata:** ICC, XMP, resolution; EXIF block from IFD0 tags plus EXIF/GPS/Interop IFDs (carries orientation).
- **Unsupported** (`NotSupportedException`): OJPEG, JBIG, LZMA, ZSTD, WebP, LERC, JPEG 2000, PixarLog, LogLuv, Lab,
  masks, signed samples, float ≠ 32 bit; JPEG with alpha, planar or ≠ 8 bit.
- **Validation:** 92-file corpus written with libtiff 4.5.1 (ctypes, `tests/assets/generate_corpus.py`) and
  ImageMagick 6; pixels match Pillow 10.2.0, libtiff `TIFFReadRGBAImage` or the rule model exactly; JPEG strips match
  the core JPEG decoder. IFD fuzzing, `MaxPixels`, cancellation. 499 tests.
- **libtiff 4.5.1 bugs found:** big-endian + predictor 3 written in host byte order (test file written by hand); RLEW
  row alignment differs between write and read (the read side is the oracle).

## 0.3.1 — `ReadMetadata`

`TiffFormat` has no `MetadataCodec`, so `Image.ReadMetadata` throws `NotSupportedException` although the README
lists it. Add `TiffMetadataCodec.Read` on top of `TiffMetadataReader` (same blocks and limits as decoding, no pixel
data). DoD: ICC/XMP/EXIF/resolution equal to `Image.Load(...).Metadata` for the whole corpus; truncated and fuzzed files
throw `ImageFormatException`. Patch release.

Done: `TiffMetadataCodec`; the corpus test compares `ReadMetadata` with `Load` for all 92 files and the truncation and
mutation tests also call `ReadMetadata`. 587 tests.

## 0.4.0 — page selection

- `TiffDecoderOptions : FormatDecoderOptions` with `Page` (index among full-resolution pages, default 0), read through
  `DecoderOptions.GetFormatOptions<T>()` in `Identify`, `Decode` and the metadata codec. A page beyond the last one
  is a caller error (`ArgumentOutOfRangeException`), not corrupt data.
- `Identify` reports the selected page's size; `ContainerItemCount` stays the page count.
- Open question for the user: also expose reduced-resolution pages and SubIFDs (tag 330)?
- DoD: multi-page corpus files (libtiff, ImageMagick) decode each page equal to libtiff; public API snapshot.

## Writing (0.4.1–0.5.0)

### Design

- **Public API:** `TiffFormat.Encoder`; `TiffEncoderOptions : EncoderOptions` (`Compression`, `ColorType`,
  `Predictor`, `JpegQuality`, `JpegSubsampling`, `DeflateLevel`, `RowsPerStrip`/`TileSize`, `MaxDegreeOfParallelism`);
  enums `TiffCompression` (None, PackBits, Lzw, Deflate, Jpeg, CcittGroup4) and `TiffColorType` (Auto, Grayscale,
  GrayscaleAlpha, Rgb, Rgba, Palette, BlackWhite) named like `PngColorType`; extensions `SaveAsTiff`/`ToTiff`.
  Default: LZW with predictor 2, `Auto` colour type (the common lossless choice of libtiff tools and Photoshop).
- **Layout:** little-endian, header, then strip data, then the IFD (entries sorted by tag, out-of-line values
  word-aligned). The header's IFD offset is known only after the data, so seekable streams get the header patched;
  non-seekable streams get the compressed strips buffered (pooled) and written in one pass. Strips of about 64 KiB
  uncompressed by default (libtiff uses 8 KiB; larger strips compress better and are still small enough to read).
- **Pixels:** `Image` is `Rgba32` straight alpha, so output is 8-bit: gray, gray+alpha, RGB, RGBA (`ExtraSamples` = 2,
  unassociated), palette (`ColorMap` scaled ×257), bilevel (1 bit, `BlackIsZero`). `Auto` picks the smallest
  lossless type like the PNG encoder (palette ≤ 256 colours, gray when R = G = B, alpha only when not opaque).
- **Reuse:** `TiffTag`, `TiffEntry` and the EXIF parsing of `TiffFile` (the EXIF block is itself a TIFF stream);
  each encoder is a port of the libtiff function next to its decoder (`// port: libtiff/tif_lzw.c LZWEncode`).
- **Contracts:** output must not depend on `MaxDegreeOfParallelism`; observe cancellation between strips; the image is
  borrowed and the stream stays open. Deflate bytes depend on the runtime's zlib, so Deflate tests compare decoded
  pixels, never bytes.
- **Oracles:** every written file is read back by our decoder, by libtiff 4.5.1 through ctypes (`TIFFReadDirectory`
  and `TIFFReadRGBAImage` with a warning handler that fails the test) and by Pillow 10.2.0. Lossless output must match
  the source pixels exactly in all three. Byte-exact snapshots only for None, PackBits and LZW.

### 0.4.1 — writer foundation

- `TiffWriter` (IFD serialization, offsets, alignment, header patching or buffering), `TiffEncoder`,
  `TiffEncoderOptions` with `Compression = None` and explicit colour types gray, gray+alpha, RGB, RGBA.
- Tags: `ImageWidth/Length`, `BitsPerSample`, `Compression`, `Photometric`, `StripOffsets`, `SamplesPerPixel`,
  `RowsPerStrip`, `StripByteCounts`, `PlanarConfiguration` = 1, `ExtraSamples`, `Software`; resolution
  (282/283/296) from `Metadata`; ICC (34675) and XMP (700) when allowed by `EncoderOptions.Metadata`.
- `image.Save("x.tif")` chooses the encoder by extension; `SaveAsTiff`/`ToTiff`.
- DoD: round trip for every colour type and odd sizes (1×1, widths not a multiple of 8, one-row strips); the three
  oracles; metadata round trip; non-seekable stream equals seekable output; cancellation; public API snapshot.

### 0.4.2 — lossless compression

- PackBits (port `tif_packbits.c` `PackBitsEncode`), LZW (port `tif_lzw.c` `LZWEncode`, MSB-first with early change),
  Deflate (`ZLibStream`, `DeflateLevel` mapped to `CompressionLevel`), predictor 2 (port `horDiff8`); predictor is
  rejected for PackBits and None, as libtiff ignores it there.
- Palette output and `ColorType = Auto`.
- DoD: all compressions × colour types × predictor through the oracles; LZW and PackBits byte-identical to libtiff
  4.5.1 on the same pixels and strip size (written through ctypes); compression ratio within 2% of libtiff for LZW and
  PackBits on the corpus; worst-case inputs (random noise, single colour) stay within the libtiff expansion bounds.

### 0.4.3 — JPEG and CCITT Group 4

- JPEG: each strip encoded by the core JPEG encoder (`Image.Encode(new JpegEncoderOptions { … })` on a strip-sized
  image), stored as a complete stream with its own tables (no `JPEGTables`, valid per TTN2 and read by libtiff).
  `Photometric` YCbCr with `YCbCrSubSampling` matching `JpegSubsampling`, or gray for gray output; `RowsPerStrip` a
  multiple of the MCU height. Alpha with JPEG → `NotSupportedException` (as libtiff). To verify first: the core
  encoder's grayscale output and that it writes no APP markers the TIFF reader would misinterpret.
- CCITT Group 4 for `BlackWhite` (port `tif_fax3.c` `Fax4Encode`/`Fax3Encode2DRow`); bilevel also with None,
  PackBits, LZW and Deflate. Threshold for `BlackWhite` from colour input: luminance < 128 (document in XML docs).
- DoD: G4 byte-identical to libtiff on the bilevel corpus; JPEG strips decoded by the core JPEG decoder equal our
  decoder's output, and libtiff `TIFFReadRGBAImage` within the same tolerance as the 0.3.0 JPEG tests.

### 0.4.4 — EXIF and metadata editing

- `Metadata.Exif` (a TIFF-structured block) parsed with the existing reader: IFD0 descriptive tags (Make, Model,
  DateTime, Artist, Copyright, ImageDescription, Orientation) copied into the image IFD; EXIF, GPS and Interop IFDs
  written as sub-IFDs (34665, 34853, 40965). Structural tags from the block (offsets, sizes, compression) are never
  copied. Unknown or BigTIFF-only types dropped.
- `TiffMetadataCodec.Rewrite` for `Image.EditMetadata`: copy the file, write a new IFD (and sub-IFDs) at the end and
  point the previous link at it, as libtiff `TIFFRewriteDirectory` does; strip data stays byte-identical. Other pages
  keep their IFDs.
- DoD: EXIF round trip (our reader, Pillow `getexif`, libtiff `TIFFReadEXIFDirectory`); orientation honoured by
  `ApplyExifOrientation`; `EditMetadata` on the whole decoder corpus leaves pixels identical and libtiff reads the
  result without warnings.

### 0.5.0 — multi-page, tiles, BigTIFF; release

- Every frame of the `Image` becomes a page (`NewSubfileType` = 2 for multi-page, `PageNumber` 297); option
  `SaveAllPages` (default true, like `PngEncoderOptions.SaveAnimation`). Decoding stays one page per load (0.4.0).
- Tiles: `TileSize` (multiple of 16) instead of strips, for all compressions.
- BigTIFF: `BigTiff` option (`Auto` by default, chosen when the uncompressed size exceeds 4 GiB, since compressed
  sizes are unknown up front), 8-byte offsets and counts.
- Parallel strip/tile compression with deterministic output (results written in order).
- Performance: benchmark against libtiff 4.5.1 (ctypes) for None, LZW and Deflate on a 24 MP image; documented in
  README.
- DoD: all earlier DoDs; multi-page files read page by page by libtiff and Pillow `ImageSequence`; tiled and BigTIFF
  output through the oracles; Native AOT consumer encoding and decoding without trim warnings; release notes,
  `PackageReleaseNotes`, README with a writing example; core README footnote updated (TIFF read and write).

## Core requests

- `DecodeEmbeddedPart` (JPEG strips within the image budget): closed in core 0.7.8.
- Per-plugin decoder options (page selection): closed in core 0.7.6 (`DecoderOptions.FormatOptions`); used from 0.4.0.
- To check during 0.5.0: a frame-writer SPI for plugins (`Image.CreateFrameWriter` supports only GIF, APNG and WebP),
  which would allow writing multi-page TIFF page by page without holding every frame. Report to the core `plan.md` if
  needed.
