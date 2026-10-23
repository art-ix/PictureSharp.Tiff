# Metadata

TIFF stores metadata as tags of each page. The plugin maps them to `ImageMetadata`:

| `ImageMetadata` | TIFF | Read | Written |
| --- | --- | :---: | :---: |
| `HorizontalResolution`, `VerticalResolution` | XResolution, YResolution, ResolutionUnit (282, 283, 296) | ✔ | ✔ always, in inches |
| `IccProfile` | InterColorProfile (34675) | ✔ | ✔ |
| `Xmp` | XMLPacket (700) | ✔ | ✔ |
| `Exif` | descriptive tags of the page, Orientation, EXIF, GPS and interoperability IFDs | ✔ | ✔ |

Without resolution tags, the resolution reads as 96 dpi; centimetres are converted to inches.

## Reading

```csharp
ImageMetadata metadata = Image.ReadMetadata("scan.tif");                   // the first page, no pixel decoding
ExifOrientation orientation = metadata.Orientation;
```

`Image.Load` gives the same metadata with the pixels. `TiffDecoderOptions.Page` selects another page for both.

EXIF has no block of its own in TIFF: its tags are spread over the page's directory and its sub-directories. The
reader builds `ImageMetadata.Exif` as a TIFF stream holding the page's ImageDescription, Make, Model, Orientation,
Software, DateTime, Artist and Copyright, and the EXIF (34665), GPS (34853) and interoperability (40965) directories.
Structural tags (offsets, sizes, compression) and thumbnails are never part of it. A damaged sub-directory is
dropped, not the whole block. `MaxMetadataBytes` limits each block.

## Writing

When `EncoderOptions.Metadata` allows them (all by default), the encoder writes on every page:

- ICC and XMP as tags 34675 and 700;
- the EXIF block's descriptive tags and orientation into the page's directory, and its EXIF, GPS and interoperability
  directories as sub-directories, converted to little-endian. Software always names this encoder; resolution comes
  from `HorizontalResolution` and `VerticalResolution`, not from the block. A block that is not a TIFF stream is left
  out. Maker notes are copied as bytes, so maker notes with absolute offsets may not survive (as in most editors).

## Editing without re-encoding

`Image.EditMetadata` changes ICC, XMP, EXIF and resolution of an existing file and leaves the image data untouched:

```csharp
byte[] edited = Image.EditMetadata(File.ReadAllBytes("scan.tif"), metadata =>
{
    metadata.Xmp = xmpPacket;
    metadata.HorizontalResolution = metadata.VerticalResolution = 600;
});
File.WriteAllBytes("scan.tif", edited);
```

- The page's directory is copied to the end of the file with only the changed kinds of tags replaced, and the link
  that pointed at the old directory is patched, as libtiff's `TIFFRewriteDirectory` does. Strips, tiles and the other
  pages stay byte-identical; the file keeps its byte order and variant (classic or BigTIFF). In a chain that loops
  back to the old directory, that link is patched too, so the pages stay the same.
- `TiffDecoderOptions.Page` in the decoder options selects the page to edit.
- A page whose strips or tiles extend past the end of the file, or with tags whose values lie outside it, throws
  `ImageFormatException`: the appended directory would take the place of the missing data.
- Anything else the file reaches past its end would land in the appended bytes, so it is made explicit or cut off
  (0.7.2): byte counts the decoder estimates (missing, or implausible as libtiff judges them) are stored in the new
  directory; EXIF, GPS and Interoperability directories that are not entirely in the file are rewritten from the
  block read, so nothing new appears; and an IFD link that leads outside the file, which ended the chain, is set to 0.
  A page whose strips or tiles overlap a link the edit has to change (a strip at offset 0 holds the header) throws
  `ImageFormatException`, since its pixels would change.
- Gamma, chromaticities and sRGB rendering intent have no TIFF tags here and throw `NotSupportedException`.

Validation: EXIF written by the encoder is read back the same by Pillow (`getexif`) and libtiff
(`TIFFReadEXIFDirectory`, `TIFFReadGPSDirectory`); editing every corpus file leaves every page's decoded data
identical, and libtiff reads the result without new warnings.
