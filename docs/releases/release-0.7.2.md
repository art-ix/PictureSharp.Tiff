# PictureSharp.Tiff 0.7.2

Fixes from a coverage-guided fuzzing campaign, for PictureSharp 0.7.10 and later. No API changes; files that decoded
before decode to the same pixels.

- **Fixed: a crafted file could crash the process.** A WebP strip or tile whose offset points at the TIFF file itself
  was decoded as TIFF again by the core (which picks the codec by signature), recursing until the stack overflowed,
  which no `catch` can handle. WebP strips must now start with a RIFF/WEBP header, as JPEG strips already had to start
  with SOI; anything else is an `ImageFormatException`.
- **Fixed: `Image.EditMetadata` on damaged files.** The rewritten directory is appended to the file, and whatever the
  file referred to past its end came to lie in the new bytes. Now byte counts the decoder estimates are stored in the
  new directory, EXIF and GPS directories not entirely in the file are rewritten from the block read, and IFD links
  that led outside the file end the chain, so no phantom page appears. Files whose strips, tiles or directories overlap
  the links the edit has to patch (a strip at offset 0 holds the header) are refused with `ImageFormatException`
  instead of changing their pixels.
- **Documented:** a JPEG or WebP tile is checked against `MaxPixels` on its own, so a tile larger than a small image
  needs `MaxPixels` of at least its size; the raw-sample buffers a decode holds besides the image (at most 128 bytes
  per pixel of a strip or tile).
- **New for contributors:** `tools/` holds the fuzzing harness (AFL++ with SharpFuzz) for `Load`, `Identify`,
  `ReadMetadata`, `EditMetadata` and the encoder, see
  [tools/README.md](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.7.2/tools/README.md).
- **Breaking:** none.

**Validation:** AFL++ with SharpFuzz on `Load`, `Identify`, `ReadMetadata`, `EditMetadata` and the encoder, about
1.9 CPU-hours per target and 120 million inputs in all, seeded with the committed and real-world corpora; only
`ImageFormatException` and `NotSupportedException` escape, every degree of parallelism gives the same result, and
memory stays within the documented buffers. Every finding has a file in `tests/assets/fuzz/` and a regression test
that fails on 0.7.1. 3878 tests (840 on external files) on linux-x64; libtiff reads 154 edited corpus files and 397
edited real-world files as before.
