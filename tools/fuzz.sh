#!/usr/bin/env bash
# Coverage-guided fuzzing of PictureSharp.Tiff with AFL++ and SharpFuzz (0.7.2); see tools/README.md.
#
#   tools/fuzz.sh build                      build the harness and instrument PictureSharp.Tiff.dll
#   tools/fuzz.sh seeds                      collect seeds from the committed and external corpora
#   tools/fuzz.sh run <target> <jobs> <sec>  run one main and <jobs>-1 secondary afl-fuzz instances
#   tools/fuzz.sh status [target]            afl-whatsup for every target (or one)
#   tools/fuzz.sh findings [target]          replay crashes and hangs uninstrumented
#
# Work lives in tools/fuzz-work/ (not committed). Targets: load identify metadata edit encode.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
work=${FUZZ_WORK:-$root/tools/fuzz-work}
project=$root/tools/PictureSharp.Tiff.Fuzz
targets=(load identify metadata edit encode)
# The time limit per input in ms. Uninstrumented, the slowest seed takes about 0.13 s.
timeout=${FUZZ_TIMEOUT:-2000}
# Seeds above this size slow every execution; AFL++ works best on small inputs.
max_seed=${FUZZ_MAX_SEED:-65536}

# The fork server is the .NET runtime starting up; on a loaded machine that takes longer than AFL++'s default.
export AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 AFL_NO_UI=1 AFL_SKIP_BIN_CHECK=1 AFL_FORKSRV_INIT_TMOUT=60000
export DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_gcServer=0

harness() { echo "$work/bin/PictureSharp.Tiff.Fuzz"; }

build() {
  dotnet build -c Release "$project" -o "$work/bin" -v quiet -nologo
  (cd "$root/tools" && dotnet tool restore >/dev/null && dotnet sharpfuzz "$work/bin/PictureSharp.Tiff.dll")
  # A second build would copy the uninstrumented DLL back; the stamp marks this one as instrumented.
  date -u +%FT%TZ > "$work/bin/instrumented.stamp"
}

# Options prefixes for the encode target: one per compression, with varied colour types and layouts (FuzzTargets.EncoderOptions).
encode_prefixes=(00000000 01001040 10000000 0a008801 04012050 05000000 06080340 0d0b8309)

seeds() {
  local raw=$work/seeds-raw
  rm -rf "$raw" && mkdir -p "$raw/tiff" "$raw/encode"
  local i=0
  while IFS= read -r -d '' file; do
    case $(head -c 4 "$file" | od -An -tx1 | tr -d ' \n') in 49492a00 | 49492b00 | 4d4d002a | 4d4d002b) ;; *) continue ;; esac
    [ "$(stat -c %s "$file")" -le "$max_seed" ] || continue
    i=$((i + 1))
    cp "$file" "$raw/tiff/$i.tif"
    local prefix=${encode_prefixes[$((i % ${#encode_prefixes[@]}))]}
    { printf '%b' "$(echo "$prefix" | sed 's/../\\x&/g')"; cat "$file"; } > "$raw/encode/$i.bin"
  done < <(find "$root/tests/assets/corpus" "$root/tests/assets/external" -path '*/edited' -prune -o -path '*/results' -prune \
    -o -path '*/oracle' -prune -o -type f -print0 | sort -z)
  # afl-cmin is not used: afl-showmap, which it runs, restarts the target without the fork server after reading the
  # map size, and SharpFuzz then aborts (AFL++ 4.09c). afl-fuzz itself drops seeds that add no coverage from its favourites.
  rm -rf "$work/seeds" && mkdir -p "$work/seeds"
  for target in "${targets[@]}"; do
    local from=$raw/tiff
    [ "$target" = encode ] && from=$raw/encode
    cp -r "$from" "$work/seeds/$target"
  done
  echo "$i seed files per target"
}

run() {
  local target=$1 jobs=$2 seconds=$3
  local out=$work/out/$target
  mkdir -p "$out"
  local input=$work/seeds/$target
  # Resume a previous campaign of this target instead of starting over.
  [ -d "$out/main/queue" ] && input=-
  for ((j = 0; j < jobs; j++)); do
    local role=(-S "s$j")
    [ "$j" -eq 0 ] && role=(-M main)
    AFL_FINAL_SYNC=1 timeout --signal=INT "$((seconds + 30))" afl-fuzz "${role[@]}" -V "$seconds" -t "$timeout" -m none \
      -i "$input" -o "$out" -x "$root/tools/tiff.dict" -- "$(harness)" fuzz "$target" > "$out/afl-$j.log" 2>&1 &
    sleep 5
  done
  wait
}

status() {
  for target in "${@:-${targets[@]}}"; do
    [ -d "$work/out/$target" ] && { echo "== $target"; afl-whatsup -s "$work/out/$target" | grep -vE '^$'; }
  done
}

findings() {
  local plain=$work/plain
  dotnet build -c Release "$project" -o "$plain" -v quiet -nologo >/dev/null
  for target in "${@:-${targets[@]}}"; do
    # An input that kills the process (a stack overflow) stops afl-fuzz instead of being saved; it is .cur_input.
    for log in "$work"/out/"$target"/afl-*.log; do
      [ -e "$log" ] && grep -aq 'Unable to communicate with fork server' "$log" || continue
      local instance=main
      [ "${log##*afl-}" = 0.log ] || instance=s$(basename "$log" .log | cut -d- -f2)
      echo "== $target $instance died: $work/out/$target/$instance/.cur_input"
      "$plain/PictureSharp.Tiff.Fuzz" replay "$target" "$work/out/$target/$instance/.cur_input" 2>&1 | grep -vE '^   at ' | head -5 || true
    done
    for kind in crashes hangs; do
      local files=("$work"/out/"$target"/*/"$kind"/id:*)
      [ -e "${files[0]}" ] || continue
      echo "== $target $kind: ${#files[@]}"
      "$plain/PictureSharp.Tiff.Fuzz" replay "$target" "${files[@]}" | grep -vE '^   at ' || true
    done
  done
}

case ${1:-} in
  build) build ;;
  seeds) seeds ;;
  run) run "$2" "$3" "$4" ;;
  status) shift; status "$@" ;;
  findings) shift; findings "$@" ;;
  *) sed -n '2,10p' "$0"; exit 2 ;;
esac
