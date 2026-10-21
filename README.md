# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

Reads and writes TIFF and BigTIFF on .NET 10+ with PictureSharp 0.7.9+.

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

int pages = Image.Identify("fax.tif").ContainerItemCount;
using var second = Image.Load("fax.tif", new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 1 }] });
```

Every frame of an `Image` is written as a page.

## Support

| | |
| --- | --- |
| Read | TIFF and BigTIFF, strips and tiles; none, PackBits, LZW, Deflate, JPEG, WebP, CCITT fax; grey 1–16 bits, palette, RGB, CMYK, YCbCr, 32-bit float, with or without alpha |
| Write | pages, strips or tiles, TIFF or BigTIFF; none, PackBits, LZW, Deflate, JPEG, WebP, CCITT Group 4; 8-bit grey, RGB, palette and black and white, with or without alpha |
| Metadata | ICC, XMP, EXIF/GPS, orientation and resolution: read, written and edited in place |
| Not supported | old-style JPEG, JBIG, LZMA, Zstandard, CIE Lab, signed samples; writing 16-bit, CMYK or planar data |

Pixels match libtiff 4.5.1 and Pillow 10.2.0, and compression runs in parallel
([speed](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/encoding.md#speed)).

Guides: [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/decoding.md) ·
[encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/encoding.md) ·
[metadata](https://github.com/art-ix/PictureSharp.Tiff/blob/main/docs/metadata.md) ·
[release notes](https://github.com/art-ix/PictureSharp.Tiff/tree/main/docs/releases)

## License

MIT. Ported sources (libtiff, Pillow conversion rules) are listed in
[THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp.Tiff/blob/main/THIRD-PARTY-NOTICES.txt).

## Build

```bash
dotnet build -c Release
dotnet test -c Release
```
