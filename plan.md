# PictureSharp.Tiff: plan

TIFF plugin for PictureSharp, split out of `PictureSharp.Formats.Extra` (user decision, 2026-10-17). Separate
repository and SemVer, like `PictureSharp.Heif`. Depends only on the public `PictureSharp` package (minimum 0.7.8) and
the BCL; no core code is copied. `System.IO.Compression` is allowed for decompression (2026-10-17) and compression
(2026-10-21).

**Status: 0.5.1 released 2026-10-21 (parallel reading, 8-bit fast paths, row bands). 0.5.2 released 2026-10-21
(vectorized Auto analysis; the ordered pipeline is deferred). 0.5.3 implemented (uncommitted); next 0.5.4 CIELab.
CMYK JPEG (0.5.5) waits for a core request.**

## Versions

| Version | Scope | Status |
| --- | --- | --- |
| 0.1.0–0.2.0 | Skeleton; baseline decoder | Not released separately |
| 0.3.0 | Extended decoder | Released 2026-10-21 |
| 0.3.1 | `Image.ReadMetadata` for TIFF (metadata codec, read only) | Released in 0.4.0 |
| 0.4.0 | Page selection (`TiffDecoderOptions`) | Released 2026-10-21 |
| 0.4.1 | Writer foundation: uncompressed 8-bit strips, ICC, XMP, resolution | Released in 0.5.0 |
| 0.4.2 | Lossless compression: PackBits, LZW, Deflate, predictor 2; palette and automatic colour type | Released in 0.5.0 |
| 0.4.3 | JPEG and CCITT Group 4 compression; bilevel output | Released in 0.5.0 |
| 0.4.4 | EXIF/GPS and orientation on write; `Image.EditMetadata` | Released in 0.5.0 |
| 0.4.5 | WebP compression (50001), read and write, through the core WebP codec | Released in 0.5.0 |
| 0.5.0 | Multi-page output, tiles, BigTIFF, parallel compression; release with writing | Released 2026-10-21 |
| 0.5.1 | Parallel strip and tile decoding; 8-bit converter fast paths | Released 2026-10-21 |
| 0.5.2 | Vectorized automatic colour type analysis (pipeline deferred) | Released 2026-10-21 |
| 0.5.3 | Reading: more sample formats and bit depths | Implemented, released in 0.6.0 |
| 0.5.4 | Reading: CIELab | Planned, released in 0.6.0 |
| 0.5.5 | Reading: JPEG variants (planar, CMYK) | Implemented, released in 0.6.0 |
| 0.6.0 | Release of the reading gaps | Planned |

0.4.1–0.4.5 are internal milestones, each with its own DoD and commit, but not published to NuGet: the encoder is
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
throw `ImageFormatException`. Released in 0.4.0, not separately.

Done: `TiffMetadataCodec`; the corpus test compares `ReadMetadata` with `Load` for all 92 files and the truncation and
mutation tests also call `ReadMetadata`. 587 tests.

## 0.4.0 — page selection

- `TiffDecoderOptions : FormatDecoderOptions` with `Page` (index among full-resolution pages, default 0), read through
  `DecoderOptions.GetFormatOptions<T>()` in `Identify`, `Decode` and the metadata codec. A negative page throws
  `ArgumentOutOfRangeException` when the options are created; a page beyond the last one throws
  `ImageFormatException`, like a missing `HeifDecoderOptions.ItemId` (a broken IFD chain cannot be told apart from a
  short one).
- `Identify` reports the selected page's size; `ContainerItemCount` stays the page count.
- Reduced-resolution pages and SubIFDs (tag 330) are not selectable; a separate option can add them later if needed.
- DoD: multi-page corpus files (libtiff, ImageMagick) decode each page equal to libtiff; public API snapshot.

Done: `pages_mixed` (6 IFDs: gray, thumbnail, palette, tiled RGB, RGBA, G4), `pages_bigtiff_be` (BigTIFF big-endian,
16-bit tiles), `im_pages_lzw` (ImageMagick) and `pages_thumbnail_first`; every page is checked against libtiff
`TIFFReadRGBAImage` where its RGBA path is exact (8 bits, no alpha), otherwise against the rule model. 95-file
corpus, 618 tests.

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
- Done 2026-10-21. Decisions and deviations:
  - `TiffColorType.Auto` already in 0.4.1 (grey, grey+alpha, RGB, RGBA); 0.4.2 adds palette to it. `TiffCompression`
    has only `None` and is the default until LZW lands in 0.4.2. Enum values are added per milestone.
  - `Software` is `PictureSharp.Tiff` without a version, so snapshots do not change with releases.
  - Resolution is always written (inch), as the smallest power-of-ten rational up to 10^6 that reads back as the same
    `float`. EXIF and orientation are not written before 0.4.4.
  - Oracles run offline: `WrittenFileTests` compares the output with byte-exact snapshots in `tests/assets/written`
    (with `written.json`), which `tests/assets/verify_written.py` checks with libtiff and Pillow. libtiff premultiplies
    RGB with unassociated alpha but passes grey+alpha through (`putagreytile`), so the script compares accordingly.

### 0.4.2 — lossless compression

- PackBits (port `tif_packbits.c` `PackBitsEncode`), LZW (port `tif_lzw.c` `LZWEncode`, MSB-first with early change),
  Deflate (`ZLibStream`, `DeflateLevel` mapped to `CompressionLevel`), predictor 2 (port `horDiff8`); predictor is
  rejected for PackBits and None, as libtiff ignores it there.
- Palette output and `ColorType = Auto`.
- DoD: all compressions × colour types × predictor through the oracles; LZW and PackBits byte-identical to libtiff
  4.5.1 on the same pixels and strip size (written through ctypes); compression ratio within 2% of libtiff for LZW and
  PackBits on the corpus; worst-case inputs (random noise, single colour) stay within the libtiff expansion bounds.

