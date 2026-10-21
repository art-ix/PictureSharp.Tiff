# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

Reads and writes TIFF and BigTIFF on .NET 10+ with PictureSharp 0.7.9+. No native libraries or P/Invoke.

```bash
dotnet add package PictureSharp.Tiff
```

## Use

Register the format once; `Image.Load`, `Identify`, `ReadMetadata`, `EditMetadata` and `Save` then handle `.tif` and
`.tiff`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();

using var image = Image.Load("scan.tif");          // first page; ICC, EXIF and XMP kept
image.Mutate(x => x.AutoOrient().Resize(1600, 0));
image.SaveAsTiff("small.tif");                     // LZW, smallest lossless colour type
image.SaveAsTiff("photo.tif", new TiffEncoderOptions { Compression = TiffCompression.Jpeg, JpegQuality = 90 });
```

Other pages are selected with `TiffDecoderOptions`; every frame of an `Image` is written as a page:

```csharp
int pages = Image.Identify("fax.tif").ContainerItemCount;
using var second = Image.Load("fax.tif", new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 1 }] });
```

A library can pass its own configuration through `DecoderOptions.Configuration` and `EncoderOptions.Configuration`
instead of changing the default.

## Support

| | |
| --- | --- |
| Read | classic TIFF and BigTIFF, both byte orders, strips and tiles, planar; none, PackBits, LZW, Deflate, JPEG, WebP, CCITT RLE, Group 3 and Group 4; greyscale 1–16 bits, palette, RGB and CMYK 8/16 bits, YCbCr, 32-bit float, with or without alpha; one page at a time |
| Write | every frame as a page, strips or tiles, classic TIFF or BigTIFF; none, PackBits, LZW, Deflate, JPEG, WebP and CCITT Group 4; 8-bit grey, grey + alpha, RGB, RGBA, palette and 1-bit black and white; compressed in parallel |
| Metadata | ICC, XMP, EXIF/GPS, orientation and resolution, read and written; `EditMetadata` without re-encoding |
| Not supported | old-style JPEG, JBIG, LZMA, Zstandard, CIE Lab, signed samples; writing 16-bit, CMYK or planar data |

Limits (`MaxPixels`, `MaxInputBytes`, `MaxMetadataBytes`) and cancellation work as for built-in formats. Decoded pixels
match libtiff 4.5.1 and Pillow 10.2.0; lossless output reads back exactly in both, and LZW, PackBits and Group 4 data
is byte-identical to libtiff's. LZW writing on one thread runs as fast as libtiff, and parallel compression on 16
threads is about 6× faster ([measurements](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/encoding.md#speed)).

Guides: [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/decoding.md) ·
[encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/encoding.md) ·
[metadata](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/metadata.md)

[0.5.0 release notes](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/releases/release-0.5.0.md): writing
TIFF and BigTIFF, multi-page, tiles, parallel compression and `EditMetadata`.

## License

MIT. Ported sources (libtiff, Pillow conversion rules) and their licenses are listed in
[THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp.Tiff/blob/main/THIRD-PARTY-NOTICES.txt). libtiff,
Pillow and ImageMagick are used only as test oracles.

## Build

```bash
dotnet build -c Release
dotnet test -c Release
```
