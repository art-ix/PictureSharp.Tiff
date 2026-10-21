#!/usr/bin/env python3
"""Checks TIFF files written by the PictureSharp.Tiff encoder against libtiff 4.5.1 and Pillow 10.2.0.

Usage: python3 -I tests/assets/verify_written.py [directory]   (default: tests/assets/written)

The directory holds the files and written.json, which WrittenFileTests produces: for each file its size, the SHA-256 of
the straight 8-bit RGBA our decoder reads, the resolution in dots per inch and the SHA-256 of the ICC and XMP blocks.
Pillow must read the same pixels and metadata. libtiff must open the file without a warning or an error, return the
same ICC profile, and convert it with TIFFReadRGBAImage to the same pixels. libtiff premultiplies RGB with unassociated
alpha, (a * v + 127) / 255 as its UaToAa table computes, but passes grey with alpha through (putagreytile).

For LZW, PackBits and CCITT Group 4 files, libtiff also writes the decoded strips again with the same layout, compression and
predictor (TIFFWriteEncodedStrip); its raw strips must equal ours byte for byte.
"""

import ctypes
import hashlib
import json
import os
import sys
import tempfile

import numpy as np
from PIL import Image

lib = ctypes.CDLL("libtiff.so.6")
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFGetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFReadRGBAImageOriented.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
lib.TIFFGetFieldDefaulted.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFSetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFNumberOfStrips.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfStrips.restype = ctypes.c_uint32
lib.TIFFStripSize.argtypes = [ctypes.c_void_p]
lib.TIFFStripSize.restype = ctypes.c_int64
lib.TIFFRawStripSize.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFRawStripSize.restype = ctypes.c_int64
lib.TIFFReadEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFReadEncodedStrip.restype = ctypes.c_int64
lib.TIFFReadRawStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFReadRawStrip.restype = ctypes.c_int64
lib.TIFFWriteEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFWriteEncodedStrip.restype = ctypes.c_int64

IMAGEWIDTH, IMAGELENGTH, BITSPERSAMPLE, COMPRESSION, PHOTOMETRIC = 256, 257, 258, 259, 262
SAMPLESPERPIXEL, ROWSPERSTRIP, PLANARCONFIG, PREDICTOR, EXTRASAMPLES = 277, 278, 284, 317, 338
XMLPACKET, ICCPROFILE = 700, 34675
CCITT_G4, LZW, DEFLATE, PACKBITS = 4, 5, 8, 32773

messages = []
totals = {LZW: 0, PACKBITS: 0, CCITT_G4: 0}
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


def get_u16(tif, tag, default=None):
    value = ctypes.c_uint16()
    if not lib.TIFFGetFieldDefaulted(tif, tag, ctypes.byref(value)):
        if default is None:
            raise RuntimeError(f"tag {tag} missing")
        return default
    return value.value


def read_strips(path, raw):
    """The strips of a file, raw or decoded, and the file's layout."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    if not tif:
        raise RuntimeError("TIFFOpen failed")
    try:
        # Predictor is known only to the codecs that use it.
        layout = {tag: get_u16(tif, tag) for tag in (BITSPERSAMPLE, COMPRESSION, SAMPLESPERPIXEL)}
        layout[PREDICTOR] = get_u16(tif, PREDICTOR, default=1) if layout[COMPRESSION] in (LZW, DEFLATE) else 1
        layout.update({tag: get_u32(tif, tag) for tag in (IMAGEWIDTH, IMAGELENGTH, ROWSPERSTRIP)})
        strips = []
        for s in range(lib.TIFFNumberOfStrips(tif)):
            size = lib.TIFFRawStripSize(tif, s) if raw else lib.TIFFStripSize(tif)
            buf = ctypes.create_string_buffer(size)
            n = (lib.TIFFReadRawStrip if raw else lib.TIFFReadEncodedStrip)(tif, s, buf, size)
            if n < 0:
                raise RuntimeError(f"strip {s} unreadable")
            strips.append(buf.raw[:n])
        return layout, strips
    finally:
        lib.TIFFClose(tif)


def libtiff_reencode(path, layout, strips):
    """The raw strips libtiff writes for the decoded strips with the same layout, compression and predictor."""
    with tempfile.TemporaryDirectory() as tmp:
        out_path = os.path.join(tmp, "libtiff.tif")
        tif = lib.TIFFOpen(out_path.encode(), b"w")
        spp = layout[SAMPLESPERPIXEL]
        for tag in (IMAGEWIDTH, IMAGELENGTH, ROWSPERSTRIP):
            lib.TIFFSetField(tif, tag, ctypes.c_uint32(layout[tag]))
        for tag, value in ((BITSPERSAMPLE, layout[BITSPERSAMPLE]), (SAMPLESPERPIXEL, spp), (PLANARCONFIG, 1),
                           (PHOTOMETRIC, (0 if layout[BITSPERSAMPLE] == 1 else 1) if spp <= 2 else 2),
                           (COMPRESSION, layout[COMPRESSION])):
            lib.TIFFSetField(tif, tag, ctypes.c_int(value))
        if layout[PREDICTOR] != 1:
            lib.TIFFSetField(tif, PREDICTOR, ctypes.c_int(layout[PREDICTOR]))
        if spp in (2, 4):
            lib.TIFFSetField(tif, EXTRASAMPLES, ctypes.c_int(1), ctypes.byref(ctypes.c_uint16(2)))
        for s, data in enumerate(strips):
            if lib.TIFFWriteEncodedStrip(tif, s, data, len(data)) < 0:
                raise RuntimeError(f"libtiff could not write strip {s}")
        lib.TIFFClose(tif)
        return read_strips(out_path, raw=True)[1]


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

    layout, ours = read_strips(path, raw=True)
    if layout[COMPRESSION] in totals:
        theirs = libtiff_reencode(path, layout, read_strips(path, raw=False)[1])
        if messages:
            errors.append("libtiff re-encoding: " + "; ".join(messages))
        different = [i for i, (a, b) in enumerate(zip(ours, theirs)) if a != b]
        if len(ours) != len(theirs) or different:
            errors.append(f"libtiff writes different strips: {different[:5]} ({sum(map(len, ours))} vs {sum(map(len, theirs))} bytes)")
        else:
            totals[layout[COMPRESSION]] += sum(map(len, ours))
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
    print(f"{len(manifest) - failed}/{len(manifest)} files verified; strips identical to libtiff: "
          f"LZW {totals[LZW]} bytes, PackBits {totals[PACKBITS]} bytes, Group 4 {totals[CCITT_G4]} bytes")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
