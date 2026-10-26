# PictureSharp.Tiff 0.7.3

Support for PictureSharp 0.8.5, which is now the minimum version. 0.8.5 changed `ImageInfo` and rejects plugins built
against older versions, so earlier PictureSharp.Tiff releases do not load with it. Files decode to the same pixels.

- **Colour layout in `Identify`.** `ImageInfo.Color` reports what the page's directory declares, also for pages
  `Load` rejects (signed or float samples, LogLuv): the colour model from Photometric (`Gray`, `Rgb` also for YCbCr,
  `Palette`, `Cmyk` for separated images with InkSet 1, `Lab` for CIELab, ICCLab and ITULab, otherwise `Other`),
  `ChannelCount` from SamplesPerPixel, `BitsPerSample`, `SampleFormat` (unsigned, signed, float; complex types as
  `Other`), straight or premultiplied alpha from ExtraSamples, and whether the page has an ICC profile (tag 34675),
  which is not read. See [Decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.7.3/docs/decoding.md).
- **`ImageInfo.Compression`:** `Lossy` for JPEG, `Unknown` for WebP (either) and unknown schemes, `Lossless`
  otherwise.
- **`ImageInfo.Resolution`** is null when the page has no resolution in inches or centimetres, instead of 96 DPI.
- **Breaking:** requires PictureSharp 0.8.5.

**Validation:** 3899 tests on linux-x64 against a local PictureSharp 0.8.5 package, 21 of them new for the colour
layout.
