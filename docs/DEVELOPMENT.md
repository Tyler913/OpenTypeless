# Development

How to build, test and release OpenTypeless. For what the app does and how to install it, see the [README](../README.md); for why it's built the way it is, see [DESIGN.md](DESIGN.md).

## Build from source

### macOS

Needs Xcode 26+ installed (the Command Line Tools alone are enough to compile, but the build borrows SwiftUI's macro plugin from `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # optional but recommended, one time
scripts/build-app.sh             # builds, signs and installs /Applications/OpenTypeless.app
```

`create-signing-cert.sh` creates a local code-signing identity. Without it the app is signed ad hoc, and macOS asks for Accessibility and Microphone permission again after every rebuild.

To make the release files instead of installing: `scripts/build-app.sh --package` writes `macos/dist/OpenTypeless-<version>-macOS-arm64.dmg` and `.zip` (`-macOS-x64` on an Intel Mac), ad-hoc signed, and prints their SHA-256.

`build-app.sh` keeps exactly one copy of the app on the machine. It assembles the bundle in a hidden staging folder, moves it into `/Applications`, unregisters stale copies from LaunchServices, and clears outdated privacy entries when the signature changes.

### Windows

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio is optional.

```powershell
# from windows\ in a clone of this repository
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # tests, builds, installs to %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # writes windows\dist\OpenTypeless-<version>-windows-x64.zip and -setup.exe
```

`build.ps1` keeps exactly one installed copy: it stops the running app, replaces the install folder, refreshes the Start menu shortcut and launches the new build. Add `-Arch arm64` for Windows on ARM. The installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`); its script is [`windows/installer/OpenTypeless.iss`](../windows/installer/OpenTypeless.iss).

