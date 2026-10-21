# PictureSharp.Tiff 0.5.1

Faster reading, for PictureSharp 0.7.9 and later. No API changes: decoded pixels are the same as in 0.5.0.

- **New: parallel decoding.** Strips and tiles decode on up to `DecoderOptions.MaxDegreeOfParallelism` threads (-1,
  the default, is one per processor). With the automatic degree, images under 262,144 pixels stay on one thread. The
  pixels, and the exception a corrupt file throws (the one of its first failing strip or tile), are the same for every
  thread count. Cancellation is still checked between strips and tiles, on every thread.
- **New: an image in a single strip or tile** is decompressed once, then un-predicted and converted in parallel row
  bands.
- **Faster on one thread:** contiguous 8-bit greyscale, greyscale with alpha, RGB and RGBA convert straight to
  `Rgba32`, and uncompressed data converts from the file without an intermediate copy.
- **Speed** (24 MP RGB photo, Intel Core i5-12500H, 1 / 16 threads, against libtiff 4.5.1 `TIFFReadRGBAImage`):

  | Compression | 0.5.0 | 0.5.1, 1 thread | 0.5.1, 16 threads | libtiff 4.5.1 |
  | --- | ---: | ---: | ---: | ---: |
  | None | 98 ms | 25 ms | 10 ms | 45 ms |
  | LZW + predictor | 428 ms | 363 ms | 46 ms | 268 ms |
  | Deflate + predictor | 406 ms | 338 ms | 39 ms | 148 ms |

  See [decoding](https://github.com/art-ix/PictureSharp.Tiff/blob/v0.5.1/docs/decoding.md#speed).
- **Behaviour:** threads hold at most one strip or tile buffer each, and their count is capped so that the buffers stay
  within the size of the image. JPEG and WebP strips are decoded by the core codecs as before, now several at once.
- **Breaking:** none.

**Validation:** the corpus, JPEG and WebP files, the corpus re-encoded in one-row strips and 16-pixel tiles, and
mutated and truncated inputs decode to the same pixels or fail with the same exception type and message on one thread
and on several; a file whose every strip is corrupt reports its first strip on every degree; cancellation stops a
decode of 1536 strips within a strip per thread. 2137 tests. A Native AOT consumer of the package decodes on one and
on all threads without trim warnings. Tested on linux-x64 only.
