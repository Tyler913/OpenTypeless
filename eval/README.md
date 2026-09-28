# Clean-up evaluation

The clean-up prompt lives in `src/TypelessCore/Prompts.cs`, byte-identical to the macOS app's `Prompts.swift`, so results carry over between platforms. Don't tune it by feel: change it, run these sets, and compare.

## Test sets

| File | Purpose |
|---|---|
| `polish-cases.json` | Development set. Some cases resemble the prompt's own examples, so it can be over-fit. |
| `polish-holdout.json` | Held-out set: different wording and content. **Use this to decide whether a change is an improvement.** |

Each case has an `input` and optional checks:

- `mustContain`: every string must appear in the output.
- `mustNotContain`: none may appear (e.g. the value a self-correction replaced).
- `anyOf`: a list of groups; each group needs at least one match.

Every output is also checked for "looks like an answer" (much longer than the input) and truncation.

Automatic checks aren't the whole story: always read the outputs for long, real dictations too. That is where models differ most (reordering, dropped details, how much filler survives). Keep real dictations outside the repository.

## Running

```powershell
dotnet build src\OpenTypeless.Cli -p:Platform=x64
New-Item -ItemType Directory -Force $env:TEMP\opentypeless-eval | Out-Null
src\OpenTypeless.Cli\bin\x64\Debug\net10.0-windows10.0.22621.0\OpenTypeless.Cli.exe --eval-polish eval\polish-holdout.json `
    --models google/gemini-3.8-flash,qwen/qwen3.7-flash `
    --rounds 3 `
    --key-from-credential-store `
    --budget-ledger $env:TEMP\opentypeless-eval\budget.json --budget-usd 1 `
    --out $env:TEMP\opentypeless-eval\holdout.json
```

- **API key:** `--key-from-credential-store` (alias `--key-from-keychain`) uses the OpenRouter key saved by the app; otherwise set `OPENROUTER_API_KEY`. Only OpenRouter is supported.
- **Output location:** `--out` and `--budget-ledger` must be outside the repository, because outputs can contain your text.
- **Routing:** requests are identical to the app's, same body and same provider routing, so latency and reliability match real use. Add `--provider-tag <tag> --endpoint-catalog <file>` to benchmark a single upstream provider.
- **Budget:** every request is costed from OpenRouter's `usage.cost` (falling back to the generation API), recorded in the ledger, and checked against `--budget-usd` before it is sent. Use `--dry-run` to see the request count and a conservative cost bound without spending anything.
- **Prompt experiments:** `--prompt-file new-prompt.txt` replaces the built-in prompt for the run.
- **Rounds:** models are non-deterministic, so use `--rounds 3` before drawing conclusions. Models are interleaved per case and round.

Stderr shows a line per request (pass/fail, latency, running cost); the `--out` JSON has the full outputs, token usage and cost per attempt.
