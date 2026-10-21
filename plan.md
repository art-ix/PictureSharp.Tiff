# PictureSharp.Tiff: plan

Wtyczka TIFF (tylko odczyt) dla PictureSharp, wydzielona z `PictureSharp.Formats.Extra` (decyzja użytkownika
z 2026-10-17). Osobne repozytorium i własny SemVer, jak `PictureSharp.Heif`. Zależność wyłącznie od publicznego pakietu
`PictureSharp` (minimum 0.7.8), bez kopii kodu rdzenia. Deflate przez `System.IO.Compression.ZLibStream` (decyzja
użytkownika z 2026-10-17, zamiast wcześniej planowanej kopii `Inflater` rdzenia). Kompresja przez
`System.IO.Compression` też jest dopuszczona (decyzja użytkownika z 2026-10-21, na potrzeby ewentualnego zapisu TIFF).

**Status: 0.3.0 — pierwsze wydanie (2026-10-21, tag `v0.3.0`, GitHub Release, NuGet).** Wersje 0.1.0 (szkielet) i
0.2.0 nie były wydawane osobno.

## Wersje

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.1.0 | Szkielet: repo, CI, `TiffFormat` (rozpoznawanie TIFF/BigTIFF), dekoder zastępczy (`NotSupportedException`), testy architektury i API | Bez wydania |
| 0.2.0 | TIFF podstawowy: IFD, paski i kafle, None/PackBits/LZW/Deflate, predyktor 2, gray/palette/RGB(A) 8/16 bit | Bez osobnego wydania |
| 0.3.0 | TIFF rozszerzony: JPEG (7), CCITT G3/G4, predyktor 3 i float, CMYK, YCbCr, associated alpha, EXIF, BigTIFF | Wydana 2026-10-21 (pierwsze wydanie) |

Decyzja użytkownika (2026-10-21): pierwszym wydaniem jest od razu 0.3.0; 0.1.0 i 0.2.0 nie trafiają na NuGet.

## 0.2.0 — TIFF podstawowy

- Little- i big-endian, klasyczny TIFF; łańcuch IFD z limitem (ochrona przed pętlami i duplikatami offsetów); paski
  i kafle; `PlanarConfiguration` 1 i 2.
- Pierwsza strona w pełnej rozdzielczości (pomija IFD z bitem reduced-resolution w `NewSubfileType`); `Identify`
  zwraca `ContainerItemCount` = liczba stron, `FrameCount` = 1.
- Kompresja: None, PackBits, LZW (MSB-first z „early change” i zgodność ze starym LZW LSB-first wg libtiff
  `tif_lzw.c`), Deflate 8/32946 (`ZLibStream` z limitem wyjścia = oczekiwany rozmiar paska; uszkodzony lub za krótki
  strumień → `ImageFormatException`). Nadmiar danych za paskiem jest ignorowany jak w libtiff `ZIPDecode` (pisarze
  dopełniają ostatni pasek do `RowsPerStrip` wierszy); limit wyjścia i tak chroni przed rozdmuchaniem.
- `FillOrder` 2 (odwrócenie bitów surowych danych jak `TIFFReverseBits`); brak `StripByteCounts` tylko dla danych
  nieskompresowanych (pełne paski, jak `EstimateStripByteCounts`); `StripByteCounts` poza plikiem przycinane do pliku.
- Predyktor 2 (8/16 bit). Fotometria: WhiteIsZero/BlackIsZero (1/2/4/8/16 bit), RGB(A) 8/16, Palette (1/2/4/8),
  `ExtraSamples` (associated → unpremultiply, unassociated wprost).
- Metadane: ICC (34675), XMP (700), `Orientation` (274) → `Metadata.Orientation`, rozdzielczość (282/283/296); limity
  przez `ReserveMetadata`.
- Zaokrąglanie: 1/2/4 bity `v * 255 / max`, 16 bitów `round(v / 257)`, mapa kolorów `round(v / 257)`; associated alpha
  dzielone w głębi źródła jak Pillow `unpackRGBa`; reguły `ExtraSamples` jak Pillow `OPEN_INFO` (0 → próbka
  ignorowana, brak tagu przy 4 próbkach RGB → alpha). Brak `Photometric` → RGB dla ≥ 3 próbek, inaczej BlackIsZero.
- DoD: korpus z libtiff 4.5.1 (ta sama co w Pillow) przez ctypes w `tests/assets/generate_corpus.py` zamiast `tiffcp`
  (którego nie ma w systemie; te same warianty: wszystkie kompresje, predyktor, kafle, `separate`, obie kolejności
  bajtów, FillOrder 2, strony z miniaturą) oraz własne pisarze starego LZW i IFD bez `StripByteCounts`; piksele =
  `reference.json` z modelu reguł, 40 z 52 plików zgodnych bit w bit z Pillow 10.2.0 (reszta: 16 bit, gdzie Pillow
  bierze starszy bajt, mapy kolorów spoza wielokrotności 257, LA associated, orientacja i miniatura jako pierwsza
  strona); fuzz IFD (pętle, offsety poza plikiem, ogromne `StripByteCounts`, mutacje i obcięcia korpusu);
  `MaxPixels` i anulowanie. Status: spełnione, 304 testy.

