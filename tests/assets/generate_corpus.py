#!/usr/bin/env python3
"""Generates the deterministic TIFF corpus in tests/assets/corpus and the expected pixels in reference.json.

Files are written by the system libtiff (libtiff.so.6, 4.5.1 like Pillow 10.2.0's) through ctypes, which reaches
every layout tiffcp would (strips, tiles, separate planes, predictor 2, every compression, fill order 2, both byte
orders, reduced-resolution pages). Two small writers below cover what libtiff cannot write: old-style LSB-first LZW
and an IFD without StripByteCounts.

Expected pixels come from the source arrays and the decoder's documented rules: b-bit samples become
round(v * 255 / (2^b - 1)) (exact for 1, 2, 4 and 8 bits, (v + 128) // 257 for 16), signed samples are first offset by
2^(b - 1), WhiteIsZero is inverted, premultiplied colour is divided by alpha at
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
import subprocess
import tempfile
import zlib

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
GROUP3OPTIONS, GROUP4OPTIONS, INKSET, SAMPLEFORMAT = 292, 293, 332, 339
YCBCRSUBSAMPLING, REFERENCEBLACKWHITE, JPEGQUALITY, JPEGCOLORMODE = 530, 532, 65537, 65538
WEBP_LEVEL, WEBP_LOSSLESS = 65568, 65569
COMPRESSIONS = dict(none=1, lzw=5, deflate=8, packbits=32773, adobe=32946, jpeg=7, rle=2, g3=3, g4=4, rlew=32771,
                    webp=50001)
PHOTOMETRICS = dict(miniswhite=0, minisblack=1, rgb=2, palette=3, cmyk=5, ycbcr=6)


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
    dtype = object if depth > 16 else np.int64
    return ((color.astype(dtype) * a.astype(dtype)[..., None] + top // 2) // top).astype(color.dtype)


# --------------------------------------------------------------------------- expected pixels (the decoder's rules)

def to8(v, bits):
    top = (1 << bits) - 1
    v = v.astype(object if bits > 16 else np.int64)
    return ((v * 510 + top) // (2 * top)).astype(np.uint8)


def unpremultiply(color, a, bits):
    top = (1 << bits) - 1
    dtype = object if bits > 16 else np.int64
    c = color.astype(dtype)
    a = a.astype(dtype)[..., None]
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
    dtype = object if bits > 16 else np.int64
    color = samples[..., :color_count].astype(dtype)
    if photometric == "miniswhite":
        color = top - color
    if extra in ("straight", "assoc"):
        a = samples[..., color_count].astype(dtype)
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
    if bits in (8, 16, 32, 64):
        # The sample's own type: unsigned, signed (int8 ... int32) or IEEE (float16 ... float64), in native order.
        kind = flat.dtype.kind if flat.dtype.kind in "uif" else "u"
        return flat.astype(f"={kind}{bits // 8}").tobytes()
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
                    orientation=None, resolution=None, icc=None, xmp=None, sample_format=1, t4=None, t6=None,
                    jpeg_quality=None, jpeg_rgb=False, subsampling=None, ref_black_white=None,
                    webp_level=None, webp_lossless=False):
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
    if sample_format != 1:
        set_field(tif, SAMPLEFORMAT, ctypes.c_int(sample_format))
    if t4 is not None:
        set_field(tif, GROUP3OPTIONS, ctypes.c_uint32(t4))
    if t6 is not None:
        set_field(tif, GROUP4OPTIONS, ctypes.c_uint32(t6))
    if jpeg_quality:
        set_field(tif, JPEGQUALITY, ctypes.c_int(jpeg_quality))
    if jpeg_rgb:
        set_field(tif, JPEGCOLORMODE, ctypes.c_int(1))
    if webp_level:
        set_field(tif, WEBP_LEVEL, ctypes.c_int(webp_level))
    if webp_lossless:
        set_field(tif, WEBP_LOSSLESS, ctypes.c_int(1))
    if subsampling:
        set_field(tif, YCBCRSUBSAMPLING, ctypes.c_int(subsampling[0]), ctypes.c_int(subsampling[1]))
    if ref_black_white:
        arr = (ctypes.c_float * 6)(*ref_black_white)
        keep.append(arr)
        set_field(tif, REFERENCEBLACKWHITE, arr)
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
                    data = pack(padded[ty * tl:(ty + 1) * tl, tx * tw:(tx + 1) * tw], bits, photometric, subsampling, compression)
                    index = p * across * down + ty * across + tx
                    if lib.TIFFWriteEncodedTile(tif, index, data, len(data)) < 0:
                        raise RuntimeError("TIFFWriteEncodedTile failed")
    else:
        rps = rows_per_strip or h
        set_field(tif, ROWSPERSTRIP, ctypes.c_uint32(rps))
        strips = -(-h // rps)
        for p, plane in enumerate(planes):
            for s in range(strips):
                data = pack(plane[s * rps:(s + 1) * rps], bits, photometric, subsampling, compression)
                if lib.TIFFWriteEncodedStrip(tif, p * strips + s, data, len(data)) < 0:
                    raise RuntimeError("TIFFWriteEncodedStrip failed")
    if lib.TIFFWriteDirectory(tif) != 1:
        raise RuntimeError("TIFFWriteDirectory failed")


def pack(samples, bits, photometric, subsampling, compression):
    """Rows as libtiff expects them; uncompressed-style YCbCr is packed into blocks of h x v luma plus Cb and Cr,
    taking the chroma of each block's top-left pixel."""
    if photometric != "ycbcr" or compression == "jpeg" or tuple(subsampling or (2, 2)) == (1, 1):
        return pack_rows(samples, bits)
    sh, sv = subsampling or (2, 2)
    rows, w, _ = samples.shape
    bh, bw = -(-rows // sv), -(-w // sh)
    padded = np.zeros((bh * sv, bw * sh, 3), np.uint8)
    padded[:rows, :w] = samples
    padded[rows:, :] = padded[rows - 1:rows, :]
    padded[:, w:] = padded[:, w - 1:w]
    out = bytearray()
    for by in range(bh):
        for bx in range(bw):
            block = padded[by * sv:(by + 1) * sv, bx * sh:(bx + 1) * sh]
            out += block[..., 0].tobytes() + bytes([block[0, 0, 1], block[0, 0, 2]])
    return bytes(out)


def write_tiff(path, pages, big_endian=False, big_tiff=False):
    mode = (b"wb" if big_endian else b"wl") + (b"8" if big_tiff else b"")
    tif = lib.TIFFOpen(path.encode(), mode)
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


def write_classic(path, w, h, bits, spp, photometric, compression, strips, byte_counts=True, order="<", extra_entries=()):
    """A TIFF with the given strips in byte order `order`; byte_counts=False omits StripByteCounts."""
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
    entries += list(extra_entries)
    entries.sort()
    ifd_offset = header + len(blob)
    extra = bytearray()
    ifd = bytearray(struct.pack(order + "H", len(entries)))
    data_offset = ifd_offset + 2 + 12 * len(entries) + 4
    for tag, typ, values in entries:
        fmt = order + ("H" if typ == 3 else "I")
        raw = b"".join(struct.pack(fmt, v) for v in values)
        if len(raw) <= 4:
            ifd += struct.pack(order + "HHI", tag, typ, len(values)) + raw.ljust(4, b"\0")
        else:
            ifd += struct.pack(order + "HHII", tag, typ, len(values), data_offset + len(extra))
            extra += raw
    ifd += struct.pack(order + "I", 0)
    magic = b"II*\0" if order == "<" else b"MM\0*"
    with open(path, "wb") as f:
        f.write(magic + struct.pack(order + "I", ifd_offset) + blob + ifd + extra)


def fp_diff_rows(samples, order):
    """Predictor 3 as the TIFF technical note specifies it: per row, the sample bytes in byte order `order` split
    into planes most significant first, then differenced byte by byte with a stride of the samples per pixel."""
    rows, w, spp = samples.shape
    out = bytearray()
    for r in range(rows):
        be = samples[r].astype(">f4").tobytes()
        planes = np.frombuffer(be, np.uint8).reshape(w * spp, 4).T.copy().reshape(-1)
        diff = planes.astype(np.int16)
        diff[spp:] = planes[spp:].astype(np.int16) - planes[:-spp].astype(np.int16)
        out += (diff & 0xFF).astype(np.uint8).tobytes()
    return bytes(out)


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
    return page_expected(next(p for p in pages if not p.get("subfile")))


def page_expected(page):
    if page.get("sample_format") == 2:
        # Signed samples: offset by half the range, then the unsigned rules of the same depth.
        page = dict(page, samples=page["samples"].astype(object) + (1 << (page["bits"] - 1)), sample_format=1)
    extra = page.get("extra")
    mode = None
    if extra is None and page["samples"].shape[2] == (3 if page["photometric"] == "rgb" else 1) + 1:
        mode = "straight"
    elif extra:
        mode = {0: "ignored", 1: "assoc", 2: "straight"}[extra[0]]
    return expected(page["samples"], page["bits"], page["photometric"], mode, page.get("colormap"))


def run(*args):
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def pillow_rgba(path):
    try:
        with Image.open(path) as im:
            if im.mode not in ("1", "L", "LA", "P", "RGB", "RGBA", "RGBX", "CMYK"):
                return None
            return np.asarray(im.convert("RGBA"))
    except Exception:
        return None


# --------------------------------------------------------------------------- 0.3.0: readers used as oracles

lib.TIFFReadScanline.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint16]
lib.TIFFScanlineSize.argtypes = [ctypes.c_void_p]
lib.TIFFScanlineSize.restype = ctypes.c_ssize_t
lib.TIFFReadRGBAImageOriented.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
lib.TIFFGetField.restype = ctypes.c_int
lib.TIFFIsTiled.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfStrips.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfTiles.argtypes = [ctypes.c_void_p]
lib.TIFFReadRawStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFReadRawStrip.restype = ctypes.c_ssize_t
lib.TIFFReadRawTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFReadRawTile.restype = ctypes.c_ssize_t
lib.TIFFGetStrileByteCount.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFGetStrileByteCount.restype = ctypes.c_uint64


