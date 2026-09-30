# OpenTypeless for Windows — port plan

This is a full re-implementation of the macOS app (`macos/`, SwiftUI) on **WinUI 3 / Windows App SDK (C#, .NET 10)**.
The goal is feature parity: the same pipeline, the same prompts, the same settings, the same history format and the same
failure handling. Only the system-integration layer and the look differ.

## Project layout

| macOS | Windows | Notes |
|---|---|---|
| `macos/Sources/TypelessCore` (Swift library) | `windows/src/TypelessCore` (`net10.0` class library) | Pure logic, no UI, no Windows APIs. Line-by-line port. |
| `macos/Sources/OpenTypeless` (AppKit + SwiftUI app) | `windows/src/OpenTypeless` (WinUI 3, unpackaged) | Tray app, HUD, settings window, hotkey, recorder, paste. |
| CLI modes of the app binary | `windows/src/OpenTypeless.Cli` (console exe) | A WinUI app has no console, so `--transcribe-file`, `--chunks-only`, `--eval-polish` live in a sibling exe that shares settings and keys with the app. `--snapshot-ui` stays in the app. |
| `macos/Tests/TypelessCoreTests` (swift-testing) | `windows/tests/TypelessCore.Tests` (xUnit) | Same cases, including the mock server that fails every chunk of a 130 s recording once. |
| `eval/` | `eval/` | Shared by both apps; the prompt is byte-identical so results are comparable. |

## Core (TypelessCore) — 1:1 port

| Swift | C# | Behaviour kept |
|---|---|---|
| `WAV.swift` | `Wav.cs` | 16 kHz mono PCM16, 44-byte header, chunk-walking decoder, streaming `WavFileWriter` that patches sizes on `Finalize`. |
| `Chunker.swift` | `Chunker.cs` | Cut at the centre of the quietest 0.4 s window between 18 and 28 s, 20 ms hop; `AudioLevel.Rms` / `IsSilent` (30 ms frames, 0.004). |
| `TranscriptJoiner.swift` | `TranscriptJoiner.cs` | No space around CJK, none before punctuation. |
| `Retry.swift` | `ApiError.cs`, `RetryPolicy.cs` | Same error kinds and messages, retryable = network/timeout/badResponse/408/409/425/429/5xx; 1 s·2ⁿ ±20 % jitter; `WithTimeout`. |
| `Providers.swift` | `Providers.cs` | Same 6 providers, URLs, default models, key placeholders, STT format. |
| `APIClient.swift` | `ApiClient.cs` | One shared `HttpClient` (connection reuse, 6 per host), OpenRouter JSON vs multipart STT, per-attempt timeout `clamp(2×len+15, 30…90)`, 200-with-error handling, `/models`, `/key` check, attribution headers. |
| `PolishClient.swift` | `PolishClient.cs` | Streaming SSE, **idle** timeout 25 s + 240 s runaway guard, reasoning off/minimum, OpenRouter providers sorted by latency with a 50 tok/s throughput floor, retry without optional params on 400/422, `finish_reason=length` → truncated. |
| `HedgedPolish.swift` | `HedgedPolish.cs` | Backup model raced after a fixed 0.55 s without a first token (or at once if the primary fails); first stream to produce a token wins, the other is cancelled. A `Channel` of events instead of an `AsyncStream`. |
| `Prompts.swift` | `Prompts.cs` | Prompt text and post-transcript reminder copied verbatim (the minimal-edit, keep-every-word's-language prompt); misheard-term hints; `SanitizePolishOutput`, `LooksLikeAnAnswer`. |
| `CorrectionLearner.swift` | `CorrectionLearner.cs`, `Transliteration.cs` | Same diff (LCS over Latin words / CJK characters), the same widening to whole Chinese words (the app passes `Windows.Data.Text.WordsSegmenter`, `Native/ChineseWords.cs`, where macOS uses `NLTokenizer`), the same Metaphone sound key and the same filters. Foundation's `.toLatin` transform becomes ICU's `Any-Latin; Latin-ASCII` called from the OS's own ICU (`icu.dll`, Windows 10 1903+), so 逻辑 / 罗技 compare as the same pinyin. Chinese numerals count as digits, like Swift's `Character.isNumber`. |
| `TranscriptionPipeline.swift` | `TranscriptionPipeline.cs` | Transcribe while talking, ≤ 3 concurrent, skip silent / < 0.3 s chunks, silence trimmed off each chunk, preset transcripts for retries, one extra round for transient failures, `PipelineFailure` with partial text. Speculative tail at every 0.3 s pause, adopted at release when only silence followed; `onSpeculation` reports the transcript the recording would give if it ended now. |
| `VoiceActivity.swift` | `VoiceActivity.cs` | 30 ms frames, speech threshold 2 × the quietest tenth clamped to 0.004–0.008, 0.3 s padding; `PauseTracker` for the live audio. Both suites run `testdata/voice-activity-cases.json`. |
| `Preconnector.swift` | `Preconnector.cs` | HEAD to each distinct host's base URL (no key) when the key goes down and every 25 s while recording, at most once per host every 20 s. |
| `PolishEvaluation.swift` | `PolishEvaluation.cs` | Evaluation-only accounting, durable budget ledger with an exclusive lock file, first-token time, `--provider-routing`, `PolishMetrics` (English kept, similarity). |
| `Localization.swift` | `Localization.cs` | Inline `L("中文", "English")`; other languages looked up in the shared `i18n/strings.json` (Swift: `ExpressibleByStringInterpolation`, C#: an interpolated string handler, both turning the English text into a `{0}` template). `system / zh / en / ja / ko / es / pt / fr / de / ru`, switchable at runtime. |
| `Usage.swift` | `Usage.cs` | `RequestUsage` from STT / SSE `usage`, token estimate, `ModelPrice`, `CostEstimator` (reported cost vs. price), OpenRouter `PriceCatalog`, `UsageFormat`. |
| `UsageLedger.swift` | `UsageLedger.cs` | `WordCount`, per-day `usage.json`, totals, time saved, streaks, heatmap quartiles. `CalendarDay` on macOS is `DateOnly` on Windows. Both suites run `testdata/usage-cases.json`. |
| `CancelPolicy.swift` | `CancelPolicy.cs` | Esc keeps recordings of 10 s or more, as `cancelled`, for 24 hours. |
| `AudioSink.swift` | `AudioSink.cs` | Pre-roll while the microphone is kept warm, handed over first when a dictation starts. |
| `TranscriptionLatency.swift` | `TranscriptionLatency.cs` | Fitted wait-vs-length line, shifted by the route's median residual, and the delay before the backup speech-to-text route is asked (only once the recording has ended). `TranscriptionPipeline` races the two routes with a task group on macOS, `Task.WhenAny` on Windows. |
| `LivePreviewText.swift` | `LivePreviewText.cs` | The end of the live preview on one line (grapheme-safe: `Character` / `StringInfo`). |
| `UpdateCheck.swift` | `UpdateCheck.cs` | GitHub release list → newest non-draft, non-prerelease zip named `OpenTypeless-<version>-<platform>.zip` for this platform, numeric version order, `sha256:` digest. |

## System integration — macOS API → Windows API

| Concern | macOS | Windows |
|---|---|---|
| App shell | `LSUIElement` menu-bar app | No main window; notification-area (tray) icon via `Shell_NotifyIcon`. Single instance (named mutex); launching again opens Home, like reopening from Finder. |
| Tray icon states | SF Symbols `waveform` / `waveform.circle.fill` / `ellipsis.circle` | Same three glyphs rasterised at runtime for the current DPI and taskbar theme (light/dark). |
| Menu-bar popover | `NSPopover` sized before showing | Borderless WinUI window anchored above the tray, sized to its content before it is shown, closes when it loses focus. |
| Global hotkey | `CGEventTap` (flagsChanged/keyDown/keyUp) | `WH_KEYBOARD_LL` hook on a dedicated thread with its own message loop (never blocked by UI). Same rules: modifier-only keys, combos swallowed, tap < 0.35 s → hands-free, Esc cancels, another *physical* key within 1 s cancels (injected keys are ignored, like the Logi Options+ case). Alt/Win hotkeys get a masking key so Windows doesn't open the menu bar / Start. |
| Default hotkey | Fn | **Right Ctrl** (Windows keyboards don't expose Fn to software). See the Hotkey section for presets. |
| Microphone choice | CoreAudio device list, by UID; `kAudioOutputUnitProperty_CurrentDevice` on the engine's input unit | WASAPI `EnumAudioEndpoints(eCapture)`, by endpoint ID (friendly name from the property store; root-enumerated drivers marked virtual); `GetDevice` when opening, default endpoint when the choice is missing. Default-device changes are only followed when no device is chosen. |
| Live preview | `SpeechAnalyzer` + `SpeechTranscriber` on the recorded samples | `Windows.Media.SpeechRecognition` continuous dictation (listens to the default microphone itself; needs the speech language pack and online speech recognition). |
| Recording | Input-only AUHAL on the chosen device → 16 kHz Int16, rebuilt on device or format change; switches to a voice-processing unit when a call leaves plain capture silent | WASAPI shared-mode capture with `AUTOCONVERTPCM` (system resampler → 16 kHz mono Int16), event-driven thread; rebuilt on default-device change or device loss so long dictations survive a headset connecting. |
| Keys | Keychain (one item) | Windows Credential Manager, one generic credential `OpenTypeless/credentials` holding all keys (JSON), legacy import not needed. `OPENROUTER_API_KEY` env override kept. |
| Preferences | `UserDefaults` | `%LOCALAPPDATA%\OpenTypeless\settings.json` with the same keys and defaults. |
| History | `~/Library/Application Support/OpenTypeless/Sessions/<id>/` | `%LOCALAPPDATA%\OpenTypeless\Sessions\<id>\` with the same `audio.wav` + `session.json` schema, including per-dictation `timing`. Recordings expire by the retention setting (none / 1 day / 7 days / 1 month / 1 year / forever); expired text stays among the newest 200; failed dictations are never expired; interrupted → failed & retryable. Expiry runs at launch, after each dictation, on a setting change and hourly (a `DispatcherQueueTimer`). |
| Focus probe | Accessibility API roles | UI Automation (`IUIAutomation`, run off the UI thread with a 0.5 s budget) + `GetGUIThreadInfo` caret: editable → paste & restore clipboard; clearly not editable (desktop, lists, buttons) → clipboard; unknown (browsers/Electron/CEF) → paste *and* keep text on clipboard. |
| Paste | `⌘V` via `CGEvent`; the text is a *promised* pasteboard item, so the app learns whether the target read it; snapshot/restore, `TransientType` | `Ctrl+V` via `SendInput`; the text is offered with **delayed rendering** (`SetClipboardData(CF_UNICODETEXT, NULL)`), and the tray window renders it on `WM_RENDERFORMAT`, which tells us the paste landed. Landed → the Win32 snapshot of every HGLOBAL format is restored; nothing read it within 1.2 s → the text stays on the clipboard as a plain copy and the HUD says "Copied". `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory=0` keep clipboard history and managers from reading (and recording) the temporary entry; restore only if the sequence number is unchanged. |
| Learning from corrections | `EditWatcher` reads the focused field's `AXValue` twice a second; `NSSpellChecker` for "ordinary English word" | `EditWatcher` reads the focused element through UI Automation (Value pattern, or the Text pattern's document range for editors like Word) on the thread pool with a 0.4 s budget; password fields skipped; same end conditions (focus left, field sent/cleared, next dictation, 2 minutes). The Windows Spell Checking API (`ISpellCheckerFactory`, en-US) answers "ordinary English word". |
| HUD | Non-activating click-through glass capsule | Topmost, non-activating, click-through tool window (`WS_EX_NOACTIVATE`/`TOOLWINDOW`), Acrylic backdrop kept "active" so it never greys out; bottom-centre of the monitor under the mouse. Same phases: recording (red dot, 18 level bars, timer), working (hopping dots over the processing bar; the compositor runs the hops, and the bar advances on `CompositionTarget.Rendering`), copied, learned ("Added to vocabulary: …"), error. |
| Sounds | `Tink` / `Pop` at 0.35 volume | Short soft chimes synthesised in-process (no bundled assets), played at the same low volume. |
| Permissions | Microphone (TCC), Accessibility | Microphone: read the privacy consent store (`ConsentStore\microphone` + `NonPackaged`), link to `ms-settings:privacy-microphone`. Accessibility: not required on Windows (row explains the one exception: apps running as administrator). |
| Updates | `Updater`: download to `~/Library/Caches/OpenTypeless/Updates`, check SHA-256, unpack with `ditto`, check bundle ID, version and signature (same certificate as the running copy unless it's ad hoc); a helper script swaps `/Applications/OpenTypeless.app` after the app quits and reopens it | `Updater`: download to `%LOCALAPPDATA%\OpenTypeless\Updates`, check SHA-256, unzip, check the version of `OpenTypeless.dll`; the *new* `OpenTypeless.exe --apply-update <pid> <folder>` waits for the old copy to exit, renames the install folder to `.old`, copies itself in (renaming `.old` back if that fails) and starts the app. Picks the x64 or ARM64 zip by process architecture. The paths don't change, so the login entry and Start menu shortcut keep working. |
| Launch at login | `SMAppService` | `HKCU\…\Run`; "needs approval" = disabled in Task Manager (`StartupApproved\Run`). Enabled once by default for installed (non-dev) builds. |
| Settings window | `NSSplitViewController` + Liquid Glass sidebar | `NavigationView` sidebar on a Mica window with a custom title bar. Same seven pages (Home first) and contents. |
| Home page | `HomePage.swift`, `UsageStore.swift` (SwiftUI `Grid` of stat tiles, heatmap in a `GeometryReader`) | `HomePage.cs`, `UsageStore.cs` (a two-by-two `Grid` of cards; the heatmap re-renders when its width fits a different number of weeks). Login launch: `--autostart` in the Run entry instead of the Apple event. |
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

- `windows/tests/TypelessCore.Tests`: xUnit tests, all Swift test cases ported (WAV, chunker, joiner, SSE, retry
  classification, timeout, prompt sanitising, reasoning config, mock-server pipeline with a 130 s recording whose every
  chunk fails once, permanent failures, silence skipping, multipart, custom endpoints, polish fallbacks, evaluation
  ledger, hedged clean-up against a per-model fake server, clean-up metrics, the correction learner), plus retry reuse
  of finished chunks, idle-timeout handling, a prompt-integrity check and a check that Windows' ICU provides pinyin.
- End to end against a local OpenAI-compatible mock server, with the app itself and a real WinForms text box as the
  target: hold-to-talk, tap for hands-free, Esc cancel, injected keys ignored while holding, a combination hotkey
  (Alt + Space, keystrokes swallowed), a 35 s dictation whose first chunk was transcribed during recording, a failure
  while the provider was down followed by Retry from the tray panel, paste with the clipboard restored afterwards, and
  recording a new shortcut on the Shortcut page.
- `OpenTypeless.Cli`: chunk analysis on an 86 s speech file (4 chunks, every cut at a true pause), the full pipeline
  with injected 503s, 44.1 kHz stereo input through the Media Foundation transcoder, and an evaluation dry run.
- UI: every page in Chinese and English (or one language with `--lang`), the tray panel and each HUD state via `--snapshot-ui`, and live on-screen captures.

## UI parity checklist

- **Popover**: header (tile, name, status pill), hero ("Hold [keys] and speak", tip), setup warnings with Fix, last failed dictation with Retry, 5 recent items (click to copy, check mark), footer (Settings, History, Quit).
- **General**: permissions, language (System or one of the nine UI languages), open at login (+ error / approval footer), restore clipboard, sounds, maximum recording (1–60 min).
- **Shortcut**: current keys (large key caps), record / cancel, presets, how it works, warnings.
- **Providers**: six expandable cards: usage tags, configured pill, API key (password box + Paste), Base URL (+ Reset), get-a-key link, Test connection.
- **Models**: STT provider / model (free text + browse, grouped by vendor when > 40) / spoken language; clean-up toggle / provider / model; backup model toggle / provider / model; missing-key banners.
- **Vocabulary & Style**: vocabulary editor, "Learn from my corrections" with the learned terms (heard as…, date, remove = never learn again), preferences editor.
- **History**: recording retention picker with the space used; list + detail (status, error, timing, cleaned + raw text, Copy, Re-transcribe, Show in Explorer, Delete).
- UI language switching instantly, Esc cancel, max-duration auto stop, < 0.4 s accidental tap discarded, "No speech detected", truncation / answer detection fallbacks.
