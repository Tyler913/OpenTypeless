# OpenTypeless for Windows — port plan

This is a full re-implementation of the macOS app (`Tyler913/OpenTypeless`, SwiftUI) on **WinUI 3 / Windows App SDK (C#, .NET 10)**.
The goal is feature parity: the same pipeline, the same prompts, the same settings, the same history format and the same
failure handling. Only the system-integration layer and the look differ.

## Project layout

| macOS | Windows | Notes |
|---|---|---|
| `Sources/TypelessCore` (Swift library) | `src/TypelessCore` (`net10.0` class library) | Pure logic, no UI, no Windows APIs. Line-by-line port. |
| `Sources/OpenTypeless` (AppKit + SwiftUI app) | `src/OpenTypeless` (WinUI 3, unpackaged) | Tray app, HUD, settings window, hotkey, recorder, paste. |
| CLI modes of the app binary | `src/OpenTypeless.Cli` (console exe) | A WinUI app has no console, so `--transcribe-file`, `--chunks-only`, `--eval-polish` live in a sibling exe that shares settings and keys with the app. `--snapshot-ui` stays in the app. |
| `Tests/TypelessCoreTests` (swift-testing) | `tests/TypelessCore.Tests` (xUnit) | Same cases, including the mock server that fails every chunk of a 130 s recording once. |
| `eval/` | `eval/` | Copied unchanged; the prompt is byte-identical so results are comparable. |

## Core (TypelessCore) — 1:1 port

| Swift | C# | Behaviour kept |
|---|---|---|
| `WAV.swift` | `Wav.cs` | 16 kHz mono PCM16, 44-byte header, chunk-walking decoder, streaming `WavFileWriter` that patches sizes on `Finalize`. |
| `Chunker.swift` | `Chunker.cs` | Cut at the centre of the quietest 0.4 s window between 18 and 28 s, 20 ms hop; `AudioLevel.Rms` / `IsSilent` (30 ms frames, 0.004). |
| `TranscriptJoiner.swift` | `TranscriptJoiner.cs` | No space around CJK, none before punctuation. |
| `Retry.swift` | `ApiError.cs`, `RetryPolicy.cs` | Same error kinds and messages, retryable = network/timeout/badResponse/408/409/425/429/5xx; 1 s·2ⁿ ±20 % jitter; `WithTimeout`. |
| `Providers.swift` | `Providers.cs` | Same 6 providers, URLs, default models, key placeholders, STT format. |
| `APIClient.swift` | `ApiClient.cs` | One shared `HttpClient` (connection reuse, 6 per host), OpenRouter JSON vs multipart STT, per-attempt timeout `clamp(2×len+15, 30…90)`, 200-with-error handling, `/models`, `/key` check, attribution headers. |
| `PolishClient.swift` | `PolishClient.cs` | Streaming SSE, **idle** timeout 25 s + 240 s runaway guard, reasoning off/minimum, provider sort by throughput, retry without optional params on 400/422, `finish_reason=length` → truncated. |
| `Prompts.swift` | `Prompts.cs` | Prompt text copied verbatim; `SanitizePolishOutput`, `LooksLikeAnAnswer`. |
| `TranscriptionPipeline.swift` | `TranscriptionPipeline.cs` | Transcribe while talking, ≤ 3 concurrent, skip silent / < 0.3 s chunks, preset transcripts for retries, one extra round for transient failures, `PipelineFailure` with partial text. |
| `PolishEvaluation.swift` | `PolishEvaluation.cs` | Evaluation-only accounting, durable budget ledger with an exclusive lock file. |
| `Localization.swift` | `Localization.cs` | Inline `L("中文", "English")`, `system / zh / en`, switchable at runtime. |

## System integration — macOS API → Windows API

| Concern | macOS | Windows |
|---|---|---|
| App shell | `LSUIElement` menu-bar app | No main window; notification-area (tray) icon via `Shell_NotifyIcon`. Single instance (named mutex); launching again opens Settings, like reopening from Finder. |
| Tray icon states | SF Symbols `waveform` / `waveform.circle.fill` / `ellipsis.circle` | Same three glyphs rasterised at runtime for the current DPI and taskbar theme (light/dark). |
| Menu-bar popover | `NSPopover` sized before showing | Borderless WinUI window anchored above the tray, sized to its content before it is shown, closes when it loses focus. |
| Global hotkey | `CGEventTap` (flagsChanged/keyDown/keyUp) | `WH_KEYBOARD_LL` hook on a dedicated thread with its own message loop (never blocked by UI). Same rules: modifier-only keys, combos swallowed, tap < 0.35 s → hands-free, Esc cancels, another *physical* key within 1 s cancels (injected keys are ignored, like the Logi Options+ case). Alt/Win hotkeys get a masking key so Windows doesn't open the menu bar / Start. |
| Default hotkey | Fn | **Right Ctrl** (Windows keyboards don't expose Fn to software). See the Hotkey section for presets. |
| Recording | `AVAudioEngine` → 16 kHz Int16, rebuilt on route change | WASAPI shared-mode capture with `AUTOCONVERTPCM` (system resampler → 16 kHz mono Int16), event-driven thread; rebuilt on default-device change or device loss so long dictations survive a headset connecting. |
| Keys | Keychain (one item) | Windows Credential Manager, one generic credential `OpenTypeless/credentials` holding all keys (JSON), legacy import not needed. `OPENROUTER_API_KEY` env override kept. |
| Preferences | `UserDefaults` | `%LOCALAPPDATA%\OpenTypeless\settings.json` with the same keys and defaults. |
| History | `~/Library/Application Support/OpenTypeless/Sessions/<id>/` | `%LOCALAPPDATA%\OpenTypeless\Sessions\<id>\` with the same `audio.wav` + `session.json` schema (keep 200 / audio of newest 30, interrupted → failed & retryable). |
| Focus probe | Accessibility API roles | UI Automation (`IUIAutomation`, run off the UI thread with a 0.5 s budget) + `GetGUIThreadInfo` caret: editable → paste & restore clipboard; clearly not editable (desktop, lists, buttons) → clipboard; unknown (browsers/Electron/CEF) → paste *and* keep text on clipboard. |
| Paste | `⌘V` via `CGEvent`, pasteboard snapshot/restore, `TransientType` | `Ctrl+V` via `SendInput`; Win32 clipboard snapshot/restore of every HGLOBAL format; `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory=0` so clipboard history/managers skip the temporary entry; restore only if the sequence number is unchanged. |
| HUD | Non-activating click-through glass capsule | Topmost, non-activating, click-through tool window (`WS_EX_NOACTIVATE`/`TOOLWINDOW`), Acrylic backdrop kept "active" so it never greys out; bottom-centre of the monitor under the mouse. Same phases: recording (red dot, 18 level bars, timer), working, copied, error. |
| Sounds | `Tink` / `Pop` at 0.35 volume | Short soft chimes synthesised in-process (no bundled assets), played at the same low volume. |
| Permissions | Microphone (TCC), Accessibility | Microphone: read the privacy consent store (`ConsentStore\microphone` + `NonPackaged`), link to `ms-settings:privacy-microphone`. Accessibility: not required on Windows (row explains the one exception: apps running as administrator). |
| Launch at login | `SMAppService` | `HKCU\…\Run`; "needs approval" = disabled in Task Manager (`StartupApproved\Run`). Enabled once by default for installed (non-dev) builds. |
| Settings window | `NSSplitViewController` + Liquid Glass sidebar | `NavigationView` sidebar on a Mica window with a custom title bar. Same six pages and contents. |
| Keyboard settings hint (Fn → emoji) | Banner on the Shortcut page | Windows equivalents: an AltGr notice when Right Alt is the hotkey, and a warning when a combination shadows a common `Ctrl+key` shortcut. |

## Hotkey on Windows

- Modifier-only: Right Ctrl (default), Right Alt, Right Shift, and any modifier the user records (Left/Right Ctrl, Alt, Shift, Win).
- Combination: any of Ctrl / Alt / Shift / Win + a key, or F1–F24 alone. A plain letter is refused with the same message.
- `ShadowsCommonShortcut` = only Ctrl + key (the Windows analogue of ⌘ + key).

## Deliberate platform differences

These follow from how Windows works rather than from missing features:

- **Default hotkey Right Ctrl** instead of Fn (Windows keyboards don't expose Fn). Alt / Win hotkeys send an unassigned
  masking key so that tapping them doesn't open the menu bar or Start.
- **Permissions**: only the microphone is a real permission on Windows. The mac "Accessibility" row becomes
  "Keyboard & paste: not required", and pasting into elevated (administrator) apps, which Windows forbids, falls back to
  the clipboard instead of silently doing nothing.
- **Terminals** (Windows Terminal, conhost, WezTerm, Alacritty, PuTTY…) are treated as text inputs: they expose their
  buffer to UI Automation as static text, but are the usual home of command-line AI tools.
- **Clipboard hygiene**: the temporary paste entry is marked so Windows clipboard history, cloud clipboard and
  clipboard managers skip it (the macOS app uses `org.nspasteboard.TransientType`).
- **Launching again** shows the settings (like reopening the app on macOS); a manual launch also shows them, because
  Windows hides new tray icons in the overflow and an app that shows nothing looks broken.
- **The CLI** is `OpenTypeless.Cli.exe` next to the app instead of flags on the app binary; `--key-from-keychain` is
  accepted as an alias of `--key-from-credential-store`.
- **Look**: Mica / Acrylic and Fluent controls instead of Liquid Glass; the HUD is a rounded Acrylic capsule sized to
  its content.

## Verification

- `tests/TypelessCore.Tests`: 34 xUnit tests, all Swift test cases ported (WAV, chunker, joiner, SSE, retry
  classification, timeout, prompt sanitising, reasoning config, mock-server pipeline with a 130 s recording whose every
  chunk fails once, permanent failures, silence skipping, multipart, custom endpoints, polish fallbacks, evaluation
  ledger), plus retry reuse of finished chunks, idle-timeout handling and a prompt-integrity check.
- End to end against a local OpenAI-compatible mock server, with the app itself and a real WinForms text box as the
  target: hold-to-talk, tap for hands-free, Esc cancel, injected keys ignored while holding, a combination hotkey
  (Alt + Space, keystrokes swallowed), a 35 s dictation whose first chunk was transcribed during recording, a failure
  while the provider was down followed by Retry from the tray panel, paste with the clipboard restored afterwards, and
  recording a new shortcut on the Shortcut page.
- `OpenTypeless.Cli`: chunk analysis on an 86 s speech file (4 chunks, every cut at a true pause), the full pipeline
  with injected 503s, 44.1 kHz stereo input through the Media Foundation transcoder, and an evaluation dry run.
- UI: every page in both languages, the tray panel and each HUD state via `--snapshot-ui`, and live on-screen captures.

## UI parity checklist

- **Popover**: header (tile, name, status pill), hero ("Hold [keys] and speak", tip), setup warnings with Fix, last failed dictation with Retry, 5 recent items (click to copy, check mark), footer (Settings, History, Quit).
- **General**: permissions, language (System / 简体中文 / English), open at login (+ error / approval footer), restore clipboard, sounds, maximum recording (1–60 min).
- **Shortcut**: current keys (large key caps), record / cancel, presets, how it works, warnings.
- **Providers**: six expandable cards: usage tags, configured pill, API key (password box + Paste), Base URL (+ Reset), get-a-key link, Test connection.
- **Models**: STT provider / model (free text + browse, grouped by vendor when > 40) / spoken language; clean-up toggle / provider / model; missing-key banners.
- **Vocabulary & Style**: vocabulary and preferences editors.
- **History**: list + detail (status, error, cleaned + raw text, Copy, Re-transcribe, Show in Explorer, Delete).
- Bilingual UI switching instantly, Esc cancel, max-duration auto stop, < 0.4 s accidental tap discarded, "No speech detected", truncation / answer detection fallbacks.
