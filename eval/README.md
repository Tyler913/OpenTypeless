# Clean-up evaluation

The clean-up prompt lives in `macos/Sources/TypelessCore/Prompts.swift` and `windows/src/TypelessCore/Prompts.cs`. The two are byte-identical, so these sets and their results apply to both apps: change the prompt in both places, run the sets on either platform, and compare. Don't tune it by feel.

## Test sets

| File | Purpose |
|---|---|
| `polish-cases.json` | Development set. Some cases resemble the prompt's own examples, so it can be over-fit. |
| `polish-holdout.json` | Held-out set: different wording and content. **Use this to decide whether a change is an improvement.** |

Each case has an `input` and optional checks:

- `mustContain`: every string must appear in the output.
- `mustNotContain`: none may appear (e.g. the value a self-correction replaced).
- `anyOf`: a list of groups; each group needs at least one match.

Every output is also checked for "looks like an answer" (much longer than the input) and truncation, and two metrics are reported per model:

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
- **Prompt experiments:** `--prompt-file new-prompt.txt` replaces the built-in prompt for the run.
- **Routing experiments:** `--provider-routing '{"sort":"latency"}'` replaces OpenRouter's `provider` object for the run (in PowerShell, escape the inner quotes: `'{\"sort\":\"latency\"}'`).
- **Latency:** each attempt records time to first token and total time; the summary prints p50/p90 for both.
- **Rounds:** models are non-deterministic, so use `--rounds 3` before drawing conclusions. Models are interleaved per case and round.

Stderr shows a line per request (pass/fail, latency, running cost); the `--out` JSON has the full outputs, token usage and cost per attempt.

## Reference numbers (Sep 2026)

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
