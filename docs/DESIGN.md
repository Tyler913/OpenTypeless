# Design

## Goal

Hold a key, speak for anywhere from two seconds to several minutes, and get the text you would have typed, inserted at the cursor. Long dictation has to be as reliable as short dictation. That is the main reason this project exists.

Out of scope: translation, rewriting selected text, voice Q&A, cross-platform support, accounts and sync.

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

## UI

- **Menu-bar app** (`LSUIElement`) with a popover. The popover is sized from its SwiftUI content *before* being shown; letting it resize while on screen made it re-anchor off-screen.
- **Settings window:** an `NSSplitViewController` whose sidebar item gets the system Liquid Glass sidebar, with SwiftUI pages.
- **HUD:** a non-activating, click-through glass capsule that shows recording (level + timer), working, copied, or a short error.
- **Localization:** inline `L("中文", "English")` strings, switchable at runtime without string tables.