- Done 2026-10-21. Decisions and deviations:
  - `TiffPredictor` enum (`Auto`, `None`, `Horizontal`) instead of a bare value: `Auto` is horizontal for LZW and
    Deflate except palette images. Explicit `Horizontal` with None or PackBits throws `ArgumentException` at save
    (options are validated per property, the combination only when encoding). `DeflateLevel` is 0–9 (default 6, as
    `PngEncoderOptions.CompressionLevel`), passed to `ZLibCompressionOptions`.
  - Palette is 8-bit with a 256-entry colour map (unused entries black), colours in order of first appearance; explicit
    `Palette` drops alpha and throws `InvalidOperationException` beyond 256 colours, as the PNG encoder. `Auto` picks a
    palette only for opaque colour images with at most 256 colours and more than 768 pixels, below which the 1.5 KiB
    colour map makes the file larger than RGB. Smaller palettes (1, 2, 4 bits) are not written.
  - The 4 GiB check is exact before writing for None; compressed strips are checked as they are written, so an
    oversized compressed image throws `NotSupportedException` after part of the output (BigTIFF comes in 0.5.0).
  - Oracles: 16 new snapshots (28 in all; Deflate files are kept for the script but not compared bytewise);
    `verify_written.py` also re-encodes the decoded strips of LZW and PackBits files with libtiff
    (`TIFFWriteEncodedStrip`) and requires identical raw strips. `Corpus_RoundTripsWithEveryCompression` writes every
    corpus image with PackBits, LZW and Deflate to `written.corpus/`: 273/273 files pass Pillow and libtiff, and all
    LZW and PackBits strips equal libtiff's (so the compression ratio is libtiff's exactly). Noise and single-colour
    inputs are covered by snapshots and `WorstCaseInputs_RoundTrip`. 1100 tests.

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

Decisions (2026-10-21): JPEG waits for core 0.7.9, which adds a public option that keeps three components (the core
encoder writes an all-grey strip with one component, which libtiff rejects in a YCbCr file; see Core requests). G4 and
bilevel output go first.

Done (G4 and bilevel):
- `TiffColorType.BlackWhite`: 1 bit, Photometric WhiteIsZero (1 = black, the natural polarity for fax coding), black
  when the luminance is below 128, alpha dropped. `Auto` picks it when every pixel is opaque black or white, except with
  an explicit horizontal predictor (which needs 8-bit samples, so the image stays grey).
- `TiffCompression.CcittGroup4` (`Fax4Encoder`, port of `Fax4Encode`, `Fax3Encode2DRow`, `putspan`, `find0span`/
  `find1span`; the run code tables are built from the decoder's code lists). It forces `BlackWhite` under `Auto`; another
  colour type, or the horizontal predictor with G4 or with `BlackWhite`, throws `ArgumentException`.
- Oracles: 9 new snapshots (37 in all), and `verify_written.py` also re-encodes G4 strips with libtiff: identical. The
  corpus test adds a G4 pass (the threshold of every corpus image): 364/364 files pass Pillow and libtiff, and every
  LZW, PackBits and G4 strip equals libtiff's. `rgb_1x1` now asks for RGB explicitly (`Auto` makes its black pixel
  bilevel). 1292 tests.

Done (JPEG, on PictureSharp 0.7.9):
- `TiffCompression.Jpeg` with `JpegQuality` (1–100, default 75) and `JpegSubsampling` (default 4:2:0, as libtiff). Each
  strip is a strip-sized `Image` (alpha set opaque, so it is dropped as for the other compressions, not composited)
  encoded by the core JPEG encoder with `ColorMode` fixed: `Color` for RGB (Photometric YCbCr, `YCbCrSubSampling`,
  `ReferenceBlackWhite` [0 255 128 255 128 255] as libtiff's JPEGSetupEncode) and `Grayscale` with 4:4:4 for grey
  (libtiff rejects a single component with sampling factors other than 1x1). The JFIF APP0 stays; no `JPEGTables`.
- `Auto` writes grey when every pixel has R = G = B, else RGB. Deviation from the design: another colour type (alpha,
  palette, bilevel) throws `ArgumentException` like the other option conflicts, not `NotSupportedException`.
  `RowsPerStrip` is rounded up to whole MCU rows (8, or 16 with vertical chroma subsampling); width above 65535 throws
  `NotSupportedException`.
- Oracles: 7 JPEG snapshots (44 in all), each with our decode as `name.decoded.png`; `verify_written.py` compares
  Pillow and libtiff with it within the 0.3.0 tolerance. Measured: Pillow decodes all 98 JPEG files (snapshots and the
  new corpus JPEG pass) identically to ours, and libtiff stays within the tolerance; every strip is a complete stream
  that the core JPEG decoder decodes to our pixels, with three components also for grey strips of colour images.
  455/455 corpus files and 44/44 snapshots verified. 1358 tests.

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

Done 2026-10-21. Decisions and deviations:
- `TiffIfdWriter` now writes either byte order and BigTIFF, copies entries from another stream converting the byte
  order per type, and places child IFDs (EXIF, GPS, interop) after the IFD. `TiffExifWriter` is the inverse of
  `TiffExifBuilder`: the block's IFD0 tags from the same list (ImageDescription, Make, Model, Orientation, Software,
  DateTime, Artist, Copyright), sub-IFDs relinked; IFD1 (thumbnail), YCbCrPositioning and resolution are dropped.
