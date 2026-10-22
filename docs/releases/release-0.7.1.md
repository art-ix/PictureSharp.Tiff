# PictureSharp.Tiff 0.7.1

Two more compressions, and checked on Windows, macOS and ARM64, for PictureSharp 0.7.10 and later. No API changes;
files that decoded before decode to the same pixels.

- **New: NeXT (32766) and ThunderScan (32809) compression**, ports of libtiff's `tif_next.c` and `tif_thunder.c`:
  2-bit NeXT and 4-bit ThunderScan greyscale with one sample per pixel, ThunderScan in strips only, as in libtiff.
  NeXT data that ends early leaves the remaining rows white, as libtiff does; a row cut short is an error.
- **Platforms:** CI now builds and tests on Linux, Windows and macOS (arm64), and publishes a Native AOT consumer on
  all three that decodes every corpus page exactly as the JIT tests do. The full test suite also passes on Linux
  ARM64 (RK3588), with and without hardware intrinsics.
- **Breaking:** none. Still `NotSupportedException`: old-style JPEG, LogLuv, Zstandard, LZMA, LERC and others (see
  [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.7.1/docs/decoding.md)).

**Validation:** three new corpus files (NeXT with every row mode and with data ending early, ThunderScan with every
code), written by the generator's own encoders since libtiff only decodes these compressions, match libtiff 4.5.1's
`TIFFReadRGBAImage`; so do GDAL's three NeXT files and its `thunder.tif`. Real-world files: 329 pages exact, 35
within 1 of libtiff. 3023 tests besides the 840 on external files, on linux-x64, win-x64, osx-arm64 and linux-arm64;
62 snapshots, 1078 re-encoded and 154 edited files verified by libtiff and Pillow.
