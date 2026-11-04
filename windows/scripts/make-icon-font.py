#!/usr/bin/env python3
"""Builds the app's icon font from Fluent UI System Icons (https://github.com/microsoft/fluentui-system-icons, MIT).

Writes src/OpenTypeless/Assets/Fonts/FluentIcons.ttf, holding only the icons listed in ICONS below, and
src/OpenTypeless/UI/Glyphs.cs, their code points. To add an icon, add it to ICONS (its name as on
https://react.fluentui.dev/?path=/docs/icons-catalog--docs, without the size and style) and run, from windows/:

    python3 scripts/make-icon-font.py

It needs fontTools (pip install fonttools) and downloads the pinned release of the upstream fonts.

Fluent icons are drawn separately for each size, so each icon is kept as its 16, 20 and 24 px drawings (the nearest
one where a size wasn't drawn), each regular and filled. The code points are laid out so that C# finds them by adding
an offset (see Icons.Glyph in UI/DesignSystem.cs): an icon's constant is its 20 px regular drawing, U+E000 onwards.
"""
import json
import sys
import tempfile
import urllib.request
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.misc.timeTools import timestampFromString
from fontTools.ttLib import TTFont

VERSION = "1.1.344"
RELEASED = "Fri Oct  9 00:00:00 2026"
SOURCE = f"https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{VERSION}/fonts"
FAMILY = "OpenTypeless Fluent Icons"

# (constant in Glyphs.cs, Fluent icon). Constants are named for what the icon shows, as in the app's code.
ICONS = [
    # Sidebar
    ("Home", "home"),
    ("Settings", "settings"),
    ("Keyboard", "keyboard"),
    ("Key", "key"),
    ("Component", "developer_board"),
    ("Font", "text_font"),
    ("AllApps", "apps"),
    ("History", "history"),
    # Dictation
    ("Microphone", "mic"),
    ("Speech", "sound_wave_circle"),
    ("Sparkle", "sparkle"),
    ("TouchPointer", "tap_single"),
    ("Paste", "clipboard_paste"),
    ("Dictionary", "book_letter"),
    ("Message", "chat"),
    ("Power", "power"),
    # Files
    ("Volume", "speaker_2"),
    ("Video", "video"),
    ("Page", "document"),
    ("OpenFile", "document_add"),
    ("Folder", "folder_open"),
    ("Drive", "hard_drive"),
    ("Export", "arrow_export_ltr"),
    ("Import", "arrow_import"),
    ("Download", "arrow_download"),
    # Status
    ("Warning", "warning"),
    ("Info", "info"),
    ("Help", "question_circle"),
    ("CheckMark", "checkmark"),
    ("Completed", "checkmark_circle"),
    ("ErrorBadge", "dismiss_circle"),
    # Actions
    ("Add", "add"),
    ("Edit", "edit"),
    ("Delete", "delete"),
    ("Cancel", "dismiss"),
    ("Copy", "copy"),
    ("Refresh", "arrow_clockwise"),
    ("Search", "search"),
    ("List", "list"),
    ("Code", "code"),
    ("OpenInNew", "open"),
    ("ChevronRight", "chevron_right"),
    ("ArrowRight", "arrow_right"),
    ("ArrowUp", "arrow_up"),
    # Settings rows, as in Windows 11 Settings
    ("TextStyle", "text_edit_style"),
    ("Enter", "arrow_enter_left"),
    ("Paragraph", "text_paragraph"),
    ("Mute", "speaker_mute"),
    ("Eye", "eye"),
    ("Timer", "timer"),
    ("MicPulse", "mic_pulse"),
    ("Flash", "flash"),
    ("Language", "local_language"),
    ("Speed", "top_speed"),
    ("Sync", "arrow_sync"),
    ("News", "news"),
    ("Wand", "wand"),
    ("Shift", "keyboard_shift"),
    ("Send", "send"),
    ("Server", "server"),
    ("Brain", "brain_circuit"),
    ("Money", "money"),
    ("Translate", "translate"),
    ("Backup", "arrow_repeat_all"),
    ("ScanText", "scan_text"),
    ("Lightbulb", "lightbulb"),
    ("TextExpand", "text_expand"),
    ("Globe", "globe"),
    ("Rocket", "rocket"),
    ("Shield", "shield_checkmark"),
    ("Cloud", "cloud"),
    ("DataUsage", "data_usage"),
    ("Placement", "panel_bottom"),
]

