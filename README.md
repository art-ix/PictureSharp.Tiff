# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

The plugin reads TIFF and BigTIFF files, one full-resolution page at a time.

- **Compression:** none, PackBits, LZW, Deflate, JPEG and CCITT fax (RLE, Group 3, Group 4).
- **Pixels:** greyscale, palette, RGB, CMYK, YCbCr and 32-bit float, with or without alpha.
- **Metadata:** ICC, XMP, EXIF/GPS, orientation and resolution.
- **Not supported:** old-style JPEG, JBIG, LZMA, Zstandard, WebP, CIE Lab and signed samples
  (`NotSupportedException`).

Requires PictureSharp 0.7.8 or later. See the
[release notes](https://github.com/art-ix/PictureSharp.Tiff/tree/main/docs/releases) and the
[plan](https://github.com/art-ix/PictureSharp.Tiff/blob/main/plan.md).

```bash
dotnet add package PictureSharp.Tiff
```

## Use

Register the format once; `Image.Load`, `Identify` and `ReadMetadata` then handle `.tif` and `.tiff` (read only, so
save to another format):

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();

ImageInfo info = Image.Identify("scan.tif");      // size and page count without decoding pixels
Console.WriteLine($"{info.Width}x{info.Height}, {info.ContainerItemCount} page(s)");

using var image = Image.Load("scan.tif");          // first full-resolution page; ICC, EXIF and XMP kept
image.Mutate(x => x.AutoOrient().Resize(1600, 0));
image.Save("scan.png");                            // format from the extension
```

Other pages are selected with `TiffDecoderOptions`; `Identify` and `ReadMetadata` accept the same options:

```csharp
using var second = Image.Load("scan.tif", new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 1 }] });
```

A library can pass its own configuration through `DecoderOptions.Configuration` instead of changing the default.

## License

MIT. Ported sources (libtiff, Pillow conversion rules) and their licenses are listed in
[THIRD-PARTY-NOTICES.txt](https://github.com/art-ix/PictureSharp.Tiff/blob/main/THIRD-PARTY-NOTICES.txt).
