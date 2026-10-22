# PictureSharp.Tiff 0.6.1

Housekeeping release for PictureSharp 0.7.10 and later. No API changes; files that decoded before decode to the same
pixels.

- **Fixed:** YCbCr `YCbCrCoefficients` and `ReferenceBlackWhite` are read as libtiff reads them, as
  `(float)numerator / (float)denominator`, and a zero denominator now gives 0, as in libtiff, instead of breaking the
  conversion.
- **Docs:** the README states the maturity before 1.0 and the required PictureSharp version (0.7.10, not 0.7.9); the
  validation counts in [encoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.6.1/docs/encoding.md#exactness)
  are those of the current corpus.
- **Breaking:** none.

**Validation:** one new corpus file, YCbCr 4:2:0 with BT.709 coefficients and a fractional `ReferenceBlackWhite`
(one value with a zero denominator), bit-identical with libtiff 4.5.1's `TIFFReadRGBAImage`; the earlier corpus and
its references are unchanged. 2799 tests; 62 snapshots, 959 re-encoded and 137 edited files verified by libtiff and
Pillow. Tested on linux-x64 only.
