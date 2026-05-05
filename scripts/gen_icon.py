#!/usr/bin/env python3
"""Generate the PeerlessPatcher app icon (256x256 PNG)."""
import struct, zlib, pathlib, sys

def chunk(tag: bytes, data: bytes) -> bytes:
    crc = zlib.crc32(tag + data) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", crc)

def make_png(width: int, height: int, get_pixel) -> bytes:
    sig = bytes([137, 80, 78, 71, 13, 10, 26, 10])
    ihdr = chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    raw = b""
    for y in range(height):
        raw += b"\x00"
        for x in range(width):
            raw += get_pixel(x, y)
    idat = chunk(b"IDAT", zlib.compress(raw, 9))
    iend = chunk(b"IEND", b"")
    return sig + ihdr + idat + iend

def render(x: int, y: int, w: int = 256, h: int = 256) -> bytes:
    cx, cy = w // 2, h // 2
    r = min(w, h) // 2 - 4

    BG  = bytes([26, 26, 46])    # #1a1a2e dark navy
    FG  = bytes([79, 195, 247])  # #4fc3f7 teal
    OUT = bytes([0, 0, 0])       # outside circle

    # Circular clip
    if (x - cx) ** 2 + (y - cy) ** 2 > r ** 2:
        return OUT

    # "P" geometry (all relative to circle centre)
    dx, dy = x - cx, y - cy

    stem_left  = -r * 35 // 100
    stem_right = -r * 10 // 100
    stem_top   = -r * 62 // 100
    stem_bot   =  r * 62 // 100

    in_stem = stem_left <= dx <= stem_right and stem_top <= dy <= stem_bot

    # Bowl: semicircle on the right side of the stem, top half only
    bowl_cx = stem_right
    bowl_cy = (stem_top + 0) // 2       # midpoint of top half
    bowl_r  = r * 38 // 100
    in_bowl = (
        (dx - bowl_cx) ** 2 + (dy - bowl_cy) ** 2 <= bowl_r ** 2
        and dy <= 0
    )

    if in_stem or in_bowl:
        return FG
    return BG

size = 256
png_bytes = make_png(size, size, render)

out = pathlib.Path(__file__).parent.parent / "profiles" / "assets" / "icon.png"
out.write_bytes(png_bytes)
print(f"Wrote {len(png_bytes)} bytes to {out}")
