# PictureSharp.Tiff 0.5.2

Faster automatic colour type when writing, for PictureSharp 0.7.9 and later. No API changes: written files are
byte-identical to 0.5.1.

- **Faster `TiffColorType.Auto` (the default):** the pixels are checked for opacity, grey and black and white in one
  vectorized pass that stops as soon as the answer is known. On a 24 MP photo the check takes about 8 ms instead of
  about 30 ms, so a default LZW save on 16 threads is about 20 ms faster; an uncompressed save goes from 55 to 31 ms and
  a grey 24 MP image saved with LZW from 66 to 45 ms. JPEG and WebP compression use the same check for grey and alpha.
  See [encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.2/docs/encoding.md#speed).
- **Breaking:** none.

**Validation:** every snapshot in `tests/assets/written/` stays byte-identical; a new test puts one grey, transparent
or coloured pixel at the start, middle and end and around vector and block edges of images up to 16001 pixels, for
LZW, JPEG and WebP. 2183 tests. Tested on linux-x64 only.
