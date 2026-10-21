# PictureSharp.Tiff 0.3.1

A patch release for PictureSharp 0.7.8 and later. Release date: 2026-10-21.

- **Fixed:** `Image.ReadMetadata` threw `NotSupportedException` for TIFF although the README listed it. The plugin now
  supplies a metadata codec that reads ICC, XMP, EXIF (with orientation) and resolution from the first
  full-resolution page without decoding pixels; the result equals `Image.Load(...).Metadata`.
  `DecoderOptions.MaxMetadataBytes` applies as when decoding.
- `Image.EditMetadata` is still not supported (`NotSupportedException`); it is planned together with writing.

**Validation:** for every file of the 92-file corpus `ReadMetadata` matches the decoded metadata byte for byte; the
truncation and mutation tests also cover `ReadMetadata`. Tested on linux-x64 only.
