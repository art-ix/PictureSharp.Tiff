#!/usr/bin/env python3
"""Checks TIFF files written by the PictureSharp.Tiff encoder against libtiff 4.5.1 and Pillow 10.2.0.

Usage: python3 -I tests/assets/verify_written.py [directory]   (default: tests/assets/written)

The directory holds the files and written.json, which WrittenFileTests produces: for each file its size, the SHA-256 of
the straight 8-bit RGBA our decoder reads, the resolution in dots per inch and the SHA-256 of the ICC and XMP blocks.
Pillow must read the same pixels and metadata. libtiff must open the file without a warning or an error, return the
same ICC profile, and convert it with TIFFReadRGBAImage to the same pixels. libtiff premultiplies RGB with unassociated
alpha, (a * v + 127) / 255 as its UaToAa table computes, but passes grey with alpha through (putagreytile).

JPEG files differ slightly between decoders (IDCT, chroma upsampling): for them and for lossy WebP files written.json
names our decode as a PNG, and Pillow and libtiff must stay within the tolerance of JpegTests (mean difference below
1.5, at most 24) with the same alpha instead.

For LZW, PackBits and CCITT Group 4 files, libtiff also writes the decoded strips or tiles again with the same layout,
compression and predictor (TIFFWriteEncodedStrip, TIFFWriteEncodedTile); its raw chunks must equal ours byte for byte.

Known oracle limits, each checked another way:
- libtiff 4.5.1 TIFFReadRGBAImage misplaces grey-with-alpha pixels in tiles cut by the right edge (putagreytile does not
  scale fromskew by the samples per pixel), also in files libtiff writes itself; for such files the samples
  TIFFReadEncodedTile returns must equal Pillow's grey and alpha instead.
- Pillow 10.2.0 does not know the IFD8 type (18) that BigTIFF EXIF and GPS pointers use, so for BigTIFF files only
  libtiff reads the EXIF block.

An entry with "pages" describes a multi-page file: the entry itself is page 0 and each item of "pages" the next page,
checked in the same way (Pillow seek, TIFFSetDirectory); Pillow and libtiff must count the same pages.

An entry with "exif" names a JPEG that carries the EXIF block written into the file. Pillow (getexif) must read the same
descriptive IFD0 tags (Software too when "software" is set) and the same EXIF, GPS and interoperability IFDs from both,
and libtiff must read the EXIF and GPS IFDs (TIFFReadEXIFDirectory, TIFFReadGPSDirectory) without a warning, with the
tags Pillow reads.

An entry with "original" is a file rewritten by Image.EditMetadata: libtiff must read every IFD of it to the same
pixels as of the original (TIFFReadRGBAImage) with no warning the original does not give, and the metadata of the
first page as for the other entries; Pillow is used only for the files it opens.
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
lib.TIFFReadDirectory.argtypes = [ctypes.c_void_p]
lib.TIFFSetDirectory.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFIsTiled.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfTiles.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfTiles.restype = ctypes.c_uint32
lib.TIFFTileSize.argtypes = [ctypes.c_void_p]
lib.TIFFTileSize.restype = ctypes.c_int64
lib.TIFFReadEncodedTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFReadEncodedTile.restype = ctypes.c_int64
lib.TIFFReadRawTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFReadRawTile.restype = ctypes.c_int64
lib.TIFFWriteEncodedTile.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFWriteEncodedTile.restype = ctypes.c_int64
lib.TIFFNumberOfDirectories.argtypes = [ctypes.c_void_p]
lib.TIFFReadEXIFDirectory.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
lib.TIFFReadGPSDirectory.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
lib.TIFFGetTagListCount.argtypes = [ctypes.c_void_p]
lib.TIFFGetTagListEntry.argtypes = [ctypes.c_void_p, ctypes.c_int]
lib.TIFFGetTagListEntry.restype = ctypes.c_uint32

IMAGEWIDTH, IMAGELENGTH, BITSPERSAMPLE, COMPRESSION, PHOTOMETRIC = 256, 257, 258, 259, 262
SAMPLESPERPIXEL, ROWSPERSTRIP, PLANARCONFIG, PREDICTOR, EXTRASAMPLES = 277, 278, 284, 317, 338
TILEWIDTH, TILELENGTH = 322, 323
XMLPACKET, ICCPROFILE = 700, 34675
EXIFIFD, GPSIFD, INTEROPIFD = 34665, 34853, 40965
NEWSUBFILETYPE, SUBFILETYPE = 254, 255
DESCRIPTIVE = (270, 271, 272, 274, 306, 315, 33432)
# libtiff 4.5.1 has no field for the interoperability pointer in its EXIF field array; it warns for any file with one.
INTEROP_WARNING = "TIFFReadCustomDirectory: Unknown field with tag 40965 (0xa005) encountered"
SOFTWARE = 305
ORIENTATION = 274
# Pillow applies the orientation on load (exif_transpose); these undo it.
UNDO = {2: Image.Transpose.FLIP_LEFT_RIGHT, 3: Image.Transpose.ROTATE_180, 4: Image.Transpose.FLIP_TOP_BOTTOM,
        5: Image.Transpose.TRANSPOSE, 6: Image.Transpose.ROTATE_90, 7: Image.Transpose.TRANSVERSE,
        8: Image.Transpose.ROTATE_270}
CCITT_G4, LZW, DEFLATE, PACKBITS = 4, 5, 8, 32773

messages = []
totals = {LZW: 0, PACKBITS: 0, CCITT_G4: 0}
checked = {"exif": 0, "edited": 0, "pages": 0}
HANDLER = ctypes.CFUNCTYPE(None, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p)


libc = ctypes.CDLL(None)
libc.vsnprintf.argtypes = [ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p]


@HANDLER
def record(module, fmt, args):
    text = ctypes.create_string_buffer(1024)
    libc.vsnprintf(text, len(text), fmt, args)
    messages.append(f"{(module or b'').decode()}: {text.value.decode(errors='replace')}")


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


def raw_tile_size(tif, tile):
    """The stored size of a tile; libtiff 4.5.1 exports no TIFFRawTileSize, so it comes from TileByteCounts (uint64)."""
    counts = ctypes.POINTER(ctypes.c_uint64)()
    if not lib.TIFFGetField(tif, 325, ctypes.byref(counts)):
        raise RuntimeError("TileByteCounts missing")
    return counts[tile]


def read_strips(path, raw, page=0):
    """The strips or tiles of a page, raw or decoded, and the page's layout."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    if not tif:
        raise RuntimeError("TIFFOpen failed")
    try:
        lib.TIFFSetDirectory(tif, page)
        # Predictor is known only to the codecs that use it.
        layout = {tag: get_u16(tif, tag) for tag in (BITSPERSAMPLE, COMPRESSION, SAMPLESPERPIXEL)}
        layout[PREDICTOR] = get_u16(tif, PREDICTOR, default=1) if layout[COMPRESSION] in (LZW, DEFLATE) else 1
        tiled = bool(lib.TIFFIsTiled(tif))
        layout.update({tag: get_u32(tif, tag) for tag in (IMAGEWIDTH, IMAGELENGTH) + ((TILEWIDTH, TILELENGTH) if tiled else (ROWSPERSTRIP,))})
        count = lib.TIFFNumberOfTiles(tif) if tiled else lib.TIFFNumberOfStrips(tif)
        if raw:
            size_of, read = (raw_tile_size, lib.TIFFReadRawTile) if tiled else (lib.TIFFRawStripSize, lib.TIFFReadRawStrip)
        else:
            read = lib.TIFFReadEncodedTile if tiled else lib.TIFFReadEncodedStrip
            size = (lib.TIFFTileSize if tiled else lib.TIFFStripSize)(tif)
            size_of = lambda tif, s: size
        strips = []
        for s in range(count):
            n_max = size_of(tif, s)
            buf = ctypes.create_string_buffer(n_max)
            n = read(tif, s, buf, n_max)
            if n < 0:
                raise RuntimeError(f"chunk {s} unreadable")
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
        for tag in (IMAGEWIDTH, IMAGELENGTH, ROWSPERSTRIP, TILEWIDTH, TILELENGTH):
            if tag in layout:
                lib.TIFFSetField(tif, tag, ctypes.c_uint32(layout[tag]))
        for tag, value in ((BITSPERSAMPLE, layout[BITSPERSAMPLE]), (SAMPLESPERPIXEL, spp), (PLANARCONFIG, 1),
                           (PHOTOMETRIC, (0 if layout[BITSPERSAMPLE] == 1 else 1) if spp <= 2 else 2),
                           (COMPRESSION, layout[COMPRESSION])):
            lib.TIFFSetField(tif, tag, ctypes.c_int(value))
        if layout[PREDICTOR] != 1:
            lib.TIFFSetField(tif, PREDICTOR, ctypes.c_int(layout[PREDICTOR]))
        if spp in (2, 4):
            lib.TIFFSetField(tif, EXTRASAMPLES, ctypes.c_int(1), ctypes.byref(ctypes.c_uint16(2)))
        write = lib.TIFFWriteEncodedTile if TILEWIDTH in layout else lib.TIFFWriteEncodedStrip
        for s, data in enumerate(strips):
            if write(tif, s, data, len(data)) < 0:
                raise RuntimeError(f"libtiff could not write strip {s}")
        lib.TIFFClose(tif)
        return read_strips(out_path, raw=True)[1]


