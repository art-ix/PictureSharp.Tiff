#!/usr/bin/env python3
"""libtiff reference for the real-world files in tests/assets/external/ (git-ignored, see fetch-external.sh).

For every TIFF file and every IFD of its main chain it records the layout tags and, when libtiff 4.5.1 converts the
IFD with TIFFReadRGBAImageOriented, the 8-bit RGBA raster in the file's stored orientation (the same order the plugin
decodes into) as external/oracle/<file>.<ifd>.rgba. Greyscale, RGB and CMYK that TIFFReadRGBAImage rejects
(floating point, integer depths it does not convert such as 10, 12 and 32 bits, 16-bit CMYK) get the raster of the documented rule
(docs/decoding.md) applied to the samples libtiff decodes with TIFFReadScanline or TIFFReadTile ("oracle": "model"). Files and IFDs libtiff rejects keep libtiff's error message.
The index goes to external/external.json, which ExternalTests (Category=Local) reads.

With --edited it instead checks the files ExternalTests rewrote with Image.EditMetadata (external/edited/): libtiff
must read the same IFDs as in the original and convert each to the same raster, without new errors.

Run with:  python3 -I tests/assets/external_oracle.py [--edited]
"""

import ctypes
import hashlib
import json
import re
import os
import sys

import numpy as np

ROOT = os.path.dirname(os.path.abspath(__file__))
EXTERNAL = os.path.join(ROOT, "external")
ORACLE = os.path.join(EXTERNAL, "oracle")
MAX_PIXELS = 1 << 26
MAX_IFDS = 64

lib = ctypes.CDLL("libtiff.so.6")
libc = ctypes.CDLL(None)
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFReadDirectory.argtypes = [ctypes.c_void_p]
lib.TIFFRGBAImageOK.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
lib.TIFFReadRGBAImageOriented.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                          ctypes.c_int, ctypes.c_int]
lib.TIFFGetFieldDefaulted.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFIsTiled.argtypes = [ctypes.c_void_p]
lib.TIFFScanlineSize.argtypes = [ctypes.c_void_p]
lib.TIFFScanlineSize.restype = ctypes.c_ssize_t
lib.TIFFReadScanline.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint16]
lib.TIFFTileSize.argtypes = [ctypes.c_void_p]
lib.TIFFTileSize.restype = ctypes.c_ssize_t
lib.TIFFReadTile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32,
                             ctypes.c_uint16]
lib.TIFFReadTile.restype = ctypes.c_ssize_t
libc.vsnprintf.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p]

errors = []

# On x86-64 and ARM64 Linux a va_list argument arrives as a pointer that vsnprintf accepts as it is.
HANDLER = ctypes.CFUNCTYPE(None, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p)


@HANDLER
def on_error(module, fmt, ap):
    buf = ctypes.create_string_buffer(1024)
    libc.vsnprintf(buf, len(buf), fmt, ap)
    errors.append(buf.value.decode("latin-1"))


lib.TIFFSetErrorHandler(on_error)
lib.TIFFSetWarningHandler(None)

TAGS = dict(subfile=254, oldsubfile=255, width=256, height=257, bits=258, compression=259, photometric=262,
            orientation=274, samples=277, planar=284, format=339)


def field(tif, tag, kind=ctypes.c_uint16):
    v = kind()
    return v.value if lib.TIFFGetFieldDefaulted(ctypes.c_void_p(tif), tag, ctypes.byref(v)) else None


def extra_samples(tif):
    count, values = ctypes.c_uint16(), ctypes.POINTER(ctypes.c_uint16)()
    if not lib.TIFFGetFieldDefaulted(ctypes.c_void_p(tif), 338, ctypes.byref(count), ctypes.byref(values)):
        return []
    return [values[i] for i in range(count.value)]


