# Package managers

Every published release can be installed with Homebrew on macOS and WinGet on Windows. The
[Publish to package managers](../.github/workflows/publish.yml) workflow runs when a release is published (not a
pre-release): it fills in the files here with the version and the SHA-256 GitHub published for each zip
([render.py](render.py)), then

- **Homebrew:** commits the cask to `Casks/opentypeless.rb` in [Tyler913/homebrew-tap](https://github.com/Tyler913/homebrew-tap).
- **WinGet:** opens a pull request in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) with the manifests for `TylerHong.OpenTypeless`.

```bash
python3 packaging/render.py 1.3.7 dist    # the same files, by hand: dist/homebrew/ and dist/winget/
```

## Why not the official Homebrew casks

Since 2026-09-01, homebrew/cask only takes apps that pass Gatekeeper, which needs Apple notarization (a paid
developer account). OpenTypeless isn't notarized, so it has its own tap instead. The cask clears the download
quarantine after installing, as the manual install's `xattr` step does, and declares `auto_updates`: the app updates
itself, and `brew upgrade --greedy` (or the next release's cask) catches up.

## One-time setup

### Homebrew

1. Create a **public** repository named **`homebrew-tap`** under Tyler913, with a README (so it has a `main` branch).
   The `homebrew-` prefix is what lets `brew install --cask tyler913/tap/opentypeless` find it.
2. Create a [fine-grained personal access token](https://github.com/settings/personal-access-tokens/new): repository
   access *Only select repositories → homebrew-tap*, permission **Contents: Read and write**.
3. Add it to this repository as the secret **`HOMEBREW_TAP_TOKEN`** (Settings → Secrets and variables → Actions).

### WinGet

1. Create a [classic personal access token](https://github.com/settings/tokens/new) with the **`public_repo`** scope
   (wingetcreate forks microsoft/winget-pkgs into your account and opens the pull request from the fork).
2. Add it as the secret **`WINGET_TOKEN`**.

The first submission adds a new package, so Microsoft's moderators review it by hand (usually a few days); later
versions are checked automatically. Watch the pull request: a moderator may ask for a change, or the automated
check may flag the unsigned exe for a closer look.

Then publish the release as usual, or run the workflow for a release that's already out
(**Actions → Publish to package managers → Run workflow**, with its tag). Until a token is set, its part is skipped
with a notice and nothing fails.

## Installing

```bash
brew install --cask tyler913/tap/opentypeless
```

```powershell
winget install TylerHong.OpenTypeless
```

WinGet installs the zip as a portable app (under `%LOCALAPPDATA%\Microsoft\WinGet\Packages`) and puts its folder on
`PATH`, without a Start menu entry: start it the first time with **Win + R → `OpenTypeless`**. After that it starts
at sign-in, and updates itself in place.
