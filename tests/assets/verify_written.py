#!/usr/bin/env python3
"""Checks TIFF files written by the PictureSharp.Tiff encoder against libtiff 4.5.1 and Pillow 10.2.0.

Usage: python3 -I tests/assets/verify_written.py [directory]   (default: tests/assets/written)

The directory holds the files and written.json, which WrittenFileTests produces: for each file its size, the SHA-256 of
the straight 8-bit RGBA our decoder reads, the resolution in dots per inch and the SHA-256 of the ICC and XMP blocks.
Pillow must read the same pixels and metadata. libtiff must open the file without a warning or an error, return the
same ICC profile, and convert it with TIFFReadRGBAImage to the same pixels. libtiff premultiplies RGB with unassociated
alpha, (a * v + 127) / 255 as its UaToAa table computes, but passes grey with alpha through (putagreytile).
"""

import ctypes
import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image

lib = ctypes.CDLL("libtiff.so.6")
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFGetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFReadRGBAImageOriented.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]

IMAGEWIDTH, IMAGELENGTH, XMLPACKET, ICCPROFILE = 256, 257, 700, 34675

messages = []
HANDLER = ctypes.CFUNCTYPE(None, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p)


@HANDLER
def record(module, fmt, _args):
    messages.append(f"{(module or b'').decode()}: {(fmt or b'').decode()}")


lib.TIFFSetWarningHandler(record)
lib.TIFFSetErrorHandler(record)


def sha(data):
    return None if data is None else hashlib.sha256(bytes(data)).hexdigest()


def get_u32(tif, tag):
    value = ctypes.c_uint32()
    if not lib.TIFFGetField(tif, tag, ctypes.byref(value)):
        raise RuntimeError(f"tag {tag} missing")
    return value.value


def get_blob(tif, tag):
    count, ptr = ctypes.c_uint32(), ctypes.c_void_p()
    if not lib.TIFFGetField(tif, tag, ctypes.byref(count), ctypes.byref(ptr)):
        return None
    return ctypes.string_at(ptr, count.value)


def libtiff_read(path):
    """The top-left oriented TIFFReadRGBAImage raster as an (h, w, 4) array, the ICC profile and the XMP packet."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    if not tif:
        raise RuntimeError("TIFFOpen failed")
    try:
        w, h = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH)
        raster = (ctypes.c_uint32 * (w * h))()
        if not lib.TIFFReadRGBAImageOriented(tif, w, h, raster, 1, 1):
            raise RuntimeError("TIFFReadRGBAImageOriented failed")
        v = np.frombuffer(raster, np.uint32).reshape(h, w)
        rgba = np.dstack([(v >> s) & 0xFF for s in (0, 8, 16, 24)]).astype(np.uint8)
        return rgba, get_blob(tif, ICCPROFILE), get_blob(tif, XMLPACKET)
    finally:
        lib.TIFFClose(tif)


def premultiply(rgba):
    a = rgba[..., 3:4].astype(np.uint32)
    color = (rgba[..., :3].astype(np.uint32) * a + 127) // 255
    return np.concatenate([color, a], axis=2).astype(np.uint8)


def verify(path, entry):
    errors = []
    with Image.open(path) as im:
        im.load()
        pil = np.asarray(im.convert("RGBA"))
        premultiplied = im.mode == "RGBA"
        if im.size != (entry["width"], entry["height"]):
            errors.append(f"Pillow size {im.size}")
        if sha(pil.tobytes()) != entry["sha256"]:
            errors.append(f"Pillow pixels differ (mode {im.mode})")
        if sha(im.info.get("icc_profile")) != entry["icc"]:
            errors.append("Pillow ICC differs")
        if sha(im.tag_v2.get(XMLPACKET)) != entry["xmp"]:
            errors.append("Pillow XMP differs")
        dpi = [float(d) for d in im.info.get("dpi", (0, 0))]
        if any(abs(a - b) > 1e-3 for a, b in zip(dpi, entry["dpi"])):
            errors.append(f"Pillow dpi {dpi}")

    messages.clear()
    raster, icc, xmp = libtiff_read(path)
    if messages:
        errors.append("libtiff: " + "; ".join(messages))
    if not np.array_equal(raster, premultiply(pil) if premultiplied else pil):
        errors.append("libtiff pixels differ")
    if sha(icc) != entry["icc"]:
        errors.append("libtiff ICC differs")
    if sha(xmp) != entry["xmp"]:
        errors.append("libtiff XMP differs")
    return errors


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "written")
    with open(os.path.join(root, "written.json")) as f:
        manifest = json.load(f)
    failed = 0
    for name, entry in sorted(manifest.items()):
        errors = verify(os.path.join(root, name), entry)
        print(f"{'FAIL' if errors else 'ok  '} {name}" + ("".join(f"\n     {e}" for e in errors)))
        failed += bool(errors)
    print(f"{len(manifest) - failed}/{len(manifest)} files verified")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