FIRST = 0xE000
SIZES = {20: 0x000, 16: 0x100, 24: 0x200}  # offset of each size's drawing
FILLED = 0x400  # added for the filled drawing
EM = 500  # upstream units per em: every drawing fills the em, so a 16 px drawing is pixel-aligned at FontSize 16
# Upstream drawings whose outline is broken in the font (they render differently from the SVG): the nearest size is used.
BROKEN = {"ic_fluent_checkmark_circle_24_regular"}  # the tick is a wedge

ROOT = Path(__file__).resolve().parent.parent
FONT_OUT = ROOT / "src/OpenTypeless/Assets/Fonts/FluentIcons.ttf"
LICENSE_OUT = ROOT / "src/OpenTypeless/Assets/Fonts/FluentIcons-LICENSE.txt"
CS_OUT = ROOT / "src/OpenTypeless/UI/Glyphs.cs"


def download(directory):
    for name in ("FluentSystemIcons-Regular.ttf", "FluentSystemIcons-Filled.ttf",
                 "FluentSystemIcons-Regular.json", "FluentSystemIcons-Filled.json"):
        urllib.request.urlretrieve(f"{SOURCE}/{name}", directory / name)
    license_url = f"https://raw.githubusercontent.com/microsoft/fluentui-system-icons/{VERSION}/LICENSE"
    urllib.request.urlretrieve(license_url, directory / "LICENSE")


def drawing(font, codes, icon, size, style):
    """The upstream glyph for the icon at the size, or at the nearest size drawn (preferring larger)."""
    for candidate in sorted({size, 20, 16, 24, 28, 12}, key=lambda s: (abs(s - size), -s)):
        name = f"ic_fluent_{icon}_{candidate}_{style}"
        code = codes.get(name)
        if code is not None and name not in BROKEN:
            return font.getBestCmap()[code]
    sys.exit(f"Fluent UI System Icons {VERSION} has no {style} '{icon}'")