- Encoder: EXIF when `Metadata` includes `Exif`; Software always names the encoder (the block's is dropped). A block
  that is not a TIFF stream is left out silently, as the reader drops broken sub-IFDs (the core does not validate the
  setter). Maker notes are copied as bytes, so offset-based ones may not survive (as with the reader's block).
- `Rewrite`: the page's IFD (the same page `TiffDecoderOptions` selects for reading) is copied to the end of the file
  in the file's byte order and variant, only the changed kinds replace their tags (EXIF: the list above plus 34665/34853;
  ICC; XMP; resolution as inches), the link to the old IFD is patched (`TiffFile.Link`) and the new IFD links to the old
  one's next. Gamma, chromaticities or sRGB intent changes throw `NotSupportedException` (as the core JPEG codec).
  Unchanged entries keep their values, including pointers into the file (strips, tiles, SubIFDs, unchanged EXIF).
- Note: every file the encoder writes reads back with an EXIF block holding Software, as before (the reader includes
  Software in the block).
- Oracles: `verify_written.py` compares EXIF with Pillow `getexif` against a JPEG carrying the same block
  (`name.exif.jpg`) and reads the EXIF and GPS IFDs with libtiff (`TIFFReadEXIFDirectory`, `TIFFReadGPSDirectory`);
  libtiff 4.5.1 has no field for the interop pointer 40965 and warns about it even on the original corpus file, so that
  one warning is ignored. Pillow and libtiff apply the orientation, so the script undoes it before comparing pixels.
  The `written.edited` pass checks every IFD's decoded strips/tiles against the original and that libtiff gives no new
  warning. Measured: 46/46 snapshots (2 new with EXIF in both byte orders), 455/455 corpus files (EXIF in 10), 91/91
  edited corpus files (EXIF compared in 90), all pages identical. `EditMetadata` added to the fuzz loop. 1372 tests.

### 0.4.5 — WebP compression

User decision (2026-10-21): add WebP like JPEG, through the core codec instead of a port.

- Compression 50001 as libtiff 4.5.1 `tif_webp.c` writes it (not in the Adobe registry; read by libtiff with libwebp,
  GDAL and Pillow, not by Photoshop): each strip or tile is one complete WebP stream (RIFF), contiguous 8-bit RGB or
  RGBA only (no grey, no palette), at most 16383 x 16383 per strip or tile.
- Reading: decode each chunk with `ImageDecodeContext.DecodeEmbeddedPart` and the core WebP decoder (as JPEG strips);
  a stream larger than its chunk, or a sample count other than 3 or 4, throws `ImageFormatException`. It replaces the
  `NotSupportedException` for WebP.
- Writing: `TiffCompression.WebP`, each strip encoded by the core WebP encoder (`WebPEncoderOptions`) on a strip-sized
  image; options `WebPQuality` and `WebPLossless` (tag 65569 `WEBP_LOSSLESS`, as libtiff; the lossy quality in tag
  65568 `WEBP_LEVEL` is not needed to read). `Auto` writes RGB or RGBA (alpha only when a pixel is not opaque); grey,
  palette and bilevel colour types throw `ArgumentException`. Strips are limited to 16383 rows and the width to 16383.
- To verify first: whether the core encoder keeps the alpha channel and RGB values of transparent pixels in lossless
  mode (libtiff's `exact` setting), and that it writes no VP8X/metadata chunks libtiff rejects.
- DoD: corpus written by libtiff with WebP (lossy and lossless, RGB and RGBA; the system libtiff 4.5.1 has the WebP
  codec configured) decodes like the core WebP decode of each chunk and close to Pillow; our lossless
  output round-trips exactly and is read by libtiff and Pillow without warnings; lossy output within a PSNR bound set
  before implementation.

Done 2026-10-21. Decisions and deviations:
- `WEBP_LEVEL` (65568) and `WEBP_LOSSLESS` (65569) are libtiff pseudo-tags (codec settings above 65535), never written
  to the file; the file holds only Compression 50001, so the plan's tag note above was wrong and nothing extra is written.
- Reading (`TiffWebP`): each chunk is decoded with `DecodeEmbeddedPart` at the chunk size (a strip's own rows, a tile's
  full size, as `TWebPPreDecode`), its RGB or RGBA values become the chunk's 8-bit samples (alpha dropped for three
  samples, 255 for four when the stream has none) and the usual photometric and `ExtraSamples` rules convert them. A
  stream smaller than its chunk, sample counts other than 3 or 4, other bit depths or formats, planar data and chunks
  over 16383 throw `ImageFormatException` (libtiff errors); photometric other than RGB throws `NotSupportedException`.
- Writing: options `WebPQuality` (1–100, default 75 as libtiff; the effort for lossless, where libtiff forces 100) and
  `WebPLossless`; lossless sets the core encoder's `Exact`, so the colour of fully transparent pixels survives (libtiff
  leaves libwebp's default). Strips carry no metadata chunks (`Metadata = None`); lossy RGBA uses VP8X + ALPH, which
  libtiff reads. `RowsPerStrip` is capped at 16383; widths above 16383 throw `NotSupportedException`.
- Verified first: the core lossless encoder keeps alpha and the colour under alpha 0 with `Exact`, and lossy alpha is
  lossless (`AlphaQuality` 100).
- Measured: 4 libtiff-written files (lossy and lossless, RGB and RGBA, strips and tiles, big-endian) decode equal to the
  core decode of each chunk and bit-identical to Pillow (the core decoder matches libwebp, lossy included); lossy
  strips are byte-identical to the core WebP of the strip; PSNR bound 30 dB at quality 75 met. 51/51 snapshots
  (5 new WebP), 546/546 corpus files (91 re-encoded as lossless WebP, exact through libtiff and Pillow). JPEG and WebP
  corpus files added to the mutation and truncation tests. 1423 tests.

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

Part 1 (multi-page, tiles, BigTIFF, parallel compression) done 2026-10-21. Decisions and deviations:
- The BigTIFF option is `Container` (`TiffContainer.Auto`, `Classic`, `BigTiff`) instead of `BigTiff`, so the choice
  reads as a variant rather than a flag. Auto picks BigTIFF when the uncompressed samples and IFDs exceed 4 GiB; a
  compressed classic file that still exceeds 4 GiB throws `NotSupportedException` naming `TiffContainer.BigTiff`.
  Offsets and byte counts are LONG8 in BigTIFF, sub-IFD pointers IFD8.
- Layout: header, the chunks of every page, then all IFDs linked in page order (libtiff writes each IFD after its
  page; any order is valid). Only the header is patched, so non-seekable streams spool the data as before. Single-page
  classic output is byte-identical to 0.4.5 (all 51 earlier snapshots unchanged).
- Pages: NewSubfileType 2 and PageNumber (index, count) only when there are several pages. Each page chooses its own
  `Auto` colour type and palette; resolution, ICC, XMP and EXIF are written on every page, so any page loads with the
  image's metadata.
- Tiles: `TileSize` is square (one int, a positive multiple of 16; 0 = strips); together with `RowsPerStrip` throws
  `ArgumentException`. Edge tiles repeat the last column and row (better for predictors and JPEG chroma than zeros).
  JPEG and WebP tiles are complete streams of the full tile; WebP tiles are limited to 16383.
- Parallelism: `MaxDegreeOfParallelism` (-1 = processor count, as the core encoders). Chunks are compressed in batches
  of 4 per thread, each thread with its own buffers and codec state, and written in order; the core JPEG and WebP
  encoders get 1 when chunks run in parallel. Output is the same for every degree (tested for every compression,
  strips and tiles, including Deflate within one runtime).
- Oracle limits found (`verify_written.py`): libtiff 4.5.1 `TIFFReadRGBAImage` misplaces grey+alpha pixels in tiles cut
  by the right edge (`putagreytile` does not scale `fromskew` by the samples per pixel; same for files libtiff writes),
  so those files compare `TIFFReadEncodedTile` samples; Pillow 10.2.0 does not know IFD8, so BigTIFF EXIF is checked by
  libtiff only. libtiff exports no `TIFFRawTileSize`; raw tile sizes come from TileByteCounts.
- Measured: 62/62 snapshots (11 new: tiles for every compression, BigTIFF strips and tiles with EXIF, two 3-page files)
  through libtiff and Pillow, with LZW, PackBits and Group 4 tiles byte-identical to libtiff's; corpus 637/637 (91
  images also as LZW tiles of 32 in BigTIFF). 1467 tests.
