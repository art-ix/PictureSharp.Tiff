# Working on PictureSharp.Tiff

PictureSharp.Tiff is a format plugin for [PictureSharp](https://github.com/art-ix/PictureSharp) that reads and writes
TIFF entirely in C#. It is a separate repository and package with its own version; it uses PictureSharp only through its
public NuGet API. Version 0.3.0 (extended TIFF) is the first release; 0.5.0 adds writing (internal milestones
0.4.1–0.4.5 and 0.5.0); 0.6.0 adds the remaining reading gaps (internal milestones 0.5.3–0.5.5).

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
  every platform). Compression may use it too (decided by the user on 2026-10-21, for writing TIFF). Copy no code
  from the core.
- Do not use the `unsafe` keyword. Use `Span<T>`, `ref`, `Unsafe.Add` and `MemoryMarshal`.
- Public API uses `PictureSharp.Formats.Tiff` (`TiffFormat`, `ImageFormat.Tiff`); codecs are `internal`.
- Return straight alpha as `Rgba32` by default, or `Rgba64` for deeper sources with `PreserveBitDepth`. Reserve the actual pixel type and encode 16-bit samples without reducing through 8 bits.
- Call `ImageDecodeContext.ReservePixels` / `ReserveMetadata` before allocating and check cancellation between strips
  and tiles. Corrupt input must throw `ImageFormatException`; valid input with unsupported features must throw
  `NotSupportedException`. Malicious input must never expose other exceptions.
- Port libtiff function by function with `// port: libtiff/tif_lzw.c LZWDecode` comments; maintain
  `THIRD-PARTY-NOTICES.txt` for ported and copied sources.

## Code and layout

Follow `.editorconfig`. Enable nullable annotations, `TreatWarningsAsErrors` and XML documentation for public API. Use
English for identifiers, comments, XML docs and `plan.md` (user decision, 2026-10-21); `progres.md` may use Polish. Keep types focused, match filenames
to the main type, name partial files `Type.Responsibility.cs`, and keep hot loops allocation-free.

| Path | Purpose |
| --- | --- |
| `src/PictureSharp.Tiff/` | Plugin |
| `tests/PictureSharp.Tiff.Tests/` | TUnit tests |
| `tests/assets/` | Corpus, reference data, public API snapshot and generation scripts |
| `reference/` | Uncommitted reference sources and tools (libtiff) |
| `tools/` | Fuzzing harness (AFL++ and SharpFuzz, 0.7.2; see `tools/README.md`); packages allowed, not shipped |

## Reference data

- Pixels must match `tests/assets/reference.json` exactly; JPEG and WebP files are listed in `tests/assets/jpeg.json`
  and `tests/assets/webp.json` with their strips as standalone JPEG or WebP streams. Regenerate them with the system libtiff 4.5.1 (`libtiff.so.6`, the version Pillow
  10.2.0 uses), Pillow 10.2.0 and ImageMagick 6 `convert`:

  ```bash
  python3 -I tests/assets/generate_corpus.py
  ```

- Encoder output is snapshotted in `tests/assets/written/` (files plus `written.json`). When `WrittenFileTests` fails,
  it writes the new output to `written.received/` in the test output directory; check it, then copy it over the
  snapshots:

  ```bash
  python3 -I tests/assets/verify_written.py tests/PictureSharp.Tiff.Tests/bin/Release/net10.0/written.received
  ```

  The same script checks `written.corpus/`, where every test run writes the corpus re-encoded with PackBits, LZW, CCITT Group 4, JPEG,
  lossless WebP and Deflate (for LZW, PackBits and Group 4 it also requires strips byte-identical to libtiff's), and `written.edited/`,
  where the corpus is rewritten by `Image.EditMetadata` next to the originals (libtiff must decode every IFD as before).
  Files with EXIF come with a JPEG carrying the same block (`name.exif.jpg`), which Pillow reads for comparison.

- Real-world files (0.7.0) live in `tests/assets/external/<source>/`, never committed. `fetch-external.sh <source>`
  fetches them (every source but `libtiff-images` downloads: only with the user's approval) and records origin and
  SHA-256 in `external/SOURCES.tsv`; `external_oracle.py` writes libtiff's rasters and `external/external.json`;
  `ExternalTests` (`Category=Local`) compares, writes `external/results/` and `external/edited/`; then
  `external_triage.py` lists every page that is not exact and `external_oracle.py --edited` checks the edited files:

  ```bash
  tests/assets/fetch-external.sh libtiff-images
  python3 -I tests/assets/external_oracle.py
  dotnet test -c Release -- --treenode-filter "/*/*/ExternalTests/*"
  python3 -I tests/assets/external_triage.py
  python3 -I tests/assets/external_oracle.py --edited
  ```

  A difference found there that is a bug gets a minimal synthetic reproduction in the committed corpus.

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
