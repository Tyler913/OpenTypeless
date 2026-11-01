# OpenTypeless logo

<p align="center">
  <img src="app-icon.svg" width="160" alt="OpenTypeless app icon">
</p>

**Voice → caret.** Three voice bars run into a text caret: what you say becomes text at your cursor. The bars carry
the colour of the voice, the caret is plain white, the way text is. It is drawn from four capsules of one width, so it
stays legible from a 16 px tray icon to a 1024 px Dock icon.

## Files

| File | Use |
|---|---|
| [`app-icon.svg`](app-icon.svg) | The app icon: the mark on its tile. Websites, social cards, store listings. |
| [`mark.svg`](mark.svg) | The mark alone, for light backgrounds (the caret in ink). |
| [`mark-mono.svg`](mark-mono.svg) | One colour (`currentColor`), the bars at half strength: like the menu-bar and tray icons. |

Every file here and every app icon (`macos/Resources/AppIcon.icns`, `windows/src/OpenTypeless/Assets/AppIcon.ico`
and their PNGs) is written by [`scripts/make_icon.py`](../../scripts/make_icon.py), which holds the geometry and
colours. Change the artwork there, run `python3 scripts/make_icon.py`, and update the in-app copies to match:
`Logo` in `macos/Sources/OpenTypeless/DesignSystem.swift` and in `windows/src/OpenTypeless/UI/DesignSystem.cs`
(the tiles, the macOS menu-bar icon and the Windows tray icon draw from those).

## Colours

| | Hex | |
|---|---|---|
| Tile, top | `#2A2566` | Gradient from (0.2, 0) to (0.8, 1) across the tile |
| Tile, bottom | `#0C0B20` | |
| Glow | `#5A5CFF` at 38 % | Radial, centred behind the bars, fading out at 0.48 of the tile |
| Voice, start | `#8A78FF` | Gradient across the bars, first bar's left edge to last bar's right edge |
| Voice, end | `#37C3FF` | |
| Caret | `#FFFFFF` | `#14122E` (ink) when the mark stands on a light background |

## Geometry

In tile units (0 to 1 across the tile). Every capsule is 0.088 wide and centred on y = 0.5.

| | Centre x | Height |
|---|---|---|
| Bar 1 | 0.270 | 0.24 |
| Bar 2 | 0.405 | 0.46 |
| Bar 3 | 0.540 | 0.32 |
| Caret | 0.705 | 0.58 |

The tile's corner radius is 0.225 of its width. On macOS the tile is 824 px on the 1024 px canvas (Apple's icon grid)
with a soft drop shadow; on Windows it fills the canvas but for a 4 % margin.

## Use

- Keep clear space around the mark of at least one bar pitch (0.135 of the tile).
- Keep the caret the tallest and brightest element; don't recolour it to match the bars.
- Don't add a microphone, a speech bubble or outlines, and don't stretch, rotate or re-space the bars.
- On busy or mid-tone backgrounds, use the app icon rather than the bare mark.