- Frame-writer SPI: `ISequentialImageEncoder` (behind `Image.CreateFrameWriter`) is internal to the core, so a plugin
  cannot write pages one at a time; the encoder needs the whole `Image`. Not requested yet; a core request if users
  need page-by-page writing of large documents.

Part 2 (benchmark, Native AOT, documentation) done 2026-10-21:
- Benchmark: `benchmarks/PictureSharp.Tiff.Speed` (.NET, best of N, TSV) and `benchmarks/libtiff_speed.py` (libtiff
  through ctypes with the same samples, rows per strip, predictor and Deflate level). 24 MP RGB photo
  (`../PictureSharp/samples/iphone/IMG_8114.jpeg`), i5-12500H, 16 threads; table in the README. Writing: LZW 691 ms on
  one thread against libtiff's 683 ms (same strips), 106 ms on 16; Deflate 2515 ms against 1159 ms, because Ubuntu's
  libtiff 4.5.1 compresses whole strips with libdeflate while the runtime's zlib takes 2572 ms on the same strips by
  itself (808 ms with libdeflate); 308 ms on 16 threads. Reading to RGBA (single-threaded decoder): None 98 / 45 ms,
  LZW 428 / 268 ms, Deflate 406 / 148 ms against `TIFFReadRGBAImage`. Parallel strip decoding is a candidate for a
  later version.
- Native AOT: a consumer of the packed 0.5.0 package (`PublishAot`, `TreatWarningsAsErrors`, linux-x64) encodes every
  compression in strips and tiles, classic and BigTIFF, with two pages, reads them back (pixels, page count, ICC) and
  runs `EditMetadata`, with no trim or AOT warnings.
- Documentation: README (reading and writing, example, performance), `docs/releases/release-0.5.0.md`,
  `PackageReleaseNotes`, AGENTS.md; core README footnote ² changed to read and write (uncommitted in the core
  repository).

## Next: 0.5.1–0.6.0

Planned 2026-10-21. Order: reading speed first (the largest measured gap), writing only if profiling shows headroom,
then the reading gaps. No public API changes are planned for 0.5.1 and 0.5.2; 0.6.0 widens what decodes.

### 0.5.1 — parallel reading

Baseline (0.5.0 benchmark, 24 MP RGB, i5-12500H): reading to RGBA takes None 98 ms, LZW 428 ms, Deflate 406 ms,
against 45 / 268 / 148 ms for libtiff `TIFFReadRGBAImage`, because `TiffPixelDecoder` handles one chunk at a time.

- **Degree:** `DecoderOptions.MaxDegreeOfParallelism` (core, -1 = processor count), as the core PNG and JPEG
  decoders. No TIFF option.
- **Unit of work:** one chunk index across all its planes (decompress, un-predict, convert). Chunks write disjoint
  rectangles of the image, so the pixel span needs no locking. Contiguous ranges of chunks per thread (cache locality
  and fewer handoffs); sequential below a threshold (few chunks or a small image) to avoid overhead.
- **Per-thread state:** chunk buffer, `LzwDecoder`, `FaxDecoder` and the `IntegerChunkConverter` sample row are
  per instance today; create one set per thread (like the encoder's `ChunkEncoder`). Read-only state (`TiffJpeg`
  tables, colour maps, YCbCr tables) is shared; check each for hidden mutation.
- **Memory:** each thread holds one chunk buffer (up to `ChunkBytes × Planes`) and, for JPEG and WebP, one decoded
  part. `DecodeEmbeddedPart` documents "the image and one part"; with N threads it is the image and N parts. Cap the
  degree so the buffers stay within one image's size (≤ 2× the image in total, the same bound as today's JPEG path).
- **Nested parallelism:** `DecodeEmbeddedPart` passes the outer `MaxDegreeOfParallelism` to the core JPEG and WebP
  decoders, so parallel parts each parallelize again. Measure first; if oversubscription costs time, request in the
  core `plan.md` a way to set the part's degree (the encoder side already passes 1).
- **Errors:** deterministic. Collect exceptions per chunk and rethrow the one from the lowest chunk index, unwrapped
  from `AggregateException`, so a corrupt file reports the same `ImageFormatException` for every degree. Cancellation
  is checked between chunks on every thread and surfaces as `OperationCanceledException`.
- **One large strip** (writers that store the whole image in one strip): decompression cannot split, but predictor
  undo and conversion can run in row bands in parallel after it.
