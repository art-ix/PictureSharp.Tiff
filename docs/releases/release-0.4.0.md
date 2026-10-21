# PictureSharp.Tiff 0.4.0

Page selection for PictureSharp 0.7.8 and later. Includes the 0.3.1 fix (`Image.ReadMetadata`).

```csharp
using var second = Image.Load("scan.tif", new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 1 }] });
```

- **New:** `TiffDecoderOptions.Page` selects a full-resolution page by its zero-based index; `Image.Load`,
  `Image.Identify` and `Image.ReadMetadata` all describe the selected page, and `ImageInfo.ContainerItemCount` is the
  number of pages. Reduced-resolution pages such as thumbnails are not counted. A negative page throws
  `ArgumentOutOfRangeException`; a page beyond the last one throws `ImageFormatException`.
- **Fixed in 0.3.1:** `Image.ReadMetadata` reads ICC, XMP, EXIF and resolution without decoding pixels; it threw
  `NotSupportedException` in 0.3.0.

**Validation:** three new multi-page corpus files (libtiff 4.5.1 and ImageMagick 6), with pages of different sizes,
compressions and colour models, classic and BigTIFF, both byte orders. Every page matches libtiff `TIFFReadRGBAImage`
where its conversion is exact, otherwise the documented conversion rules. Tested on linux-x64 only.
