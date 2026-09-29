# Design

## Goal

Hold a key, speak for anywhere from two seconds to several minutes, and get the text you would have typed, inserted at the cursor. Long dictation has to be as reliable as short dictation. That is the main reason this project exists.

Out of scope: translation, rewriting selected text, voice Q&A, accounts and sync.

This document describes the macOS app; file names below are under `macos/Sources/`. The Windows app follows the same design with Windows APIs. [WINDOWS-PORT.md](WINDOWS-PORT.md) maps each file and system API to its Windows counterpart.

## Why other tools fail on long dictation

From reading several open-source voice-typing apps and the provider documentation:

1. **One request per recording.** The full recording is uploaded as a single speech-to-text request. OpenRouter documents that "upstream providers time out after 60 seconds per request" and recommends splitting long recordings. A longer client-side timeout doesn't help.
2. **Total timeouts on streaming calls.** One app gives the clean-up request a 30 s timeout that includes streaming the response body, so long outputs are killed mid-stream.
3. **All-or-nothing.** Any failure discards the recording.
4. **Connection drops under concurrency.** Real runs against OpenRouter showed `NSURLErrorNetworkConnectionLost` (-1005) when the server rejects a request mid-upload and shared connections are dropped. Treating it as retryable fixes it.

## Pipeline

```
mic ─► AVAudioEngine ─► 16 kHz mono PCM ─┬─► WAV file on disk (written continuously)
                                         └─► Chunker
                                               │ every 18–28 s: cut at the quietest 0.4 s window
                                               ▼
                                         STT queue (≤3 concurrent, per-chunk retries)
                                               │
release key ─► flush last chunk ─► join in order ─► clean-up LLM (streaming) ─► paste / clipboard
                                                          │ on failure
                                                          └─► insert raw transcript
```

### Chunker (`TypelessCore/Chunker.swift`)
Once pending audio reaches 28 s, it slides a 0.4 s window over the 18–28 s span and cuts at the centre of the lowest-energy window. There are no thresholds to tune, since the quietest spot always wins. On real speech, cut points land 22–70 dB below speech level.

### Transcription (`TypelessCore/TranscriptionPipeline.swift`)
- Chunks are transcribed as soon as they're cut, so after release only the tail remains.
- Silent chunks are skipped (STT models hallucinate "Thanks for watching!" on silence).
- Per-attempt timeout: `clamp(2 × chunk length + 15 s, 30…90 s)`.
- Retryable: network errors, timeouts, 408/409/425/429/5xx. Permanent: 400/401/402/403, missing keys.
- After the first pass, chunks that failed with a *transient* error get one more full round.
- Transcripts are joined in order, with no space between CJK fragments and a space between Latin ones.

### Clean-up (`TypelessCore/PolishClient.swift`, `HedgedPolish.swift`, `Prompts.swift`)
- Streaming chat completion with an **idle** timeout (25 s without data) plus a 240 s runaway guard.
- On OpenRouter: reasoning is disabled or set to its minimum based on `/models` metadata, and providers are sorted by latency, with those under 50 tokens/s at the median moved to the back.
- **Hedged requests.** An optional backup model (by default a fast model from another vendor) starts when the main model has produced no token after a fixed 0.8 s, or fails before that. Whichever streams its first token first is kept and the other request is cancelled, so a slow upstream costs about 0.8 s plus the backup's own first-token time instead of the whole wait, and the extra spend is limited to that slow tail. The delay is a constant rather than learned at runtime because flash-class first-token latency is stable (see [eval/README.md](../eval/README.md)).
- Each dictation records where the wait went (transcription tail, clean-up first token and total, which model answered) in `session.json`, shown in History.
- If a server rejects an optional parameter (e.g. `temperature` on reasoning models), the request is retried with the bare minimum.
- Safety nets: echoed wrappers are stripped, and an output far longer than the input (the model *answered* the prompt instead of rewriting it) falls back to the raw transcript.
- The prompt asks for **minimal edits**: only self-corrections, speech noise, recognition errors, punctuation and explicit enumerations are touched; wording, order, tone, pronouns and every word's language are kept. Mixed Chinese/English is preserved word by word (an earlier, more ambitious prompt translated about a third of the English words in real mixed dictations). A one-line reminder after the transcript restates the two rules models drift from most.
- The prompt uses few-shot examples, which proved far more effective than extra rules. It is tuned with `--eval-polish` against a development set, a held-out set and real dictations.

