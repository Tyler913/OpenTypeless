#!/usr/bin/env python3
"""Draws the OpenTypeless logo and writes every icon file from it.

    python3 scripts/make_icon.py        (from anywhere; standard library only)

The mark is three voice bars running into a text caret: what you say becomes text at your cursor. The bars carry
the brand's violet-to-azure gradient, the caret is plain white, and both sit on a deep indigo tile. This file is the
one source of the artwork: the in-app tiles (macos/.../DesignSystem.swift, windows/.../UI/DesignSystem.cs), the macOS
menu-bar icon and the Windows tray icon draw the same geometry with the same numbers.

Writes:
    macos/Resources/AppIcon.icns, AppIcon.png        Apple icon grid: 824/1024 tile with a soft drop shadow
    windows/src/OpenTypeless/Assets/AppIcon.ico, .png   near full-bleed tile, every size Windows asks for
    docs/brand/app-icon.svg, mark.svg, mark-mono.svg   vector masters for the web, docs and print

Every bitmap is drawn at its own pixel size (not scaled down from a big one), with the bars snapped to whole pixels,
so the 16 and 32 px icons stay sharp.
"""

import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# --- The artwork, in tile units (0…1 across the tile, y down) -----------------------------------------------------

STROKE = 0.088                                    # width of every bar and of the caret
BARS = ((0.270, 0.24), (0.405, 0.46), (0.540, 0.32))  # (centre x, height) of the voice bars, centred on y = 0.5
CARET = (0.705, 0.58)                             # the text caret: taller, set apart, white
TILE_RADIUS = 0.225                               # corner radius of the tile

TILE_TOP = (0x2A, 0x25, 0x66)                     # tile gradient, from (0.2, 0) to (0.8, 1)
TILE_BOTTOM = (0x0C, 0x0B, 0x20)
GLOW = (0x5A, 0x5C, 0xFF)                         # soft light behind the bars
GLOW_CENTER, GLOW_RADIUS, GLOW_ALPHA = (0.40, 0.52), 0.48, 0.38
RIM_WIDTH = 0.006                                 # hairline highlight inside the tile's edge
RIM_ALPHA = ((0.0, 0.22), (0.5, 0.04), (1.0, 0.08))  # white, by height
BAR_START = (0x8A, 0x78, 0xFF)                    # bar gradient across the bars, left edge to right edge
BAR_END = (0x37, 0xC3, 0xFF)
CARET_COLOR = (0xFF, 0xFF, 0xFF)
INK = (0x14, 0x12, 0x2E)                          # the caret's colour when the mark stands on a light background

BARS_LEFT = BARS[0][0] - STROKE / 2
BARS_RIGHT = BARS[-1][0] + STROKE / 2
GLYPH_LEFT, GLYPH_RIGHT = BARS_LEFT, CARET[0] + STROKE / 2
GLYPH_TOP, GLYPH_BOTTOM = 0.5 - CARET[1] / 2, 0.5 + CARET[1] / 2


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
        if alpha <= 0:
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