def build(directory):
    sources = {}
    for style in ("regular", "filled"):
        name = f"FluentSystemIcons-{style.capitalize()}"
        font = TTFont(directory / f"{name}.ttf")
        if font["head"].unitsPerEm != EM:
            sys.exit(f"{name}.ttf has {font['head'].unitsPerEm} units per em, not {EM}")
        sources[style] = (font, json.loads((directory / f"{name}.json").read_text()))

    glyph_order = [".notdef"]
    glyphs = {".notdef": TTGlyphPen(None).glyph()}
    cmap = {}
    y_min, y_max = 0, EM
    for index, (_, icon) in enumerate(ICONS):
        for style, style_offset in (("regular", 0), ("filled", FILLED)):
            font, codes = sources[style]
            glyph_set = font.getGlyphSet()
            for size, size_offset in SIZES.items():
                source_name = drawing(font, codes, icon, size, style)
                if source_name not in glyphs:
                    pen = TTGlyphPen(glyph_set)
                    glyph_set[source_name].draw(pen)
                    glyphs[source_name] = pen.glyph()
                    glyph_order.append(source_name)
                    bounds = BoundsPen(glyph_set)
                    glyph_set[source_name].draw(bounds)
                    if bounds.bounds:
                        y_min, y_max = min(y_min, bounds.bounds[1]), max(y_max, bounds.bounds[3])
                cmap[FIRST + index + size_offset + style_offset] = source_name

    builder = FontBuilder(EM, isTTF=True)
    builder.setupGlyphOrder(glyph_order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(glyphs)
    glyf = builder.font["glyf"]
    metrics = {}
    for name in glyph_order:
        glyph = glyf[name]
        glyph.recalcBounds(glyf)
        metrics[name] = (EM, getattr(glyph, "xMin", 0))
    builder.setupHorizontalMetrics(metrics)
    # The line box is the em, so a FontIcon centres the drawing as upstream does.
    builder.setupHorizontalHeader(ascent=EM, descent=0, lineGap=0)
    builder.setupNameTable({
        "copyright": "Copyright (c) 2020 Microsoft Corporation",
        "familyName": FAMILY,
        "styleName": "Regular",
        "uniqueFontIdentifier": f"{FAMILY} {VERSION}",
        "fullName": FAMILY,
        "version": f"Version {VERSION}",
        "psName": FAMILY.replace(" ", ""),
        "description": f"The icons OpenTypeless uses, from Fluent UI System Icons {VERSION}",
        "licenseDescription": "MIT License",
        "licenseInfoURL": "https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE",
    })
    builder.setupOS2(
        sTypoAscender=EM, sTypoDescender=0, sTypoLineGap=0,
        usWinAscent=int(round(y_max)), usWinDescent=int(round(-y_min)),
        fsSelection=0x40 | 0x80,  # REGULAR | USE_TYPO_METRICS
        achVendID="OTLS",
        version=4,
    )
    builder.setupPost(keepGlyphNames=False)
    # Dated as the upstream release, so running this again writes the same file.
    builder.font["head"].created = builder.font["head"].modified = timestampFromString(RELEASED)
    builder.font.recalcTimestamp = False
    FONT_OUT.parent.mkdir(parents=True, exist_ok=True)
    builder.save(FONT_OUT)
    LICENSE_OUT.write_text(
        f"{FONT_OUT.name} holds icons from Fluent UI System Icons {VERSION}\n"
        "(https://github.com/microsoft/fluentui-system-icons), under this license:\n\n"
        + (directory / "LICENSE").read_text())


def write_constants():
    width = max(len(name) for name, _ in ICONS)
    lines = [
        "// Generated by windows/scripts/make-icon-font.py, with Assets/Fonts/FluentIcons.ttf: add icons there, not here.",
        "",
        "namespace OpenTypeless.UI;",
        "",
        "/// <summary>",
        f"/// The app's icons, from Fluent UI System Icons {VERSION}: code points in Assets/Fonts/FluentIcons.ttf",
        "/// (<see cref=\"Icons.Font\"/>). Each is the icon's 20 px regular drawing; <see cref=\"Icons.Glyph\"/> picks the",
        "/// drawing for another size, or the filled one.",
        "/// </summary>",
        "public static class Glyphs",
        "{",
    ]
    for index, (name, icon) in enumerate(ICONS):
        lines.append(f"    public const string {name.ljust(width)} = \"\\u{FIRST + index:04X}\"; // {icon}")
    lines += [
        "",
        f"    internal const char First = '\\u{FIRST:04X}';",
        f"    internal const char Last = '\\u{FIRST + len(ICONS) - 1:04X}';",
        f"    internal const int Size16 = 0x{SIZES[16]:X};",
        f"    internal const int Size24 = 0x{SIZES[24]:X};",
        f"    internal const int Filled = 0x{FILLED:X};",
        "}",
        "",
    ]
    CS_OUT.write_text("\n".join(lines), encoding="utf-8")


def main():
    if len(ICONS) > SIZES[16]:
        sys.exit("Too many icons for the code point layout")
    with tempfile.TemporaryDirectory() as temp:
        directory = Path(temp)
        download(directory)
        build(directory)
    write_constants()
    print(f"{FONT_OUT.relative_to(ROOT)}: {len(ICONS)} icons, {FONT_OUT.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
