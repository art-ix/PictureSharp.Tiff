# PictureSharp.Tiff 0.7.0

Checked against 420 real-world TIFF files and fixed where they disagreed with libtiff, for PictureSharp 0.7.10 and
later. No API changes; files that decoded before decode to the same pixels. Includes the unreleased 0.6.1
housekeeping.

- **New: files libtiff reads that 0.6 rejected.**
  - Strip and tile arrays are read as libtiff reads them: `StripOffsets` and `TileOffsets` (and the byte count tags)
    stand in for each other, signed integer types are accepted, and missing, zero or implausible `StripByteCounts`
    are replaced by libtiff's estimate (one compressed strip per plane; a single uncompressed strip whose count is
    too small or too large; uncompressed strips whose first two counts differ).
  - The horizontal predictor (2) also for floating-point and 64-bit samples, differenced as integers of the same
    width, as libtiff and GDAL write them.
  - `BitsPerSample` and `SampleFormat` only need to agree for the first `SamplesPerPixel` values.
  - 8-bit palette images with straight alpha (`ExtraSamples` 2, Pillow's "PA") and Group 4 with a 1-bit palette.
- **Fixed:** YCbCr `YCbCrCoefficients` and `ReferenceBlackWhite` are read as libtiff reads them,
  `(float)numerator / (float)denominator`; a zero denominator now gives 0 instead of breaking the conversion.
- **Fixed: `Image.EditMetadata`.**
  - In an IFD chain that loops back to the edited directory, the loop now follows the new directory too, so the
    file keeps its pages (libtiff saw one page more).
  - A page whose strips or tiles extend past the end of the file, or with tags outside it, throws
    `ImageFormatException`: the appended directory would have taken the place of the missing data.
- **Docs:** the README states the maturity before 1.0 and the required PictureSharp version (0.7.10); IFD loops end
  the chain as in libtiff (they were documented as errors); 16-bit colour maps are rounded where libtiff and Pillow
  take the high byte; the validation counts are current.
- **Breaking:** none. Still `NotSupportedException`: old-style JPEG, LogLuv, Zstandard, LZMA, LERC, NeXT,
  ThunderScan, JPEG with contiguous alpha, among others (see
  [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.7.0/docs/decoding.md)).

**Validation:** 420 files from libtiff's test images and `pics-3.8.0`, GDAL 3.9.0's autotest data, Pillow 10.2.0's
test images and ExifTool (466 IFDs, not committed; `tests/assets/fetch-external.sh` and `ExternalTests`): 325 pages
exact with libtiff 4.5.1's `TIFFReadRGBAImage` or with the documented rules applied to libtiff's samples, 35 within
1 (documented rounding), every file libtiff rejects or decodes only with errors throws `ImageFormatException` or
`NotSupportedException`, and no other exception type escapes; 396 files edited with `EditMetadata` read back the same
in libtiff. 15 new corpus files reproduce the fixes. 2984 tests besides the external ones; 62 snapshots, 1057
re-encoded and 151 edited files verified by libtiff and Pillow. Tested on linux-x64 only.