def draw_icon(size, inset, shadow=None):
    """The tile with the mark, `inset` (a fraction of `size`) from each edge. `shadow` = (alpha, dy, sigma) in
    fractions of `size`, for the macOS drop shadow."""
    c = Canvas(size)
    edge = round(size * inset)
    t = size - 2 * edge                       # tile size in pixels, snapped to the pixel grid
    tx = ty = edge
    half = t / 2
    cx = cy = tx + half
    radius = TILE_RADIUS * t
    rim = RIM_WIDTH * t

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

    gx0, gy0 = tx + 0.2 * t, ty
    gdx, gdy = 0.6 * t, t
    glen2 = gdx * gdx + gdy * gdy
    glow_x, glow_y, glow_r = tx + GLOW_CENTER[0] * t, ty + GLOW_CENTER[1] * t, GLOW_RADIUS * t
    for y in range(ty, ty + t):
        for x in range(tx, tx + t):
            px, py = x + 0.5, y + 0.5
            d = rounded_rect_distance(px, py, cx, cy, half, half, radius)
            cover = clamp01(0.5 - d)
            if cover <= 0:
                continue
            k = clamp01(((px - gx0) * gdx + (py - gy0) * gdy) / glen2)
            rgb = mix(TILE_TOP, TILE_BOTTOM, k)
            g = clamp01(1 - math.hypot(px - glow_x, py - glow_y) / glow_r) * GLOW_ALPHA
            rgb = mix(rgb, GLOW, g)
            c.over(x, y, rgb, cover)
            band = cover - clamp01(0.5 - (d + rim))     # the part of the pixel within `rim` of the edge
            if band > 0:
                v = (py - ty) / t
                lo, hi = (RIM_ALPHA[0], RIM_ALPHA[1]) if v < RIM_ALPHA[1][0] else (RIM_ALPHA[1], RIM_ALPHA[2])
                a = lo[1] + (hi[1] - lo[1]) * clamp01((v - lo[0]) / (hi[0] - lo[0]))
                c.over(x, y, (255, 255, 255), band * a)

    w, centers, heights = glyph_layout(t)
    for i, (gx, h) in enumerate(zip(centers, heights)):
        caret = i == len(centers) - 1
        bx = tx + gx
        top = ty + round(half - h / 2)
        left_edge = tx + BARS_LEFT * t
        span = (BARS_RIGHT - BARS_LEFT) * t
        for y in range(top - 1, top + h + 1):
            for x in range(math.floor(bx - w / 2) - 1, math.ceil(bx + w / 2) + 1):
                d = rounded_rect_distance(x + 0.5, y + 0.5, bx, top + h / 2, w / 2, h / 2, w / 2)
                cover = clamp01(0.5 - d)
                if cover <= 0:
                    continue
                rgb = CARET_COLOR if caret else mix(BAR_START, BAR_END, clamp01((x + 0.5 - left_edge) / span))
                c.over(x, y, rgb, cover)
    return c


def glyph_layout(t):
    """Bar width, centres (from the tile's left edge) and heights in pixels for a tile `t` pixels wide: the bars
    land on whole pixels, the pitch is the same between every bar, and no gap closes up at small sizes."""
    w = max(1, round(STROKE * t))
    pitch = max(w + 1, round((BARS[1][0] - BARS[0][0]) * t))
    gap = max(w + 1, round((CARET[0] - BARS[-1][0]) * t))
    offsets = [i * pitch for i in range(len(BARS))] + [(len(BARS) - 1) * pitch + gap]
    ideal_left = (BARS[0][0] - STROKE / 2) * t
    span = offsets[-1] + w
    ideal_center = ideal_left + ((GLYPH_RIGHT - GLYPH_LEFT) * t) / 2
    left = round(ideal_center - span / 2)
    centers = [left + o + w / 2 for o in offsets]
    heights = [max(w, round(h * t)) for _, h in BARS] + [max(w, round(CARET[1] * t))]
    return w, centers, heights


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


def capsule(x, h, s, fill, extra=""):
    return (f'<rect x="{(x - STROKE / 2) * s:g}" y="{(0.5 - h / 2) * s:g}" width="{STROKE * s:g}" height="{h * s:g}" '
            f'rx="{STROKE * s / 2:g}" fill="{fill}"{extra}/>')


def svg_glyph(s, caret_fill, bars_fill="url(#bars)", bars_extra=""):
    return "".join(capsule(x, h, s, bars_fill, bars_extra) for x, h in BARS) + capsule(CARET[0], CARET[1], s, caret_fill)


def svg_bar_gradient(s):
    return (f'<linearGradient id="bars" x1="{BARS_LEFT * s:g}" y1="0" x2="{BARS_RIGHT * s:g}" y2="0" '
            f'gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="{hexcolor(BAR_START)}"/>'
            f'<stop offset="1" stop-color="{hexcolor(BAR_END)}"/></linearGradient>')


