#!/usr/bin/env python3
"""Require every UI smoke-test image to exist, decode, and contain visible content.

This is a rendering smoke test, not a pixel baseline or an interaction test.
Keep the inventory aligned with SettingsPage, OnboardingStep and UISnapshots on both platforms.
"""
import argparse
from pathlib import Path

from PIL import Image

PAGES = ("home", "general", "shortcut", "providers", "models", "style", "history")
HUD_STATES = ("recording", "working", "copied", "learned", "error")


def expected_files(language):
    return ({f"settings-{language}-{page}.png" for page in PAGES}
            | {f"onboarding-{language}-{step}.png" for step in range(1, 5)}
            | {f"popover-{language}.png"}
            | {f"hud-{state}.png" for state in HUD_STATES})


def check(directory, language):
    errors = []
    expected = expected_files(language)
    actual = {p.name for p in directory.glob("*.png")}
    for name in sorted(expected - actual):
        errors.append(f"missing {name}")
    for name in sorted(actual - expected):
        errors.append(f"unexpected {name}; update the snapshot inventory if intentional")
    if (directory / "error.txt").exists():
        errors.append("the app reported error.txt (see the diagnostics artifact)")
    for name in sorted(expected & actual):
        try:
            path = directory / name
            with Image.open(path) as image:
                if image.format != "PNG":
                    raise ValueError("not a PNG")
                image.verify()
            with Image.open(path) as image:
                image.load()  # verify alone does not decode all pixel data
                if min(image.size) < 16:
                    raise ValueError(f"implausible dimensions: {image.size}")
                rgba = image.convert("RGBA")
                visible = Image.alpha_composite(Image.new("RGBA", rgba.size, "white"), rgba).convert("RGB")
                if all(low == high for low, high in visible.getextrema()):
                    raise ValueError("blank or fully transparent image")
        except (OSError, ValueError, SyntaxError) as error:
            errors.append(f"{name}: {error}")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--lang", required=True)
    args = parser.parse_args()
    errors = check(args.directory, args.lang)
    if errors:
        parser.exit(1, "UI smoke test failed:\n" + "\n".join(errors) + "\n")
    print(f"Validated all {len(expected_files(args.lang))} UI images in {args.directory}")


if __name__ == "__main__":
    main()
