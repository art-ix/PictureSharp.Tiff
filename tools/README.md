# Fuzzing

Coverage-guided fuzzing of the plugin (0.7.2) with [AFL++](https://aflplus.plus/) and
[SharpFuzz](https://github.com/Metalnem/sharpfuzz), which instruments `PictureSharp.Tiff.dll` for AFL++'s coverage map.
The harness, `PictureSharp.Tiff.Fuzz`, is not part of the solution and not shipped; it may use packages that `src`
may not.

## Targets

| Target | Input | Checks |
| --- | --- | --- |
| `load` | TIFF | `Load` of the first page and up to three more that `Identify` counts |
| `identify` | TIFF | `Identify` gives the size `Load` decodes |
| `metadata` | TIFF | `ReadMetadata` gives the ICC, EXIF and XMP blocks `Load` attaches |
| `edit` | TIFF | `EditMetadata` (XMP, orientation, resolution) refuses the file, or the result decodes to the same pixels of the first page and reads back the edit |
| `encode` | 4 option bytes, then TIFF | a page that decodes is encoded with the options the bytes choose (`FuzzTargets.EncoderOptions`); only a documented refusal of the options may fail, the output must decode, and lossless options must keep the pixels |

Every target lets only `ImageFormatException` and `NotSupportedException` escape from the plugin; anything else, and
any broken check, is a crash for AFL++. Inputs without a TIFF signature are skipped, so the core's own decoders are not
fuzzed. The limits are small so that no input is slow: `MaxPixels` 2^18, `MaxMetadataBytes` 1 MiB, one thread. Each
call may allocate at most the budget `FuzzTargets.Budget` derives from `docs/decoding.md` (the image twice, two buffers
of raw samples at up to 128 bytes per pixel plus the 16 MiB a tile may overhang, the metadata twice, the input four
times); more is a crash, which catches allocations sized by values in the file rather than by the limits. AFL++'s time
limit per input (2 s) catches hangs.

## Running

Requires AFL++ (`sudo apt install afl++`; 4.09c tested) and the .NET SDK. The SharpFuzz instrumenter is a local tool
(`tools/dotnet-tools.json`). Work goes to `tools/fuzz-work/` (not committed).

```bash
tools/fuzz.sh build                 # build the harness, instrument PictureSharp.Tiff.dll
tools/fuzz.sh seeds                 # TIFF files up to 64 KiB from tests/assets/corpus and tests/assets/external
tools/fuzz.sh run edit 3 7200       # one main and two secondary afl-fuzz instances for two hours
tools/fuzz.sh status                # afl-whatsup for every target
tools/fuzz.sh findings              # replay crashes, hangs and inputs that killed an instance, uninstrumented
```

`run` resumes a target's earlier campaign when its output exists. Rebuild with `build` after changing the plugin; a
plain `dotnet build` of the harness leaves an uninstrumented DLL, which is what `findings` uses.

The harness also runs inputs directly:

```bash
dotnet run -c Release --project tools/PictureSharp.Tiff.Fuzz -- replay edit tests/assets/fuzz
dotnet run -c Release --project tools/PictureSharp.Tiff.Fuzz -- minimize edit crash.tif small.tif
```

`replay` prints one signature per failure (exception type, message with numbers replaced, innermost plugin frame) and
the slowest input and largest allocation; `FUZZ_VERBOSE=1` adds stack traces. `minimize` removes blocks and zeroes
bytes while the signature stays the same.

## Known limitations of the setup

- `afl-showmap`, and so `afl-cmin` and `afl-tmin`, cannot run SharpFuzz targets with AFL++ 4.09c: after reading the
  map size it restarts the target without the fork server, and SharpFuzz aborts. Seeds are therefore not minimized
  (afl-fuzz keeps only the inputs that add coverage among its favourites), and `minimize` replaces `afl-tmin`.
- An input that kills the process (a stack overflow) stops that afl-fuzz instance ("Unable to communicate with fork
  server") instead of being saved; `findings` replays the instance's `.cur_input`.
- Coverage is that of `PictureSharp.Tiff.dll` only; the core's JPEG and WebP decoders run uninstrumented.
- A timeout does the same: afl-fuzz kills the target, which in SharpFuzz's persistent mode is the fork server. On a
  fully loaded machine, instrumented, a large tiled WebP encode took 5.7 s (0.7 s idle) and stopped an `encode`
  instance; raise `FUZZ_TIMEOUT` or run fewer instances when `findings` reports a dead one whose input is only slow.

## Findings

Every finding gets a file in `tests/assets/fuzz/` (checked by `FuzzRegressionTests.FuzzCorpus_KeepsTheContract`, which
repeats the harness's checks) and, where the case can be stated, a test in `FuzzRegressionTests`. The campaign and its
findings are recorded in `plan.md` under 0.7.2.
