# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

> Work in progress (0.2.0, not released yet). The plugin reads the first full-resolution page of a classic TIFF
> file: strips or tiles, contiguous or planar samples, no compression, PackBits, LZW or Deflate with predictor 2, and
> greyscale (1-16 bits), palette (1-8 bits) or RGB (8 or 16 bits) with optional alpha, plus ICC, XMP, orientation and
> resolution. JPEG, CCITT, CMYK, YCbCr, floating-point samples and BigTIFF throw `NotSupportedException`; they are
> planned for 0.3.0 (see [plan.md](plan.md)).

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
```

## License

MIT. Ported sources (libtiff, Pillow conversion rules) and their licenses are listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