def get_u32(tif, tag):
    v = ctypes.c_uint32()
    return v.value if lib.TIFFGetField(ctypes.c_void_p(tif), ctypes.c_uint32(tag), ctypes.byref(v)) else None


def get_u16(tif, tag):
    v = ctypes.c_uint16()
    return v.value if lib.TIFFGetField(ctypes.c_void_p(tif), ctypes.c_uint32(tag), ctypes.byref(v)) else None


def libtiff_float_samples(path):
    """The float32 samples of a contiguous striped image as libtiff returns them (native order, predictor undone)."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        w, h, spp = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH), get_u16(tif, SAMPLESPERPIXEL)
        size = lib.TIFFScanlineSize(tif)
        buf = ctypes.create_string_buffer(size)
        rows = []
        for y in range(h):
            if lib.TIFFReadScanline(tif, buf, y, 0) < 0:
                raise RuntimeError("TIFFReadScanline failed")
            rows.append(np.frombuffer(buf.raw[:w * spp * 4], "=f4").reshape(w, spp))
        return np.stack(rows)
    finally:
        lib.TIFFClose(tif)


def libtiff_rgba(path, directory=0):
    """libtiff's own TIFFRGBAImage conversion of an IFD (index in the chain), top-left oriented, as 8-bit RGBA."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        if directory and not lib.TIFFSetDirectory(ctypes.c_void_p(tif), ctypes.c_uint32(directory)):
            raise RuntimeError(f"TIFFSetDirectory {directory} failed")
        w, h = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH)
        raster = (ctypes.c_uint32 * (w * h))()
        if not lib.TIFFReadRGBAImageOriented(tif, w, h, raster, 1, 1):
            raise RuntimeError("TIFFReadRGBAImageOriented failed")
        v = np.frombuffer(raster, np.uint32).reshape(h, w)
        return np.dstack([(v >> s) & 0xFF for s in (0, 8, 16, 24)]).astype(np.uint8)
    finally:
        lib.TIFFClose(tif)


