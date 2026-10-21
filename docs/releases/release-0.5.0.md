# PictureSharp.Tiff 0.5.0

Writing TIFF, for PictureSharp 0.7.9 and later. The plugin now reads and writes TIFF and BigTIFF.

```csharp
image.SaveAsTiff("scan.tif");                                    // LZW, smallest lossless colour type
image.SaveAsTiff("archive.tif", new TiffEncoderOptions { Compression = TiffCompression.Deflate, TileSize = 256 });
byte[] edited = Image.EditMetadata(File.ReadAllBytes("scan.tif"), m => m.Xmp = xmp); // pixels untouched
```

- **New: the encoder.** `image.Save("x.tif")`, `SaveAsTiff`, `ToTiff` and `TiffEncoderOptions`:
  - **Compression (`TiffCompression`):** None, PackBits, LZW (default, with the horizontal predictor), Deflate
    (`DeflateLevel`), JPEG (`JpegQuality`, `JpegSubsampling`; each strip or tile a complete JPEG stream), WebP
    (`WebPQuality`, `WebPLossless`; read by libtiff with libwebp, GDAL and Pillow) and CCITT Group 4.
  - **Pixels (`TiffColorType`):** 8-bit greyscale, greyscale with alpha, RGB, RGBA (unassociated alpha), palette and
    1-bit black and white. `Auto` (the default) picks the smallest lossless type for each page.
  - **Pages:** every frame becomes a page (NewSubfileType and PageNumber); `SaveAllPages = false` writes the first
    frame only.
  - **Layout:** strips of about 64 KiB (`RowsPerStrip`) or square tiles (`TileSize`, a multiple of 16); classic TIFF or
    BigTIFF (`Container`, `Auto` switches to BigTIFF above 4 GiB of uncompressed data).
  - **Speed:** strips and tiles are compressed in parallel (`MaxDegreeOfParallelism`); the output is the same for every
    thread count. See [encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.0/docs/encoding.md#speed).
  - **Metadata:** resolution always, and ICC, XMP and EXIF/GPS (with orientation) when `EncoderOptions.Metadata`
    allows them, on every page.
- **New: `Image.EditMetadata` for TIFF** rewrites ICC, XMP, EXIF and resolution of an existing file without decoding or
  re-encoding the pixels; the new IFD is appended and every other page is kept.
- **New: WebP compression (50001) is read.**
- **Breaking:** none for reading. `TiffImageExtensions.SaveAsTiff` and `ToTiff` are new.

- **New: guides** for [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.0/docs/decoding.md),
  [encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.0/docs/encoding.md) and
  [metadata](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.0/docs/metadata.md); the README is shorter.

**Validation:** every written file is read back by this decoder, libtiff 4.5.1 (`TIFFReadRGBAImage`, with any warning
failing the test) and Pillow 10.2.0: 62 byte-exact snapshots and the 91 corpus images written with every compression,
and as LZW tiles in BigTIFF (637 files). Lossless output reads back exactly in all three; LZW, PackBits and Group 4
strips and tiles are byte-identical to what libtiff writes from the same samples. Two oracle limits are worked around
and documented in `verify_written.py` (libtiff misplaces grey-with-alpha pixels in right-edge tiles; Pillow cannot read
BigTIFF EXIF pointers). A Native AOT consumer of the package encodes and decodes every combination without trim
warnings. Tested on linux-x64 only.