### Delivery (`OpenTypeless/FocusProbe.swift`, `TextInserter.swift`)
The Accessibility API is asked what has focus:
- **Editable** (text field, text view, editable web content): paste with ⌘V, then restore the previous clipboard.
- **Clearly not editable** (desktop, lists, buttons in native apps): copy to the clipboard.
- **Unknown** (browsers and Electron apps that don't expose their page tree, e.g. Firefox): paste *and* leave the text on the clipboard.

### Learning from corrections (`OpenTypeless/EditWatcher.swift`, `TypelessCore/CorrectionLearner.swift`)
Like Wispr Flow and Typeless, fixes the user makes to pasted text grow the vocabulary.
- After a paste into a field whose Accessibility value contains the dictated text, that field is read twice a second, locally. Secure fields and very large fields are skipped.
- The watch ends when focus leaves the field, the field is sent or cleared (the dictated text can no longer be located), a new dictation starts, or after two minutes. Only that final state is compared, so half-finished edits (a known failure mode of eager learners) are never learned.
- The dictated span is diffed against its edited version token by token (Latin words, single CJK characters). A fix inside Chinese text is then widened to the word it belongs to, using the system word segmenter on the edited text (`NLTokenizer` on macOS, `Windows.Data.Text.WordsSegmenter` on Windows), so correcting one wrong character of 罗级鼠标 learns 罗技, heard as 罗级. Names the segmenter doesn't know come out as single characters (千|问, 飞|书), so a fix that is still one character takes in the neighbouring one-character words, up to 4 characters; a fixed particle (的/地/得…) is never widened.
- A replacement is learned only if it sounds alike, keeps the same digits, isn't a first-letter capitalisation, isn't one ordinary English word swapped for another (system spell checker), and the whole edit isn't a rewrite (similarity ≥ 0.5, ≤ 5 changes). "Sounds alike" is either spelling (edit distance ≤ 0.5 on a pinyin/Latin key, so 逻辑 → 罗技 and "TypeList" → "Typeless" qualify but "Mike" → "Sarah" doesn't) or sound: each word, Chinese as pinyin, reduced to its consonant sounds with a simplified Metaphone, within a third of each other. The sound key catches English spelt differently ("Versel" → "Vercel", "Cooper Netties" → "Kubernetes") and English terms heard as Chinese (克劳德 → Claude, 杰森 → JSON, 瑞艾克特 → React).
- Every paste is recorded in `learning-log.jsonl` in the data folder: the app, whether its field could be read (and if not, why: no focused field, text unreadable, dictated text not found, with whether it would match ignoring spaces), why the watch ended, and each replacement with the reason it wasn't learned. Local only, trimmed to the newest half past 512 KB.
- New terms are appended to the vocabulary; the misheard forms go into the clean-up prompt as hints. Removing a learned term blocks it from being learned again.

### Backup speech-to-text (`TypelessCore/TranscriptionPipeline.swift`, `TranscriptionLatency.swift`)
Optional, off by default. Each chunk goes to the main route first. When it is late, or the main route fails, the backup route is asked too; the first answer wins and the other request is cancelled (like the clean-up hedge).
- **When a chunk is late.** Fitted on 128 timed dictations (`microsoft/mai-transcribe-2` through OpenRouter, 1–27 s chunks): the wait barely grows with length, about 0.39 s + 0.02 s per second of audio (Theil–Sen line through the chunks left after dropping those more than three MADs above it; residual MAD 0.35 s). Most answers land on the line; a slower mode around 1.6–2.3 s and the odd 4.7–10.6 s answer don't. Only the last chunk is usually waited on and speech-to-text is cheap next to the wait, so a chunk is late 0.2 s past the line (0.63 s for a 2 s chunk, 1.15 s for 28 s), clamped to 0.5–25 s; in that data about 40% of last chunks would ask the backup. A route that is slower or faster across the board moves the line by the median of its last 20 residuals once three chunks are timed; the median ignores the odd very late answer. (The previous rule, twice the median wait ÷ audio-seconds ratio plus a second, carried a short chunk's fixed overhead over to long chunks: replayed over the same dictations, chunks of 15 s or more waited 3.9–13.7 s before the backup was asked, against 0.9–1.2 s now.)
- **Only once the recording has ended.** A chunk sent while the user is still talking holds nothing up, so it isn't hedged for being late (only for failing); when the recording ends, a chunk still out that is already past its deadline is asked of the backup at once, and the others at their deadline. The backup is paid for mainly on the last chunk, the one the text waits for.
- Backup answers are priced with the backup's model, and History's timing line counts them.

