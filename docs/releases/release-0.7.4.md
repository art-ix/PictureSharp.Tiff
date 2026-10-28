# PictureSharp.Tiff 0.7.4

Read, edit and write 16-bit TIFF images with PictureSharp 0.9.0. Requires .NET 10 and core 0.9.0 or later.
Earlier plugin builds cannot be registered with core 0.9.0.

Loading still returns 8-bit pixels by default. To keep deeper samples, enable `PreserveBitDepth`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Tiff;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TiffFormat()).Build();
using Image scan = Image.Load("scan.tif", new DecoderOptions { PreserveBitDepth = true });
scan.SaveAsTiff("copy.tif", new TiffEncoderOptions { BitDepth = TiffBitDepth.Sixteen });
```

- Deeper integer and floating-point samples can be read into `Rgba64`, with 16-bit color and alpha.
  Grayscale, RGB, CMYK and CIE L*a*b* follow the conversion rules in the [decoding guide](../decoding.md).
- Write 16-bit grayscale or RGB, with or without alpha, using None, PackBits, LZW or Deflate compression.
  Strips, tiles, BigTIFF and the horizontal predictor are supported.
- `BitDepth = Auto` keeps the needed precision. `UpTo8` rounds to 8 bits; `Sixteen` forces 16-bit output.
  JPEG, WebP and Group 4 output remains 8-bit or bilevel. See [encoding options](../encoding.md#16-bit-samples).
- Existing 8-bit decoding and encoding results are unchanged. A 16-bit pixel counts twice against `MaxPixels`.
- Direct pixel access uses the typed core API, for example `GetPixelSpan<Rgba64>()`.

Validation: 4220 Release tests passed against core 0.9.0. The trimmed Native AOT consumer decoded 322 corpus
images without mismatches and preserved all 65536 values through LZW and Deflate round trips.
The API review and other work toward TIFF 1.0 remain in the next planned milestone, 0.8.0.
