# PictureSharp.Tiff: plan

Wtyczka TIFF (tylko odczyt) dla PictureSharp, wydzielona z `PictureSharp.Formats.Extra` (decyzja użytkownika
z 2026-10-17). Osobne repozytorium i własny SemVer, jak `PictureSharp.Heif`. Zależność wyłącznie od publicznego pakietu
`PictureSharp` (minimum 0.7.1), bez kopii kodu rdzenia. Deflate przez `System.IO.Compression.ZLibStream` (decyzja
użytkownika z 2026-10-17, zamiast wcześniej planowanej kopii `Inflater` rdzenia).

**Status: prace nad formatem wstrzymane.** Wersja 0.1.0 to szkielet projektu.

## Wersje

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.1.0 | Szkielet: repo, CI, `TiffFormat` (rozpoznawanie TIFF/BigTIFF), dekoder zastępczy (`NotSupportedException`), testy architektury i API | Gotowa lokalnie, bez wydania |
| 0.2.0 | TIFF podstawowy: IFD, paski i kafle, None/PackBits/LZW/Deflate, predyktor 2, gray/palette/RGB(A) 8/16 bit | Wstrzymana |
| 0.3.0 | TIFF rozszerzony: JPEG (7), CCITT G3/G4, predyktor 3 i float, CMYK, YCbCr, associated alpha, EXIF, BigTIFF | Wstrzymana |

Szkieletu 0.1.0 nie należy publikować na NuGet: pakiet rozpoznaje TIFF, ale go nie dekoduje. Pierwsze wydanie to 0.2.0.

## 0.2.0 — TIFF podstawowy

- Little- i big-endian, klasyczny TIFF; łańcuch IFD z limitem (ochrona przed pętlami i duplikatami offsetów); paski
  i kafle; `PlanarConfiguration` 1 i 2.
- Pierwsza strona w pełnej rozdzielczości (pomija IFD z bitem reduced-resolution w `NewSubfileType`); `Identify`
  zwraca `ContainerItemCount` = liczba stron, `FrameCount` = 1.
- Kompresja: None, PackBits, LZW (MSB-first z „early change” i zgodność ze starym LZW LSB-first wg libtiff
  `tif_lzw.c`), Deflate 8/32946 (`ZLibStream` z limitem wyjścia = oczekiwany rozmiar paska; nadmiar lub
  uszkodzony strumień → `ImageFormatException`).
- Predyktor 2 (8/16 bit). Fotometria: WhiteIsZero/BlackIsZero (1/2/4/8/16 bit), RGB(A) 8/16, Palette (1/2/4/8),
  `ExtraSamples` (associated → unpremultiply, unassociated wprost).
- Metadane: ICC (34675), XMP (700), `Orientation` (274) → `Metadata.Orientation`, rozdzielczość (282/283/296); limity
  przez `ReserveMetadata`.
- DoD: korpus z `tiffcp` (`-c none|packbits|lzw:2|zip:2`, `-t`, `-p separate`) i Pillow 10.2.0; piksele =
  `reference.json`; fuzz IFD (pętle, offsety poza plikiem, ogromne `StripByteCounts`); `MaxPixels` i anulowanie.

## 0.3.0 — TIFF rozszerzony

- JPEG (7): `JPEGTables` + pasek → samodzielny JPEG → `ImageDecodeContext.DecodeEmbedded`.
- CCITT RLE/G3/G4 (libtiff `tif_fax3.c`), predyktor 3 i float32, CMYK 8 bit, YCbCr z subsamplingiem, EXIF IFD, BigTIFF.
- OJPEG, JBIG, LZMA, ZSTD, WebP-in-TIFF, Lab/LogLuv → `NotSupportedException`.
- DoD: korpus `tiffcp -c jpeg|g3|g4`, ImageMagick (CMYK, float, BigTIFF); JPEG porównywany z dekodem JPEG rdzenia tych
  samych danych, reszta = Pillow.

## Zgłoszenia do rdzenia

- Opcje dekodera per wtyczka (wybór strony TIFF): rozszerza istniejący wpis w tabeli luk 0.8.0 rdzenia.
