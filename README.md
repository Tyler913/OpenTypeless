<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="OpenTypeless icon">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Native voice typing for macOS and Windows that doesn't fall apart on long dictation.</b><br>
  Hold a key, talk for as long as you need, and get clean, structured text at your cursor.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="The recording capsule shown while you talk">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="The menu-bar panel: shortcut hint and recent dictations">
</p>

<p align="center">
  Two native apps, one design: <b>macOS</b> (Swift / SwiftUI) and <b>Windows</b> (C# / WinUI 3).<br>
  Same pipeline, same clean-up prompt, same settings and failure handling; only the system integration and the look differ.
</p>

---

## Why this exists

Voice typing is the fastest way to write long prompts for AI tools. That also means long dictations: one to three minutes of thinking out loud is normal.

Most voice-typing apps, open source and commercial, work fine for a sentence and then **fail on exactly those long dictations**. Recording for 40 s, 1 min or 2 min ends in a timeout, an empty result, or lost text. Reading the source of several open-source alternatives turned up the same root causes again and again:

- **The whole recording is sent as one speech-to-text request.** Upstream providers time out after about 60 s of processing (OpenRouter documents this explicitly), so the longer you talk, the more likely the request dies.
- **The clean-up LLM call has a short *total* timeout.** A 30 s cap that includes streaming kills a long answer halfway through.
- **One failure throws everything away.** Two minutes of talking are gone, and you have to say it all again.

OpenTypeless is built around that problem.

## How it handles long dictation

| | What happens |
|---|---|
| **Chunk at pauses** | While you talk, audio is cut into 18–28 s segments at the quietest 0.4 s window in each span, so words are never cut in half and no request comes close to the upstream 60 s limit. |
| **Transcribe while you talk** | Each segment is transcribed in the background as soon as it is cut. After a two-minute dictation, only the last few seconds are left to process when you release the key. |
| **Retry per segment** | Network drops, 429s and 5xx errors are retried with backoff. Bad keys and billing errors fail fast. One failed segment never affects the others, and failed segments get one more full round at the end. |
| **Backup model for slow starts** | If the clean-up model hasn't started answering within 0.8 s, or fails, a backup model from another vendor is asked too and whichever answers first wins. A slow upstream provider costs under a second more, not the whole wait. |
| **Idle timeout, not total timeout** | The clean-up step streams its answer, and is only considered stuck when *no* data arrives for 25 s, so long outputs are never cut off. |
| **Never lose words** | Audio is written to disk as you speak. Every dictation is kept in History; a failed one can be retried later, and only its failed segments are re-sent. If clean-up fails, the raw transcript is inserted instead. |

A synthetic 121 s dictation, for example, is split into 6 segments at natural pauses. When the speaker stops, 5 of them are already transcribed.

## Clean-up that reads like you typed it

Raw speech-to-text is messy: fillers, restarts, "no wait, I mean…", thinking out loud. The clean-up step turns it into what you would have typed:

- **Self-corrections resolved.** The final version wins ("Wednesday, no, Thursday" → Thursday). That covers corrections made much later, implicit ones ("50k, uh, 60k to be safe"), and whole points withdrawn ("…the third one, never mind").
- **Filler and thinking out loud removed.** um / uh / 嗯 / 那个 / "let me think" / "that's about it" all go.
- **Nothing real is lost.** Numbers, versions, names and comparisons are kept exactly. The model is told that products newer than it knows are real, so "Gemini 3.5" never becomes "Gemini 2.5".
- **Structure when it helps.** Three or more parallel points become a numbered list; everything else stays as plain paragraphs.
- **Never answers you.** Dictated prompts ("can you explain why…") are cleaned up, not answered or executed.
- **Your words, your languages.** Edits are kept to a minimum: wording, order and tone stay yours. When you mix Chinese and English, every word stays in the language you said it in, everyday words too ("shortcut", "dark mode"), with spacing between CJK and Latin text.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="History: the cleaned-up text above the raw transcript, with timing for each step">
</p>

The prompt is tuned against development and held-out test sets (see [eval/](eval/)), including real dictations.

## Features

- **Global hotkey.** Hold **Fn** (macOS) or **Right Ctrl** (Windows) by default, or record any single modifier (right ⌘, right ⌥, Right Alt, …) or a combination (⌥ Space, Alt + Space, F5, …).
- **Push-to-talk or hands-free.** Hold to talk; tap once to keep recording hands-free and tap again to finish. **Esc** cancels.
- **Pastes where your cursor is.** In a text field the text is pasted and your clipboard restored; with no text field focused it goes to the clipboard. Browsers and Electron apps are handled too, and terminals on Windows.
- **Bring your own provider.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek, or any OpenAI-compatible endpoint. Speech-to-text, clean-up and the backup clean-up model can each use a different provider.
- **Custom vocabulary and style preferences** for names, products and jargon.
- **Learns from your fixes.** Correct a misrecognised word after it's pasted (TypeList → Typeless) and it's added to your vocabulary automatically, along with how it was misheard. Only sound-alike fixes are learned, never rewrites, changed numbers or ordinary word swaps, and a word you remove is never learned again.
- **History** of every dictation with raw and cleaned text, timing, copy and re-transcribe. Choose how long recordings are kept: not at all, a day, a week, a month, a year, or forever.
- **Bilingual UI** (English / 简体中文), following the system language or chosen manually.
- **Native design.** Liquid Glass on macOS 26+ with a menu-bar panel; Mica and Acrylic on Windows 11 with a tray panel. Both show a small recording capsule while you talk.
- **Launch at login.**
- **Small and native.** A ~3 MB Swift/SwiftUI app on macOS and a self-contained WinUI 3 app on Windows. No Electron, no account and no server of its own.

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="Models: a provider and model for each step, plus a backup clean-up model"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="Vocabulary: your terms, including ones learned from your corrections"></td>
  </tr>
  <tr>
    <td align="center">Pick a provider and model for each step, with a backup clean-up model</td>
    <td align="center">Vocabulary, including words learned from your corrections</td>
  </tr>
</table>


## Requirements

- **macOS:** macOS 26 or later, Apple Silicon.
- **Windows:** Windows 10 (version 2004 or later) or Windows 11, x64 or ARM64.
- An API key for at least one provider ([OpenRouter](https://openrouter.ai/keys) is the easiest: one key covers both steps).

## Install on macOS

### Download

1. Download `OpenTypeless-<version>-macOS-arm64.zip` from [Releases](https://github.com/Tyler913/OpenTypeless/releases) and unzip it.
2. Move **OpenTypeless.app** to your **Applications** folder.
3. The app isn't notarized by Apple (that needs a paid developer account), so macOS blocks it on first launch, and may even say it "is damaged and can't be opened". Remove the download quarantine flag once in Terminal:

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   Then open it normally:

   ```bash
   open /Applications/OpenTypeless.app
   ```

   Alternatively, try to open it once, then go to **System Settings → Privacy & Security** and click **Open Anyway**.

OpenTypeless lives in the menu bar (waveform icon), not the Dock.

### Build from source

Needs Xcode 26+ installed (the Command Line Tools alone are enough to compile, but the build borrows SwiftUI's macro plugin from `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # optional but recommended, one time
scripts/build-app.sh             # builds, signs and installs /Applications/OpenTypeless.app
```

`create-signing-cert.sh` creates a local code-signing identity. Without it the app is signed ad hoc, and macOS asks for Accessibility and Microphone permission again after every rebuild.

To make a release zip instead of installing: `scripts/build-app.sh --package` writes `macos/dist/OpenTypeless-<version>-macOS-arm64.zip` (ad-hoc signed) and prints its SHA-256.

`build-app.sh` keeps exactly one copy of the app on the machine. It assembles the bundle in a hidden staging folder, moves it into `/Applications`, unregisters stale copies from LaunchServices, and clears outdated privacy entries when the signature changes.

### First run

1. Grant **Microphone** and **Accessibility** access (Accessibility is used to listen for the hotkey and paste text).
2. Add an API key under **Settings → Providers**.
3. Recommended if you use Fn: set **System Settings → Keyboard → "Press 🌐 key to"** to **Do Nothing**, so tapping Fn doesn't open the emoji picker.

## Install on Windows

### Download

1. Download `OpenTypeless-<version>-windows-x64.zip` (or `-arm64`) from [Releases](https://github.com/Tyler913/OpenTypeless/releases) and unzip it anywhere (e.g. `%LOCALAPPDATA%\Programs`).
2. Run **OpenTypeless.exe**. It's self-contained: nothing else to install.
3. The app isn't code-signed, so SmartScreen may say "Windows protected your PC": click **More info → Run anyway**.

OpenTypeless lives in the **notification area** (waveform icon next to the clock). Windows hides new icons in the overflow (^) at first; drag it onto the taskbar, or turn it on under **Settings → Personalization → Taskbar → Other system tray icons**.

### Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio is optional.

```powershell
# from windows\ in a clone of this repository
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # tests, builds, installs to %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # writes windows\dist\OpenTypeless-<version>-windows-x64.zip
```

`build.ps1` keeps exactly one installed copy: it stops the running app, replaces the install folder, refreshes the Start menu shortcut and launches the new build. Add `-Arch arm64` for Windows on ARM.

No Windows PC is needed to develop the Windows app: GitHub Actions builds it on every change (see [Continuous integration](#continuous-integration)).

### First run

1. Add an API key under **Settings → Providers**.
2. Make sure **Settings → Privacy & security → Microphone → Let desktop apps access your microphone** is on.
3. Hold Right Ctrl and talk. Windows needs no accessibility permission; the one limit is that it doesn't allow pasting into apps running as administrator, so there the text goes to the clipboard.

## Default models

| Step | Default | Notes |
|---|---|---|
| Speech-to-text | `microsoft/mai-transcribe-2` (OpenRouter) | Any OpenRouter transcription model, or Whisper-compatible `/audio/transcriptions` elsewhere. |
| Clean-up | `google/gemini-3.8-flash` (OpenRouter) | Best clean-up in our tests, at about $0.005 per long dictation. Cheaper options to try: `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| Backup clean-up | `deepseek/deepseek-v4.1-flash` (OpenRouter) | Only asked when the main model is slow to start or fails. Pick a fast model from another vendor. |

Reasoning is automatically turned off or set to its minimum for the clean-up model, to keep latency low.

## Privacy

- Audio and text are only sent to the providers you configure.
- API keys are stored in the macOS Keychain, or in Windows Credential Manager (one entry, `OpenTypeless/credentials`).
- History (audio + transcripts) lives in `~/Library/Application Support/OpenTypeless/Sessions/` on macOS and `%LOCALAPPDATA%\OpenTypeless\` on Windows. Recordings are kept for a month by default (History page: not at all, a day, a week, a month, a year, or forever); after that the text stays among the newest 200 entries. Failed dictations keep their audio so they can be retried.
- Learning from your fixes reads the text field you dictated into, on your computer only, for at most two minutes after a paste. Password fields are skipped. It can be turned off under **Vocabulary & Style**.

## Development

The repository holds both apps. They share the design, the evaluation sets and this README; each has its own code, tests and build scripts.

```
macos/                      The macOS app (Swift Package)
  Sources/TypelessCore/       Pipeline logic, no UI: chunker, WAV, provider client, retries, clean-up prompt
  Sources/OpenTypeless/       The app: hotkey, recorder, HUD, settings, history, paste, CLI tools
  Tests/                      swift-testing suites
  scripts/                    Build, signing, icon and test scripts
windows/                    The Windows app (.NET solution)
  src/TypelessCore/           The same pipeline logic, ported line by line
  src/OpenTypeless/           The WinUI app: hotkey hook, WASAPI recorder, HUD, tray, settings, history, paste
  src/OpenTypeless.Cli/       Command-line tools (transcribe a file, chunk analysis, prompt evaluation)
  tests/                      xUnit suites
  scripts/                    Build, test and icon scripts
eval/                       Clean-up test sets and the evaluation guide, shared by both apps
docs/DESIGN.md              Architecture and design decisions
docs/WINDOWS-PORT.md        How each macOS file and system API maps to Windows
docs/images/                README screenshots
```

The clean-up prompt (`Prompts.swift` / `Prompts.cs`) is byte-identical in both apps; change both together and check the result with [eval/](eval/).

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # unit tests, including a mock server that injects failures into a 130 s recording
```

Useful command-line modes of the built binary (from `macos/`):

```bash
# Full pipeline on an audio file; --realtime feeds audio at speaking speed like a live microphone
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Show where the chunker cuts
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Evaluate clean-up prompts and models (see eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Render the settings pages, menu-bar panel and HUD to PNGs (--live shows them on screen for real Liquid Glass).
# The README screenshots use sample history and --demo, which treats permissions as granted.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # unit tests, including a mock server that injects failures into a 130 s recording
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
# (--lang en|zh for one language, --demo treats permissions as granted; pair it with OPENTYPELESS_DATA_DIR and sample history)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Developer environment variables (Windows):

| Variable | Effect |
|---|---|
| `OPENROUTER_API_KEY` | Overrides the stored OpenRouter key. |
| `OPENTYPELESS_DATA_DIR` | Uses another folder for settings and history (handy for tests). |
| `OPENTYPELESS_DEBUG` | Writes hotkey / session / focus events to `debug.log` in the data folder. |
| `OPENTYPELESS_TEST_AUDIO` | Streams a 16 kHz mono WAV in real time instead of the microphone, for end-to-end tests. |

### Contributing

`main` is protected: every change goes through a pull request. Work on a branch, open a pull request into `main`, and merge it once the **CI passed** check is green.

### Continuous integration

GitHub Actions ([`.github/workflows/`](.github/workflows/)) builds only the app you changed:

| You change | What runs |
|---|---|
| `macos/**` | **macOS build** on a macOS runner: the tests, then the app zip. |
| `windows/**` | **Windows build** on a Windows runner: the tests, then the x64 and ARM64 zips. |
| Only `docs/`, `eval/`, `README.md` | Nothing to build. |

Download the zips from a run's **Artifacts** section on the Actions tab. **Actions → macOS build / Windows build → Run workflow** starts a build by hand.

To release, bump the version (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) and push a tag:

- `1.0.2` builds the macOS app and creates a **draft** release with its zip, marked as the latest release when you publish it.
- `1.0.2-windows` builds the Windows app and creates a **draft** release with both zips, never marked as the latest release.

Review the draft and publish it by hand.

## Acknowledgements

Inspired by Typeless. The system-integration approach learned from the open-source [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) and [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). This project is independent and not affiliated with any of them.

## License

[MIT](LICENSE)
