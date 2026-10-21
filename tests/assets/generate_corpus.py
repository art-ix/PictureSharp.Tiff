#!/usr/bin/env python3
"""Generates the deterministic TIFF corpus in tests/assets/corpus and the expected pixels in reference.json.

Files are written by the system libtiff (libtiff.so.6, 4.5.1 like Pillow 10.2.0's) through ctypes, which reaches
every layout tiffcp would (strips, tiles, separate planes, predictor 2, every compression, fill order 2, both byte
orders, reduced-resolution pages). Two small writers below cover what libtiff cannot write: old-style LSB-first LZW
and an IFD without StripByteCounts.

Expected pixels come from the source arrays and the decoder's documented rules: n-bit samples become
v * 255 // max, 16-bit ones (v + 128) // 257, WhiteIsZero is inverted, premultiplied colour is divided by alpha at
the source depth (min(max, v * max // a), 0 where a is 0) and colour maps are (v + 128) // 257. Wherever Pillow
10.2.0 decodes a file to 8-bit RGBA without its own rounding, its pixels must match exactly (oracle "pillow");
the others are checked against the model only (oracle "model").

Run with:  python3 -I tests/assets/generate_corpus.py
"""
import ctypes
import hashlib
import io
import json
import os
import shutil
import struct

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(ROOT, "corpus")

lib = ctypes.CDLL("libtiff.so.6")
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFSetField.restype = ctypes.c_int
lib.TIFFWriteEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFWriteEncodedStrip.restype = ctypes.c_ssize_t
lib.TIFFWriteEncodedTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFWriteEncodedTile.restype = ctypes.c_ssize_t
lib.TIFFWriteDirectory.argtypes = [ctypes.c_void_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFSetErrorHandler(None)
lib.TIFFSetWarningHandler(None)

# libtiff tag numbers (tiff.h)
SUBFILETYPE, IMAGEWIDTH, IMAGELENGTH, BITSPERSAMPLE, COMPRESSION = 254, 256, 257, 258, 259
PHOTOMETRIC, FILLORDER, ORIENTATION, SAMPLESPERPIXEL, ROWSPERSTRIP = 262, 266, 274, 277, 278
XRESOLUTION, YRESOLUTION, PLANARCONFIG, RESOLUTIONUNIT, PREDICTOR = 282, 283, 284, 296, 317
COLORMAP, TILEWIDTH, TILELENGTH, EXTRASAMPLES, XMLPACKET, ICCPROFILE = 320, 322, 323, 338, 700, 34675
COMPRESSIONS = dict(none=1, lzw=5, deflate=8, packbits=32773, adobe=32946)
PHOTOMETRICS = dict(miniswhite=0, minisblack=1, rgb=2, palette=3)


# --------------------------------------------------------------------------- sources

def photo(w, h, seed, depth=8):
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:h, 0:w]
    img = np.zeros((h, w, 3))
    img[..., 0] = 0.5 + 0.4 * np.sin(xx / 7.0 + seed)
    img[..., 1] = 0.5 + 0.4 * np.cos(yy / 5.0)
    img[..., 2] = ((xx + yy * 3) % w) / max(1, w - 1)
    img += rng.normal(0, 0.03, img.shape)
    top = (1 << depth) - 1
    return np.clip(np.rint(img * top), 0, top).astype(np.uint16 if depth > 8 else np.uint8)