def svg_app_icon():
    s = 1024
    rim = "".join(f'<stop offset="{o:g}" stop-color="#FFFFFF" stop-opacity="{a:g}"/>' for o, a in RIM_ALPHA)
    r = RIM_WIDTH * s
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{s}" height="{s}" viewBox="0 0 {s} {s}">
  <title>OpenTypeless</title>
  <defs>
    <linearGradient id="tile" x1="0.2" y1="0" x2="0.8" y2="1">
      <stop offset="0" stop-color="{hexcolor(TILE_TOP)}"/><stop offset="1" stop-color="{hexcolor(TILE_BOTTOM)}"/>
    </linearGradient>
    <radialGradient id="glow" cx="{GLOW_CENTER[0]:g}" cy="{GLOW_CENTER[1]:g}" r="{GLOW_RADIUS:g}">
      <stop offset="0" stop-color="{hexcolor(GLOW)}" stop-opacity="{GLOW_ALPHA:g}"/>
      <stop offset="1" stop-color="{hexcolor(GLOW)}" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">{rim}</linearGradient>
    {svg_bar_gradient(s)}
  </defs>
  <rect width="{s}" height="{s}" rx="{TILE_RADIUS * s:g}" fill="url(#tile)"/>
  <rect width="{s}" height="{s}" rx="{TILE_RADIUS * s:g}" fill="url(#glow)"/>
  <rect x="{r / 2:g}" y="{r / 2:g}" width="{s - r:g}" height="{s - r:g}" rx="{TILE_RADIUS * s - r / 2:g}" fill="none" stroke="url(#rim)" stroke-width="{r:g}"/>
  {svg_glyph(s, hexcolor(CARET_COLOR))}
</svg>
'''


def svg_mark(mono):
    s = 1024
    pad = 0.02 * s
    x, y = GLYPH_LEFT * s - pad, GLYPH_TOP * s - pad
    w, h = (GLYPH_RIGHT - GLYPH_LEFT) * s + 2 * pad, (GLYPH_BOTTOM - GLYPH_TOP) * s + 2 * pad
    if mono:
        # One colour (currentColor): the bars at half strength, the caret solid, as in the menu-bar and tray icons.
        body = svg_glyph(s, "currentColor", "currentColor", ' fill-opacity="0.5"')
        defs = ""
    else:
        body = svg_glyph(s, hexcolor(INK))
        defs = f"\n  <defs>{svg_bar_gradient(s)}</defs>"
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{w:g}" height="{h:g}" viewBox="{x:g} {y:g} {w:g} {h:g}">
  <title>OpenTypeless</title>{defs}
  {body}
</svg>
'''


# --- Output -------------------------------------------------------------------------------------------------------

def write(path, data):
    path = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb" if isinstance(data, bytes) else "w", **({} if isinstance(data, bytes) else {"newline": "\n"})) as f:
        f.write(data)
    print("✓", os.path.relpath(path, ROOT))


def main():
    # macOS: the Apple icon grid (an 824 px tile on a 1024 px canvas) with its drop shadow.
    mac = {}

    def mac_icon(size):
        if size not in mac:
            mac[size] = draw_icon(size, 100 / 1024, shadow=(0.30, 10 / 1024, 8 / 1024))
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

    # Windows: a near full-bleed tile at every size the shell asks for.
    sizes = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    write("windows/src/OpenTypeless/Assets/AppIcon.ico", ico([(s, png(draw_icon(s, 0.04))) for s in sizes]))
    write("windows/src/OpenTypeless/Assets/AppIcon.png", png(draw_icon(512, 0.04)))

    write("docs/brand/app-icon.svg", svg_app_icon())
    write("docs/brand/mark.svg", svg_mark(mono=False))
    write("docs/brand/mark-mono.svg", svg_mark(mono=True))


if __name__ == "__main__":
    main()