def raw_chunks(path):
    """Each strip or tile as stored, with its region (WebP chunks are standalone streams)."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        w, h = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH)
        if lib.TIFFIsTiled(tif):
            cw, ch = get_u32(tif, TILEWIDTH), get_u32(tif, TILELENGTH)
            n, read = lib.TIFFNumberOfTiles(tif), lib.TIFFReadRawTile
        else:
            cw, ch = w, min(get_u32(tif, ROWSPERSTRIP), h)
            n, read = lib.TIFFNumberOfStrips(tif), lib.TIFFReadRawStrip
        across = -(-w // cw)
        out = []
        for i in range(n):
            size = lib.TIFFGetStrileByteCount(tif, i)
            buf = ctypes.create_string_buffer(size)
            read(tif, i, buf, size)
            x, y = (i % across) * cw, (i // across) * ch
            out.append((buf.raw[:size], x, y, min(cw, w - x), min(ch, h - y)))
        return out
    finally:
        lib.TIFFClose(tif)


def jpeg_chunks(path):
    """Each strip or tile as a standalone JPEG (JPEGTables without EOI + the chunk without SOI), with its region."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        w, h = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH)
        count, ptr = ctypes.c_uint32(), ctypes.c_void_p()
        tables = ctypes.string_at(ptr, count.value) if lib.TIFFGetField(ctypes.c_void_p(tif), ctypes.c_uint32(347), ctypes.byref(count), ctypes.byref(ptr)) else b""
        rgb = get_u16(tif, PHOTOMETRIC) == 2
        tiled = lib.TIFFIsTiled(tif)
        if tiled:
            cw, ch = get_u32(tif, TILEWIDTH), get_u32(tif, TILELENGTH)
            n, read = lib.TIFFNumberOfTiles(tif), lib.TIFFReadRawTile
        else:
            cw, ch = w, min(get_u32(tif, ROWSPERSTRIP), h)
            n, read = lib.TIFFNumberOfStrips(tif), lib.TIFFReadRawStrip
        across = -(-w // cw)
        out = []
        for i in range(n):
            size = lib.TIFFGetStrileByteCount(tif, i)
            buf = ctypes.create_string_buffer(size)
            read(tif, i, buf, size)
            raw = buf.raw[:size]
            head = tables[:-2] if tables else raw[:2]
            if rgb and b"Adobe" not in head and b"Adobe" not in raw[:64]:
                head = head[:2] + bytes([0xFF, 0xEE, 0, 14]) + b"Adobe" + bytes([0, 100, 0, 0, 0, 0, 0]) + head[2:]
            x, y = (i % across) * cw, (i // across) * ch
            out.append((head + raw[2:], x, y, min(cw, w - x), min(ch, h - y)))
        return out
    finally:
        lib.TIFFClose(tif)


lib.TIFFReadEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFReadEncodedStrip.restype = ctypes.c_ssize_t
lib.TIFFReadEncodedTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_ssize_t]
lib.TIFFReadEncodedTile.restype = ctypes.c_ssize_t
lib.TIFFStripSize.argtypes = [ctypes.c_void_p]
lib.TIFFStripSize.restype = ctypes.c_ssize_t
lib.TIFFTileSize.argtypes = [ctypes.c_void_p]
lib.TIFFTileSize.restype = ctypes.c_ssize_t


def libtiff_bytes(path):
    """Every strip or tile as libtiff decodes it (TIFFReadEncodedStrip/Tile), joined, in host byte order."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        tiled = lib.TIFFIsTiled(tif)
        n = lib.TIFFNumberOfTiles(tif) if tiled else lib.TIFFNumberOfStrips(tif)
        size = lib.TIFFTileSize(tif) if tiled else lib.TIFFStripSize(tif)
        read = lib.TIFFReadEncodedTile if tiled else lib.TIFFReadEncodedStrip
        out = bytearray()
        for i in range(n):
            buf = ctypes.create_string_buffer(size)
            got = read(tif, i, buf, size)
            if got < 0:
                raise RuntimeError(f"{path}: chunk {i} failed")
            out += buf.raw[:got]
        return bytes(out)
    finally:
        lib.TIFFClose(tif)


def source_bytes(page):
    """The chunks write_directory hands to libtiff, in the same order, for comparison with libtiff_bytes."""
    samples, bits = page["samples"], page["bits"]
    h, w, spp = samples.shape
    planes = [samples[..., p:p + 1] for p in range(spp)] if page.get("separate") else [samples]
    out = bytearray()
    for plane in planes:
        if page.get("tile"):
            tw, tl = page["tile"]
            across, down = -(-w // tw), -(-h // tl)
            padded = np.zeros((down * tl, across * tw, plane.shape[2]), plane.dtype)
            padded[:h, :w] = plane
            for ty in range(down):
                for tx in range(across):
                    out += pack_rows(padded[ty * tl:(ty + 1) * tl, tx * tw:(tx + 1) * tw], bits)
        else:
            rps = page.get("rows_per_strip") or h
            for y in range(0, h, rps):
                out += pack_rows(plane[y:y + rps], bits)
    return bytes(out)


def float_to8(v, t=np.float32):
    v = v.astype(t)
    scaled = np.floor(np.clip(v, 0, 1) * t(255) + t(0.5))
    return np.where(~(v > 0), 0, np.where(v >= 1, 255, scaled)).astype(np.uint8)


def float_expected(samples, photometric, assoc=False):
    """16- and 32-bit samples in single precision (a half converts exactly), 64-bit ones in double precision."""
    h, w, spp = samples.shape
    t = np.float64 if samples.dtype == np.float64 else np.float32
    color = 3 if photometric == "rgb" else 1
    c = samples[..., :color].astype(t)
    a = samples[..., color].astype(t) if spp > color else np.ones((h, w), t)
    if photometric == "miniswhite":
        c = (t(1) - c).astype(t)
    if assoc:
        div = np.where(a > 0, a, t(1))[..., None]
        c = np.where(a[..., None] == 1, c, np.where(a[..., None] > 0, (c / div).astype(t), t(0)))
    c8 = float_to8(c, t)
    if color == 1:
        c8 = np.repeat(c8, 3, -1)
    return np.dstack([c8, float_to8(a, t)])


def cmyk_expected(samples, bits):
    k = to8(samples[..., 3], bits).astype(np.int64)
    nk = 255 - k
    rgb = []
    for i in range(3):
        t = to8(samples[..., i], bits).astype(np.int64) * nk + 128
        rgb.append(nk - (((t >> 8) + t) >> 8))
    a = to8(samples[..., 4], bits) if samples.shape[2] > 4 else np.full(k.shape, 255)
    return np.dstack(rgb + [a]).astype(np.uint8)


def bilevel_expected(bits01, photometric):
    v = (1 - bits01) if photometric == "miniswhite" else bits01
    g = (v * 255).astype(np.uint8)
    return np.dstack([g, g, g, np.full(g.shape, 255, np.uint8)])


def write_exif_tiff(path):
    """A little-endian greyscale TIFF whose IFD0 links EXIF (with interoperability) and GPS IFDs."""
    w, h = 8, 6
    pixels = bytes(range(0, 2 * w * h, 2))
    ascii_ = lambda t: t.encode() + b"\0"
    rational = lambda *pairs: b"".join(struct.pack("<II", n, d) for n, d in pairs)
    ifd0 = [(IMAGEWIDTH, 3, 1, struct.pack("<H", w)), (IMAGELENGTH, 3, 1, struct.pack("<H", h)),
            (BITSPERSAMPLE, 3, 1, struct.pack("<H", 8)), (COMPRESSION, 3, 1, struct.pack("<H", 1)),
            (PHOTOMETRIC, 3, 1, struct.pack("<H", 1)), (271, 2, 7, ascii_("Maker")), (272, 2, 6, ascii_("Model")),
            (273, 4, 1, struct.pack("<I", 8)), (ORIENTATION, 3, 1, struct.pack("<H", 8)),
            (SAMPLESPERPIXEL, 3, 1, struct.pack("<H", 1)), (ROWSPERSTRIP, 3, 1, struct.pack("<H", h)),
            (279, 4, 1, struct.pack("<I", len(pixels))), (34665, 4, 1, "exif"), (34853, 4, 1, "gps")]
    exif = [(33434, 5, 1, rational((1, 250))), (36867, 2, 20, ascii_("2026:10:21 09:30:00")), (40965, 4, 1, "interop")]
    interop = [(1, 2, 4, ascii_("R98"))]
    gps = [(1, 2, 2, ascii_("N")), (2, 5, 3, rational((52, 1), (13, 1), (3600, 100))),
           (3, 2, 2, ascii_("E")), (4, 5, 3, rational((21, 1), (0, 1), (0, 1)))]
    ifds = dict(ifd0=ifd0, exif=exif, interop=interop, gps=gps)
    offsets, pos = {}, 8 + len(pixels)
    for name, entries in ifds.items():
        offsets[name] = pos
        pos += 2 + 12 * len(entries) + 4 + sum(len(v) + len(v) % 2 for _, _, _, v in entries if isinstance(v, bytes) and len(v) > 4)
    out = bytearray(b"II*\0" + struct.pack("<I", offsets["ifd0"]) + pixels)
    for name, entries in ifds.items():
        values = offsets[name] + 2 + 12 * len(entries) + 4
        body, extra = bytearray(struct.pack("<H", len(entries))), bytearray()
        for tag, typ, count, v in sorted(entries, key=lambda e: e[0]):
            v = struct.pack("<I", offsets[v]) if isinstance(v, str) else v
            count = len(v) if typ == 2 else count
            if len(v) <= 4:
                body += struct.pack("<HHI", tag, typ, count) + v.ljust(4, b"\0")
            else:
                body += struct.pack("<HHII", tag, typ, count, values + len(extra))
                extra += v + b"\0" * (len(v) % 2)
        out += body + struct.pack("<I", 0) + extra
    with open(path, "wb") as f:
        f.write(out)
    return np.frombuffer(pixels, np.uint8).reshape(h, w, 1)


def cases_030():
    """0.3.0 layouts written by libtiff: (name, pages, options, expected-kind)."""
    w, h = 37, 23
    rgb = photo(w, h, 1)
    gray = (rgb.astype(np.int64) @ [299, 587, 114] // 1000).astype(np.uint8)
    s = lambda a: a if a.ndim == 3 else a[..., None]
    bilevel = ((np.add.outer(np.arange(64), np.arange(80)) // 7 + (np.arange(80) % 13 == 0)) % 2).astype(np.uint8)
    bilevel[20:30, 10:70] = 1
    bilevel[40:44, :] = 0
    for comp, extra in (("rle", {}), ("rlew", {}), ("g3", dict(t4=0)), ("g3", dict(t4=1)), ("g3", dict(t4=4)), ("g4", {})):
        tag = comp + ("_2d" if extra.get("t4") == 1 else "_fill" if extra.get("t4") == 4 else "")
        yield f"bilevel_{tag}", [dict(samples=s(bilevel), bits=1, photometric="miniswhite", compression=comp, rows_per_strip=24, **extra)], {}, "bilevel"
    yield "bilevel_g4_minisblack_tiles", [dict(samples=s(bilevel), bits=1, photometric="minisblack", compression="g4", tile=(32, 32))], {}, "bilevel"
    yield "bilevel_g4_fillorder2_be", [dict(samples=s(bilevel), bits=1, photometric="miniswhite", compression="g4", fill_order=2)], dict(big_endian=True), "bilevel"
    yield "bilevel_g3_2d_fillorder2", [dict(samples=s(bilevel), bits=1, photometric="miniswhite", compression="g3", t4=5, fill_order=2, rows_per_strip=10)], {}, "bilevel"

    rng = np.random.default_rng(11)
    cmyk = rng.integers(0, 256, (h, w, 4), dtype=np.uint8)
    cmyk[..., 3] //= 3
    cmyk16 = (cmyk.astype(np.uint16) * 257 + rng.integers(0, 257, (h, w, 4))).astype(np.uint16)
    yield "cmyk8_none", [dict(samples=cmyk, bits=8, photometric="cmyk")], {}, "cmyk"
    yield "cmyk8_lzw_predictor_tiles", [dict(samples=cmyk, bits=8, photometric="cmyk", compression="lzw", predictor=2, tile=(16, 16))], {}, "cmyk"
    yield "cmyk8_separate_deflate", [dict(samples=cmyk, bits=8, photometric="cmyk", compression="deflate", separate=True)], {}, "cmyk"
    yield "cmyka8_straight", [dict(samples=np.dstack([cmyk, alpha(w, h)]), bits=8, photometric="cmyk", extra=[2])], {}, "cmyk"
    yield "cmyk16_lzw_be", [dict(samples=cmyk16, bits=16, photometric="cmyk", compression="lzw", predictor=2)], dict(big_endian=True), "cmyk"

    f = rgb.astype(np.float32) / 255 * np.float32(1.2) - np.float32(0.1)
    f[0, 0] = np.nan
    f[0, 1] = np.inf
    fa = np.clip(alpha(w, h).astype(np.float32) / 255, 0, 1)
    fpre = np.dstack([np.clip(f, 0, 1) * fa[..., None], fa]).astype(np.float32)
    fgray = (gray.astype(np.float32) / 255).astype(np.float32)[..., None]
    yield "float32_rgb_none", [dict(samples=f, bits=32, photometric="rgb", sample_format=3)], {}, "float"
    yield "float32_rgb_lzw_predictor3", [dict(samples=f, bits=32, photometric="rgb", sample_format=3, compression="lzw", predictor=3, rows_per_strip=8)], {}, "float"
    yield "float32_gray_predictor3_tiles", [dict(samples=fgray, bits=32, photometric="minisblack", sample_format=3, compression="deflate", predictor=3, tile=(16, 16))], {}, "float"
    yield "float32_gray_separate_be", [dict(samples=np.dstack([fgray, fa[..., None]]), bits=32, photometric="minisblack", sample_format=3, extra=[2], separate=True)], dict(big_endian=True), "float"
    yield "float32_rgba_assoc", [dict(samples=fpre, bits=32, photometric="rgb", sample_format=3, extra=[1], compression="lzw", predictor=3)], {}, "float-assoc"

    ycc = rng.integers(16, 240, (h, w, 3), dtype=np.uint8)
    for sub in ((1, 1), (2, 1), (2, 2), (4, 2), (4, 4), (1, 2)):
        rps = 4 * sub[1]
        yield f"ycbcr_{sub[0]}{sub[1]}", [dict(samples=ycc, bits=8, photometric="ycbcr", subsampling=sub, rows_per_strip=rps)], {}, "libtiff"
    yield "ycbcr_22_lzw_tiles", [dict(samples=ycc, bits=8, photometric="ycbcr", subsampling=(2, 2), compression="lzw", tile=(16, 16))], {}, "libtiff"
    yield "ycbcr_11_separate_refbw", [dict(samples=ycc, bits=8, photometric="ycbcr", subsampling=(1, 1), separate=True,
                                           ref_black_white=[16, 235, 128, 240, 128, 240])], {}, "libtiff"

    big = photo(64, 48, 6)
    yield "jpeg_ycbcr_strips", [dict(samples=big, bits=8, photometric="ycbcr", compression="jpeg", jpeg_rgb=True, jpeg_quality=90, rows_per_strip=16)], {}, "jpeg"
    yield "jpeg_ycbcr_tiles", [dict(samples=big, bits=8, photometric="ycbcr", compression="jpeg", jpeg_rgb=True, tile=(32, 32))], {}, "jpeg"
    yield "jpeg_rgb", [dict(samples=big, bits=8, photometric="rgb", compression="jpeg", jpeg_quality=95, rows_per_strip=8)], {}, "jpeg"
    gray_big = (big.astype(np.int64) @ [299, 587, 114] // 1000).astype(np.uint8)
    yield "jpeg_gray_be", [dict(samples=s(gray_big), bits=8, photometric="minisblack", compression="jpeg", rows_per_strip=24)], dict(big_endian=True), "jpeg"

    # 0.4.5 WebP (libtiff tif_webp.c with the system libwebp): lossy and lossless, RGB and RGBA, strips and tiles.
    big_rgba = np.dstack([big, alpha(64, 48)])
    yield "webp_rgb_lossy_strips", [dict(samples=big, bits=8, photometric="rgb", compression="webp", webp_level=80, rows_per_strip=20)], {}, "webp"
    yield "webp_rgba_lossy_tiles", [dict(samples=big_rgba, bits=8, photometric="rgb", extra=[2], compression="webp", tile=(32, 32))], {}, "webp"
    yield "webp_rgb_lossless_be", [dict(samples=big, bits=8, photometric="rgb", compression="webp", webp_lossless=True, rows_per_strip=16)], dict(big_endian=True), "webp"
    yield "webp_rgba_lossless_strips", [dict(samples=big_rgba, bits=8, photometric="rgb", extra=[2], compression="webp", webp_lossless=True, rows_per_strip=13)], {}, "webp"

    yield "bigtiff_rgb8_lzw", [dict(samples=rgb, bits=8, photometric="rgb", compression="lzw", predictor=2, rows_per_strip=5)], dict(big_tiff=True), "model"
    yield "bigtiff_gray16_tiles_be", [dict(samples=s((gray.astype(np.uint16) * 257)), bits=16, photometric="minisblack", tile=(16, 16))], dict(big_tiff=True, big_endian=True), "model"
    yield "bigtiff_pages", [dict(samples=rgb[::4, ::4].copy(), bits=8, photometric="rgb", subfile=1),
                            dict(samples=rgb, bits=8, photometric="rgb", compression="deflate")], dict(big_tiff=True), "model"

    # 0.4.0 page selection: pages of different sizes, layouts and colour models, with reduced-resolution IFDs between.
    small = photo(19, 11, 12)
    cmap = [int(v) * 257 for v in rng.integers(0, 256, 768)]
    yield "pages_mixed", [
        dict(samples=s(gray), bits=8, photometric="minisblack"),
        dict(samples=small[::2, ::2].copy(), bits=8, photometric="rgb", subfile=1),
        dict(samples=rng.integers(0, 256, (11, 19, 1), dtype=np.uint8), bits=8, photometric="palette", colormap=cmap, compression="lzw"),
        dict(samples=small, bits=8, photometric="rgb", compression="deflate", predictor=2, tile=(16, 16)),
        dict(samples=np.dstack([rgb, alpha(w, h)]), bits=8, photometric="rgb", extra=[2], compression="packbits"),
        dict(samples=s(bilevel), bits=1, photometric="miniswhite", compression="g4"),
    ], {}, "model"
    yield "pages_bigtiff_be", [
        dict(samples=small, bits=8, photometric="rgb", compression="lzw", predictor=2),
        dict(samples=s((gray.astype(np.uint16) * 257)), bits=16, photometric="minisblack", tile=(16, 16)),
        dict(samples=rgb[::4, ::4].copy(), bits=8, photometric="rgb", subfile=1),
    ], dict(big_tiff=True, big_endian=True), "model"

    yield from cases_053(w, h, rgb, gray, s)


def cases_053(w, h, rgb, gray, s):
    """0.5.3 sample formats and depths: unsigned 3 to 14 and 32 bits, signed 8, 16 and 32 bits, IEEE 16 and 64 bits."""
    rng = np.random.default_rng(53)

    def deep(bits, channels=3):
        # The 8-bit photo widened to the depth, with noise in the low bits so that the rounding matters.
        top = (1 << bits) - 1
        src = rgb if channels == 3 else gray[..., None]
        v = src.astype(np.float64) / 255 * top + rng.uniform(-0.49, 0.49, src.shape) * max(1, top / 255)
        return np.clip(np.rint(v), 0, top).astype(np.uint32 if bits > 16 else np.uint16)

    def deep_alpha(bits):
        top = (1 << bits) - 1
        a = alpha(w, h).astype(np.float64) / 255 * top
        return np.rint(a).astype(np.uint32 if bits > 16 else np.uint16)

    for bits in (3, 5, 10, 12, 14):
        yield f"gray{bits}_none", [dict(samples=deep(bits, 1), bits=bits, photometric="minisblack")], {}, "depth"
    yield "gray12_lzw_tiles", [dict(samples=deep(12, 1), bits=12, photometric="minisblack", compression="lzw", tile=(16, 16))], {}, "depth"
    yield "gray10_deflate_be", [dict(samples=deep(10, 1), bits=10, photometric="minisblack", compression="deflate", rows_per_strip=5)], dict(big_endian=True), "depth"
    yield "gray12_miniswhite_packbits", [dict(samples=deep(12, 1), bits=12, photometric="miniswhite", compression="packbits", rows_per_strip=7)], {}, "depth"
    yield "rgb6_separate", [dict(samples=deep(6), bits=6, photometric="rgb", separate=True, rows_per_strip=9)], {}, "depth"
    yield "rgb10_none", [dict(samples=deep(10), bits=10, photometric="rgb")], {}, "depth"
    yield "rgb12_lzw_be", [dict(samples=deep(12), bits=12, photometric="rgb", compression="lzw", rows_per_strip=6)], dict(big_endian=True), "depth"
    yield "rgb14_deflate_tiles", [dict(samples=deep(14), bits=14, photometric="rgb", compression="deflate", tile=(16, 16))], {}, "depth"
    c12, a12 = deep(12), deep_alpha(12)
    yield "rgba12_assoc", [dict(samples=np.dstack([premultiply(c12, a12, 12), a12]), bits=12, photometric="rgb", extra=[1])], {}, "depth"
    yield "graya12_straight_separate", [dict(samples=np.dstack([deep(12, 1), a12]), bits=12, photometric="minisblack", extra=[2], separate=True)], {}, "depth"

    yield "gray32u_none", [dict(samples=deep(32, 1), bits=32, photometric="minisblack")], {}, "depth"
    yield "gray32u_lzw_predictor", [dict(samples=deep(32, 1), bits=32, photometric="minisblack", compression="lzw", predictor=2, rows_per_strip=8)], {}, "depth"
    yield "rgb32u_deflate_predictor_be", [dict(samples=deep(32), bits=32, photometric="rgb", compression="deflate", predictor=2)], dict(big_endian=True), "depth"
    c32, a32 = deep(32), deep_alpha(32)
    yield "rgba32u_assoc_separate", [dict(samples=np.dstack([premultiply(c32, a32, 32), a32]),
                                          bits=32, photometric="rgb", extra=[1], separate=True)], {}, "depth"

    def signed(bits, channels=3):
        u = deep(bits, channels).astype(np.int64) - (1 << (bits - 1))
        return u.astype({8: np.int8, 16: np.int16, 32: np.int32}[bits])

    yield "int8_gray_none", [dict(samples=signed(8, 1), bits=8, photometric="minisblack", sample_format=2)], {}, "depth"
    yield "int8_rgb_lzw_predictor", [dict(samples=signed(8), bits=8, photometric="rgb", sample_format=2, compression="lzw", predictor=2, rows_per_strip=4)], {}, "depth"
    yield "int16_gray_deflate_predictor_be", [dict(samples=signed(16, 1), bits=16, photometric="minisblack", sample_format=2, compression="deflate", predictor=2)], dict(big_endian=True), "depth"
    yield "int16_rgba_straight_separate", [dict(samples=np.dstack([signed(16), (deep_alpha(16).astype(np.int64) - 32768).astype(np.int16)]),
                                                bits=16, photometric="rgb", sample_format=2, extra=[2], separate=True)], {}, "depth"
    yield "int32_gray_none_be", [dict(samples=signed(32, 1), bits=32, photometric="minisblack", sample_format=2)], dict(big_endian=True), "depth"
    yield "int32_rgb_lzw_predictor_tiles", [dict(samples=signed(32), bits=32, photometric="rgb", sample_format=2, compression="lzw", predictor=2, tile=(16, 16))], {}, "depth"

    f = (rgb.astype(np.float64) / 255 * 1.2 - 0.1)
    f[0, 0] = np.nan
    f[0, 1] = np.inf
    fa = np.clip(alpha(w, h).astype(np.float64) / 255, 0, 1)
    fgray = (gray.astype(np.float64) / 255)[..., None]
    for t, bits in ((np.float16, 16), (np.float64, 64)):
        name = f"float{bits}"
        ft, fat, fg = f.astype(t), fa.astype(t), fgray.astype(t)
        yield f"{name}_gray_none", [dict(samples=fg, bits=bits, photometric="minisblack", sample_format=3)], {}, "float"
        yield f"{name}_rgb_lzw_predictor3", [dict(samples=ft, bits=bits, photometric="rgb", sample_format=3, compression="lzw", predictor=3, rows_per_strip=8)], {}, "float"
        yield f"{name}_gray_deflate_predictor3_tiles", [dict(samples=fg, bits=bits, photometric="minisblack", sample_format=3, compression="deflate", predictor=3, tile=(16, 16))], {}, "float"
        yield f"{name}_rgb_be", [dict(samples=ft, bits=bits, photometric="rgb", sample_format=3, rows_per_strip=5)], dict(big_endian=True), "float"
        pre = np.dstack([(np.clip(ft, 0, 1) * fat[..., None]).astype(t), fat]).astype(t)
        yield f"{name}_rgba_assoc", [dict(samples=pre, bits=bits, photometric="rgb", sample_format=3, extra=[1], compression="lzw", predictor=3)], {}, "float-assoc"
    yield "float16_miniswhite_separate", [dict(samples=np.dstack([fgray.astype(np.float16), fa.astype(np.float16)[..., None]]), bits=16,
                                               photometric="miniswhite", sample_format=3, extra=[2], separate=True)], {}, "float"


def imagemagick_cases(src_png):
    """Files written by ImageMagick 6 `convert` from the same source (DoD: CMYK, float, BigTIFF)."""
    yield "im_cmyk8_lzw", ["-colorspace", "CMYK", "-compress", "lzw"], "pillow"
    yield "im_float32_rgb_zip_predictor3", ["-define", "quantum:format=floating-point", "-depth", "32", "-compress", "zip",
                                            "-define", "tiff:predictor=3"], "libtiff-float"
    yield "im_float32_rgb_none", ["-define", "quantum:format=floating-point", "-depth", "32", "-compress", "none"], "libtiff-float"
    yield "im_bigtiff_rgb8_zip", ["-compress", "zip", "TIFF64:"], "libtiff"


def main():
    if os.path.isdir(CORPUS):
        shutil.rmtree(CORPUS)
    os.makedirs(CORPUS)
    reference = {}

    def record(name, want, exact_pillow, oracle="model"):
        path = os.path.join(CORPUS, name + ".tif")
        got = pillow_rgba(path)
        if got is not None and got.shape == want.shape and exact_pillow:
            if not np.array_equal(got, want):
                bad = np.argwhere(got != want)[0]
                raise SystemExit(f"{name}: Pillow differs from the model at {bad}: {got[tuple(bad[:2])]} vs {want[tuple(bad[:2])]}")
            oracle = "pillow"
        reference[f"{name}.tif"] = entry(want, oracle)

    def entry(want, oracle):
        return dict(width=int(want.shape[1]), height=int(want.shape[0]), oracle=oracle,
                    sha256=hashlib.sha256(np.ascontiguousarray(want).tobytes()).hexdigest())

    def record_pages(name, pages):
        """Every full-resolution page of a multi-page file, checked against libtiff where its RGBA path is exact."""
        if sum(not p.get("subfile") for p in pages) < 2:
            return
        path = os.path.join(CORPUS, name + ".tif")
        listed = []
        for directory, page in enumerate(pages):
            if page.get("subfile"):
                continue
            want = page_expected(page) if page["bits"] > 1 else bilevel_expected(page["samples"][..., 0], page["photometric"])
            # TIFFRGBAImage premultiplies unassociated alpha and takes the high byte of 16-bit samples.
            oracle = "model"
            if page["bits"] <= 8 and not page.get("extra"):
                got = libtiff_rgba(path, directory)
                if not np.array_equal(got, want):
                    raise SystemExit(f"{name} IFD {directory}: libtiff differs from the model")
                oracle = "libtiff"
            listed.append(entry(want, oracle))
        reference[f"{name}.tif"]["pages"] = listed

    for name, pages, opts in cases():
        write_tiff(os.path.join(CORPUS, name + ".tif"), pages, **opts)
        page = next(p for p in pages if not p.get("subfile"))
        # Pillow keeps 16-bit samples apart and reduces them by taking the high byte, so only 8-bit and lower match.
        exact = page["bits"] <= 8 and all(v % 257 == 0 for v in page.get("colormap") or [])
        record(name, expected_for(name, pages), exact)
        record_pages(name, pages)

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

    jpeg_index, webp_index = {}, {}
    os.makedirs(os.path.join(CORPUS, "jpeg"))
    os.makedirs(os.path.join(CORPUS, "webp"))
    for name, pages, opts, kind in cases_030():
        path = os.path.join(CORPUS, name + ".tif")
        write_tiff(path, pages, **opts)
        page = next(p for p in pages if not p.get("subfile"))
        smp = page["samples"]
        if kind == "bilevel":
            # libtiff 4.5.1 aligns RLEW rows differently when writing and reading: its decode of its own file
            # differs from the source, and libtiff's decode is what the port reproduces.
            if page.get("compression") == "rlew":
                record(name, libtiff_rgba(path), True, oracle="libtiff")
            else:
                record(name, bilevel_expected(smp[..., 0], page["photometric"]), True)
        elif kind == "cmyk":
            record(name, cmyk_expected(smp, page["bits"]), page["bits"] == 8 and smp.shape[2] == 4)
        elif kind in ("float", "float-assoc"):
            want = float_expected(smp, page["photometric"], assoc=kind == "float-assoc")
            if smp.shape[2] == 1 and smp.dtype == np.float32:
                # Pillow reads single-sample float images; the rule applied to its values must give the same.
                with Image.open(path) as im:
                    assert np.array_equal(float_to8(np.asarray(im)), want[..., 0]), name
            record(name, want, False)
        elif kind == "libtiff":
            # Pillow converts uncompressed YCbCr itself, ignoring subsampling and ReferenceBlackWhite; compressed
            # files go through the same libtiff TIFFRGBAImage path and must match.
            record(name, libtiff_rgba(path), page.get("compression", "none") != "none", oracle="libtiff")
        elif kind == "model":
            record(name, expected_for(name, pages), page["bits"] <= 8)
            record_pages(name, pages)
        elif kind == "depth":
            # libtiff must read back the samples as written (predictor and compression undone); pixels from the model.
            assert libtiff_bytes(path) == source_bytes(page), name
            record(name, expected_for(name, pages), False)
        elif kind == "webp":
            chunks = []
            for i, (stream, x, y, cw, ch) in enumerate(raw_chunks(path)):
                file = f"webp/{name}.{i}.webp"
                with open(os.path.join(CORPUS, file), "wb") as f:
                    f.write(stream)
                chunks.append([file, x, y, cw, ch])
            with Image.open(path) as im:
                Image.fromarray(np.asarray(im.convert("RGBA"))).save(os.path.join(CORPUS, f"webp/{name}.pillow.png"))
            webp_index[f"{name}.tif"] = dict(width=int(smp.shape[1]), height=int(smp.shape[0]), chunks=chunks,
                                             pillow=f"webp/{name}.pillow.png", lossless=bool(page.get("webp_lossless")))
            if page.get("webp_lossless"):
                # Lossless strips are the source samples: Pillow must give them back exactly.
                assert np.array_equal(pillow_rgba(path)[..., :smp.shape[2]], smp), name
        else:
            chunks = []
            for i, (stream, x, y, cw, ch) in enumerate(jpeg_chunks(path)):
                file = f"jpeg/{name}.{i}.jpg"
                with open(os.path.join(CORPUS, file), "wb") as f:
                    f.write(stream)
                chunks.append([file, x, y, cw, ch])
            with Image.open(path) as im:
                Image.fromarray(np.asarray(im.convert("RGBA"))).save(os.path.join(CORPUS, f"jpeg/{name}.pillow.png"))
            jpeg_index[f"{name}.tif"] = dict(width=int(smp.shape[1]), height=int(smp.shape[0]), chunks=chunks,
                                             pillow=f"jpeg/{name}.pillow.png")

    # libtiff 4.5.1 writes big-endian predictor-3 files with the byte planes in host order and cannot read them back,
    # so this one is written here as the specification describes.
    fsrc = (photo(21, 13, 9).astype(np.float32) / np.float32(255) * np.float32(1.1)).astype(np.float32)
    strips = [zlib.compress(fp_diff_rows(fsrc[y:y + 5], ">")) for y in range(0, 13, 5)]
    write_classic(os.path.join(CORPUS, "float32_rgb_deflate_predictor3_be.tif"), 21, 13, 32, 3, 2, 8, strips, order=">",
                  extra_entries=[(317, 3, [3]), (339, 3, [3, 3, 3])])
    record("float32_rgb_deflate_predictor3_be", float_expected(fsrc, "rgb"), False)

    exif_pixels = write_exif_tiff(os.path.join(CORPUS, "exif.tif"))
    record("exif", expected(exif_pixels, 8, "minisblack"), False)

    work = tempfile.mkdtemp()
    try:
        src = os.path.join(work, "src.png")
        Image.fromarray(photo(41, 27, 8)).save(src)
        for name, args, oracle in imagemagick_cases(src):
            path = os.path.join(CORPUS, name + ".tif")
            target = [a for a in args if a.endswith(":")]
            options = [a for a in args if not a.endswith(":")]
            run("convert", src, *options, (target[0] if target else "") + path)
            if oracle == "pillow":
                got = pillow_rgba(path)
                record(name, got, True, oracle="pillow")
            elif oracle == "libtiff":
                record(name, libtiff_rgba(path), True, oracle="libtiff")
            else:
                record(name, float_expected(libtiff_float_samples(path), "rgb"), False, oracle="libtiff")

        # A multi-page file from ImageMagick: one page per input image.
        src2 = os.path.join(work, "src2.png")
        Image.fromarray(photo(23, 17, 13)).save(src2)
        path = os.path.join(CORPUS, "im_pages_lzw.tif")
        run("convert", src, src2, "-compress", "lzw", path)
        pages = [libtiff_rgba(path, d) for d in range(2)]
        record("im_pages_lzw", pages[0], True, oracle="libtiff")
        reference["im_pages_lzw.tif"]["pages"] = [entry(p, "libtiff") for p in pages]
    finally:
        shutil.rmtree(work)

    with open(os.path.join(ROOT, "jpeg.json"), "w") as f:
        json.dump(dict(sorted(jpeg_index.items())), f, indent=1)
        f.write("\n")

    with open(os.path.join(ROOT, "webp.json"), "w") as f:
        json.dump(dict(sorted(webp_index.items())), f, indent=1)
        f.write("\n")

    with open(os.path.join(ROOT, "reference.json"), "w") as f:
        json.dump(dict(sorted(reference.items())), f, indent=1)
        f.write("\n")
    oracles = {o: sum(v["oracle"] == o for v in reference.values()) for o in ("pillow", "libtiff", "model")}
    print(f"{len(reference)} files ({oracles}), {len(jpeg_index)} JPEG files, {len(webp_index)} WebP files")


if __name__ == "__main__":
    main()