- **Single-thread fast paths:** contiguous 8-bit gray, gray+alpha, RGB and RGBA convert straight to `Rgba32` without the
  `ushort` sample row; uncompressed data without `FillOrder` 2 converts directly from the file instead of being copied
  to the chunk buffer first. Output must stay bit-identical.
- DoD: whole corpus and fuzz/truncation/mutation tests at degree 1 and at the processor count with identical pixels
  and identical exceptions; cancellation latency test with many strips; benchmark rerun and documented (README,
  `docs/decoding.md`). Targets, not gates: None ≤ libtiff on one thread; LZW and Deflate below libtiff with all threads.

Parallel decoding done 2026-10-21 (uncommitted):

- `TiffPixelDecoder` decodes contiguous ranges of chunk indices (`degree × 4` ranges) with one `ChunkDecoder` per
  thread (chunk buffer, `LzwDecoder`, `FaxDecoder`), writing through `Image.GetPixelMemory()`. The converters are
  shared: `IntegerChunkConverter` rents its sample row per call instead of keeping one.
- **Degree (decision):** the automatic degree (-1) stays on one thread below 2^18 pixels, as the core's loops; an
  explicit degree is used as given, so the small corpus files exercise the parallel path in tests (no internals are
  visible to them). Capped at the chunks per plane and at `w × h × 4 / per-thread buffers`.
- **LZW:** the old-style decoder is chosen once, from the first two bytes of chunk 0 (after bit reversal), and passed
  to every `LzwDecoder`, as libtiff's `LZWPreDecode` picks it from the first strip decoded.
- **Fax:** `FaxDecoder` clears its run arrays at the start of each strip, so corrupt data cannot read runs left by the
  strip the same thread decoded before (valid data never does; pixels unchanged).
- **Errors:** a range stops at its first failure; ranges above the lowest failing chunk stop early, lower ones finish;
  the exception of the lowest chunk is rethrown unwrapped, and a cancellation wins over format errors.
- **Tests:** `ParallelDecodingTests`: corpus, JPEG and WebP files at degrees 1, 2 and max(4, processors) (about half
  the files have several chunks per plane); the corpus re-encoded in 1-row strips (PackBits, LZW) and 16-pixel tiles
  (None, Deflate); mutation and truncation outcomes (pixels or exception type and message) equal at 1 and N; every
  strip of a 600 x 600 file corrupt reports strip 0; cancellation latency on 1536 strips. 2133 tests pass.
- **Benchmark** (24 MP RGB, i5-12500H, 16 threads): None 83 / 18 ms, LZW 414 / 53 ms, Deflate 393 / 49 ms (1 / 16
  threads), libtiff 45 / 268 / 148 ms. LZW and Deflate targets met; None on one thread (83 vs 45 ms) is left to the
  fast paths. `docs/decoding.md` updated; the README has no speed section.
- **Nested parallelism (measured):** JPEG strips of 16, 64 and 256 rows: 91 / 80 / 80 ms on 1 thread, 18 / 16 / 17 ms
  on 16. Strips of 256 rows exceed the core's 2^18-pixel threshold and decode in parallel inside too (CPU time 223 ms
  against 160 ms for 16 rows), without a wall-clock cost. No core request for now.

Fast paths and row bands done 2026-10-21 (uncommitted):

- **8-bit fast paths:** `TiffColorConverter.Convert(ReadOnlySpan<byte>, …)` converts contiguous 8-bit rows straight
  from the chunk (no `ushort` row): RGBA with straight alpha is a block copy (`Rgba32` is sequential R, G, B, A), RGB(X)
  and grey(+alpha) have their own loops, the rest (premultiplied, WhiteIsZero, CMYK, palette) one 8-bit loop with the
  same rules. Planar 8-bit keeps the unpacking path.
- **In place:** uncompressed contiguous chunks without FillOrder 2 are converted from the file bytes; the thread then
  has no chunk buffer (and the memory cap counts none).
- **Row bands:** an image with one chunk per plane (not JPEG, not packed YCbCr) is decompressed once, then un-predicted
  and converted in `threads × 4` row bands. Only visible rows are un-predicted (rows are independent).
- **Tests:** `OneLargeStrip_DecodesTheSameInRowBands` (777 x 555 in one strip, None, LZW, Deflate, grey and RGBA, at -1,
  1 and N, equal to the source); the corpus single-strip files run in bands at an explicit degree. Breaking one band
  fails 128 tests. 2137 tests pass.
- **Benchmark** (24 MP RGB, 1 / 16 threads): None 25 / 10 ms, LZW 363 / 46 ms, Deflate 338 / 39 ms; libtiff 45 / 268 /
  148 ms. All targets met. One strip: None 52 / 11 ms, LZW 364 / 307 ms, Deflate 342 / 286 ms (decompression stays
  serial). `samples/IMG_20200214_0049.tif` (12 MP grey, 4-row strips): 116 ms in 0.5.0, now 17 / 5 ms.
- Release prepared (uncommitted): VERSION 0.5.1, `docs/releases/release-0.5.1.md`, `PackageReleaseNotes`. Native AOT
  consumer of the packed 0.5.1 package (local feed): the 28 write combinations, `EditMetadata`, and decoding at 1 and
  -1 threads (None, LZW, Deflate; many strips and one strip, pixels equal) pass without trim or AOT warnings.
- Released 2026-10-21: tag v0.5.1 (3c4da36), GitHub Release with absolute blob/v0.5.1 links; the Release workflow
  published the package to NuGet and attached the .nupkg.

### 0.5.2 — parallel writing

0.5.0 already compresses chunks in parallel (LZW 691 → 106 ms, Deflate 2515 → 308 ms on 16 threads). What stays
sequential, and the candidates:

- **Batch barrier:** `EncodeChunks` runs `Parallel.For` over batches of `degree × 4` chunks and writes between them, so
  threads wait for the slowest chunk of each batch and while the batch is written. Replace with an ordered pipeline:
  workers take chunk indices in order, the caller writes finished chunks in index order, with a bounded window of
  pending results (same memory bound as now).
