# Clean-up evaluation

The clean-up prompt lives in `macos/Sources/TypelessCore/Prompts.swift` and `windows/src/TypelessCore/Prompts.cs`. The two are byte-identical, so these sets and their results apply to both apps: change the prompt in both places, run the sets on either platform, and compare. Don't tune it by feel.

Besides the prompt itself, a clean-up's system prompt can carry, after it: two examples in the spoken language (Japanese, Korean, Spanish, Portuguese, French, German, Russian; Chinese and English are in the prompt), the layout of the app it's dictated into (chat, light structure or email; natural is the prompt's own), and for one part of a long dictation, the ends of the parts around it. `testdata/prompt-variant-cases.json` holds the SHA-256 of every fixed piece, which both apps' tests check, so a change to one app's copy fails until the other matches.

## Test sets

| File | Purpose |
|---|---|
| `polish-cases.json` | Development set. Some cases resemble the prompt's own examples, so it can be over-fit. |
| `polish-holdout.json` | Held-out set: different wording and content, in all nine UI languages (at least five cases each), with the same input under different layouts where the layout should change the result. **Use this to decide whether a change is an improvement.** |
| `translate-cases.json` | Translation mode: the translate prompt (`Prompts.baseTranslatePrompt` / `Prompts.BaseTranslatePrompt`, also byte-identical, checked by `testdata/translate-guard-cases.json`). Corrections resolved, names, numbers and terms kept, requests not answered, into each listed language and one typed in. |
| `polish-context.json` | Text around the cursor (**Use text around the cursor to spell names**): names and terms to spell from it, and context that tries to mislead (instructions inside it, a thread in another language, the answer to a dictated question, a greeting and sign-off to copy). Run it after changing the context note in `Prompts`. |

Each case has an `input` and optional checks:

- `mustContain`: every string must appear in the output.
- `mustNotContain`: none may appear (e.g. the value a self-correction replaced).
- `anyOf`: a list of groups; each group needs at least one match.
- `translateTo`: translate into this language (a listed language's code such as `en` or `zh-Hans`, or a name such as `Italiano`) with the translate prompt, as translation mode does, instead of cleaning up. The output is then checked against the translation guards (`Prompts.translationFallback`: cut off, far longer or shorter than a translation gets, not mostly in the target's script) instead of the clean-up's, and English kept and similarity aren't reported.
- `context`: text around the cursor, sent after the transcript exactly as the app sends it.
- `layout`: the layout of the app it's dictated into (`natural`, the default, `chat`, `structured` or `email`), as an app profile or the app's built-in layout would set it.
- `language`: the speech-to-text language set (a code such as `ja`); without it the language is guessed from the transcript, as for most speakers, which picks the examples sent with it.
- `paragraphs`, `listItems`: `[least, most]` paragraphs (blocks between blank lines with text that isn't a list item) and list items (lines starting `1. `, `- `, `• ` or `* `) the output may have. `[0, 0]` list items: no list.
- `length`: `[least, most]` of the output's length (letters, without spaces or punctuation) as a share of the input's, once the input's fillers (嗯, um, えーと, ähm…) and every `mustNotContain` string are left out of it. `[0.8, 1.2]` suits a case where the clean-up should only remove fillers and mend.

Every output is also checked for stock phrases of written-up text that the speaker didn't say (值得一提的是, 综合来看, "It's worth noting", "Overall,", "Furthermore", and their like in each UI language; `PolishChecks.stockPhrases`). The checks are in TypelessCore (`PolishCase`), shared by both apps' command lines, and the apps' tests make sure each case accepts its own heaviest legitimate clean-up from `testdata/polish-guard-cases.json`, and that a case named `h-<language>-…` is told apart as that language.

Every output is also checked for truncation and against the apps' own guards (`Prompts.fallback`), which insert the transcript as spoken instead of an output that is much longer than the input (an answer), keeps under 55% of the words of an input of 150 words or more (a summary), or has little in common with the input (an answer, translation or refusal), or, with a `context`, took text over from it beyond the names it was there to spell. `testdata/polish-guard-cases.json` holds the heaviest legitimate clean-up of every case here, which the guards must let through. Two metrics are reported per model:

- **English kept**: the share of English words in a mostly-Chinese input that survive in the output (fillers and `mustNotContain` words excluded). Translating "dark mode" to 深色模式 lowers it; recognition fixes such as "swift UI" → SwiftUI don't.
- **Similarity**: 1 − normalised character edit distance between input and output, ignoring spacing and punctuation. Lower means heavier rewriting. It should drop for cases full of fillers and corrections and stay high otherwise.

Automatic checks aren't the whole story: always read the outputs for long, real dictations too. That is where models differ most (reordering, dropped details, how much filler survives). Keep real dictations outside the repository.

## Running

Run from the repository root.

macOS:

```bash
swift build --package-path macos
mkdir -p /tmp/opentypeless-eval
macos/.build/debug/OpenTypeless --eval-polish eval/polish-holdout.json \
    --models google/gemini-3.8-flash,qwen/qwen3.7-flash \
    --rounds 3 \
    --key-from-keychain \
    --budget-ledger /tmp/opentypeless-eval/budget.json --budget-usd 1 \
    --out /tmp/opentypeless-eval/holdout.json
```

Windows:

```powershell
dotnet build windows\src\OpenTypeless.Cli -p:Platform=x64
New-Item -ItemType Directory -Force $env:TEMP\opentypeless-eval | Out-Null
windows\src\OpenTypeless.Cli\bin\x64\Debug\net10.0-windows10.0.22621.0\OpenTypeless.Cli.exe --eval-polish eval\polish-holdout.json `
    --models google/gemini-3.8-flash,qwen/qwen3.7-flash `
    --rounds 3 `
    --key-from-credential-store `
    --budget-ledger $env:TEMP\opentypeless-eval\budget.json --budget-usd 1 `
    --out $env:TEMP\opentypeless-eval\holdout.json
```

- **API key:** `--key-from-keychain` (on Windows also `--key-from-credential-store`) uses the OpenRouter key saved by the app; otherwise set `OPENROUTER_API_KEY`. Only OpenRouter is supported.
- **Output location:** `--out` and `--budget-ledger` must be outside the repository, because outputs can contain your text.
- **Routing:** requests are identical to the app's, same body and same provider routing, so latency and reliability match real use. Add `--provider-tag <tag> --endpoint-catalog <file>` to benchmark a single upstream provider.
- **Budget:** every request is costed from OpenRouter's `usage.cost` (falling back to the generation API), recorded in the ledger, and checked against `--budget-usd` before it is sent. Use `--dry-run` to see the request count and a conservative cost bound without spending anything.
- **Prompt experiments:** `--prompt-file new-prompt.txt` replaces the built-in system prompt for the run, all of it: a case's `layout` and language examples aren't added to it.
- **Routing experiments:** `--provider-routing '{"sort":"latency"}'` replaces OpenRouter's `provider` object for the run (in PowerShell, escape the inner quotes: `'{\"sort\":\"latency\"}'`).
- **Latency:** each attempt records time to first token and total time; the summary prints p50/p90 for both.
- **Rounds:** models are non-deterministic, so use `--rounds 3` before drawing conclusions. Models are interleaved per case and round.

Stderr shows a line per request (pass/fail, latency, running cost); the `--out` JSON has the full outputs, token usage and cost per attempt.

## Does it read better?

The checks say whether a clean-up kept what it must and dropped what it must, not whether it reads like what the speaker would have typed. For that, compare two runs blind: run the holdout set with the old prompt (`--prompt-file`) and with the new one, on the same models and rounds, then

```bash
python3 eval/blind_compare.py pair eval/polish-holdout.json /tmp/opentypeless-eval/old.json /tmp/opentypeless-eval/new.json \
    --out /tmp/opentypeless-eval/blind.csv --key /tmp/opentypeless-eval/key.json
```

writes each pair of outputs in a shuffled order. Fill in the `vote` column (1, 2, or `=` for no difference) by hand, or have another model judge it (give it the CSV, never the key), then `python3 eval/blind_compare.py score /tmp/opentypeless-eval/blind.csv /tmp/opentypeless-eval/key.json` prints the new run's win rate, overall and per model. Only the win rate counts.

The best set for this is your own: 30–50 real dictations from History whose clean-up you didn't like, each with the version you wanted. Keep it outside the repository, like any real dictation, and run it as you would the holdout set.

## Reference numbers (Sep 2026)

These were measured before the Nov 2026 prompt change (mending within a sentence, tone particles, layouts, examples per language) and the holdout set's growth from 23 to 73 cases; run the holdout set again with at least two models before relying on them.

Measured with the macOS app on the holdout set, 2 rounds, current prompt, requests sent from the client exactly as the app sends them (the Windows app sends the same requests). First-token and total times are client-side.

| Model | Checks passed | First token p50 / p90 | Total p50 / p90 | Cost per request |
|---|---|---|---|---|
| google/gemini-3.1-flash-lite | 46/46 | 0.41 / 0.49 s | 0.43 / 0.50 s | $0.00049 |
| google/gemini-3.5-flash-lite | 41/46 | 0.46 / 0.53 s | 0.48 / 0.58 s | $0.00061 |
| deepseek/deepseek-v4.1-flash | 42/46 | 0.20 / 2.68 s¹ | 0.25 / 2.69 s | $0.00009 |
| deepseek/deepseek-v4-flash | 44/46 | 0.87 / 4.86 s | 1.03 / 5.08 s | $0.00008 |
| qwen/qwen3.7-flash | 44/46 | 0.81 / 1.27 s | 1.03 / 1.53 s | $0.00002 |
| qwen/qwen3.8-flash | 42/46 | 1.16 / 1.67 s | 1.46 / 1.90 s | $0.00005 |

¹ A temporary slowdown at its provider; two reruns an hour later had p90 0.25–0.27 s.

gemini-3.1-flash-lite, the fastest of these, has its first token at 0.41 s at the median and 0.49 s at p90, so a first token still missing at 0.55 s is already in the slow tail. That is where the fixed 0.55 s hedge delay (`HedgedPolish.defaultHedgeDelay` in Swift, `HedgedPolish.DefaultHedgeDelay` in C#) comes from; before, it was 0.8 s, from the 0.85 s mean first-token time across these models.

Prompt change, gemini-3.1-flash-lite: the minimal-edit prompt raised English kept on 63 real mixed-language dictations from 0.65 to 0.96, and similarity from 0.54 to 0.82. Holdout checks went from 58/69 to 46/46, dev checks from 44/54 to 36/36.
