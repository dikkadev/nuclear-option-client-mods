# /// script
# requires-python = ">=3.13,<3.14"
# dependencies = ["numpy>=2.2,<2.4", "numba>=0.63,<0.66", "pillow>=12,<14", "etcpak==0.9.15", "psutil>=7,<8"]
# ///
"""CPU-only, bounded-memory satellite producer. Run: uv run --script FILE --map all --preview

No tiles, supersample images, or intermediate mips are written to disk. The only
outputs are .nosat files and optional small PNGs. Geometry is memory-mapped with
allow_pickle=False. Mip 0 stays uint8; subsequent mips retain float32 parents.
The Windows etcpak 0.9.15 decoder has an upstream bytes/bytearray accessor bug;
the isolated codec probe corrects its private import slot temporarily (see below).
"""

from __future__ import annotations

import os
import sys

# uv forwards arguments after the script to argparse, rather than to Python.
# Re-exec before importing packages or reading downloaded input, preserving uv's
# dependency interpreter while enforcing Python isolated mode for all commands.
if __name__ == "__main__" and not sys.flags.isolated:
    import subprocess
    with subprocess.Popen([sys.executable, "-I", "-u", __file__, *sys.argv[1:]],
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as child:
        for line in child.stdout:
            print(line, end="", flush=True)
        raise SystemExit(child.wait())

# Set these before importing numpy/numba. No GPU libraries are used.
for _name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS",
              "NUMEXPR_NUM_THREADS", "NUMBA_NUM_THREADS"):
    os.environ[_name] = "4" if _name == "NUMBA_NUM_THREADS" else "1"

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import ctypes
from datetime import datetime, timezone
import io
import json
import math
from pathlib import Path
import struct
import subprocess
import time
import urllib.error
import urllib.request
import zlib

import etcpak
import numpy as np
from numba import njit, set_num_threads
from PIL import Image
import psutil

set_num_threads(4)
WORKERS = 4
MAGIC = b"NOSATMP1"
# Version 3 adds the tile grid after v2's compression and stored payload length.
FIXED = struct.Struct("<8s6I2f2IQIQ2I")
VERSION = 3
COMPRESSION_DEFLATE = 1
MAX_TILE_SIDE = 16384
WATER_RGB = (18, 78, 101)
MAPS = {
    "heartland": ("heartland-46396", "Heartland", 81920, 81920, 4096, 4096),
    "ignus": ("ignus-archipelago-46395", "Ignus Archipelago", 163840, 81920, 8192, 4096),
}


def mip_count(width: int, height: int) -> int:
    return max(width, height).bit_length()