- **Automatic colour type:** `Choose`, `IsGray`, `IsOpaque` and `TiffSamplePacker.Palette` scan the whole frame on one
  thread before compression. Scan row bands in parallel and merge in band order; the palette keeps first-occurrence
  order, so the colour map and the bytes stay identical, with an early stop once a band exceeds 256 colours.
- **Pages:** pages are encoded one after another, and a page with fewer chunks than threads leaves threads idle
  (many small pages). Let the next page's chunks start while the previous page finishes, still written in page order.
- **Gate:** before coding, profile the 24 MP benchmark and a many-small-pages case on 16 threads (idle time at the
  barrier, time in the analysis scans). Implement only the parts worth more than ~10% of the encode; otherwise record
  the measurement here and skip the milestone.
- Deflate on one thread stays limited by the runtime's zlib (libtiff uses libdeflate); not part of this milestone.
- DoD: every snapshot in `tests/assets/written/` byte-identical; output identical for every degree (existing tests);
  benchmark rerun and documented.

Gate profiled 2026-10-21 (temporary Stopwatch counters in `TiffEncoder`, removed again; 24 MP RGB photo, i5-12500H,
16 threads, best of 5). "Parallel" is the wall time inside the batch loops, "ideal" the summed chunk time / 16:

| Case | 1 thread | 16 threads | Parallel / ideal | Analysis (`Plan`) |
| --- | ---: | ---: | ---: | ---: |
| LZW, RGB | 686 ms | 117 ms | 98 / 67 ms | — |
| LZW, Auto (default) | 716 ms | 132 ms | 93 / 67 ms | 30 ms |
| Deflate, RGB | 2519 ms | 324 ms | 315 / 261 ms | — |
| Deflate, Auto | 2578 ms | 363 ms | 324 / 263 ms | 31 ms |
| None, RGB / Auto | 33 / 62 ms | 44 / 55 ms | 8 / 2 ms | 29 ms |
| 160 pages of 256 x 256, LZW | 323 ms | 152 ms | 130 / 20 ms | 15 ms |
| 160 pages of 256 x 256, Deflate | 1184 ms | 506 ms | 472 / 79 ms | 16 ms |

- Batch barrier: about 30 ms of 117 (LZW) and 55 ms of 324 (Deflate) is waiting, 17–26 %: above the gate.
- Automatic colour type: `Choose` scans the whole frame on one thread, about 30 ms per 24 MP, 22 % of the default LZW
  save and 8 % of Deflate: above the gate.
- Pages: a 256 x 256 RGB page has 3 strips, so 13 of 16 threads idle; 2.1× (LZW) and 2.3× (Deflate) on 16 threads
  where the chunk work alone would allow about 6×: far above the gate.
- Also seen: None is slower on 16 threads than on one (44 against 33 ms; copying is memory-bound); encode it on one
  thread unless profiling of the pipeline says otherwise.
- All three parts pass the gate; 0.5.2 goes ahead when the user decides.

Follow-up experiments 2026-10-21 (temporary, reverted):

- `BatchPerThread` 4 → 16 → 64 (64 holds almost the whole 24 MP image in one batch, so it bounds what removing the
  barrier can give): LZW 122 → 123 → 97 ms, Deflate 328 → 309 → 292 ms; 160 small pages unchanged (152, 508 ms), as
  batches stop at page boundaries. Larger batches are no substitute: the gain needs the whole page in flight, which
  costs the compressed size in memory; only a pipeline gives it within the current bound.
- `Choose` scan of 24 MP: 29.9 ms scalar, 7.5 ms with `System.Numerics.Vector<uint>` (opacity and R = G = B on one
  thread). Vectorizing gets most of the gain without threads, bands or merging.

