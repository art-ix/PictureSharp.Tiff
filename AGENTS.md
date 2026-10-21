# Working on PictureSharp.Tiff

PictureSharp.Tiff is a format plugin for [PictureSharp](https://github.com/art-ix/PictureSharp) that will read TIFF
entirely in C#. It is a separate repository and package with its own version; it uses PictureSharp only through its
public NuGet API. Version 0.3.0 (extended TIFF) is implemented locally and not released yet; 0.2.0 is committed.

Before starting, read [plan.md](plan.md) to identify the current version and its definition of done (DoD), then read
the latest entries in `progres.md` if it exists. Append progress after significant steps and before ending a session.
`plan.md` tracks scope and status; `progres.md` is a local log and must stay out of commits. Changes the plugin needs
in PictureSharp itself go to the core repository's `plan.md` (sibling folder `../PictureSharp`).

## Required constraints

- Target `net10.0` with C# 14 (`LangVersion=14.0`); support .NET 10 and later.
- `src/PictureSharp.Tiff` depends only on the `PictureSharp` package (minimum version in `Directory.Build.props`) and
  the BCL: no other `PackageReference`, native libraries or P/Invoke. Use only public PictureSharp API; no
  `InternalsVisibleTo` or reflection. An architecture test checks this.
- Deflate uses `System.IO.Compression.ZLibStream` (decided by the user on 2026-10-17: it ships with the runtime on
  every platform). Copy no code from the core.
- Do not use the `unsafe` keyword. Use `Span<T>`, `ref`, `Unsafe.Add` and `MemoryMarshal`.
- Public API uses `PictureSharp.Formats.Tiff` (`TiffFormat`, `ImageFormat.Tiff`); codecs are `internal`.
- Return `Image` pixels as `Rgba32` with straight alpha; reduce deeper samples to 8 bits with documented rounding.
- Call `ImageDecodeContext.ReservePixels` / `ReserveMetadata` before allocating and check cancellation between strips
  and tiles. Corrupt input must throw `ImageFormatException`; valid input with unsupported features must throw
  `NotSupportedException`. Malicious input must never expose other exceptions.
- Port libtiff function by function with `// port: libtiff/tif_lzw.c LZWDecode` comments; maintain
  `THIRD-PARTY-NOTICES.txt` for ported and copied sources.

## Code and layout

Follow `.editorconfig`. Enable nullable annotations, `TreatWarningsAsErrors` and XML documentation for public API. Use
English for identifiers, comments and XML docs; project documents may use Polish. Keep types focused, match filenames
to the main type, name partial files `Type.Responsibility.cs`, and keep hot loops allocation-free.

| Path | Purpose |
| --- | --- |
| `src/PictureSharp.Tiff/` | Plugin |
| `tests/PictureSharp.Tiff.Tests/` | TUnit tests |
| `tests/assets/` | Corpus, reference data, public API snapshot and generation scripts |
| `reference/` | Uncommitted reference sources and tools (libtiff) |

## Reference data

- Pixels must match `tests/assets/reference.json` exactly; JPEG files are listed in `tests/assets/jpeg.json` with
  their strips as standalone JPEGs. Regenerate both with the system libtiff 4.5.1 (`libtiff.so.6`, the version Pillow
  10.2.0 uses), Pillow 10.2.0 and ImageMagick 6 `convert`:

  ```bash
  python3 -I tests/assets/generate_corpus.py
  ```

- `reference/tiff-4.5.1/` holds the libtiff sources the codecs are ported from (downloaded with the user's approval
  on 2026-10-21 from download.osgeo.org, SHA-256 `d7f38b67…a5e2b`).

## Build and verification

```bash
dotnet build -c Release
dotnet test -c Release
```

A public API change updates `tests/assets/PublicApi.approved.txt` from the `PublicApi.received.txt` the test writes.
Obtain user approval before downloading external corpora or reference sources.

## Git and versioning

- Commit and push only when the user explicitly asks.
- `VERSION` contains the SemVer version; `Directory.Build.props` applies it.
- Never commit `progres.md`, `reference/`, `tests/assets/external/`, `bin/` or `obj/`.
