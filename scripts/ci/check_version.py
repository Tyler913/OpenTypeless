#!/usr/bin/env python3
"""Both apps carry the same version, and a release tag (1.2.3, v1.2.3 or V1.2.3) names that version.

    python3 scripts/ci/check_version.py     also checks the tag when GITHUB_REF is one (refs/tags/...)
"""
import os
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
MACOS = "macos/scripts/build-app.sh"
WINDOWS = "windows/Directory.Build.props"


def versions(root=ROOT):
    macos = re.search(r'^VERSION="([^"]*)"', (root / MACOS).read_text(encoding="utf-8"), re.MULTILINE)
    windows = re.search(r"<Version>([^<]*)</Version>", (root / WINDOWS).read_text(encoding="utf-8"))
    return macos and macos[1], windows and windows[1]


def problems(macos, windows, ref=""):
    errors = []
    if not macos:
        errors.append(f'no VERSION="x.y.z" in {MACOS}')
    if not windows:
        errors.append(f"no <Version> in {WINDOWS}")
    if macos and windows and macos != windows:
        errors.append(f"{MACOS} says {macos} but {WINDOWS} says {windows}: bump both")
    if ref.startswith("refs/tags/"):
        tag = ref.removeprefix("refs/tags/")
        for path, version in ((MACOS, macos), (WINDOWS, windows)):
            if version and version != re.sub(r"^[vV]", "", tag):
                errors.append(f"the tag is {tag} but {path} says {version}")
    return errors


if __name__ == "__main__":
    macos, windows = versions()
    errors = problems(macos, windows, os.environ.get("GITHUB_REF", ""))
    for error in errors:
        print(f"::error::{error}")
    if errors:
        raise SystemExit(1)
    print(f"Both apps are version {macos}")