### Microphone (`OpenTypeless/AudioRecorder.swift`, `Microphones.swift`, `TypelessCore/AudioSink.swift`)
- **Choosing the input.** CoreAudio lists the inputs (a device's UID is remembered, since device IDs change); the chosen one is set on the engine's input unit (`kAudioOutputUnitProperty_CurrentDevice`) before the engine starts, and a device that isn't connected falls back to the system default. Virtual and aggregate devices are labelled. General → Microphone has a Test button with a live level meter that listens only while testing.
- **Keeping it ready.** Samples always pass through `AudioSink`. With **Keep microphone ready** on, the recorder keeps running between dictations and the sink holds the last 0.4 s; when a dictation starts, that pre-roll is handed over first and the rest follows in order, under one lock. So recording starts with no device start-up delay (Bluetooth headsets take a noticeable moment) and includes the start of a word spoken as the key goes down. The pre-roll doesn't count toward the accidental-tap and Esc thresholds. Off by default, since the microphone indicator stays on and Bluetooth headphones switch to call mode.

### Esc (`TypelessCore/CancelPolicy.swift`)
A recording under 10 s is discarded as before. From 10 s on, Esc stops recording and inserts nothing, but the rest of the recording is transcribed in the background (no clean-up), and the dictation is kept in History with status `cancelled`, where it can be copied or re-transcribed. Esc while processing keeps what was transcribed the same way. Cancelled dictations are removed 24 hours after they were made, whatever the retention setting.

### Live preview (`OpenTypeless/LivePreview.swift`)
Optional (beta). While recording, the words recognised so far appear above the capsule. macOS runs `SpeechAnalyzer` with a `SpeechTranscriber` reporting volatile results, fed the same 16 kHz samples being recorded (converted to the analyzer's format); a language used for the first time has its model downloaded in the background and previews from the next dictation. It is never used for the result: the inserted text still comes from the speech-to-text provider. `LivePreviewText` keeps the last 60 characters on one line without splitting a character.

### Hotkey (`OpenTypeless/Hotkey.swift`)
- A session-level `CGEventTap` (default tap, which only needs Accessibility).
- A modifier-only hotkey is tracked through `flagsChanged`. Combinations are matched on `keyDown` and swallowed.
- A short tap (< 0.35 s) switches to hands-free mode.
- Another key pressed *with the hotkey's modifier flag* within the first second is treated as a key combo (Fn+F1 …) and cancels the dictation. Keys injected by other apps at the same moment, such as Logi Options+ when a mouse button is remapped to Fn, don't carry the flag and are ignored.

## Persistence

`~/Library/Application Support/OpenTypeless/Sessions/<timestamp>/`:
- `audio.wav`, written while recording.
- `session.json`: status, raw text, cleaned text, and per-chunk transcripts, so a retry only re-sends failed chunks.

A session found in `recording`/`processing` state at launch is marked failed-but-retryable.

Retention is a user setting: don't keep recordings, 1 day, 7 days, 1 month (default), 1 year, or forever. Recordings are about 1.9 MB per minute of 16 kHz WAV; transcripts are a few KB. When a finished dictation expires, its `audio.wav` is deleted, and the whole folder goes too once it is also outside the newest 200 dictations. Failed dictations are never expired, so they can always be retried. Expiry runs at launch, after every dictation, when the setting changes, and hourly.

## Home page and usage accounting

The Home page (`OpenTypeless/HomePage.swift`, the first sidebar page) shows words dictated, time saved, speaking speed, spend and a daily activity heatmap. The numbers come from `TypelessCore/UsageLedger.swift` and `Usage.swift`, which the Windows app ports line by line; `testdata/usage-cases.json` holds cases both test suites read, so the two apps count and price the same way.

- **Words.** Every Han character and Japanese kana is a word; every run of letters or digits in other scripts is one word, with `'`, `-`, `.`, `,` joining a word only when a letter or digit follows ("don't", "e-mail", "3.5").
- **Time saved** = the time typing the same words would take at the user's typing speed (100 wpm by default, set under General) − the time spent recording (pauses included), never below zero. **Speaking speed** = words ÷ recording minutes.
- **Usage per request.** OpenRouter reports `usage` (tokens, audio seconds and the billed `cost` in USD) with every transcription and in the last chunk of every streamed completion. OpenAI-compatible servers are asked for stream usage with `stream_options.include_usage` (dropped like any other optional parameter if the server rejects it). When a server reports nothing, tokens are estimated from the text (one per CJK character, one per four other characters) and transcription is measured by the chunk's audio length.
- **Cost.** OpenRouter requests count the `cost` OpenRouter reported. Other providers are priced from what the user entered under Models: USD per million input/output tokens for clean-up, USD per minute of audio (or per token when the server counts tokens) for speech-to-text. Requests that can't be priced are counted, and the Home page says how many. Every successful speech-to-text request counts (a retried chunk is billed again), plus the clean-up answer that was kept; the losing request of a hedged clean-up is cancelled after its first tokens and isn't counted.
- **Price list.** `PriceCatalog` reads OpenRouter's public `/models` and `/models?output_modalities=transcription` lists (USD per token, stored per million; negative "varies" prices skipped), cached in `openrouter-prices.json` and refreshed when older than six hours (checked at launch, hourly, and when Home or Models opens). The Models page shows each OpenRouter model's current price.
- **Ledger.** Per-day totals (words, dictations, recording seconds, speech-to-text cost, clean-up cost, unpriced requests) live in `usage.json`, keyed by the local date, so they outlive History's retention and deletions. A dictation's words are added once, when it first finishes (`counted` in `session.json`), dated by when it was recorded; costs are added whenever requests are made, dated today. On the first launch of a version with the ledger it is filled from the dictations History still has.
- **Heatmap.** As many weeks as fit the page width (up to 53), starting on the locale's first weekday; empty days are grey and busy days take one of four shades of the accent colour by quartile of the days shown. Streaks count consecutive days with a dictation, still alive until today ends. Hovering a day shows its date, dictations and words in a bubble at once (system tooltips were too slow to notice on squares this small). On the page, Spend takes a third of the width next to the heatmap's two thirds.
- **Launch.** Opening the app by hand (or again from Finder / the Start menu) shows Home. A launch at login shows nothing unless **Show Home when opened at login** is on (off by default). macOS recognises a login launch from the open-application Apple event, or a launch within two minutes of the Dock starting; Windows from the `--autostart` argument of its Run entry.

## Updates

`TypelessCore/UpdateCheck.swift` reads the repository's GitHub release list (no sign-in; the unauthenticated limit of 60 requests an hour is far above one check a day) and picks the newest published, non-prerelease release that carries this platform's zip. Releases are matched by the zip's file name, `OpenTypeless-<version>-macOS-arm64.zip`, not by tag, so a release may hold one app or both.

`OpenTypeless/Updater.swift` checks 10 s after launch and then whenever 24 h have passed, if **Check automatically** is on (the default); **Check now** is always available. A newer version is downloaded in the background and installed only when the user clicks **Restart to update**, never during a dictation (the click is then held until the dictation finishes). A skipped version isn't offered by automatic checks.

Before anything is replaced:
- The zip's SHA-256 must match the `digest` GitHub publishes for every release file. Without one, the update is offered as a download link only.
- The unpacked app must have our bundle ID and the expected version, and a valid signature (`SecStaticCodeCheckValidity`, strict, nested code).
- If the running copy is signed with a certificate, the new one must satisfy the running copy's designated requirement, i.e. be signed with the same certificate. An ad-hoc copy has nothing to compare against (its requirement is a hash of that exact build).

Install: a helper script started by the app waits for it to quit, moves the old bundle aside, moves the new one into place (putting the old one back if that fails), and opens it. The app notes the version it was installing; if the relaunched app is still older, it says the install failed. When the app can't replace itself (a folder the user can't write to, or macOS running it from an App Translocation path), the update is shown with a link to the release page instead.

Releases are signed in CI with a stable self-signed certificate (`scripts/create-release-cert.sh`, stored as repository secrets), because macOS ties Accessibility and Microphone permission to the signature: with ad-hoc signing every update would ask for them again. Files downloaded by the app itself carry no quarantine flag, so Gatekeeper only warns on the very first manual install.

## UI

- **Menu-bar app** (`LSUIElement`) with a popover. The popover is sized from its SwiftUI content *before* being shown; letting it resize while on screen made it re-anchor off-screen.
- **Settings window:** an `NSSplitViewController` whose sidebar item gets the system Liquid Glass sidebar, with SwiftUI pages. Home comes first and is where the window opens.
- **HUD:** a non-activating, click-through glass capsule that shows recording (level + timer), working, copied, or a short error.
- **Localization:** inline `L("中文", "English")` strings, switchable at runtime. The other UI languages (ja, ko, es, pt-BR, fr, de, ru) come from `i18n/strings.json`, keyed by the English text with interpolations numbered `{0}`, `{1}`…, embedded into both apps at build time; a missing translation falls back to English, and `python3 i18n/check.py` (run by CI) finds missing, unused or malformed entries.
