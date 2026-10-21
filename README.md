# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

> Work in progress (0.3.0, not released yet). The plugin reads the first full-resolution page of a classic TIFF or
> BigTIFF file: strips or tiles, contiguous or planar samples, no compression, PackBits, LZW, Deflate (predictors 2
> and 3), JPEG and CCITT RLE/Group 3/Group 4; greyscale, palette, RGB, CMYK, YCbCr and 32-bit floating-point samples
> with optional alpha; ICC, XMP, EXIF/GPS, orientation and resolution. Old-style JPEG, JBIG, LZMA, Zstandard, WebP,
> CIE Lab and signed samples throw `NotSupportedException`. A JPEG-compressed TIFF currently needs `MaxPixels` of
> about twice its pixel count, because every strip is decoded by the JPEG codec under the same limit. See
> [plan.md](plan.md).

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
```

## License

MIT. Ported sources (libtiff, Pillow conversion rules) and their licenses are listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
