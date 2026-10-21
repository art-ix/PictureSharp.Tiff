# PictureSharp.Tiff

TIFF and BigTIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in pure C# with no native dependencies.
Requires .NET 10+ and PictureSharp 0.7.9+.

```bash
dotnet add package PictureSharp.Tiff
```

## Usage

Register the format once, then load and save `.tif` files like any other image:

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();

using var image = Image.Load("scan.tif");
image.Mutate(x => x.Resize(1600, 0));
image.SaveAsTiff("small.tif");
```

Loading reads the first page and keeps ICC, EXIF and XMP. Saving uses lossless LZW and writes each frame as a page.
Other pages, JPEG compression and tiles are covered in the guides below.

## Support

- **Read:** strips and tiles; none, PackBits, LZW, Deflate, JPEG, WebP and CCITT fax; grey, palette, RGB, CMYK
  and YCbCr, with or without alpha; integer samples of 1 to 16 and 32 bits (also signed), float of 16, 32 and 64 bits.
- **Write:** 8-bit grey, RGB, palette and black and white, with or without alpha; the same compressions (CCITT
  Group 4 only).
- **Metadata:** ICC, XMP, EXIF/GPS, orientation and resolution, also edited in place.
- **Not supported:** old-style JPEG, JBIG, LZMA, Zstandard, CIE Lab; writing 16-bit, CMYK or planar data.

Pixels match libtiff 4.5.1 and Pillow 10.2.0.

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
