#!/usr/bin/env bash
# Fetches real-world TIFF files into tests/assets/external/<source>/ (git-ignored, never committed) and records the
# origin and SHA-256 of every file in tests/assets/external/SOURCES.tsv.
#
# Usage: tests/assets/fetch-external.sh <source>...
#   libtiff-images  libtiff 4.5.1's test/images, copied from reference/tiff-4.5.1 (no network)
#   libtiff-pics    the libtiff test image archive pics-3.8.0.tar.gz (download.osgeo.org)
#   gdal            GDAL v3.9.0 autotest/gcore/data/*.tif (sparse git checkout, github.com/OSGeo/gdal)
#   pillow          Pillow 10.2.0 Tests/images/*.tif* (sparse git checkout, github.com/python-pillow/Pillow)
#   exiftool        ExifTool 12.76 t/images/*.tif (sparse git checkout, github.com/exiftool/exiftool)
#
# Every source except libtiff-images downloads; run those only with the maintainer's consent. Afterwards run
# python3 -I tests/assets/external_oracle.py to write the libtiff reference (external.json).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
DEST="$ROOT/external"
mkdir -p "$DEST"

record() { # source, origin: appends one line per TIFF file of the source
  (cd "$DEST" && find "$1" -type f | LC_ALL=C sort | while read -r f; do
    printf '%s\t%s\t%s\n' "$f" "$2" "$(sha256sum "$f" | cut -d' ' -f1)"
  done) >> "$DEST/SOURCES.tsv"
}

sparse() { # source, repository, tag, directory, patterns...
  local src="$1" repo="$2" tag="$3" dir="$4"
  shift 4
  local tmp
  tmp="$(mktemp -d)"
  git clone -q --depth 1 --branch "$tag" --filter=blob:none --sparse "$repo" "$tmp/repo"
  git -C "$tmp/repo" sparse-checkout set --no-cone "${@/#/$dir/}"
  mkdir -p "$DEST/$src"
  find "$tmp/repo/$dir" -maxdepth 1 -type f -exec cp {} "$DEST/$src/" \;
  rm -rf "$tmp"
  record "$src" "$repo@$tag/$dir"
}

for source in "$@"; do
  if [ -d "$DEST/$source" ]; then
    echo "$source: already present"
    continue
  fi
  case "$source" in
    libtiff-images)
      mkdir -p "$DEST/$source"
      cp "$ROOT/../../reference/tiff-4.5.1/test/images/"*.tif* "$DEST/$source/"
      record "$source" "reference/tiff-4.5.1/test/images"
      ;;
    libtiff-pics)
      tmp="$(mktemp -d)"
      curl -fsSL https://download.osgeo.org/libtiff/pics-3.8.0.tar.gz -o "$tmp/pics.tar.gz"
      mkdir -p "$DEST/$source"
      tar -xzf "$tmp/pics.tar.gz" -C "$DEST/$source" --strip-components=1
      rm -rf "$tmp"
      record "$source" "https://download.osgeo.org/libtiff/pics-3.8.0.tar.gz"
      ;;
    gdal) sparse "$source" https://github.com/OSGeo/gdal v3.9.0 autotest/gcore/data '*.tif' '*.tiff' ;;
    pillow) sparse "$source" https://github.com/python-pillow/Pillow 10.2.0 Tests/images '*.tif' '*.tiff' ;;
    exiftool) sparse "$source" https://github.com/exiftool/exiftool 12.76 t/images '*.tif' ;;
    *) echo "unknown source: $source" >&2; exit 2 ;;
  esac
  echo "$source: $(find "$DEST/$source" -type f | wc -l) files"
done
