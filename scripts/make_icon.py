#!/usr/bin/env python3
"""Draws the OpenTypeless logo and writes every icon file from it.

    python3 scripts/make_icon.py        (from anywhere; standard library only)

The mark is three voice bars running into a text caret: what you say becomes text at your cursor. The bars step from
amber through tangerine to vermilion as the voice warms up, and the caret, the tallest stroke, is crimson. The mark
works on its own, with no tile, on light and dark backgrounds alike; the macOS icon and the in-app tiles put it on a
white tile, like text on a page.

This file is the one source of the artwork: the in-app tiles (macos/.../DesignSystem.swift, windows/.../UI/
DesignSystem.cs), the macOS menu-bar icon and the Windows tray icon draw the same geometry with the same numbers.

Writes:
    macos/Resources/AppIcon.icns, AppIcon.png           the mark on its tile, on Apple's icon grid, with a drop shadow
    windows/src/OpenTypeless/Assets/AppIcon.ico, .png    the mark alone, no tile, every size Windows asks for
    docs/brand/app-icon.svg, mark.svg, mark-mono.svg     vector masters for the web, docs and print

Every bitmap is drawn at its own pixel size (not scaled down from a big one), with the strokes snapped to whole
pixels, so the 16 and 32 px icons stay sharp.
"""

import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# --- The artwork, in tile units (0…1 across the tile, y down) -----------------------------------------------------

STROKE = 0.104                                    # width of every bar and of the caret; the ends are round
BARS = ((0.245, 0.26), (0.395, 0.50), (0.545, 0.34))  # (centre x, height) of the voice bars, centred on y = 0.5
CARET = (0.725, 0.62)                             # the text caret: the tallest stroke, set a little apart
TILE_RADIUS = 0.225                               # corner radius of the tile

BAR_COLORS = ((0xFF, 0xB0, 0x00), (0xFF, 0x8A, 0x00), (0xFF, 0x5A, 0x1F))  # amber, tangerine, vermilion
CARET_COLOR = (0xEE, 0x1B, 0x45)                  # crimson
TILE_TOP = (0xFF, 0xFF, 0xFF)                     # tile gradient, top to bottom
TILE_BOTTOM = (0xEE, 0xEE, 0xF2)
EDGE_WIDTH, EDGE_ALPHA = 0.004, 0.07              # black hairline inside the tile's edge, so it holds on white

GLYPH_LEFT = BARS[0][0] - STROKE / 2
GLYPH_RIGHT = CARET[0] + STROKE / 2
MARK_CX = (GLYPH_LEFT + GLYPH_RIGHT) / 2          # the mark's centre, a little left of the tile's for optical balance
MARK_SPAN = GLYPH_RIGHT - GLYPH_LEFT
BARE_HEIGHT = 0.88                                # the caret's share of the canvas in the tile-less icon