Proposed scope (2026-10-21, awaiting the user's decision):

1. **Vectorized analysis** (do): `Choose`, `IsGray`, `IsOpaque` with `Vector<uint>`; the palette scan stays as it is
   (it stops after 257 colours, so photos leave it at once). About −22 ms of a default 24 MP LZW save (−17 %), −22 ms of
   an uncompressed Auto save (62 → ~40 ms). No parallel bands: 7.5 ms is under the gate.
2. **None on one thread** (do, after re-measuring): copying is memory-bound and slower in parallel (44 against 33 ms).
3. **Ordered pipeline over the chunks of all pages** (optional): one mechanism for the barrier and for pages. Workers
   take (page, chunk) indices in order and keep one `ChunkEncoder` per page; the caller writes in index order with at
   most `degree × 4` results pending. Expected: LZW −20 %, Deflate −11 % on one large page; 160 small pages roughly
   152 → 50 ms. Medium complexity (blocking window, cancellation, per-page encoders); covered by the byte-identical
   snapshots and the degree tests. Helps the latency of one save; a server saving several images at once already
   fills the idle threads with other work.
4. **Parallel analysis in bands with an ordered palette merge** (drop): superseded by 1.

Alternative to weigh against 3: single-thread decoding is still behind libtiff for LZW (363 against 268 ms); for
throughput (many files at once) a faster LZW decoder is worth more than encode latency.

Decision (user, 2026-10-21): the small scope, items 1 and 2; the pipeline (3) is deferred.

Done 2026-10-21 (uncommitted):

- **Vectorized analysis:** `Codecs/TiffPixelScan.Scan` checks opacity, R = G = B and R in {0, 255} with
  `Vector<uint>` (little-endian; scalar otherwise and for the tail), in blocks of 4096 pixels, and stops once every
  property the caller needs is false (`Choose`: alpha and not grey; `IsGray`, `IsOpaque`: their own). The choice is
  unchanged, so every snapshot stays byte-identical.
- **None on one thread: dropped.** Re-measured warm (benchmark probe, best of 9, twice): 32 ms on one thread, 24 ms on
  16. The 44 ms measured for the gate was the first, cold encode of a fresh 73 MB output in the instrumented run.
- **Tests:** `Auto_SeesOnePixelAnywhere` (46 cases): one grey, alpha or colour pixel at the start, middle, end and around
  the 4096-pixel block edge of lengths 1–16001, plus alpha and colour in different pixels in both orders, for Auto
  (LZW), JPEG (`IsGray`) and WebP (`IsOpaque`). Dropping the vector opacity result fails 42 of them. 2183 tests pass.
- **Measured** (24 MP, best of 9): Auto minus RGB, LZW on 16 threads, alternating 20 runs: 3 ms (min) / 8 ms (median),
  against 22–29 ms before. Uncompressed Auto on 16 threads 55 → 31 ms; a grey 24 MP image: None Auto 37 → 16 ms,
  LZW Auto 66 → 45 ms. `docs/encoding.md` notes the cost of Auto.
- Released 2026-10-21: VERSION 0.5.2, `docs/releases/release-0.5.2.md`, `PackageReleaseNotes`, tag v0.5.2, GitHub Release.

### 0.5.3–0.6.0 — reading gaps

Ordered by how often the features appear in real files. Each milestone extends the corpus with libtiff-written files
(`generate_corpus.py`) and documents the rounding in `docs/decoding.md`.

- **0.5.3 — sample formats and bit depths:**
  - Unsigned gray and RGB at any depth 1–16 (10-, 12- and 14-bit scientific and scanner files), scaled
    `round(v · 255 / (2^b − 1))`; 32-bit unsigned integers, `round(v / 16843009)`.
  - Floating point: 16-bit (half) and 64-bit, gray and RGB, with and without predictor 3, same clamp as 32-bit.
  - Signed integers (`SampleFormat` 2), 8/16/32-bit gray and RGB (GeoTIFF elevation, scientific data). Mapping
    (user decision, 2026-10-21): offset `v + 2^(b−1)`, then scaled as an unsigned sample of the same depth, so the
    whole signed range maps linearly to 0–255 (the minimum to 0, the maximum to 255). Not clamping negatives to 0.
    libtiff `TIFFReadRGBAImage` reinterprets the bits as unsigned, which is no useful reference; Pillow keeps 32-bit
    integers. Oracle: samples from `TIFFReadEncodedStrip` plus the rule model.
  - `BitsPerSample` 32 for integer samples needs a wider sample row than `ushort`; keep the 8/16-bit path unchanged.
  - Done 2026-10-21 (uncommitted):
    - `TiffLayout`: unsigned grey and RGB at 1–16 and 32 bits, signed 8/16/32-bit grey and RGB, float 16/32/64-bit
      grey and RGB; predictor 2 also for signed and 32-bit integers (`horAcc32`, `swabHorAcc32`), as libtiff
      `PredictorSetupDecode`. Complex samples, 17–31 and 64-bit integers stay `NotSupportedException`.
    - `IntegerChunkConverter`: a 24-bit window unpacks depths that cross bytes (3, 5, 6, 7, 9–15 bits, MSB first in
      both byte orders); signed samples are offset by flipping the sign bit; 32-bit samples use a `uint` row and
      `TiffColorConverter.Convert(ReadOnlySpan<uint>)` (`round(v / 16843009)`, premultiplied colour divided in 64-bit
      arithmetic). The 8-bit fast path stays unsigned only. The scale table rounds (`(v·510 + max) / (2·max)`), which
      is unchanged for 1, 2, 4 and 8 bits.
    - `FloatChunkConverter`: halves convert to floats exactly and share the single-precision rule; 64-bit samples use
      the same rule in double precision.
    - Corpus: 35 libtiff-written files (`cases_053` in `generate_corpus.py`, oracle "model"; libtiff must read back
      every chunk byte-identical to the source with `TIFFReadEncodedStrip`/`Tile`): 3- to 14-bit grey and RGB
      (strips, tiles, planar, MinIsWhite, premultiplied 12-bit), 32-bit unsigned (predictor 2, both byte orders,
      premultiplied planar), signed 8/16/32 (predictor 2, planar alpha, tiles), half and double (predictor 3, tiles,
      big-endian, premultiplied, MinIsWhite planar). The 91 earlier files and their references are byte-identical.
      `verify_written.py` counted pages of edited files wrongly (an `int`, not a list); fixed.
    - Tests: 2639 pass (corpus, robustness, parallel, re-encoded corpus 882/882 through libtiff and Pillow, edited
      corpus 126/126); `SignedSamples_MapTheWholeRangeLinearly`; the unsupported-feature cases now use complex and
      24-bit samples. `docs/decoding.md` (support table, rounding), README.
- **0.5.4 — CIELab (photometric 8):** 8- and 16-bit, port of `tif_getimage.c` `putcontig8bitCIELab8` /
  `putcontig8bitCIELab16` and `tif_color.c` `TIFFCIELabToRGBInit`, `TIFFCIELabToXYZ`, `TIFFXYZToRGB` with libtiff's
  `display_sRGB` and the file's white point. Oracle: libtiff `TIFFReadRGBAImage`, exact. ICCLab (9) and ITULab (10)
  stay unsupported (libtiff's RGBA path does not convert them).
- **0.5.5 — JPEG variants:** planar JPEG (each plane a one-component JPEG, decoded through `DecodeEmbeddedPart` and
  merged) and CMYK JPEG (photometric 5, four components, as Photoshop writes). Checked 2026-10-21: the core returns
  Adobe-inverted CMYK as RGB, wrong for TIFF; CMYK JPEG waits for the core request (see "Core requests"), planar JPEG
  can go ahead. JPEG with alpha stays unsupported, as in libtiff.
  - CMYK JPEG done 2026-10-22 (uncommitted), on core 0.7.10 from a local feed (`reference/localfeed`, the core
    package packed from commit ef351a4; restore with `-p:RestoreAdditionalProjectSources=$PWD/reference/localfeed`
    until 0.7.10 is on NuGet; CI cannot restore it before then). `PictureSharpVersion` 0.7.10. `TiffLayout` accepts
    JPEG with photometric 5 (4 contiguous 8-bit samples, InkSet 1); `TiffJpeg` decodes those strips with
    `EmbeddedPartOptions { Jpeg = { CmykStorage = Plain } }`. The new `DecodeEmbeddedPart` overload made the plugin's
    `cref`s ambiguous (CS0419 under `TreatWarningsAsErrors`); they name the overload now. Corpus: `jpeg_cmyk_strips`
    and `jpeg_cmyk_tiles_be` (libtiff, no Adobe marker), `jpeg.json` marks them `cmyk` so `JpegTests` decodes the
    standalone strips as plain; whole images within 1 of `TIFFReadRGBAImage`. 2653 tests pass.
  - Planar JPEG done 2026-10-22 (uncommitted): `TiffJpeg.DecodePlane` decodes each plane of a chunk as a one-component
    JPEG (libtiff `JPEGPreDecode` for PLANARCONFIG_SEPARATE; no Adobe marker added, the SOF must have one component)
    into the chunk buffer, then the usual converter runs, so RGB, grey, CMYK, YCbCr 1x1 and alpha planes all work.
    Planar YCbCr with subsampling (libtiff stores smaller chroma planes; its RGBA path rejects them) is
    `NotSupportedException`. Corpus: `jpeg_rgb_separate`, `jpeg_rgba_separate_tiles`, `jpeg_graya_separate_be`,
    `jpeg_cmyk_separate`, `jpeg_ycbcr11_separate` (kind "jpeg-planar": libtiff's own plane samples through the model,
    YCbCr and RGB against `TIFFReadRGBAImage` exactly); every file decodes bit-exactly, as the core decoder matches
    libjpeg-turbo. Tests: a three-component plane, subsampled planar YCbCr. 2720 tests pass; re-encoded corpus
    917/917, edited 131/131.
  - 0.5.5 is complete; it ships in 0.6.0 with 0.5.4 (CIELab), after core 0.7.10 is on NuGet.
- **Not planned:** LogLuv, PixarLog (HDR; no 8-bit-only use), transparency masks (photometric 4) as the main page,
  different `BitsPerSample` per sample, CMYK with other ink sets or premultiplied alpha, YCbCr deeper than 8 bits.
  They keep throwing `NotSupportedException`.
- **0.6.0 release:** all three milestones' DoDs; corpus and fuzz tests over the new files; `docs/decoding.md` support
  table, release notes, `PackageReleaseNotes`; Native AOT consumer reads the new files.

## Core requests

- `DecodeEmbeddedPart` (JPEG strips within the image budget): closed in core 0.7.8.
- Per-plugin decoder options (page selection): closed in core 0.7.6 (`DecoderOptions.FormatOptions`); used from 0.4.0.
- JPEG output (0.4.3): a public option in `JpegEncoderOptions` that keeps three components for grey pixels. The core
  encoder writes a strip whose pixels are all grey with one component, which libtiff rejects in a YCbCr file, and the
  switch for it (`color` in `JpegEncoder.EncodeImage`) is internal. Requested in the core `plan.md` as 0.7.9 (2026-10-21);
  JPEG output waits for that release, CCITT G4 and bilevel output go first. Closed in core 0.7.9
  (`JpegEncoderOptions.ColorMode`); used from 0.4.3.
- CMYK JPEG (0.5.5): the core JPEG decoder takes four components as Adobe-inverted CMYK and returns RGB; TIFF stores
  plain ink values (libtiff 4.5.1 writes CMYK JPEG strips without an Adobe marker). Checked 2026-10-21 on a libtiff
  file: up to 255 off `TIFFReadRGBAImage` (mean 102), and not recoverable from the RGB result. Requested in the core
  `plan.md` (2026-10-21): format options for `DecodeEmbeddedPart` and a `JpegDecoderOptions` CMYK storage setting
  (plain or Adobe-inverted), optionally the part's degree of parallelism. Planar JPEG does not depend on it.
  Implemented in core 0.7.10 (2026-10-22, not released yet): `DecoderOptions.Jpeg.CmykStorage = JpegCmykStorage.Plain`
  and `DecodeEmbeddedPart(data, width, height, EmbeddedPartOptions)` with `Jpeg` and `MaxDegreeOfParallelism`; a libtiff
  CMYK strip decodes within 1 of `TIFFReadRGBAImage`. 0.5.5 uses it with a minimum PictureSharp of 0.7.10.
- To check during 0.5.0: a frame-writer SPI for plugins (`Image.CreateFrameWriter` supports only GIF, APNG and WebP),
  which would allow writing multi-page TIFF page by page without holding every frame. Report to the core `plan.md` if
  needed.

## Deferred

- **Zstandard (50000), read and write:** deferred until the project targets .NET 11 or later, whose
  `System.IO.Compression` adds `ZstandardStream`, `ZstandardEncoder` and `ZstandardDecoder` (decided 2026-10-21).
  .NET 10 has no Zstandard, and a pure C# decoder (RFC 8878, port of zstd's educational decoder) would be about
  1500–2000 lines to maintain for a format seen mostly in GDAL GeoTIFF; an encoder several times more. The system
  libtiff 4.5.1 has ZSTD configured, so the corpus can be written with `generate_corpus.py`. Until then ZSTD throws
  `NotSupportedException`. LZMA (34925) stays out of scope: .NET has no plans for it (dotnet/runtime#1542).
- **Old-style JPEG (6), read only:** a candidate if there is demand (decided 2026-10-21). It still appears in 1990s
  document archives (Kodak/Wang Imaging and similar scanners). The work is a port of the stream reconstruction in
  libtiff `tif_ojpeg.c` (about 600–1000 lines: a complete `JPEGInterchangeFormat` stream, or one rebuilt from the
  `JPEGQTables`, `JPEGDCTables` and `JPEGACTables` tags, with libtiff's workarounds), with decoding left to the core
  JPEG codec through `DecodeEmbeddedPart`. No tool writes OJPEG, so validation needs real files from outside the
  repository (asked for before downloading), compared against the system libtiff, which reads them.
- **JBIG (34661):** out of scope. Bilevel only and rare; libtiff delegates to jbigkit (GPL), which cannot be ported
  into MIT code, so a decoder would be written from ITU T.82 (about 1000–1500 lines: QM arithmetic coder, context
  templates, typical prediction, resolution layers) for very few files.