def alpha(w, h, depth=8):
    yy, xx = np.mgrid[0:h, 0:w]
    top = (1 << depth) - 1
    a = ((xx * 7 + yy * 13) * (top // 255 if depth > 8 else 1)) % (top + 1)
    a[: h // 4, :] = top
    a[h // 4: h // 3, :] = 0
    return a.astype(np.uint16 if depth > 8 else np.uint8)


def premultiply(color, a, depth):
    top = (1 << depth) - 1
    return ((color.astype(np.int64) * a[..., None] + top // 2) // top).astype(color.dtype)


# --------------------------------------------------------------------------- expected pixels (the decoder's rules)

def to8(v, bits):
    v = v.astype(np.int64)
    if bits == 16:
        return ((v + 128) // 257).astype(np.uint8)
    return (v * 255 // ((1 << bits) - 1)).astype(np.uint8)


def unpremultiply(color, a, bits):
    top = (1 << bits) - 1
    c = color.astype(np.int64)
    a = a.astype(np.int64)[..., None]
    out = np.where(a == top, c, np.minimum(top, c * top // np.maximum(a, 1)))
    return np.where(a == 0, 0, out)


def expected(samples, bits, photometric, extra=None, colormap=None):
    """samples: (h, w, spp) as stored. extra: None, 'straight', 'assoc' or 'ignored'."""
    h, w, spp = samples.shape
    top = (1 << bits) - 1
    color_count = 3 if photometric == "rgb" else 1
    if photometric == "palette":
        cmap = (np.asarray(colormap, np.int64).reshape(3, -1) + 128) // 257
        idx = samples[..., 0].astype(np.int64)
        rgb = np.stack([cmap[0][idx], cmap[1][idx], cmap[2][idx]], -1).astype(np.uint8)
        return np.dstack([rgb, np.full((h, w), 255, np.uint8)])
    color = samples[..., :color_count].astype(np.int64)
    if photometric == "miniswhite":
        color = top - color
    if extra in ("straight", "assoc"):
        a = samples[..., color_count].astype(np.int64)
        if extra == "assoc":
            color = unpremultiply(color, a, bits)
        a8 = to8(a, bits)
    else:
        a8 = np.full((h, w), 255, np.uint8)
    c8 = to8(color, bits)
    if color_count == 1:
        c8 = np.repeat(c8, 3, -1)
    return np.dstack([c8, a8]).astype(np.uint8)


# --------------------------------------------------------------------------- libtiff writer

def pack_rows(samples, bits):
    """Packs (rows, w, spp) samples into rows padded to whole bytes, MSB first, native 16-bit order."""
    rows, w, spp = samples.shape
    flat = samples.reshape(rows, w * spp)
    if bits == 8:
        return flat.astype(np.uint8).tobytes()
    if bits == 16:
        return flat.astype("=u2").tobytes()
    out = bytearray()
    for r in range(rows):
        bitstr = 0
        for v in flat[r]:
            bitstr = (bitstr << bits) | int(v)
        nbits = w * spp * bits
        pad = (-nbits) % 8
        out += (bitstr << pad).to_bytes((nbits + pad) // 8, "big")
    return bytes(out)


def set_field(tif, tag, *args):
    if lib.TIFFSetField(ctypes.c_void_p(tif), ctypes.c_uint32(tag), *args) != 1:
        raise RuntimeError(f"TIFFSetField {tag} failed")


def write_directory(tif, samples, bits, photometric, compression="none", predictor=1, rows_per_strip=None,
                    tile=None, separate=False, extra=None, colormap=None, fill_order=1, subfile=0,
                    orientation=None, resolution=None, icc=None, xmp=None):
    h, w, spp = samples.shape
    keep = []
    set_field(tif, IMAGEWIDTH, ctypes.c_uint32(w))
    set_field(tif, IMAGELENGTH, ctypes.c_uint32(h))
    set_field(tif, BITSPERSAMPLE, ctypes.c_int(bits))
    set_field(tif, SAMPLESPERPIXEL, ctypes.c_int(spp))
    set_field(tif, COMPRESSION, ctypes.c_int(COMPRESSIONS[compression]))
    set_field(tif, PHOTOMETRIC, ctypes.c_int(PHOTOMETRICS[photometric]))
    set_field(tif, PLANARCONFIG, ctypes.c_int(2 if separate else 1))
    if subfile:
        set_field(tif, SUBFILETYPE, ctypes.c_uint32(subfile))
    if fill_order != 1:
        set_field(tif, FILLORDER, ctypes.c_int(fill_order))
    if predictor != 1:
        set_field(tif, PREDICTOR, ctypes.c_int(predictor))
    if extra is not None:
        arr = (ctypes.c_uint16 * len(extra))(*extra)
        keep.append(arr)
        set_field(tif, EXTRASAMPLES, ctypes.c_int(len(extra)), arr)
    if colormap is not None:
        n = len(colormap) // 3
        maps = [(ctypes.c_uint16 * n)(*colormap[i * n:(i + 1) * n]) for i in range(3)]
        keep += maps
        set_field(tif, COLORMAP, *maps)
    if orientation:
        set_field(tif, ORIENTATION, ctypes.c_int(orientation))
    if resolution:
        x, y, unit = resolution
        set_field(tif, XRESOLUTION, ctypes.c_double(x))
        set_field(tif, YRESOLUTION, ctypes.c_double(y))
        set_field(tif, RESOLUTIONUNIT, ctypes.c_int(unit))
    for tag, blob in ((ICCPROFILE, icc), (XMLPACKET, xmp)):
        if blob:
            buf = ctypes.create_string_buffer(blob, len(blob))
            keep.append(buf)
            set_field(tif, tag, ctypes.c_uint32(len(blob)), buf)

    planes = [samples[..., p:p + 1] for p in range(spp)] if separate else [samples]
    if tile:
        tw, tl = tile
        set_field(tif, TILEWIDTH, ctypes.c_uint32(tw))
        set_field(tif, TILELENGTH, ctypes.c_uint32(tl))
        across, down = -(-w // tw), -(-h // tl)
        for p, plane in enumerate(planes):
            padded = np.zeros((down * tl, across * tw, plane.shape[2]), plane.dtype)
            padded[:h, :w] = plane
            for ty in range(down):
                for tx in range(across):
                    data = pack_rows(padded[ty * tl:(ty + 1) * tl, tx * tw:(tx + 1) * tw], bits)
                    index = p * across * down + ty * across + tx
                    if lib.TIFFWriteEncodedTile(tif, index, data, len(data)) < 0:
                        raise RuntimeError("TIFFWriteEncodedTile failed")
    else:
        rps = rows_per_strip or h
        set_field(tif, ROWSPERSTRIP, ctypes.c_uint32(rps))
        strips = -(-h // rps)
        for p, plane in enumerate(planes):
            for s in range(strips):
                data = pack_rows(plane[s * rps:(s + 1) * rps], bits)
                if lib.TIFFWriteEncodedStrip(tif, p * strips + s, data, len(data)) < 0:
                    raise RuntimeError("TIFFWriteEncodedStrip failed")
    if lib.TIFFWriteDirectory(tif) != 1:
        raise RuntimeError("TIFFWriteDirectory failed")


def write_tiff(path, pages, big_endian=False):
    tif = lib.TIFFOpen(path.encode(), b"wb" if big_endian else b"wl")
    if not tif:
        raise RuntimeError(f"TIFFOpen {path} failed")
    try:
        for page in pages:
            write_directory(tif, **page)
    finally:
        lib.TIFFClose(tif)


# --------------------------------------------------------------------------- hand-made writers

def lzw_old_style(data):
    """LSB-first LZW without early change, as libtiff wrote before 3.0 (decoded by LZWDecodeCompat)."""
    out, acc, nacc = bytearray(), 0, 0
    width, dec_free = 9, 258

    def emit(code):
        nonlocal acc, nacc
        acc |= code << nacc
        nacc += width
        while nacc >= 8:
            out.append(acc & 0xFF)
            acc >>= 8
            nacc -= 8

    def clear():
        nonlocal width, dec_free, table, next_code, emitted
        emit(256)
        width, dec_free, next_code, emitted = 9, 258, 258, 0
        table = {bytes([i]): i for i in range(256)}

    def emit_code(code):
        # Follows the decoder's table: it adds an entry for every code but the first after a clear and widens the
        # codes once its next free entry passes 2^width - 1.
        nonlocal emitted, dec_free, width
        emit(code)
        emitted += 1
        if emitted >= 2:
            dec_free += 1
            if dec_free > (1 << width) - 1 and width < 12:
                width += 1

    table, next_code, emitted = {}, 258, 0
    clear()
    w = b""
    for b in data:
        wk = w + bytes([b])
        if wk in table:
            w = wk
            continue
        emit_code(table[w])
        table[wk] = next_code
        next_code += 1
        w = bytes([b])
        if next_code >= 4093:
            emit_code(table[w])
            w = b""
            clear()
    if w:
        emit_code(table[w])
    emit(257)
    if nacc:
        out.append(acc & 0xFF)
    return bytes(out)


def write_classic(path, w, h, bits, spp, photometric, compression, strips, byte_counts=True):
    """A little-endian TIFF with the given strips; byte_counts=False omits StripByteCounts."""
    entries = [(IMAGEWIDTH, 4, [w]), (IMAGELENGTH, 4, [h]), (BITSPERSAMPLE, 3, [bits] * spp),
               (COMPRESSION, 3, [compression]), (PHOTOMETRIC, 3, [photometric]),
               (SAMPLESPERPIXEL, 3, [spp]), (ROWSPERSTRIP, 4, [-(-h // len(strips))])]
    header = 8
    blob = bytearray()
    offsets = []
    for s in strips:
        offsets.append(header + len(blob))
        blob += s
        if len(blob) % 2:
            blob += b"\0"
    entries.append((273, 4, offsets))
    if byte_counts:
        entries.append((279, 4, [len(s) for s in strips]))
    entries.sort()
    ifd_offset = header + len(blob)
    extra = bytearray()
    ifd = bytearray(struct.pack("<H", len(entries)))
    data_offset = ifd_offset + 2 + 12 * len(entries) + 4
    for tag, typ, values in entries:
        fmt = "<H" if typ == 3 else "<I"
        raw = b"".join(struct.pack(fmt, v) for v in values)
        if len(raw) <= 4:
            ifd += struct.pack("<HHI", tag, typ, len(values)) + raw.ljust(4, b"\0")
        else:
            ifd += struct.pack("<HHII", tag, typ, len(values), data_offset + len(extra))
            extra += raw
    ifd += struct.pack("<I", 0)
    with open(path, "wb") as f:
        f.write(b"II*\0" + struct.pack("<I", ifd_offset) + blob + ifd + extra)


# --------------------------------------------------------------------------- corpus

def cases():
    w, h = 37, 23
    rgb = photo(w, h, 1)
    rgb16 = photo(w, h, 2, 16)
    a8, a16 = alpha(w, h), alpha(w, h, 16)
    gray = (rgb.astype(np.int64) @ [299, 587, 114] // 1000).astype(np.uint8)
    gray16 = (rgb16.astype(np.int64) @ [299, 587, 114] // 1000).astype(np.uint16)
    big = photo(160, 120, 3)
    s = lambda a: a if a.ndim == 3 else a[..., None]

    yield "rgb8_none", [dict(samples=rgb, bits=8, photometric="rgb")], {}
    for comp in ("packbits", "lzw", "deflate", "adobe"):
        yield f"rgb8_{comp}_strips", [dict(samples=rgb, bits=8, photometric="rgb", compression=comp, rows_per_strip=5)], {}
    yield "rgb8_lzw_predictor", [dict(samples=big, bits=8, photometric="rgb", compression="lzw", predictor=2, rows_per_strip=16)], {}
    yield "rgb8_deflate_predictor", [dict(samples=big, bits=8, photometric="rgb", compression="deflate", predictor=2, rows_per_strip=7)], {}
    yield "rgb8_lzw_predictor_be", [dict(samples=big, bits=8, photometric="rgb", compression="lzw", predictor=2)], dict(big_endian=True)
    yield "rgb8_none_be", [dict(samples=rgb, bits=8, photometric="rgb")], dict(big_endian=True)
    yield "rgb8_tiles_none", [dict(samples=rgb, bits=8, photometric="rgb", tile=(16, 16))], {}
    yield "rgb8_tiles_lzw_predictor", [dict(samples=big, bits=8, photometric="rgb", compression="lzw", predictor=2, tile=(32, 48))], {}
    yield "rgb8_tiles_deflate", [dict(samples=big, bits=8, photometric="rgb", compression="deflate", tile=(64, 64))], {}
    yield "rgb8_tiles_packbits_be", [dict(samples=rgb, bits=8, photometric="rgb", compression="packbits", tile=(16, 16))], dict(big_endian=True)
    yield "rgb8_separate_lzw_predictor", [dict(samples=big, bits=8, photometric="rgb", compression="lzw", predictor=2, rows_per_strip=10, separate=True)], {}
    yield "rgb8_separate_tiles_deflate", [dict(samples=rgb, bits=8, photometric="rgb", compression="deflate", tile=(16, 16), separate=True)], {}
    yield "rgb8_rps_one", [dict(samples=rgb, bits=8, photometric="rgb", compression="packbits", rows_per_strip=1)], {}

    rgba = np.dstack([rgb, a8])
    yield "rgba8_straight", [dict(samples=rgba, bits=8, photometric="rgb", extra=[2], compression="lzw")], {}
    yield "rgba8_assoc", [dict(samples=np.dstack([premultiply(rgb, a8, 8), a8]), bits=8, photometric="rgb", extra=[1], compression="deflate")], {}
    yield "rgba8_no_extrasamples", [dict(samples=rgba, bits=8, photometric="rgb")], {}
    yield "rgbx8_unspecified", [dict(samples=rgba, bits=8, photometric="rgb", extra=[0])], {}
    yield "rgba8_separate_assoc", [dict(samples=np.dstack([premultiply(rgb, a8, 8), a8]), bits=8, photometric="rgb", extra=[1], separate=True, compression="packbits")], {}

    yield "rgb16_none", [dict(samples=rgb16, bits=16, photometric="rgb")], {}
    yield "rgb16_lzw_predictor", [dict(samples=rgb16, bits=16, photometric="rgb", compression="lzw", predictor=2, rows_per_strip=8)], {}
    yield "rgb16_lzw_predictor_be", [dict(samples=rgb16, bits=16, photometric="rgb", compression="lzw", predictor=2)], dict(big_endian=True)
    yield "rgb16_deflate_predictor_tiles_be", [dict(samples=rgb16, bits=16, photometric="rgb", compression="deflate", predictor=2, tile=(16, 16))], dict(big_endian=True)
    yield "rgba16_assoc", [dict(samples=np.dstack([premultiply(rgb16, a16, 16), a16]), bits=16, photometric="rgb", extra=[1], compression="lzw")], {}
    yield "rgba16_straight_separate_be", [dict(samples=np.dstack([rgb16, a16]), bits=16, photometric="rgb", extra=[2], separate=True)], dict(big_endian=True)

    for bits in (1, 2, 4):
        g = (gray >> (8 - bits)).astype(np.uint8)
        yield f"gray{bits}_none", [dict(samples=s(g), bits=bits, photometric="minisblack")], {}
        yield f"gray{bits}_lzw", [dict(samples=s(g), bits=bits, photometric="minisblack", compression="lzw", rows_per_strip=6)], {}
    yield "gray1_miniswhite_packbits", [dict(samples=s((gray >> 7).astype(np.uint8)), bits=1, photometric="miniswhite", compression="packbits")], {}
    yield "gray1_fillorder2", [dict(samples=s((gray >> 7).astype(np.uint8)), bits=1, photometric="minisblack", fill_order=2)], {}
    yield "gray4_lzw_fillorder2_tiles", [dict(samples=s((gray >> 4).astype(np.uint8)), bits=4, photometric="minisblack", compression="lzw", fill_order=2, tile=(16, 16))], {}
    yield "gray8_none", [dict(samples=s(gray), bits=8, photometric="minisblack")], {}
    yield "gray8_miniswhite_lzw", [dict(samples=s(gray), bits=8, photometric="miniswhite", compression="lzw", predictor=2)], {}
    yield "gray8_deflate_tiles", [dict(samples=s(gray), bits=8, photometric="minisblack", compression="deflate", tile=(16, 16))], {}
    yield "gray16_none", [dict(samples=s(gray16), bits=16, photometric="minisblack")], {}
    yield "gray16_lzw_predictor_be", [dict(samples=s(gray16), bits=16, photometric="minisblack", compression="lzw", predictor=2)], dict(big_endian=True)
    yield "graya8_straight", [dict(samples=np.dstack([gray, a8]), bits=8, photometric="minisblack", extra=[2], compression="packbits")], {}
    yield "graya8_assoc_separate", [dict(samples=np.dstack([premultiply(gray[..., None], a8, 8), a8]), bits=8, photometric="minisblack", extra=[1], separate=True)], {}

    rng = np.random.default_rng(9)
    for bits in (1, 2, 4, 8):
        n = 1 << bits
        # Pillow reduces colour maps to their high byte, so only maps of 8-bit values times 257 can be checked by it.
        cmap = [int(v) * 257 for v in rng.integers(0, 256, 3 * n)] if bits < 8 else [int(v) for v in rng.integers(0, 65536, 3 * n)]
        cmap[:3] = [0, 65535, 32896 if bits == 8 else 32896 // 257 * 257]
        idx = rng.integers(0, n, (h, w)).astype(np.uint8)
        comp = "lzw" if bits == 8 else "none"
        yield f"palette{bits}_{comp}", [dict(samples=s(idx), bits=bits, photometric="palette", colormap=cmap, compression=comp)], {}

    thumb = rgb[::4, ::4].copy()
    yield "pages_thumbnail_first", [
        dict(samples=thumb, bits=8, photometric="rgb", subfile=1),
        dict(samples=rgb, bits=8, photometric="rgb", compression="lzw"),
        dict(samples=s(gray), bits=8, photometric="minisblack"),
    ], {}
    yield "metadata", [dict(samples=rgb, bits=8, photometric="rgb", orientation=6, resolution=(300.0, 150.0, 2),
                            icc=b"ICC-PROFILE-BYTES" * 3, xmp=b"<x:xmpmeta xmlns:x='adobe:ns:meta/'/>")], {}
    yield "metadata_cm", [dict(samples=s(gray), bits=8, photometric="minisblack", resolution=(100.0, 50.0, 3))], {}


def expected_for(name, pages):
    page = next(p for p in pages if not p.get("subfile"))
    extra = page.get("extra")
    mode = None
    if extra is None and page["samples"].shape[2] == (3 if page["photometric"] == "rgb" else 1) + 1:
        mode = "straight"
    elif extra:
        mode = {0: "ignored", 1: "assoc", 2: "straight"}[extra[0]]
    return expected(page["samples"], page["bits"], page["photometric"], mode, page.get("colormap"))


def pillow_rgba(path):
    try:
        with Image.open(path) as im:
            if im.mode not in ("1", "L", "LA", "P", "RGB", "RGBA", "RGBX"):
                return None
            return np.asarray(im.convert("RGBA"))
    except Exception:
        return None


def main():
    if os.path.isdir(CORPUS):
        shutil.rmtree(CORPUS)
    os.makedirs(CORPUS)
    reference = {}

    def record(name, want, exact_pillow):
        path = os.path.join(CORPUS, name + ".tif")
        got = pillow_rgba(path)
        oracle = "model"
        if got is not None and got.shape == want.shape and exact_pillow:
            if not np.array_equal(got, want):
                bad = np.argwhere(got != want)[0]
                raise SystemExit(f"{name}: Pillow differs from the model at {bad}: {got[tuple(bad[:2])]} vs {want[tuple(bad[:2])]}")
            oracle = "pillow"
        reference[f"{name}.tif"] = dict(width=int(want.shape[1]), height=int(want.shape[0]), oracle=oracle,
                                        sha256=hashlib.sha256(np.ascontiguousarray(want).tobytes()).hexdigest())

    for name, pages, opts in cases():
        write_tiff(os.path.join(CORPUS, name + ".tif"), pages, **opts)
        page = next(p for p in pages if not p.get("subfile"))
        # Pillow keeps 16-bit samples apart and reduces them by taking the high byte, so only 8-bit and lower match.
        exact = page["bits"] <= 8 and all(v % 257 == 0 for v in page.get("colormap") or [])
        record(name, expected_for(name, pages), exact)

    w, h = 100, 60
    noise = np.random.default_rng(5).integers(0, 256, (h, w), dtype=np.uint8)
    noise[:, : w // 2] //= 32
    strips = [lzw_old_style(noise[y:y + 20].tobytes()) for y in range(0, h, 20)]
    write_classic(os.path.join(CORPUS, "gray8_lzw_old_style.tif"), w, h, 8, 1, 1, 5, strips)
    record("gray8_lzw_old_style", expected(noise[..., None], 8, "minisblack"), True)

    rgb = photo(19, 11, 4)
    write_classic(os.path.join(CORPUS, "rgb8_no_bytecounts.tif"), 19, 11, 8, 3, 2, 1,
                  [rgb[y:y + 4].tobytes() for y in range(0, 11, 4)], byte_counts=False)
    record("rgb8_no_bytecounts", expected(rgb, 8, "rgb"), True)

    with open(os.path.join(ROOT, "reference.json"), "w") as f:
        json.dump(dict(sorted(reference.items())), f, indent=1)
        f.write("\n")
    print(f"{len(reference)} files, {sum(v['oracle'] == 'pillow' for v in reference.values())} checked by Pillow")


if __name__ == "__main__":
    main()