def mix(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def clamp01(x):
    return 0.0 if x < 0 else 1.0 if x > 1 else x


def rounded_rect_distance(px, py, cx, cy, hx, hy, r):
    """Signed distance from (px, py) to a rounded rectangle with centre (cx, cy), half-size (hx, hy), radius r."""
    qx = abs(px - cx) - hx + r
    qy = abs(py - cy) - hy + r
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    return outside + min(max(qx, qy), 0.0) - r


# --- Raster -------------------------------------------------------------------------------------------------------

class Canvas:
    """Premultiplied RGBA floats (0…255 colour, 0…1 alpha), composited back to front with `over`."""

    def __init__(self, size):
        self.size = size
        self.px = [[0.0, 0.0, 0.0, 0.0] for _ in range(size * size)]

    def over(self, x, y, rgb, alpha):
        if alpha <= 0 or not (0 <= x < self.size and 0 <= y < self.size):
            return
        p = self.px[y * self.size + x]
        keep = 1.0 - alpha
        p[0] = rgb[0] * alpha + p[0] * keep
        p[1] = rgb[1] * alpha + p[1] * keep
        p[2] = rgb[2] * alpha + p[2] * keep
        p[3] = alpha + p[3] * keep

    def rgba(self):
        out = bytearray()
        for r, g, b, a in self.px:
            if a <= 0:
                out += b"\0\0\0\0"
            else:
                out += bytes((round(min(255, r / a)), round(min(255, g / a)), round(min(255, b / a)), round(a * 255)))
        return bytes(out)


def mark_layout(t, cx, cy):
    """The four strokes as (left, top, width, height, colour) in pixels, for a tile `t` pixels wide with the mark
    centred on (cx, cy). Strokes land on whole pixels, the bars keep one pitch, and no gap closes up when small."""
    w = max(1, round(STROKE * t))
    pitch = max(w + 1, round((BARS[1][0] - BARS[0][0]) * t))
    gap = max(w + 1, round((CARET[0] - BARS[-1][0]) * t))
    offsets = [i * pitch for i in range(len(BARS))] + [(len(BARS) - 1) * pitch + gap]
    left = round(cx - (offsets[-1] + w) / 2)
    heights = [h for _, h in BARS] + [CARET[1]]
    colors = list(BAR_COLORS) + [CARET_COLOR]
    strokes = []
    for o, h, color in zip(offsets, heights, colors):
        hp = max(w, round(h * t))
        strokes.append((left + o, round(cy - hp / 2), w, hp, color))
    return strokes


def draw_strokes(canvas, strokes):
    for left, top, w, h, color in strokes:
        for y in range(top - 1, top + h + 1):
            for x in range(left - 1, left + w + 1):
                d = rounded_rect_distance(x + 0.5, y + 0.5, left + w / 2, top + h / 2, w / 2, h / 2, w / 2)
                canvas.over(x, y, color, clamp01(0.5 - d))


def draw_icon(size, inset, shadow=None):
    """The mark on its tile, `inset` (a fraction of `size`) from each edge. `shadow` = (alpha, dy, sigma) in
    fractions of `size`, for the macOS drop shadow."""
    c = Canvas(size)
    edge = round(size * inset)
    t = size - 2 * edge                       # tile size in pixels, snapped to the pixel grid
    half = t / 2
    cx = cy = edge + half
    radius = TILE_RADIUS * t
    hairline = max(0.6, EDGE_WIDTH * t)

    if shadow:
        s_alpha, s_dy, s_sigma = shadow[0], shadow[1] * size, max(0.5, shadow[2] * size)
        for y in range(size):
            for x in range(size):
                if rounded_rect_distance(x + 0.5, y + 0.5, cx, cy, half, half, radius) < -1:
                    continue  # under the opaque tile
                d = rounded_rect_distance(x + 0.5, y + 0.5 - s_dy, cx, cy, half, half, radius)
                a = s_alpha * 0.5 * math.erfc(d / (s_sigma * math.sqrt(2)))
                if a > 0.001:
                    c.over(x, y, (0, 0, 0), a)

    for y in range(edge, edge + t):
        rgb = mix(TILE_TOP, TILE_BOTTOM, (y + 0.5 - edge) / t)
        for x in range(edge, edge + t):
            d = rounded_rect_distance(x + 0.5, y + 0.5, cx, cy, half, half, radius)
            cover = clamp01(0.5 - d)
            if cover <= 0:
                continue
            c.over(x, y, rgb, cover)
            band = cover - clamp01(0.5 - (d + hairline))   # the part of the pixel within `hairline` of the edge
            if band > 0:
                c.over(x, y, (0, 0, 0), band * EDGE_ALPHA)

    draw_strokes(c, mark_layout(t, edge + MARK_CX * t, cy))
    return c


def draw_bare(size):
    """The mark alone on a transparent canvas, centred, its caret `BARE_HEIGHT` of the canvas tall."""
    c = Canvas(size)
    t = BARE_HEIGHT * size / CARET[1]
    draw_strokes(c, mark_layout(t, size / 2, size / 2))
    return c


# --- File formats -------------------------------------------------------------------------------------------------

def png(canvas):
    size = canvas.size
    data = canvas.rgba()
    stride = size * 4
    raw = bytearray()
    for y in range(size):
        row = data[y * stride:(y + 1) * stride]
        raw.append(1)  # Sub filter: each byte minus the one 4 bytes to its left
        raw += row[:4]
        raw += bytes((row[i] - row[i - 4]) & 0xFF for i in range(4, stride))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def icns_argb(canvas):
    """The 16 and 32 px ARGB entries: one plane per channel, each in icns run-length form (literal runs only)."""
    data = canvas.rgba()
    out = bytearray(b"ARGB")
    for channel in (3, 0, 1, 2):
        plane = data[channel::4]
        for i in range(0, len(plane), 128):
            run = plane[i:i + 128]
            out.append(len(run) - 1)
            out += run
    return bytes(out)


def icns(entries):
    body = b"".join(kind + struct.pack(">I", len(data) + 8) + data for kind, data in entries)
    return b"icns" + struct.pack(">I", len(body) + 8) + body


def ico(images):
    """An ICO container of PNG entries (read by Windows since Vista)."""
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    directory, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    return header + directory + blobs


# --- SVG ----------------------------------------------------------------------------------------------------------

def hexcolor(rgb):
    return "#%02X%02X%02X" % tuple(round(v) for v in rgb)


def svg_strokes(s, ox=0.0, oy=0.0, fills=None, bar_extra=""):
    """The four strokes in an `s`-unit tile whose top-left corner is at (ox, oy)."""
    fills = fills or [hexcolor(c) for c in BAR_COLORS] + [hexcolor(CARET_COLOR)]
    out = []
    for i, ((x, h), fill) in enumerate(zip(list(BARS) + [CARET], fills)):
        extra = bar_extra if i < len(BARS) else ""
        out.append(f'<rect x="{ox + (x - STROKE / 2) * s:g}" y="{oy + (0.5 - h / 2) * s:g}" width="{STROKE * s:g}" '
                   f'height="{h * s:g}" rx="{STROKE * s / 2:g}" fill="{fill}"{extra}/>')
    return "\n  ".join(out)


def svg_app_icon():
    s = 1024
    e = EDGE_WIDTH * s
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{s}" height="{s}" viewBox="0 0 {s} {s}">
  <title>OpenTypeless</title>
  <defs>
    <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{hexcolor(TILE_TOP)}"/><stop offset="1" stop-color="{hexcolor(TILE_BOTTOM)}"/>
    </linearGradient>
  </defs>
  <rect width="{s}" height="{s}" rx="{TILE_RADIUS * s:g}" fill="url(#tile)"/>
  <rect x="{e / 2:g}" y="{e / 2:g}" width="{s - e:g}" height="{s - e:g}" rx="{TILE_RADIUS * s - e / 2:g}" fill="none" stroke="#000000" stroke-opacity="{EDGE_ALPHA:g}" stroke-width="{e:g}"/>
  {svg_strokes(s)}
</svg>
'''


def svg_mark(mono):
    """The mark alone, cropped to its strokes with a margin of half a stroke. `mono`: one colour (currentColor) with
    the bars at half strength, as in the menu-bar and tray icons."""
    s = 1000 / CARET[1]                       # caret 1000 units tall
    pad = STROKE * s / 2
    w, h = MARK_SPAN * s + 2 * pad, CARET[1] * s + 2 * pad
    ox, oy = pad - GLYPH_LEFT * s, pad - (0.5 - CARET[1] / 2) * s
    if mono:
        body = svg_strokes(s, ox, oy, ["currentColor"] * 4, ' fill-opacity="0.5"')
    else:
        body = svg_strokes(s, ox, oy)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{w:.4g}" height="{h:.4g}" viewBox="0 0 {w:.6g} {h:.6g}">
  <title>OpenTypeless</title>
  {body}
</svg>
'''


# --- Output -------------------------------------------------------------------------------------------------------

def write(path, data):
    path = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if isinstance(data, bytes):
        with open(path, "wb") as f:
            f.write(data)
    else:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(data)
    print("✓", os.path.relpath(path, ROOT))


def main():
    # macOS: the tile on Apple's icon grid (an 824 px tile on a 1024 px canvas) with its drop shadow.
    mac = {}

    def mac_icon(size):
        if size not in mac:
            mac[size] = draw_icon(size, 100 / 1024, shadow=(0.22, 10 / 1024, 9 / 1024))
        return mac[size]

    entries = [
        (b"ic04", icns_argb(mac_icon(16))),
        (b"ic05", icns_argb(mac_icon(32))),
        (b"ic11", png(mac_icon(32))),     # 16 pt @2x
        (b"ic12", png(mac_icon(64))),     # 32 pt @2x
        (b"ic07", png(mac_icon(128))),
        (b"ic13", png(mac_icon(256))),    # 128 pt @2x
        (b"ic08", png(mac_icon(256))),
        (b"ic14", png(mac_icon(512))),    # 256 pt @2x
        (b"ic09", png(mac_icon(512))),
        (b"ic10", png(mac_icon(1024))),   # 512 pt @2x
    ]
    write("macos/Resources/AppIcon.icns", icns(entries))
    write("macos/Resources/AppIcon.png", png(mac_icon(512)))

    # Windows: the mark alone, with no tile (Windows icons carry their own silhouette rather than sit on a plate).
    sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    write("windows/src/OpenTypeless/Assets/AppIcon.ico", ico([(s, png(draw_bare(s))) for s in sizes]))
    write("windows/src/OpenTypeless/Assets/AppIcon.png", png(draw_bare(512)))

    write("docs/brand/app-icon.svg", svg_app_icon())
    write("docs/brand/mark.svg", svg_mark(mono=False))
    write("docs/brand/mark-mono.svg", svg_mark(mono=True))


if __name__ == "__main__":
    main()
