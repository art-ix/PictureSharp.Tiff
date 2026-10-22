# PictureSharp.Tiff 0.6.0

Reads the TIFF variants that 0.5 rejected, for PictureSharp 0.7.10 and later. No API changes; files that decoded
before decode to the same pixels.

- **New: more sample formats and depths.**
  - Unsigned greyscale and RGB at any depth from 1 to 16 bits (10-, 12- and 14-bit scanner and scientific files) and
    at 32 bits, reduced with `round(v * 255 / (2^b - 1))`, which is unchanged for 1, 2, 4, 8 and 16 bits.
  - Signed 8-, 16- and 32-bit greyscale and RGB (`SampleFormat` 2, as in GeoTIFF elevation data): offset by
    `2^(b - 1)` so that the whole range maps linearly, the minimum to 0 and the maximum to 255.
  - Floating point of 16 bits (half) and 64 bits, besides 32, with and without predictor 3.
  - The horizontal predictor also for signed and 32-bit integers.
- **New: CIE L\*a\*b\*** (photometric 8), 8 and 16 bits, converted exactly as libtiff's `TIFFReadRGBAImage` does,
  with the file's `WhitePoint` (CIE D50 without it). libtiff applies no chromatic adaptation, so a D50 white comes
  out slightly yellow.
- **New: CMYK JPEG** (photometric 5). The strips hold plain ink amounts, as libtiff writes them, and are decoded with
  PictureSharp 0.7.10's `JpegCmykStorage.Plain`; their colours follow the same rule as uncompressed CMYK.
- **New: planar JPEG** (`PlanarConfiguration` 2): every plane is a one-component JPEG, so RGB, greyscale, CMYK,
  YCbCr without subsampling and alpha planes all decode.
- **Requires PictureSharp 0.7.10** (embedded part options for the CMYK strips).
- **Breaking:** none. ICC and ITU L\*a\*b\*, complex samples, integer samples of 17 to 31 or 64 bits, CIE L\*a\*b\* with
  alpha, and planar YCbCr JPEG with subsampling still throw `NotSupportedException`.

See [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.6.0/docs/decoding.md) for the support table and the
exact rounding.

**Validation:** 47 new corpus files written by libtiff 4.5.1. The new depths and signed samples: libtiff reads every
strip or tile back identical to the source, and the pixels follow the documented rules. CIE L\*a\*b\* and planar RGB and
YCbCr JPEG are bit-identical with `TIFFReadRGBAImage`; CMYK JPEG is within 1 of it (the same rounding difference as
uncompressed CMYK). All new files go through the fuzz and truncation tests, and those of the reference corpus also
through re-encoding (read back by libtiff and Pillow) and `EditMetadata`. 2786 tests. A Native AOT consumer of the package decodes every new kind without trim warnings. Tested on linux-x64 only.