def get_blob(tif, tag):
    count, ptr = ctypes.c_uint32(), ctypes.c_void_p()
    if not lib.TIFFGetField(tif, tag, ctypes.byref(count), ctypes.byref(ptr)):
        return None
    return ctypes.string_at(ptr, count.value)


def libtiff_read(path, orientation=1, page=0):
    """
    The TIFFReadRGBAImage raster as an (h, w, 4) array, the ICC profile and the XMP packet. libtiff only flips the
    raster to the requested orientation, treating 5-8 as 1-4, so requesting the file's own one keeps the stored order.
    """
    tif = lib.TIFFOpen(path.encode(), b"r")
    if not tif:
        raise RuntimeError("TIFFOpen failed")
    try:
        if not lib.TIFFSetDirectory(tif, page):
            raise RuntimeError(f"TIFFSetDirectory({page}) failed")
        w, h = get_u32(tif, IMAGEWIDTH), get_u32(tif, IMAGELENGTH)
        raster = (ctypes.c_uint32 * (w * h))()
        if not lib.TIFFReadRGBAImageOriented(tif, w, h, raster, (orientation - 1) % 4 + 1, 1):
            raise RuntimeError("TIFFReadRGBAImageOriented failed")
        v = np.frombuffer(raster, np.uint32).reshape(h, w)
        rgba = np.dstack([(v >> s) & 0xFF for s in (0, 8, 16, 24)]).astype(np.uint8)
        return rgba, get_blob(tif, ICCPROFILE), get_blob(tif, XMLPACKET)
    finally:
        lib.TIFFClose(tif)