## 0.3.0 — TIFF rozszerzony

- JPEG (7): `JPEGTables` bez EOI + pasek bez SOI → samodzielny JPEG → `ImageDecodeContext.DecodeEmbedded`; przy
  `Photometric` RGB dopisywany APP14 Adobe z transform 0 (libtiff wymusza `JCS_RGB`). Obsługiwane: 8 bit, gray/RGB/YCbCr,
  próbki razem, bez alfy; reszta → `NotSupportedException`.
- CCITT RLE (2), RLEW (32771), G3 1D/2D (`T4Options`), G4 — port `tif_fax3.c`/`tif_fax3.h`, tablice stanów
  generowane w czasie działania (port `mkg3states.c`, sprawdzone wpis po wpisie z `tif_fax3sm.c`). Jak libtiff:
  nieoczekiwane kody kończą wiersz, brak danych → `ImageFormatException`, G4 z co najmniej jednym wierszem → sukces.
- Predyktor 3 (`fpAcc`) i float32 (gray/RGB, alfa, associated): obcięcie do [0, 1], `(int)(v · 255 + 0.5)` w pojedynczej
  precyzji, NaN → 0. Predyktor (2 i 3) stosowany tylko z LZW i Deflate, jak w libtiff (dla innych kompresji tag
  ignorowany).
- CMYK 8/16 bit (InkSet 1, opcjonalna alfa straight) wg Pillow `cmyk2rgb` po redukcji do 8 bit.
- YCbCr bez JPEG: podpróbkowanie 4:4/4:2/4:1/2:2/2:1/1:2/1:1 (domyślnie 2:2), `YCbCrCoefficients`,
  `ReferenceBlackWhite` — port `tif_color.c` (`TIFFYCbCrToRGBInit`, `TIFFYCbCrtoRGB`) i adresowania bloków z
  `tif_getimage.c`; planar tylko 1:1.
- EXIF: blok TIFF w kolejności bajtów pliku: IFD0 (orientacja, Make, Model, DateTime, Artist, Copyright, Software,
  ImageDescription) + kopie IFD EXIF, GPS i Interop; typy BigTIFF pomijane. Orientacja trafia do `Metadata` przez ten blok.
- BigTIFF (43): 8-bajtowe liczniki/offsety, wpisy 20 B, LONG8/IFD8.
- OJPEG, JBIG, LZMA, ZSTD, WebP, LERC, JPEG 2000, PixarLog, LogLuv, Lab, maska, próbki signed i float ≠ 32 bit →
  `NotSupportedException`.
- DoD: korpus z libtiff 4.5.1 (ctypes) `-c jpeg|g3|g4|rle|rlew` + kafle/planar/FillOrder 2, CMYK, float, YCbCr, BigTIFF;
  ImageMagick 6: CMYK LZW, float32 (none i zip + predyktor 3), BigTIFF zip. JPEG: każdy pasek/kafel = dekod JPEG
  rdzenia tych samych danych (`jpeg.json`), cały obraz blisko Pillow (średnio < 1,5, max ≤ 24). Reszta: Pillow
  (56 plików), libtiff `TIFFReadRGBAImage` (9: YCbCr bez kompresji — Pillow konwertuje go sam, bez podpróbkowania —,
  RLEW, BigTIFF/ImageMagick) albo model reguł (23). Status: spełnione, 497 testów.
- Znalezione błędy libtiff 4.5.1 (pliki zastąpione): zapis big-endian z predyktorem 3 ma płaszczyzny bajtów w kolejności
  hosta i libtiff sam ich nie odczytuje (plik testowy zapisany ręcznie wg specyfikacji); RLEW wyrównuje wiersze inaczej
  przy zapisie i odczycie (wyrocznią jest odczyt libtiff, który port odtwarza bit w bit).
- Paski i kafle JPEG przez `DecodeEmbeddedPart` (rdzeń 0.7.8): każdy w limicie rozmiaru paska/kafla, bez doliczania do
  `MaxPixels` obrazu; JPEG większy niż jego pasek lub kafel → `ImageFormatException`. Minimum PictureSharp 0.7.8.
- Wydanie: `docs/releases/release-0.3.0.md`, `PackageReleaseNotes`.

## Zgłoszenia do rdzenia

- `DecodeEmbedded` w już zarezerwowanym budżecie (paski JPEG): zgłoszone 2026-10-21, zamknięte w rdzeniu 0.7.8
  (`ImageDecodeContext.DecodeEmbeddedPart`); wtyczka korzysta z niego od 0.3.0.
- Opcje dekodera per wtyczka (wybór strony TIFF): zamknięte w rdzeniu 0.7.6 (`DecoderOptions.FormatOptions`). Użycie
  wymaga minimalnej wersji `PictureSharp` 0.7.6 (0.3.0 ma już 0.7.8); wybór strony do zaplanowania.