def bc3_size(width: int, height: int) -> int:
    return sum(((max(1, width >> i) + 3) // 4) *
               ((max(1, height >> i) + 3) // 4) * 16
               for i in range(mip_count(width, height)))


def tile_layout(width: int, height: int) -> tuple[int, int, int, int]:
    """Smallest power-of-two grid; return columns, rows, tile width, tile height."""
    if min(width, height) < 1:
        raise ValueError("Texture dimensions must be positive")
    columns = rows = 1
    while width > columns * MAX_TILE_SIDE:
        columns *= 2
    while height > rows * MAX_TILE_SIDE:
        rows *= 2
    if width % columns or height % rows:
        raise ValueError("Texture dimensions must divide evenly into the tile grid")
    tw, th = width // columns, height // rows
    if columns * rows > 1 and (tw & (tw-1) or th & (th-1)):
        raise ValueError("Tiled texture dimensions must be powers of two")
    return columns, rows, tw, th


def pyramid_bytes(width: int, height: int, levels: int) -> int:
    """Resident image arrays: uint8 base, then full-image float32 mips."""
    return width*height*4 + sum(max(1, width >> k)*max(1, height >> k)*16
                                for k in range(1, levels))


def make_header(spec: tuple, width: int, height: int, source: str, stored_length: int = 0) -> bytes:
    map_id, name, meters_w, meters_h, native_w, native_h = spec
    columns, rows, tw, th = tile_layout(width, height)
    strings = bytearray()
    for value in (map_id, name, source):
        encoded = value.encode("utf-8")
        if len(encoded) > 65535:
            raise ValueError("Header string exceeds the u16 UTF-8 byte limit")
        strings.extend(struct.pack("<H", len(encoded)))
        strings.extend(encoded)
    header_size = (FIXED.size + len(strings) + 15) & ~15
    fixed = FIXED.pack(MAGIC, header_size, VERSION, 1, width, height,
                       mip_count(tw, th), meters_w, meters_h,
                       native_w, native_h, columns*rows*bc3_size(tw, th),
                       COMPRESSION_DEFLATE, stored_length, columns, rows)
    return (fixed + strings).ljust(header_size, b"\0")


def canonicalize_color(blocks: np.ndarray) -> None:
    endpoints = blocks[:, 8:12].copy().view("<u2").reshape(-1, 2)
    swap = endpoints[:, 0] < endpoints[:, 1]
    blocks[swap, 8:10], blocks[swap, 10:12] = (
        blocks[swap, 10:12].copy(), blocks[swap, 8:10].copy())
    blocks[swap, 12:16] ^= np.uint8(0x55)
    blocks[endpoints[:, 0] == endpoints[:, 1], 12:16] = 0


def encode_alpha(alpha: np.ndarray) -> np.ndarray:
    """Input has shape (blocks,16), with row-major pixels in each block."""
    values = alpha.astype(np.int16)
    a0, a1 = values.max(axis=1), values.min(axis=1)
    # Compare in units of 1/7 to avoid floating point nearest-index ambiguity.
    palette = np.stack([7 * a0, 7 * a1] +
                       [(8 - k) * a0 + (k - 1) * a1 for k in range(2, 8)], axis=1)
    indices = np.zeros(values.shape, np.uint64)
    best = np.full(values.shape, 32767, np.int16)
    for k in range(8):
        distance = np.abs(7 * values - palette[:, k, None])
        take = distance < best
        indices[take] = k
        np.minimum(best, distance, out=best)
    indices[a0 == a1] = 0
    bits = np.bitwise_or.reduce(indices << (3 * np.arange(16, dtype=np.uint64)), axis=1)
    result = np.zeros((len(alpha), 8), np.uint8)
    result[:, 0], result[:, 1] = a0, a1
    for i in range(6):
        result[:, 2 + i] = (bits >> (8 * i)).astype(np.uint8)
    return result


def encode_bc3(rgba: np.ndarray) -> bytes:
    """Encode an already bottom-first strip. Edge replication handles tiny mips."""
    height, width = rgba.shape[:2]
    padded = np.pad(rgba, ((0, (-height) % 4), (0, (-width) % 4), (0, 0)), mode="edge")
    ph, pw = padded.shape[:2]
    blocks = np.frombuffer(etcpak.compress_bc3(padded.tobytes(), pw, ph), np.uint8).copy().reshape(-1, 16)
    canonicalize_color(blocks)
    alpha = padded[:, :, 3].reshape(ph // 4, 4, pw // 4, 4).transpose(0, 2, 1, 3).reshape(-1, 16)
    blocks[:, :8] = encode_alpha(alpha)
    return blocks.tobytes()


def assert_canonical(data: bytes) -> None:
    blocks = np.frombuffer(data, np.uint8).reshape(-1, 16)
    equal = blocks[:, 0] == blocks[:, 1]
    if not np.all((blocks[:, 0] > blocks[:, 1]) | equal) or np.any(blocks[equal, 2:8]):
        raise AssertionError("Noncanonical alpha block")
    colors = blocks[:, 8:12].copy().view("<u2").reshape(-1, 2)
    equal = colors[:, 0] == colors[:, 1]
    if np.any(colors[:, 0] < colors[:, 1]) or np.any(blocks[equal, 12:16]):
        raise AssertionError("Noncanonical color block")


def decode_reference(data: bytes, width: int, height: int) -> np.ndarray:
    """Independent scalar BC3 decoder, including standard integer interpolation."""
    out = np.empty((((height + 3) // 4) * 4, ((width + 3) // 4) * 4, 4), np.uint8)
    offset = 0
    for y in range(0, out.shape[0], 4):
        for x in range(0, out.shape[1], 4):
            block = data[offset:offset + 16]
            offset += 16
            a0, a1 = block[:2]
            alpha = [a0, a1] + [((8-k)*a0+(k-1)*a1)//7 for k in range(2, 8)]
            c0, c1, indices = struct.unpack_from("<HHI", block, 8)
            def rgb(c):
                r, g, b = c >> 11, (c >> 5) & 63, c & 31
                return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))
            colors = [rgb(c0), rgb(c1)]
            colors.extend([tuple((2*a+b)//3 for a,b in zip(*colors)),
                           tuple((a+2*b)//3 for a,b in zip(*colors))])
            abits = int.from_bytes(block[2:8], "little")
            for p in range(16):
                out[y + p//4, x + p%4, :3] = colors[(indices >> (2*p)) & 3]
                out[y + p//4, x + p%4, 3] = alpha[(abits >> (3*p)) & 7]
    return out[:height, :width]


@contextmanager
def decoder_compat():
    """Work around etcpak 0.9.15's decoder bug in this process only, then restore.

    Upstream src/pylink.cpp creates PyBytes but calls PyByteArray_AsString.
    On Windows its import table allows redirecting that one accessor to the
    correct CPython API. No package/system file is changed. Production calls
    this only in the short-lived -I codec-probe child, never while fetching data.
    https://github.com/K0lb3/etcpak/blob/master/src/pylink.cpp#L130-L139
    """
    if sys.platform != "win32":
        raise RuntimeError("etcpak 0.9.15 decoder needs its upstream accessor fix on this platform")
    module = next(m for n, m in sys.modules.copy().items()
                  if n.startswith("etcpak._") and getattr(m, "compress_bc3", None) is etcpak.compress_bc3)
    base = ctypes.WinDLL(module.__file__)._handle
    def u32(offset):
        return struct.unpack("<I", ctypes.string_at(base + offset, 4))[0]
    optional = u32(0x3C) + 24
    if ctypes.string_at(base + optional, 2) != b"\x0b\x02":
        raise RuntimeError("Codec probe requires the Windows x64 etcpak wheel")
    descriptor = u32(optional + 120)  # PE32+ import directory RVA
    slots = []
    while u32(descriptor + 12):
        names, addresses = u32(descriptor), u32(descriptor + 16)
        i = 0
        while True:
            name_rva = struct.unpack("<Q", ctypes.string_at(base + names + 8*i, 8))[0]
            if name_rva == 0:
                break
            if not (name_rva >> 63) and ctypes.string_at(base + name_rva + 2) == b"PyByteArray_AsString":
                slots.append(ctypes.c_void_p.from_address(base + addresses + 8*i))
            i += 1
        descriptor += 20
    if len(slots) != 1:
        raise RuntimeError("Could not locate the known etcpak decoder accessor; refusing to decode")
    protect = ctypes.windll.kernel32.VirtualProtect
    protect.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]
    def replace(pointer):
        previous = ctypes.c_ulong()
        if not protect(ctypes.addressof(slots[0]), 8, 4, ctypes.byref(previous)):
            raise ctypes.WinError()
        slots[0].value = pointer
        ignored = ctypes.c_ulong()
        if not protect(ctypes.addressof(slots[0]), 8, previous.value, ctypes.byref(ignored)):
            raise ctypes.WinError()
    saved = slots[0].value
    replace(ctypes.cast(ctypes.pythonapi.PyBytes_AsString, ctypes.c_void_p).value)
    try:
        yield
    finally:
        replace(saved)


def codec_probe() -> None:
    with decoder_compat():
        # Asymmetric red/blue pattern verifies compressor AND decoder ordering.
        pattern = np.empty((8, 8, 4), np.uint8)
        pattern[:4, :4] = (240, 16, 32, 255)
        pattern[:4, 4:] = (16, 32, 240, 255)
        pattern[4:, :4] = (24, 200, 48, 255)
        pattern[4:, 4:] = (200, 72, 16, 255)
        raw = etcpak.compress_bc3(pattern.tobytes(), 8, 8)
        decoded = np.frombuffer(etcpak.decompress_bc3(raw, 8, 8), np.uint8).reshape(8, 8, 4)
        if np.max(np.abs(decoded.astype(int) - pattern.astype(int))) > 10:
            raise AssertionError("etcpak RGBA input/output probe failed")
        # Include both constant and nonconstant alpha and color blocks.
        y, x = np.indices((16, 20))
        sample = np.stack((x*10 + 20, y*13 + 10, (x+y)*5, (x*17+y*11)%256), axis=-1).astype(np.uint8)
        sample[:4, :4] = (56, 112, 168, 0)
        sample[4:8, :4] = (0, 0, 0, 255)
        encoded = encode_bc3(sample)
        assert_canonical(encoded)
        actual = np.frombuffer(etcpak.decompress_bc3(encoded, 20, 16), np.uint8).reshape(16, 20, 4)
        reference = decode_reference(encoded, 20, 16)
        if not np.array_equal(actual, reference):
            raise AssertionError(f"Reference decoder differs: max error {np.abs(actual.astype(int)-reference.astype(int)).max()}")
        mse = np.mean((actual.astype(float) - sample.astype(float)) ** 2)
        psnr = 10 * math.log10(255**2 / mse)
        if psnr < 25:
            raise AssertionError(f"Unexpectedly poor codec roundtrip: {psnr:.2f} dB")
        print(f"Codec: RGBA verified; canonical blocks; reference decode identical; PSNR {psnr:.2f} dB (Windows accessor fix in probe only)", flush=True)


class TileSource:
    def __init__(self, root: Path, spec: tuple, level: int, origin: str):
        variants = json.loads((root / "demo/public/maps/variants.json").read_text(encoding="utf-8"))
        satellite = next(v for v in variants["maps"][spec[0]] if v["id"] == "satellite")
        self.level = next(v for v in satellite["tiles"]["levels"] if v["level"] == level)
        self.size = int(self.level.get("tileSize", satellite["tiles"].get("tileSize", 512)))
        self.gutter = int(self.level.get("gutter", satellite["tiles"].get("gutter", 2)))
        self.width, self.height = int(self.level["width"]), int(self.level["height"])
        self.root, self.origin = root / "demo/public/maps", origin.rstrip("/")
        self.cache: dict[tuple[int, int], Image.Image] = {}
        self.missing = 0

    def url(self, x: int, y: int) -> str:
        return self.level.get("urlOverrides", {}).get(
            f"{x}/{y}", self.level["urlTemplate"].replace("{x}", str(x)).replace("{y}", str(y)))

    def fetch(self, xy: tuple[int, int]) -> tuple[tuple[int, int], Image.Image, bool]:
        x, y = xy
        url = self.url(x, y)
        remote = url.startswith("/maps/candidate-v8/")
        missing = False
        if remote:
            url = self.origin + "/" + url.removeprefix("/maps/candidate-v8/")
            for attempt in range(4):
                try:
                    with urllib.request.urlopen(url, timeout=18) as response:
                        data = response.read(8 * 1024 * 1024 + 1)
                    if len(data) > 8 * 1024 * 1024:
                        raise ValueError(f"Tile exceeds 8 MiB: {url}")
                    break
                except urllib.error.HTTPError as exc:
                    if exc.code == 404:
                        missing = True
                        break
                    if attempt == 3:
                        raise RuntimeError(f"Tile server request failed: {url}: {exc}") from exc
                    time.sleep(0.5 * 2**attempt)
                except (urllib.error.URLError, TimeoutError, OSError) as exc:
                    if attempt == 3:
                        raise RuntimeError(f"Tile server unreachable: {url}: {exc}") from exc
                    time.sleep(0.5 * 2**attempt)
        else:
            local = self.root / url.removeprefix("/maps/")
            data = local.read_bytes()  # A missing local override is an error, not ocean.
        if missing:
            image = Image.new("RGB", (self.size, self.size), WATER_RGB)
        else:
            with Image.open(io.BytesIO(data)) as stored:
                expected = self.size + 2*self.gutter
                if stored.size != (expected, expected):
                    raise ValueError(f"Unexpected tile dimensions {stored.size} at {url}, expected {expected} square")
                image = stored.convert("RGB").crop((self.gutter, self.gutter,
                                                     self.gutter+self.size, self.gutter+self.size))
        return xy, image, missing

    def render(self, rgba: np.ndarray) -> None:
        height, width = rgba.shape[:2]
        ratio = self.height / height
        support = 3 * max(1.0, ratio) + 2
        # Limit source-strip storage even when selecting very fine source levels.
        strip_rows = min(256, max(1, int((96*1024**2/self.width/3 - 2*support)/ratio)))
        if (strip_rows*ratio + 2*support + 2*self.size) * self.width * 3 > 256*1024**2:
            raise ValueError("Source level/scale exceeds the 256 MiB source-strip bound")
        with ThreadPoolExecutor(max_workers=WORKERS) as pool:
            for start in range(0, height, strip_rows):
                stop = min(height, start + strip_rows)
                y0 = max(0, math.floor(start*ratio-support))
                y1 = min(self.height, math.ceil(stop*ratio+support))
                first, last = y0//self.size, (y1-1)//self.size
                self.cache = {xy: image for xy, image in self.cache.items() if first <= xy[1] <= last}
                needed = [(x, y) for y in range(first, last+1)
                          for x in range(self.level["columns"]) if (x, y) not in self.cache]
                for xy, image, missing in pool.map(self.fetch, needed):
                    self.cache[xy] = image
                    self.missing += int(missing)
                strip = Image.new("RGB", (self.width, y1-y0))
                for (x, y), image in self.cache.items():
                    strip.paste(image, (x*self.size, y*self.size-y0))
                reduced = strip.resize((width, stop-start), Image.Resampling.LANCZOS,
                                       box=(0, start*ratio-y0, self.width, stop*ratio-y0))
                rgba[start:stop, :, :3] = np.asarray(reduced)
                if start == 0 or stop == height or stop % 1024 == 0:
                    print(f"  imagery {stop}/{height} rows", flush=True)
        self.cache.clear()


@njit(nogil=True)
def bin_triangles(vertices, faces, width, height, meters_w, meters_h, chunk):
    columns, rows = (width+chunk-1)//chunk, (height+chunk-1)//chunk
    counts = np.zeros(columns*rows, np.int64)
    # Two passes avoid a large per-face transformed-coordinate/bounds array.
    ids = np.empty(0, np.int32)
    pointers = np.empty(0, np.int64)
    for phase in range(2):
        if phase:
            pointers = np.empty(len(counts)+1, np.int64)
            pointers[0] = 0
            for i in range(len(counts)):
                pointers[i+1] = pointers[i] + counts[i]
            if pointers[-1] > 64*1024*1024:
                raise ValueError("Triangle chunk index exceeds its 256 MiB memory bound")
            ids = np.empty(pointers[-1], np.int32)
            counts[:] = 0
        for index in range(len(faces)):
            a, b, c = vertices[faces[index, 0]], vertices[faces[index, 1]], vertices[faces[index, 2]]
            left = (min(a[0], b[0], c[0])/meters_w+.5)*width
            right = (max(a[0], b[0], c[0])/meters_w+.5)*width
            top = (.5-max(a[2], b[2], c[2])/meters_h)*height
            bottom = (.5-min(a[2], b[2], c[2])/meters_h)*height
            if right < 0 or left >= width or bottom < 0 or top >= height:
                continue
            x0, x1 = max(0, int(math.floor(left/chunk))), min(columns-1, int(math.floor(right/chunk)))
            y0, y1 = max(0, int(math.floor(top/chunk))), min(rows-1, int(math.floor(bottom/chunk)))
            for y in range(y0, y1+1):
                for x in range(x0, x1+1):
                    cell = y*columns+x
                    if phase:
                        ids[pointers[cell]+counts[cell]] = index
                    counts[cell] += 1
    return pointers, ids


@njit(nogil=True)
def raster_chunk(vertices, faces, ids, depth, x0, y0, w, h, width, height,
                 meters_w, meters_h, supersample, water):
    sw, sh = w*supersample, h*supersample
    zbuffer = np.full((sh, sw), -np.inf, np.float32)
    sx, sy = width*supersample/meters_w, height*supersample/meters_h
    for index in ids:
        a, b, c = vertices[faces[index, 0]], vertices[faces[index, 1]], vertices[faces[index, 2]]
        ax, ay = (a[0]+meters_w/2)*sx-x0*supersample-.5, (meters_h/2-a[2])*sy-y0*supersample-.5
        bx, by = (b[0]+meters_w/2)*sx-x0*supersample-.5, (meters_h/2-b[2])*sy-y0*supersample-.5
        cx, cy = (c[0]+meters_w/2)*sx-x0*supersample-.5, (meters_h/2-c[2])*sy-y0*supersample-.5
        denominator = (by-cy)*(ax-cx)+(cx-bx)*(ay-cy)
        if abs(denominator) < 1e-12:
            continue
        left, right = max(0, math.ceil(min(ax,bx,cx))), min(sw-1, math.floor(max(ax,bx,cx)))
        top, bottom = max(0, math.ceil(min(ay,by,cy))), min(sh-1, math.floor(max(ay,by,cy)))
        for y in range(top, bottom+1):
            for x in range(left, right+1):
                wa = ((by-cy)*(x-cx)+(cx-bx)*(y-cy))/denominator
                wb = ((cy-ay)*(x-cx)+(ax-cx)*(y-cy))/denominator
                wc = 1-wa-wb
                if wa >= -1e-8 and wb >= -1e-8 and wc >= -1e-8:
                    z = wa*a[1]+wb*b[1]+wc*c[1]
                    if z > zbuffer[y, x]:
                        zbuffer[y, x] = z
    alpha = np.empty((h, w), np.uint8)
    uncovered = 0
    for row in range(h):
        for col in range(w):
            land = 0
            for yy in range(supersample):
                y = row*supersample+yy
                # Texture pixel centers, north-up. Clamp at full-extent edges.
                dy = min(depth.shape[0]-1., max(0., ((y0*supersample+y+.5)/(height*supersample))*depth.shape[0]-.5))
                iy = int(dy)
                fy = dy-iy
                jy = min(iy+1, depth.shape[0]-1)
                for xx in range(supersample):
                    x = col*supersample+xx
                    z = zbuffer[y, x]
                    if z != -np.inf:
                        land += int(z >= water)
                    else:
                        uncovered += 1
                        dx = min(depth.shape[1]-1., max(0., ((x0*supersample+x+.5)/(width*supersample))*depth.shape[1]-.5))
                        ix = int(dx)
                        fx = dx-ix
                        jx = min(ix+1, depth.shape[1]-1)
                        value = ((1-fy)*((1-fx)*depth[iy,ix]+fx*depth[iy,jx])+
                                 fy*((1-fx)*depth[jy,ix]+fx*depth[jy,jx]))
                        land += int(value >= 240)
            alpha[row,col] = round(255*land/(supersample*supersample))
    return alpha, uncovered


def build_alpha(root: Path, key: str, spec: tuple, rgba: np.ndarray,
                supersample: int, water: float) -> None:
    directory = root / "work/terrain" / f"tiles-{key}"
    vertices, faces, classes = (np.load(directory / (name+".npy"), mmap_mode="r", allow_pickle=False)
                                for name in ("vertices", "faces", "classes"))
    if vertices.dtype != np.float32 or vertices.ndim != 2 or vertices.shape[1] != 3:
        raise ValueError("Expected float32 world XYZ vertices")
    if faces.dtype != np.int32 or faces.ndim != 2 or faces.shape[1] != 3 or classes.shape != (len(faces),):
        raise ValueError("Expected int32 triangles and one class per face")
    if faces.min() < 0 or faces.max() >= len(vertices) or not np.isfinite(vertices).all():
        raise ValueError("Invalid terrain indices/coordinates")
    with Image.open(root / "work/extraction/composition-textures" / f"{spec[0]}-oceandepthmap.png") as image:
        depth = np.asarray(image.convert("L")).copy()
    height, width = rgba.shape[:2]
    chunk = min(256, max(1, 1024//supersample))  # At most 4 MiB of z-buffer per worker.
    print(f"  binning all {len(faces):,} triangles; chunk {chunk}, supersample {supersample}", flush=True)
    pointers, ids = bin_triangles(vertices, faces, width, height, spec[2], spec[3], chunk)
    columns = (width+chunk-1)//chunk
    cells = len(pointers)-1
    # Compile once before starting workers, so JIT work does not race.
    raster_chunk(vertices, faces, ids[:0], depth, 0, 0, 1, 1,
                 width, height, spec[2], spec[3], supersample, water)
    def render(cell):
        x, y = (cell % columns)*chunk, (cell//columns)*chunk
        alpha, uncovered = raster_chunk(vertices, faces, ids[pointers[cell]:pointers[cell+1]],
                                        depth, x, y, min(chunk,width-x), min(chunk,height-y),
                                        width, height, spec[2], spec[3], supersample, water)
        rgba[y:y+alpha.shape[0], x:x+alpha.shape[1], 3] = alpha
        return uncovered
    uncovered = 0
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        for cell, count in enumerate(pool.map(render, range(cells)), 1):
            uncovered += count
            if cell % max(1, cells//8) == 0 or cell == cells:
                print(f"  land coverage {cell}/{cells} chunks", flush=True)
    print(f"  uncovered samples using ocean-depth fallback: {100*uncovered/(width*height*supersample**2):.3f}%", flush=True)


def srgb_to_linear(value: np.ndarray) -> np.ndarray:
    value = value/255
    return np.where(value <= .04045, value/12.92, ((value+.055)/1.055)**2.4)


def linear_to_srgb(value: np.ndarray) -> np.ndarray:
    value = np.clip(value, 0, 1)
    return 255*np.where(value <= .0031308, value*12.92, 1.055*value**(1/2.4)-.055)


def next_mip(parent: np.ndarray) -> np.ndarray:
    """Box filter float parents, alpha-weight RGB; plain RGB for zero alpha.

    RGB is averaged in linear light because the texture is sampled as sRGB; parents and
    results stay sRGB-encoded floats."""
    h, w = parent.shape[:2]
    nh, nw = max(1, h//2), max(1, w//2)
    result = np.empty((nh, nw, 4), np.float32)
    # Work in strips: never allocate a full float32 copy of the uint8 base mip.
    for start in range(0, nh, 64):
        stop = min(nh, start+64)
        ys = np.minimum(np.arange(start*2, stop*2), h-1)
        xs = np.minimum(np.arange(nw*2), w-1)
        samples = parent[ys[:,None], xs[None,:]].astype(np.float32).reshape(stop-start, 2, nw, 2, 4)
        alpha_sum = samples[:,:,:,:,3].sum(axis=(1,3))
        linear = srgb_to_linear(samples[:,:,:,:,:3])
        weighted = (linear*samples[:,:,:,:,3:4]).sum(axis=(1,3))
        plain = linear.mean(axis=(1,3))
        averaged = np.divide(weighted, alpha_sum[:,:,None], out=plain, where=alpha_sum[:,:,None] > 0)
        result[start:stop,:,:3] = linear_to_srgb(averaged)
        result[start:stop,:,3] = alpha_sum/4
    return result


def write_texture(path: Path, spec: tuple, rgba: np.ndarray, source: str) -> None:
    height, width = rgba.shape[:2]
    columns, rows, tw, th = tile_layout(width, height)
    levels = mip_count(tw, th)
    # Leave >1 GiB for strip filters, codec/JIT, and source/terrain buffers.
    # Defaults need 2.33 GiB (Heartland) / 4.67 GiB (Ignus) of image arrays.
    if pyramid_bytes(width, height, levels) > 6*1024**3:
        raise ValueError("Full mip pyramid exceeds the 6 GiB image-array memory bound")
    header = make_header(spec, width, height, source)
    if path.exists():
        raise FileExistsError(f"Preserving existing output: {path}; select another --output-dir")
    output = path.open("xb")  # Failure here must never unlink another producer's file.
    # A failed producer never leaves a file that looks like a completed container.
    # Raw deflate (no zlib header) matches .NET DeflateStream; the game inflates it on load.
    compressor = zlib.compressobj(9, zlib.DEFLATED, -15)
    raw = 0
    try:
        with output:
            output.write(header)
            # Filter the FULL image before cutting tiles, retaining float parents.
            # Keeping the pyramid allows tile-major streaming without disk scratch.
            pyramid = [rgba]
            for level in range(1, levels):
                pyramid.append(next_mip(pyramid[-1]))
                h, w = pyramid[-1].shape[:2]
                print(f"  full-image mip {level}: {w}x{h}", flush=True)
            for row in range(rows):  # North to south; columns west to east.
                for column in range(columns):
                    for level, parent in enumerate(pyramid):
                        # Clamp only axes already reduced to one pixel (rectangular
                        # tiles); otherwise these are exactly the shifted boundaries.
                        x0 = min(parent.shape[1]-1, (column*tw) >> level)
                        y0 = min(parent.shape[0]-1, (row*th) >> level)
                        w, h = max(1, tw >> level), max(1, th >> level)
                        tile = parent[y0:y0+h, x0:x0+w]
                        if tile.shape[:2] != (h, w):
                            raise AssertionError("Full-image mip cannot supply the tile region")
                        for south in range(0, h, 64):
                            # Each tile/mip is south-first, including pixels within blocks.
                            strip = tile[max(0,h-south-64):h-south][::-1]
                            pixels = np.clip(np.rint(strip), 0, 255).astype(np.uint8) if parent.dtype != np.uint8 else strip
                            data = encode_bc3(pixels)
                            assert_canonical(data)
                            raw += len(data)
                            output.write(compressor.compress(data))
                        print(f"  BC3 tile ({column},{row}) mip {level}: {w}x{h}", flush=True)
            output.write(compressor.flush())
            if raw != columns*rows*bc3_size(tw, th):
                raise AssertionError("Written mip size does not match SatelliteBlocks.Bc3Size")
            stored = output.tell() - len(header)
            output.seek(0)
            final = make_header(spec, width, height, source, stored)
            if len(final) != len(header):
                raise AssertionError("Header size changed while recording the stored length")
            output.write(final)
            print(f"  deflate: {raw} -> {stored} bytes ({100*stored/raw:.1f}%)", flush=True)
    except BaseException:
        path.unlink(missing_ok=True)
        raise


def checker_composite(rgba: Image.Image) -> Image.Image:
    y, x = np.indices((rgba.height, rgba.width))
    gray = np.where(((x//16+y//16) & 1) != 0, 92, 132).astype(np.uint8)
    background = Image.fromarray(np.repeat(gray[:,:,None], 3, axis=2)).convert("RGBA")
    return Image.alpha_composite(background, rgba).convert("RGB")


def previews(path: Path, rgba: np.ndarray) -> None:
    h, w = rgba.shape[:2]
    image = Image.fromarray(rgba)
    small = image.resize((min(2048,w), max(1,round(h*min(2048,w)/w))), Image.Resampling.LANCZOS)
    checker_composite(small).save(path.with_suffix(".preview.png"), optimize=True)
    small.getchannel("A").save(path.with_suffix(".alpha.png"), optimize=True)
    # Show a coastline at 1:1: imagery, checker-composited result, then alpha.
    best, location = -1., (0,0)
    for y in range(0,h-511,256):
        for x in range(0,w-511,256):
            a = rgba[y:y+512:8,x:x+512:8,3]
            fraction = np.mean(a > 127)
            if .15 < fraction < .85:
                score = np.count_nonzero(np.diff(a.astype(np.int16),axis=0)) + np.count_nonzero(np.diff(a.astype(np.int16),axis=1))
                if score > best:
                    best, location = score, (x,y)
    x, y = location
    coast = image.crop((x,y,min(w,x+512),min(h,y+512)))
    comparison = Image.new("RGB", (coast.width*3,coast.height))
    comparison.paste(coast.convert("RGB"),(0,0))
    comparison.paste(checker_composite(coast),(coast.width,0))
    comparison.paste(coast.getchannel("A").convert("RGB"),(coast.width*2,0))
    comparison.save(path.with_suffix(".coast.png"), optimize=True)
    print(f"  coastline crop NW output pixel ({x}, {y}); panels RGB / composite / alpha", flush=True)


def report_orientation(rgba: np.ndarray) -> None:
    sample = rgba[::8,::8]
    rgb = sample[:,:,:3].astype(np.float32)
    blue = ((rgb[:,:,2] > 1.5*rgb[:,:,0]) & (rgb[:,:,1] > 1.3*rgb[:,:,0]) &
            (rgb[:,:,2] > 1.05*rgb[:,:,1]) & (rgb[:,:,0] < 80))
    alpha = sample[:,:,3]
    scores = {name: round(100*float(np.mean(mask[blue] < 128)),3) if blue.any() else None
              for name,mask in (("north-up",alpha),("north-south flip",alpha[::-1]),
                                ("east-west mirror",alpha[:,::-1]),("both",alpha[::-1,::-1]))}
    print(f"  blue-imagery pixels classified as water (%; orientation diagnostic): {json.dumps(scores)}", flush=True)


def peak_memory_mib() -> float:
    info = psutil.Process().memory_info()
    return getattr(info,"peak_wset",info.rss)/1024**2


def run(args) -> None:
    # A native decoder fault cannot take a long-running production job with it.
    probe = subprocess.run([sys.executable,"-I",str(Path(__file__).resolve()),"--codec-probe"],
                           capture_output=True,text=True,timeout=60,
                           creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
    if probe.returncode:
        raise RuntimeError(f"Isolated codec probe failed ({probe.returncode}): {probe.stdout}{probe.stderr}")
    print(probe.stdout.strip(),flush=True)
    keys = list(MAPS) if args.map == "all" else [args.map]
    args.output_dir.mkdir(parents=True,exist_ok=True)
    for key in keys:
        start = time.perf_counter()
        spec = MAPS[key]
        w, h = round(spec[4]*args.scale), round(spec[5]*args.scale)
        if min(w,h) < 4 or w%4 or h%4:
            raise ValueError(f"Output {w}x{h} must have sides >=4 and divisible by 4")
        columns, rows, tw, th = tile_layout(w, h)
        if pyramid_bytes(w, h, mip_count(tw, th)) > 6*1024**3:
            raise ValueError("Full mip pyramid exceeds the 6 GiB image-array memory bound")
        path = args.output_dir / f"{spec[0]}.nosat"
        if path.exists():
            raise FileExistsError(f"Preserving existing output: {path}; select another --output-dir")
        free = __import__("shutil").disk_usage(args.output_dir).free
        # Payloads stream directly into deflate; no raw-size disk reservation or cache.
        if free < 32*1024**2:
            raise OSError(f"Insufficient free disk space for {path}")
        print(f"{spec[1]}: {w}x{h}, {spec[2]/w:g} m/px; {columns}x{rows} tiles of {tw}x{th}; CPU, <=4 workers",flush=True)
        rgba = np.empty((h,w,4),np.uint8)
        tiles = TileSource(args.map_root,spec,args.source_level,args.tile_origin)
        tiles.render(rgba)
        print(f"  404 water-fill tiles fetched: {tiles.missing}",flush=True)
        build_alpha(args.map_root,key,spec,rgba,args.supersample,args.water_level)
        report_orientation(rgba)
        land = float(np.sum(rgba[:,:,3],dtype=np.uint64))/(255*w*h)
        if args.preview:
            previews(path,rgba)
        source = (f"Satellite level {args.source_level}; geometry max-Y, sea {args.water_level:g} m; "
                  f"{args.supersample}x{args.supersample} coverage; ocean depth >=240 fallback; "
                  f"{datetime.now(timezone.utc).date().isoformat()}")
        write_texture(path,spec,rgba,source)
        del rgba
        print(json.dumps({"map":spec[0],"path":str(path.resolve()),"width":w,"height":h,
                          "tile_columns":columns,"tile_rows":rows,"tile_width":tw,"tile_height":th,
                          "raw_bytes":columns*rows*bc3_size(tw,th),
                          "stored_bytes":path.stat().st_size-len(make_header(spec,w,h,source)),
                          "bytes":path.stat().st_size,"wall_seconds":round(time.perf_counter()-start,3),
                          "process_peak_memory_MiB":round(peak_memory_mib(),1),
                          "land_percent":round(100*land,4),"water_percent":round(100*(1-land),4)}),flush=True)


def main() -> None:
    root = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map",choices=["heartland","ignus","all"],default="all")
    parser.add_argument("--scale",type=float,default=4)
    parser.add_argument("--source-level",type=int,default=6)
    parser.add_argument("--water-level",type=float,default=0.)
    parser.add_argument("--supersample",type=int,default=4)
    parser.add_argument("--tile-origin",default="http://127.0.0.1:47833")
    parser.add_argument("--map-root",type=Path,default=root/"nuclear-option-map")
    parser.add_argument("--output-dir",type=Path,default=root/"nuclear_option_mods/dist/satellite-maps")
    parser.add_argument("--preview",action="store_true")
    parser.add_argument("--codec-probe",action="store_true",help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.codec_probe:
        codec_probe()
        return
    if not math.isfinite(args.scale) or args.scale <= 0 or not math.isfinite(args.water_level):
        parser.error("scale must be positive and scale/water-level must be finite")
    if not 1 <= args.supersample <= 16:
        parser.error("supersample must be in 1..16 (CPU workload bound)")
    try:
        run(args)
    except (OSError,ValueError,RuntimeError,StopIteration) as exc:
        parser.exit(1,f"Satellite producer failed: {exc}\n")


if __name__ == "__main__":
    main()