def tile_samples(path, page):
    """The samples of a contiguous tiled page from TIFFReadEncodedTile, as an (h, w, samples) array."""
    layout, tiles = read_strips(path, raw=False, page=page)
    w, h, tw, th, spp = (layout[t] for t in (IMAGEWIDTH, IMAGELENGTH, TILEWIDTH, TILELENGTH, SAMPLESPERPIXEL))
    across = (w + tw - 1) // tw
    out = np.zeros(((h + th - 1) // th * th, across * tw, spp), np.uint8)
    for i, tile in enumerate(tiles):
        y, x = i // across * th, i % across * tw
        out[y:y + th, x:x + tw] = np.frombuffer(tile, np.uint8).reshape(th, tw, spp)
    return out[:h, :w]


def lib_tiled(path, page):
    tif = lib.TIFFOpen(path.encode(), b"r")
    try:
        lib.TIFFSetDirectory(tif, page)
        return bool(lib.TIFFIsTiled(tif))
    finally:
        lib.TIFFClose(tif)


def is_bigtiff(path):
    with open(path, "rb") as f:
        return f.read(4)[2] == 43


def premultiply(rgba):
    a = rgba[..., 3:4].astype(np.uint32)
    color = (rgba[..., :3].astype(np.uint32) * a + 127) // 255
    return np.concatenate([color, a], axis=2).astype(np.uint8)


def close(a, b):
    """Whether two RGBA rasters are within the JPEG tolerance on RGB and have the same alpha."""
    d = np.abs(a[..., :3].astype(np.int32) - b[..., :3].astype(np.int32)).max(axis=2)
    same_alpha = np.array_equal(a[..., 3], b[..., 3])
    return d.mean() < 1.5 and d.max() <= 24 and same_alpha, f"mean {d.mean():.3f}, max {d.max()}, alpha {'same' if same_alpha else 'differs'}"


def pillow_exif(path, software, page=0):
    """The EXIF Pillow reads: the descriptive IFD0 tags and the EXIF, GPS and interoperability IFDs without pointers."""
    with Image.open(path) as im:
        im.seek(page)
        exif = im.getexif()
        tags = DESCRIPTIVE + ((SOFTWARE,) if software else ())
        ifd0 = {t: v for t, v in exif.items() if t in tags}
        sub = {t: v for t, v in exif.get_ifd(EXIFIFD).items() if t != INTEROPIFD}
        interop = dict(exif.get_ifd(INTEROPIFD)) if EXIFIFD in exif else {}
        return {"ifd0": ifd0, "exif": sub, "gps": dict(exif.get_ifd(GPSIFD)), "interop": interop}


def first_page(path):
    """The index of the first full-resolution IFD, which the decoder reads as page 0 (0 when every IFD is reduced)."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    if not tif:
        return 0
    try:
        index = 0
        while True:
            new, old = ctypes.c_uint32(), ctypes.c_uint16()
            reduced = (lib.TIFFGetField(tif, NEWSUBFILETYPE, ctypes.byref(new)) and new.value & 1) or \
                (lib.TIFFGetField(tif, SUBFILETYPE, ctypes.byref(old)) and old.value == 2)
            if not reduced:
                return index
            if not lib.TIFFReadDirectory(tif):
                return 0
            index += 1
    finally:
        lib.TIFFClose(tif)


def libtiff_exif(path, page=0):
    """The tags of the EXIF and GPS IFDs of IFD <page> as libtiff reads them, by IFD, and the messages of those reads."""
    found, warnings = {}, []
    for name, tag, read in (("exif", EXIFIFD, lib.TIFFReadEXIFDirectory), ("gps", GPSIFD, lib.TIFFReadGPSDirectory)):
        tif = lib.TIFFOpen(path.encode(), b"r")
        try:
            lib.TIFFSetDirectory(tif, page)
            offset = ctypes.c_uint64()
            if not lib.TIFFGetField(tif, tag, ctypes.byref(offset)):
                found[name] = set()
                continue
            messages.clear()
            if not read(tif, offset.value):
                raise RuntimeError(f"libtiff could not read the {name} IFD")
            warnings += messages
            found[name] = {lib.TIFFGetTagListEntry(tif, i) for i in range(lib.TIFFGetTagListCount(tif))}
        finally:
            lib.TIFFClose(tif)
    return found, warnings


def verify_exif(path, entry, pillow=True, page=0):
    """The EXIF checks; without <pillow> only libtiff reads the file, against what Pillow reads from the JPEG."""
    errors = []
    checked["exif"] += 1
    expected = pillow_exif(os.path.join(os.path.dirname(path), entry["exif"]), entry.get("software"))
    ours = pillow_exif(path, entry.get("software"), page) if pillow else expected
    for ifd in expected:
        if ours[ifd] != expected[ifd]:
            errors.append(f"Pillow {ifd} IFD differs: {ours[ifd]} vs {expected[ifd]}")
    tags, warnings = libtiff_exif(path, page)
    if any(m != INTEROP_WARNING for m in warnings):
        errors.append("libtiff EXIF: " + "; ".join(warnings))
    for ifd in ("exif", "gps"):
        # libtiff lists the interoperability pointer with the EXIF tags.
        wanted = set(expected[ifd]) | ({INTEROPIFD} if ifd == "exif" and expected["interop"] else set())
        if tags[ifd] != wanted:
            errors.append(f"libtiff {ifd} tags {sorted(tags[ifd])} vs {sorted(wanted)}")
    return errors


def libtiff_pages(path):
    """The SHA-256 of the decoded strips or tiles of every IFD (None where libtiff cannot decode them) and its messages."""
    messages.clear()
    tif = lib.TIFFOpen(path.encode(), b"r")
    pages = []
    if not tif:
        return pages, list(messages)
    try:
        while True:
            tiled = lib.TIFFIsTiled(tif)
            count = lib.TIFFNumberOfTiles(tif) if tiled else lib.TIFFNumberOfStrips(tif)
            size = lib.TIFFTileSize(tif) if tiled else lib.TIFFStripSize(tif)
            digest = hashlib.sha256()
            for i in range(count):
                buf = ctypes.create_string_buffer(size)
                n = (lib.TIFFReadEncodedTile if tiled else lib.TIFFReadEncodedStrip)(tif, i, buf, size)
                if n < 0:
                    digest = None
                    break
                digest.update(buf.raw[:n])
            pages.append(digest and digest.hexdigest())
            if not lib.TIFFReadDirectory(tif):
                break
    finally:
        lib.TIFFClose(tif)
    return pages, list(messages)


def verify_edited(path, entry):
    errors = []
    original = os.path.join(os.path.dirname(path), entry["original"])
    theirs, before = libtiff_pages(original)
    ours, after = libtiff_pages(path)
    # libtiff cannot open a file whose missing byte counts it does not estimate (several strips without
    # StripByteCounts, rgb8_no_bytecounts); the edit stores the plugin's estimates (0.7.2), and libtiff then reads it.
    readable_now = not theirs and ours and None not in ours
    if ours != theirs and not readable_now:
        errors.append(f"libtiff pages differ from the original: {len(ours)} vs {len(theirs)} IFDs")
    new = [m for m in after if m not in before]
    if new:
        errors.append("libtiff: " + "; ".join(new))
    checked["edited"] += 1
    page = first_page(original)
    tif = lib.TIFFOpen(path.encode(), b"r")
    if tif:
        lib.TIFFSetDirectory(tif, page)
        if (sha(get_blob(tif, ICCPROFILE)), sha(get_blob(tif, XMLPACKET))) != (entry["icc"], entry["xmp"]):
            errors.append("libtiff ICC or XMP differs")
        lib.TIFFClose(tif)
    pillow = opens(original)
    if pillow:
        with Image.open(path) as im:
            im.seek(page)
            if sha(im.tag_v2.get(XMLPACKET)) != entry["xmp"] or sha(im.info.get("icc_profile")) != entry["icc"]:
                errors.append("Pillow ICC or XMP differs")
            dpi = [float(d) for d in im.info.get("dpi", (0, 0))]
            if any(abs(a - b) > 1e-3 for a, b in zip(dpi, entry["dpi"])):
                errors.append(f"Pillow dpi {dpi}")
    if entry["exif"] and tif:
        errors += verify_exif(path, entry, pillow, page)
    return errors


def opens(path):
    """Whether Pillow opens the file; it does not support every layout libtiff reads."""
    try:
        with Image.open(path):
            return True
    except Exception:
        return False


def verify(path, entry, page=0):
    if entry.get("original"):
        return verify_edited(path, entry)
    errors = []
    ours = None
    if entry.get("decoded"):
        with Image.open(os.path.join(os.path.dirname(path), entry["decoded"])) as im:
            ours = np.asarray(im.convert("RGBA"))
        if sha(ours.tobytes()) != entry["sha256"]:
            errors.append("decoded PNG differs from written.json")
    with Image.open(path) as im:
        im.seek(page)
        orientation = im.tag_v2.get(ORIENTATION, 1)
        im.load()
        stored = im.transpose(UNDO[orientation]) if orientation in UNDO else im
        pil = np.asarray(stored.convert("RGBA"))
        premultiplied = im.mode == "RGBA"
        pil_mode = im.mode
        if stored.size != (entry["width"], entry["height"]):
            errors.append(f"Pillow size {stored.size}")
        if ours is not None:
            ok, detail = close(pil, ours)
            if not ok:
                errors.append(f"Pillow pixels not close: {detail}")
        elif sha(pil.tobytes()) != entry["sha256"]:
            errors.append(f"Pillow pixels differ (mode {im.mode})")
        if sha(im.info.get("icc_profile")) != entry["icc"]:
            errors.append("Pillow ICC differs")
        if sha(im.tag_v2.get(XMLPACKET)) != entry["xmp"]:
            errors.append("Pillow XMP differs")
        dpi = [float(d) for d in im.info.get("dpi", (0, 0))]
        if any(abs(a - b) > 1e-3 for a, b in zip(dpi, entry["dpi"])):
            errors.append(f"Pillow dpi {dpi}")

    messages.clear()
    raster, icc, xmp = libtiff_read(path, orientation, page)
    if messages:
        errors.append("libtiff: " + "; ".join(messages))
    if ours is not None:
        ok, detail = close(raster, premultiply(ours) if premultiplied else ours)
        if not ok:
            errors.append(f"libtiff pixels not close: {detail}")
    elif not np.array_equal(raster, premultiply(pil) if premultiplied else pil):
        if pil_mode == "LA" and lib_tiled(path, page):
            samples = tile_samples(path, page)
            if not np.array_equal(samples, pil[..., [0, 3]]):
                errors.append("libtiff tile samples differ")
        else:
            errors.append("libtiff pixels differ")
    if sha(icc) != entry["icc"]:
        errors.append("libtiff ICC differs")
    if sha(xmp) != entry["xmp"]:
        errors.append("libtiff XMP differs")

    if entry.get("exif"):
        errors += verify_exif(path, entry, pillow=not is_bigtiff(path), page=page)

    layout, ours = read_strips(path, raw=True, page=page)
    if layout[COMPRESSION] in totals:
        theirs = libtiff_reencode(path, layout, read_strips(path, raw=False, page=page)[1])
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
        path = os.path.join(root, name)
        errors = verify(path, entry)
        if isinstance(entry.get("pages"), list):  # edited files list only their page count
            checked["pages"] += 1 + len(entry["pages"])
            for page, sub in enumerate(entry["pages"], 1):
                errors += [f"page {page}: {e}" for e in verify(path, sub, page)]
            with Image.open(path) as im:
                if im.n_frames != 1 + len(entry["pages"]):
                    errors.append(f"Pillow reads {im.n_frames} pages")
            tif = lib.TIFFOpen(path.encode(), b"r")
            if lib.TIFFNumberOfDirectories(tif) != 1 + len(entry["pages"]):
                errors.append(f"libtiff reads {lib.TIFFNumberOfDirectories(tif)} pages")
            lib.TIFFClose(tif)
        print(f"{'FAIL' if errors else 'ok  '} {name}" + ("".join(f"\n     {e}" for e in errors)))
        failed += bool(errors)
    print(f"{len(manifest) - failed}/{len(manifest)} files verified; strips identical to libtiff: "
          f"LZW {totals[LZW]} bytes, PackBits {totals[PACKBITS]} bytes, Group 4 {totals[CCITT_G4]} bytes; "
          f"EXIF checked in {checked['exif']}, edited files {checked['edited']}, pages of multi-page files {checked['pages']}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
