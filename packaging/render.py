#!/usr/bin/env python3
"""Fills in the Homebrew cask and the WinGet manifests for a published release.

    python3 packaging/render.py 1.3.7 dist        writes dist/homebrew/opentypeless.rb and dist/winget/*.yaml

The checksums come from the release's assets on GitHub (the SHA-256 digest GitHub publishes for each upload, or the
zip itself when there's none). Set GH_TOKEN (or GITHUB_TOKEN) to avoid the API's anonymous rate limit.
"""

import hashlib
import json
import os
import re
import shutil
import sys
import urllib.request

REPO = "Tyler913/OpenTypeless"
HERE = os.path.dirname(os.path.abspath(__file__))
PLATFORMS = {"MACOS_ARM64": "macOS-arm64", "WINDOWS_X64": "windows-x64", "WINDOWS_ARM64": "windows-arm64"}


def request(url):
    headers = {"User-Agent": "OpenTypeless-packaging"}
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if token and url.startswith("https://api.github.com/"):
        headers["Authorization"] = "Bearer " + token
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers))


def sha256(asset):
    digest = asset.get("digest") or ""
    if digest.startswith("sha256:"):
        return digest.removeprefix("sha256:")
    hasher = hashlib.sha256()
    with request(asset["browser_download_url"]) as response:
        while chunk := response.read(1 << 20):
            hasher.update(chunk)
    return hasher.hexdigest()


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    tag, out = sys.argv[1], sys.argv[2]
    version = tag.lstrip("vV")
    with request(f"https://api.github.com/repos/{REPO}/releases/tags/{tag}") as response:
        release = json.load(response)
    if release.get("draft") or release.get("prerelease"):
        sys.exit(f"{tag} is a draft or a pre-release: publish it first")
    assets = {asset["name"]: asset for asset in release["assets"]}

    values = {"VERSION": version, "TAG": tag, "RELEASE_DATE": (release.get("published_at") or "")[:10]}
    for key, platform in PLATFORMS.items():
        name = f"OpenTypeless-{version}-{platform}.zip"
        if name not in assets:
            sys.exit(f"{tag} has no {name}")
        values[key + "_SHA256"] = sha256(assets[name])
    # WinGet's own manifests use upper-case checksums.
    for key in ("WINDOWS_X64", "WINDOWS_ARM64"):
        values[key + "_SHA256"] = values[key + "_SHA256"].upper()

    for folder in ("homebrew", "winget"):
        target = os.path.join(out, folder)
        shutil.rmtree(target, ignore_errors=True)
        os.makedirs(target)
        for name in sorted(os.listdir(os.path.join(HERE, folder))):
            with open(os.path.join(HERE, folder, name), encoding="utf-8") as f:
                text = f.read()
            for key, value in values.items():
                text = text.replace(f"@{key}@", value)
            if left := re.search(r"@[A-Z0-9_]+@", text):
                sys.exit(f"{folder}/{name} has a placeholder left: {left.group()}")
            with open(os.path.join(target, name), "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
            print(os.path.join(target, name))


if __name__ == "__main__":
    main()
