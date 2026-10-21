#!/usr/bin/env python3
"""libtiff 4.5.1 (ctypes) side of the TIFF speed comparison with PictureSharp.Tiff.Speed.

Usage: python3 -I benchmarks/libtiff_speed.py <ours.none.tif> [--runs 5]

Reads the RGB samples and RowsPerStrip of an uncompressed file the .NET probe wrote (encode --out), then times libtiff
writing them with None, LZW and Deflate (predictor 2, level 6 as the PictureSharp defaults) to a file in /dev/shm,
and reading each result back strip by strip (TIFFReadEncodedStrip, samples only) and as RGBA (TIFFReadRGBAImage, the
work Image.Load does). Best of N runs after a warm-up run, single-threaded. Prints TSV rows like the .NET probe.

libtiff 4.5.1 as Ubuntu builds it compresses whole Deflate strips with libdeflate, not zlib; PictureSharp uses the
runtime's zlib (System.IO.Compression), so Deflate compares libdeflate with zlib.
"""

import ctypes
import os
import sys
import time

lib = ctypes.CDLL("libtiff.so.6")
lib.TIFFOpen.restype = ctypes.c_void_p
lib.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
lib.TIFFClose.argtypes = [ctypes.c_void_p]
lib.TIFFGetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFSetField.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
lib.TIFFNumberOfStrips.argtypes = [ctypes.c_void_p]
lib.TIFFNumberOfStrips.restype = ctypes.c_uint32
lib.TIFFStripSize.argtypes = [ctypes.c_void_p]
lib.TIFFStripSize.restype = ctypes.c_int64
lib.TIFFReadEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFReadEncodedStrip.restype = ctypes.c_int64
lib.TIFFWriteEncodedStrip.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
lib.TIFFWriteEncodedStrip.restype = ctypes.c_int64
lib.TIFFReadRGBAImage.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int]

WIDTH, LENGTH, BITS, COMPRESSION, PHOTOMETRIC, SPP, ROWS, PLANAR, PREDICTOR, ZIPQUALITY = \
    256, 257, 258, 259, 262, 277, 278, 284, 317, 65557
CODECS = (("None", 1), ("Lzw", 5), ("Deflate", 8))


def u32(tif, tag):
    value = ctypes.c_uint32()
    lib.TIFFGetField(tif, tag, ctypes.byref(value))
    return value.value


def read(path):
    """The strips of a file (decoded samples) and its width, height and rows per strip."""
    tif = lib.TIFFOpen(path.encode(), b"r")
    w, h, rows = u32(tif, WIDTH), u32(tif, LENGTH), u32(tif, ROWS)
    size = lib.TIFFStripSize(tif)
    buf = ctypes.create_string_buffer(size)
    strips = []
    for s in range(lib.TIFFNumberOfStrips(tif)):
        n = lib.TIFFReadEncodedStrip(tif, s, buf, size)
        if n < 0:
            raise RuntimeError(f"strip {s} unreadable")
        strips.append(buf.raw[:n])
    lib.TIFFClose(tif)
    return strips, w, h, rows


def read_rgba(path):
    tif = lib.TIFFOpen(path.encode(), b"r")
    w, h = u32(tif, WIDTH), u32(tif, LENGTH)
    raster = (ctypes.c_uint32 * (w * h))()
    if not lib.TIFFReadRGBAImage(tif, w, h, raster, 1):
        raise RuntimeError("TIFFReadRGBAImage failed")
    lib.TIFFClose(tif)


def write(path, strips, w, h, rows, compression):
    tif = lib.TIFFOpen(path.encode(), b"w")
    for tag, value in ((WIDTH, w), (LENGTH, h), (ROWS, rows)):
        lib.TIFFSetField(tif, tag, ctypes.c_uint32(value))
    for tag, value in ((BITS, 8), (SPP, 3), (PLANAR, 1), (PHOTOMETRIC, 2), (COMPRESSION, compression)):
        lib.TIFFSetField(tif, tag, ctypes.c_int(value))
    if compression != 1:
        lib.TIFFSetField(tif, PREDICTOR, ctypes.c_int(2))
    if compression == 8:
        lib.TIFFSetField(tif, ZIPQUALITY, ctypes.c_int(6))
    for s, data in enumerate(strips):
        if lib.TIFFWriteEncodedStrip(tif, s, data, len(data)) < 0:
            raise RuntimeError(f"strip {s} not written")
    lib.TIFFClose(tif)


def best(action, runs):
    action()
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        action()
        times.append((time.perf_counter() - start) * 1000)
    return min(times)


def main():
    source = sys.argv[1]
    runs = int(sys.argv[sys.argv.index("--runs") + 1]) if "--runs" in sys.argv else 5
    strips, w, h, rows = read(source)
    name = os.path.basename(source).split(".")[0]
    print("operation\tfile\tcompression\tthreads\tms\talloc_mb\tbytes")
    for label, compression in CODECS:
        path = f"/dev/shm/libtiff_speed.{label.lower()}.tif"
        ms = best(lambda: write(path, strips, w, h, rows, compression), runs)
        print(f"libtiff-encode\t{name}\t{label}\t1\t{ms:.1f}\t-\t{os.path.getsize(path)}")
        ms = best(lambda: read(path), runs)
        print(f"libtiff-decode\t{name}\t{label}\t1\t{ms:.1f}\t-\t{os.path.getsize(path)}")
        ms = best(lambda: read_rgba(path), runs)
        print(f"libtiff-rgba\t{name}\t{label}\t1\t{ms:.1f}\t-\t{os.path.getsize(path)}")
        os.remove(path)


if __name__ == "__main__":
    main()
