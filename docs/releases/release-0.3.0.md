# PictureSharp.Tiff 0.3.0

The first public release: a TIFF format plugin (read only) for PictureSharp 0.7.8 and later, written entirely in C#
with no native libraries. Requires .NET 10. Release date: 2026-10-21.

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
using Image image = Image.Load("scan.tif");
```

- **Container:** classic TIFF and BigTIFF, little- and big-endian; strips or tiles, contiguous or planar samples. The
  first full-resolution page is decoded (reduced-resolution pages such as thumbnails are skipped); `Identify` reports
  the number of full-resolution pages as `ContainerItemCount`.
- **Compression:** none, PackBits, LZW (including the old LSB-first variant), Deflate (through
  `System.IO.Compression`), predictors 2 and 3, JPEG (strips and tiles decoded by the configuration's JPEG codec, each
  within its own strip or tile size) and CCITT RLE, RLEW, Group 3 (1D and 2D) and Group 4; `FillOrder` 2.
- **Colour:** greyscale (1–16 bits, WhiteIsZero and BlackIsZero), palette (1–8 bits), RGB and CMYK (8 or 16 bits),
  YCbCr (8 bits, all standard subsamplings) and 32-bit floating-point samples, with associated or unassociated alpha.
  The result is `Rgba32` with straight alpha; 16-bit samples become `round(v / 257)`, floating-point samples are
  clamped to [0, 1].
- **Metadata:** ICC, XMP, EXIF with the EXIF, GPS and Interop directories, orientation and resolution.
- **Safety:** directory chains are bounded (loops, duplicate and out-of-file offsets), `MaxPixels` and cancellation are
  honoured between strips and tiles, and corrupt input throws `ImageFormatException`.

**Not supported** (`NotSupportedException`): old-style JPEG (6), JBIG, LZMA, Zstandard, WebP, LERC, JPEG 2000,
PixarLog, LogLuv, CIE Lab, transparency masks, signed samples and floating-point samples other than 32 bits. Selecting
a page other than the first is planned for a later version.

**Validation:** the codecs are ported from libtiff 4.5.1. A corpus of 92 files written with libtiff 4.5.1 and
ImageMagick 6 covers every supported compression, layout and colour model; decoded pixels match Pillow 10.2.0,
libtiff `TIFFReadRGBAImage` or the documented conversion rules exactly, and every JPEG strip or tile matches the core
JPEG decoder on the same data. Fuzzing of directories and truncated or mutated files is part of the test suite. Tested
on linux-x64 only.