def row_values(raw, count, page):
    """`count` samples of one row as libtiff returns them: native order for whole bytes, MSB first below."""
    bits, fmt = page["bits"], page["format"]
    if fmt == 3:
        return np.frombuffer(raw, {16: np.float16, 32: np.float32, 64: np.float64}[bits], count)
    if bits in (8, 16, 32):
        kind = "i" if fmt == 2 else "u"
        return np.frombuffer(raw, f"={kind}{bits // 8}", count).astype(np.int64)
    packed = np.unpackbits(np.frombuffer(raw, np.uint8, (count * bits + 7) // 8))[:count * bits].reshape(count, bits)
    return packed.astype(np.int64) @ (1 << np.arange(bits - 1, -1, -1, dtype=np.int64))


def read_samples(tif, page):
    """The decoded samples (height, width, samples), read with TIFFReadTile or TIFFReadScanline."""
    w, h, spp = page["width"], page["height"], page["samples"]
    planes = spp if page["planar"] == 2 else 1
    per = 1 if planes > 1 else spp
    out = np.zeros((h, w, spp), np.float64 if page["format"] == 3 else np.int64)
    t = ctypes.c_void_p(tif)
    if lib.TIFFIsTiled(t):
        tw, th = field(tif, 322, ctypes.c_uint32), field(tif, 323, ctypes.c_uint32)
        size = lib.TIFFTileSize(t)
        buf = ctypes.create_string_buffer(size)
        row = size // th
        for p in range(planes):
            for y in range(0, h, th):
                for x in range(0, w, tw):
                    if lib.TIFFReadTile(t, buf, x, y, 0, p) < 0:
                        raise RuntimeError("; ".join(errors) or "TIFFReadTile failed")
                    ch, cw = min(th, h - y), min(tw, w - x)
                    for r in range(ch):
                        v = row_values(buf.raw[r * row:(r + 1) * row], tw * per, page).reshape(tw, per)
                        out[y + r, x:x + cw, p:p + per] = v[:cw]
    else:
        buf = ctypes.create_string_buffer(lib.TIFFScanlineSize(t))
        for p in range(planes):
            for y in range(h):
                if lib.TIFFReadScanline(t, buf, y, p) < 0:
                    raise RuntimeError("; ".join(errors) or "TIFFReadScanline failed")
                out[y, :, p:p + per] = row_values(buf.raw, w * per, page).reshape(w, per)
    if page["format"] == 3:
        return out.astype({16: np.float32, 32: np.float32, 64: np.float64}[page["bits"]])
    return out


def plugin_photometric(page):
    """docs/decoding.md: without the tag, RGB for three or more samples, MinIsBlack otherwise."""
    if page["photometric"] >= 0:
        return page["photometric"]
    return 2 if page["samples"] >= 3 else 1


def model_rgba(v, page):
    """docs/decoding.md, as tests/assets/generate_corpus.py applies it: the extra sample after the colour samples is
    associated alpha (ExtraSamples 1), ignored (0) or straight alpha (other values, or no tag with one extra sample);
    unsigned b-bit samples round(v * 255 / (2^b - 1)), signed ones offset by 2^(b - 1) first; floating point clamped
    to [0, 1], then (int)(v * 255 + 0.5), NaN to 0, in single precision (double for 64 bits); MinIsWhite inverted."""
    h, w, spp = v.shape
    page = dict(page, photometric=plugin_photometric(page))
    color = {2: 3, 5: 4}.get(page["photometric"], 1)
    extra = page["extra"]
    mode = (["ignored", "assoc"][extra[0]] if extra[0] in (0, 1) else "straight") if extra else \
        ("straight" if spp == color + 1 else "ignored")
    if spp == color:
        mode = "none"
    if page["format"] == 3:
        t = v.dtype.type
        c = v[..., :color]
        a = v[..., color] if mode in ("assoc", "straight") else np.ones((h, w), t)
        if page["photometric"] == 0:
            c = (t(1) - c).astype(t)
        if mode == "assoc":
            div = np.where(a > 0, a, t(1))[..., None]
            c = np.where(a[..., None] == 1, c, np.where(a[..., None] > 0, (c / div).astype(t), t(0)))

        def to8(x):
            scaled = np.floor(np.clip(x, 0, 1) * t(255) + t(0.5))
            return np.where(~(x > 0), 0, np.where(x >= 1, 255, scaled)).astype(np.uint8)
    else:
        bits = page["bits"]
        top = (1 << bits) - 1
        if page["format"] == 2:
            v = v + (1 << (bits - 1))
        c = v[..., :color]
        a = v[..., color] if mode in ("assoc", "straight") else np.full((h, w), top, np.int64)
        if page["photometric"] == 0:
            c = top - c
        if mode == "assoc":
            out = np.where(a[..., None] == top, c, np.minimum(top, c * top // np.maximum(a, 1)[..., None]))
            c = np.where(a[..., None] == 0, 0, out)

        def to8(x):
            x = x.astype(object if bits > 16 else np.int64)
            return ((x * 510 + top) // (2 * top)).astype(np.uint8)
    c8 = to8(c)
    if color == 4:
        # Pillow's cmyk2rgb on the reduced inks, as the plugin converts CMYK.
        k = c8[..., 3].astype(np.int64)
        nk = 255 - k
        channels = []
        for i in range(3):
            t = c8[..., i].astype(np.int64) * nk + 128
            channels.append(nk - (((t >> 8) + t) >> 8))
        c8 = np.dstack(channels).astype(np.uint8)
    elif color == 1:
        c8 = np.repeat(c8, 3, axis=2)
    return np.dstack([c8, to8(a)])


def model_applies(page):
    """Greyscale, RGB and CMYK that TIFFReadRGBAImage rejects but the plugin reads: other integer depths, floating
    point, 16-bit CMYK."""
    photometric = plugin_photometric(page)
    if photometric not in (0, 1, 2, 5) or page["samples"] < {2: 3, 5: 4}.get(photometric, 1):
        return False
    if photometric == 5:
        return page["format"] == 1 and page["bits"] in (8, 16)
    if page["format"] == 3:
        return page["bits"] in (16, 32, 64)
    return page["format"] in (1, 2) and (page["bits"] <= 16 or page["bits"] == 32) and \
        (page["format"] == 1 or page["bits"] in (8, 16, 32))


def is_tiff(path):
    with open(path, "rb") as f:
        head = f.read(4)
    return head in (b"II*\0", b"MM\0*", b"II+\0", b"MM\0+")


def save(rel, ifd, data):
    out = os.path.join(ORACLE, f"{rel}.{ifd}.rgba")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(data)
    return os.path.relpath(out, EXTERNAL)


def model(tif, rel, ifd, page):
    """The model raster from libtiff's samples, for an IFD TIFFReadRGBAImage does not convert."""
    if not model_applies(page) or page["width"] * page["height"] * page["samples"] > 4 * MAX_PIXELS:
        return
    del errors[:]
    try:
        page["rgba"] = save(rel, ifd, model_rgba(read_samples(tif, page), page).tobytes())
        page["oracle"] = "model"
    except (RuntimeError, ValueError) as e:
        page["libtiff"] += f"; samples: {e}"


def directory(tif, rel, ifd):
    page = dict(ifd=ifd)
    page["width"] = field(tif, TAGS["width"], ctypes.c_uint32) or 0
    page["height"] = field(tif, TAGS["height"], ctypes.c_uint32) or 0
    for name in ("bits", "compression", "orientation", "samples", "planar", "format"):
        page[name] = field(tif, TAGS[name]) or 0
    # -1 without the tag.
    photometric = field(tif, TAGS["photometric"])
    page["photometric"] = -1 if photometric is None else photometric
    page["extra"] = extra_samples(tif)
    subfile = field(tif, TAGS["subfile"], ctypes.c_uint32) or 0
    page["reduced"] = bool(subfile & 1) or field(tif, TAGS["oldsubfile"]) == 2
    w, h = page["width"], page["height"]
    message = ctypes.create_string_buffer(1024)
    del errors[:]
    if w * h > MAX_PIXELS:
        page["libtiff"] = f"skipped: {w}x{h} is above {MAX_PIXELS} pixels"
    elif not lib.TIFFRGBAImageOK(ctypes.c_void_p(tif), message):
        page["libtiff"] = message.value.decode("latin-1")
        model(tif, rel, ifd, page)
    else:
        raster = (ctypes.c_uint32 * (w * h))()
        orientation = page["orientation"] or 1
        ok = lib.TIFFReadRGBAImageOriented(ctypes.c_void_p(tif), w, h, raster, orientation, 0)
        if not ok:
            page["libtiff"] = "; ".join(errors) or "TIFFReadRGBAImageOriented failed"
            model(tif, rel, ifd, page)
        else:
            # The raster is ABGR in host order, little-endian here: bytes R, G, B, A.
            page["rgba"] = save(rel, ifd, bytes(raster))
            page["oracle"] = "libtiff"
            if errors:
                page["warnings"] = "; ".join(errors)
    return page


def main():
    assert sys.byteorder == "little"
    index = {}
    for source in sorted(os.listdir(EXTERNAL)):
        base = os.path.join(EXTERNAL, source)
        if source in ("oracle", "edited", "results") or not os.path.isdir(base):
            continue
        for dirpath, _, names in os.walk(base):
            for name in sorted(names):
                path = os.path.join(dirpath, name)
                if not is_tiff(path):
                    continue
                rel = os.path.relpath(path, EXTERNAL)
                with open(path, "rb") as f:
                    entry = dict(sha256=hashlib.sha256(f.read()).hexdigest(), pages=[])
                del errors[:]
                tif = lib.TIFFOpen(path.encode(), b"r")
                if not tif:
                    entry["libtiff"] = "; ".join(errors) or "TIFFOpen failed"
                else:
                    try:
                        ifd = 0
                        while True:
                            entry["pages"].append(directory(tif, rel, ifd))
                            ifd += 1
                            del errors[:]
                            if ifd >= MAX_IFDS or not lib.TIFFReadDirectory(ctypes.c_void_p(tif)):
                                if errors:
                                    entry["libtiff"] = "; ".join(errors)
                                break
                    finally:
                        lib.TIFFClose(ctypes.c_void_p(tif))
                index[rel] = entry
    with open(os.path.join(EXTERNAL, "external.json"), "w") as f:
        json.dump(index, f, indent=1, sort_keys=True)
    pages = [p for e in index.values() for p in e["pages"]]
    print(f"{len(index)} files, {len(pages)} IFDs, {sum(p.get('oracle') == 'libtiff' for p in pages)} converted by libtiff, "
          f"{sum(p.get('oracle') == 'model' for p in pages)} by the model from libtiff's samples, "
          f"{sum('libtiff' in e for e in index.values())} files with libtiff errors")


def rasters(path):
    """libtiff's raster (or error) of every IFD of the main chain, and the errors it reported."""
    del errors[:]
    out = []
    tif = lib.TIFFOpen(path.encode(), b"r")
    if tif:
        try:
            while len(out) < MAX_IFDS:
                w, h = field(tif, TAGS["width"], ctypes.c_uint32) or 0, field(tif, TAGS["height"], ctypes.c_uint32) or 0
                ok = False
                if w * h <= MAX_PIXELS:
                    raster = (ctypes.c_uint32 * max(1, w * h))()
                    ok = lib.TIFFReadRGBAImageOriented(ctypes.c_void_p(tif), w, h, raster, field(tif, TAGS["orientation"]) or 1, 0)
                out.append(hashlib.sha256(bytes(raster)).hexdigest() if ok else "rejected")
                if not lib.TIFFReadDirectory(ctypes.c_void_p(tif)):
                    break
        finally:
            lib.TIFFClose(ctypes.c_void_p(tif))
    # libtiff starts some messages with the file name.
    return out, {re.sub(r"\d+", "#", e.replace(path + ": ", "")) for e in errors}


def verify_edited():
    edited_dir = os.path.join(EXTERNAL, "edited")
    checked, failed = 0, 0
    for dirpath, _, names in os.walk(edited_dir):
        for name in sorted(names):
            path = os.path.join(dirpath, name)
            original = os.path.join(EXTERNAL, os.path.relpath(path, edited_dir))
            before, before_errors = rasters(original)
            after, after_errors = rasters(path)
            # Each raster is requested in the IFD's own orientation, so the edited orientation changes nothing.
            problems = []
            if before != after:
                problems.append(f"{sum(a != b for a, b in zip(before, after))} of {len(before)} IFDs differ "
                                f"({len(after)} after)")
            new = after_errors - before_errors
            if new:
                problems.append("new libtiff errors: " + "; ".join(sorted(new)))
            checked += 1
            if problems:
                failed += 1
                print(f"FAIL {os.path.relpath(original, EXTERNAL)}: {'; '.join(problems)}")
    print(f"{checked} edited files checked, {failed} failed")


if __name__ == "__main__":
    verify_edited() if "--edited" in sys.argv else main()
