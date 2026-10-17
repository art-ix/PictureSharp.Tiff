# PictureSharp.Tiff

**TIFF for [PictureSharp](https://github.com/art-ix/PictureSharp), in 100% C#. No native dependencies.**

> Work in progress. Version 0.1.0 is a project skeleton: it recognizes TIFF and BigTIFF files, and identifying or
> loading one throws `NotSupportedException`. Decoding is planned; see [plan.md](plan.md).

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
```

## License

MIT. Ported sources and their licenses will be listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