No Windows PC is needed to develop the Windows app: GitHub Actions builds it on every change (see [Continuous integration](#continuous-integration)).

## Repository layout

The repository holds both apps. They share the design, the evaluation sets and the README; each has its own code, tests and build scripts.

```
macos/                      The macOS app (Swift Package)
  Sources/TypelessCore/       Pipeline logic, no UI: chunker, WAV, provider client, retries, clean-up prompt
  Sources/OpenTypeless/       The app: hotkey, recorder, HUD, settings, history, paste, CLI tools
  Tests/                      swift-testing suites
  scripts/                    Build, signing and test scripts
windows/                    The Windows app (.NET solution)
  src/TypelessCore/           The same pipeline logic, ported line by line
  src/OpenTypeless/           The WinUI app: hotkey hook, WASAPI recorder, HUD, tray, settings, history, paste
  src/OpenTypeless.Cli/       Command-line tools (transcribe a file, chunk analysis, prompt evaluation)
  tests/                      xUnit suites
  scripts/                    Build, test and icon scripts
eval/                       Clean-up test sets and the evaluation guide, shared by both apps
i18n/                       UI translations (other than Chinese and English), shared by both apps
docs/DESIGN.md              Architecture and design decisions
docs/DEVELOPMENT.md         Building, testing and releasing (this page)
docs/WINDOWS-PORT.md        How each macOS file and system API maps to Windows
docs/images/                README screenshots
docs/brand/                 The logo: vector masters and how to use them
scripts/make_icon.py        Draws the logo and writes every app icon from it
```

The clean-up prompt (`Prompts.swift` / `Prompts.cs`) is byte-identical in both apps; change both together and check the result with [eval/](../eval/).

## Translations

Every user-facing string is written inline with its Chinese and English text: `L("有新版本 \(version)", "Version \(version) is available")` in Swift, `L($"有新版本 {version}", $"Version {version} is available")` in C#. The other languages live in [`i18n/strings.json`](../i18n/strings.json), keyed by the English text with each interpolation numbered in order:

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

Both apps embed the file at build time, and a string with no translation shows in English. A translation may move the placeholders around but must keep every one of them. After adding or changing a string, add its translations and run `python3 i18n/check.py`: it lists missing, unused and malformed entries, and CI runs it on every pull request. To review a language in context, render the UI with `--snapshot-ui` and `--lang ja` (or any other language code).

## Test and debug on macOS

```bash
cd macos
swift build
scripts/test.sh                  # unit tests, including a mock server that injects failures into a 130 s recording
scripts/perf.sh                  # performance budgets in a release build (audio path, main-thread work); CI runs them too
```

Useful command-line modes of the built binary (from `macos/`):

```bash
# Full pipeline on an audio file; --realtime feeds audio at speaking speed like a live microphone
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Show where the chunker cuts
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Speech-to-text by the recognizer on this Mac, whatever the settings say (no key; downloads the language's model once)
.build/debug/OpenTypeless --transcribe-file speech.m4a --on-device --no-polish

# Evaluate clean-up prompts and models (see eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Microphone silent or too quiet in some situation (e.g. during a call)? Record a few seconds through the app's recorder
# and the raw capture paths, and compare levels (works on the installed app too: /Applications/OpenTypeless.app/Contents/MacOS/OpenTypeless)
.build/debug/OpenTypeless --mic-probe 4

# Render the settings pages, menu-bar panel and HUD to PNGs (--live shows them on screen for real Liquid Glass).
# The README screenshots use sample history and --demo, which treats permissions as granted.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --dark --lang en
```

## Test and debug on Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # unit tests, including a mock server that injects failures into a 130 s recording
scripts\perf.ps1       # performance budgets in a Release build (audio path, UI-thread work); CI runs them too
```

Command-line tools (`OpenTypeless.Cli.exe`, shipped next to the app; it uses the app's settings and keys):

```powershell
# Full pipeline on an audio file (WAV, MP3, M4A, WMA, FLAC…); --realtime feeds audio at speaking speed
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# Show where the chunker cuts
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# Evaluate clean-up prompts and models (see eval/README.md)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# Render every settings page, the tray panel and the HUD states to PNGs
# (--lang en|zh|ja|… for one language, --demo treats permissions as granted; pair it with OPENTYPELESS_DATA_DIR and sample history)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Developer environment variables (Windows):

| Variable | Effect |
|---|---|
| `OPENROUTER_API_KEY` | Overrides the stored OpenRouter key. |
| `OPENTYPELESS_DATA_DIR` | Uses another folder for settings and history (handy for tests). |
| `OPENTYPELESS_DEBUG` | Writes hotkey / session / focus events to `debug.log` in the data folder. |
| `OPENTYPELESS_TEST_AUDIO` | Streams a 16 kHz mono WAV in real time instead of the microphone, for end-to-end tests. |

## Contributing

`main` is protected: every change goes through a pull request. Work on a branch, open a pull request into `main`, and merge it once the **CI passed** check is green.

## Continuous integration

GitHub Actions ([`.github/workflows/`](../.github/workflows/)) builds only the app you changed:

| You change | What runs |
|---|---|
| `macos/**` | **macOS build** on an Apple silicon and an Intel runner: the tests, then each one's disk image and zip, checked and launched. |
| `windows/**` | **Windows build** on an x64 and an ARM64 runner: the tests, then each one's portable zip and installer; the installer is run, the app started and uninstalled again. |
| `testdata/**` | Both builds: the shared test cases (word counts, prices, time saved, what each key does to a dictation…) that both test suites read, so the two apps agree. |
| `i18n/**` | Both builds: the translations both apps embed. |
| `eval/**`, `scripts/ci/**` | Both builds, plus the shared validation checks. |
| Only `docs/`, `README*.md` | No platform build; shared checks still run. |

Every CI run checks translations, that both apps carry the same version (and, for a release tag, the tag's), workflow syntax, evaluation fixture structure and packaging templates (**Shared checks**). A macOS prompt change also runs the Windows tests that check prompt parity. Release and package-manager publishing run the same shared checks.

Both architectures of both apps must render all settings pages, onboarding steps, the popover and eight HUD states in English/Chinese and light/dark themes. Missing, blank or corrupt images, crashes and timeouts fail CI; screenshots and failure logs are retained as artifacts. These are rendering smoke tests, not interaction tests or visual baseline comparisons.

Each packaged build also dictates a minute of synthetic speech through its own pipeline (`--transcribe-file --realtime`: the macOS app binary, `OpenTypeless.Cli.exe` from the Windows zip) against a local fake provider ([`scripts/ci/fake_provider.py`](../scripts/ci/fake_provider.py)). The first request fails with 503 and the clean-up streams a few bytes at a time; the transcript and clean-up must come out exactly as spoken. No API key is used and no audio leaves the runner. The recording and the Windows zip sit in folders with spaces and Chinese in their names.

The Linux pipeline tests run four times: en-US/UTC, de-DE (decimal comma), tr-TR (Turkish casing) and zh-CN/Asia/Shanghai.

Core-library coverage reports are attached to macOS test runs and the Linux .NET test run. They do not measure the native app or prove end-to-end correctness. See the [coverage audit and testing proposal](TESTING-AUDIT.zh-CN.md) for the remaining gaps.

Download the builds from a run's **Artifacts** section on the Actions tab. **Actions → macOS build / Windows build → Run workflow** starts a build by hand.

To release, bump the version in both apps (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) and push one tag, `1.0.2` (or `V1.0.2`). It builds both apps and creates one **draft** release, "OpenTypeless V1.0.2", with eight files: the macOS disk images and zips for Apple silicon and Intel (signed with the release certificate), and the Windows installers and portable zips for x64 and ARM64. The build fails if any of them is missing or its version doesn't match the tag.

Review the draft and publish it by hand; it becomes the latest release. Installed copies find it within a day: the in-app updater looks for the newest published, non-prerelease release that has a zip for its platform (`OpenTypeless-<version>-macOS-arm64.zip`, `-macOS-x64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`). Mark a release as a pre-release to keep it from being offered. Publishing also updates the [Homebrew tap and WinGet](../packaging/README.md), as does turning a pre-release into a release later.

**macOS release signing (one time).** Every release must be signed with the same certificate: a copy signed with the release certificate only installs an in-app update signed with that same certificate, so a release signed ad hoc or with another certificate would never reach installed copies (and macOS ties Accessibility and Microphone permission to the signature too). Run `macos/scripts/create-release-cert.sh`, keep the `.p12` it writes somewhere private, and add the two repository secrets it prints (`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`). Release builds then sign with it. A tag build fails without the secrets, or when the app isn't signed with the certificate whose hash is `RELEASE_CERTIFICATE_LEAF` in [`macos.yml`](../.github/workflows/macos.yml); pull request and manual builds are signed ad hoc.
