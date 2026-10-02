#!/usr/bin/env python3
"""Dictate a minute of synthetic speech through the packaged app's own pipeline, against a local fake provider.

    run_dictation.py <OpenTypeless (macOS binary) | OpenTypeless.Cli.exe> <output dir>

The packaged binary's --transcribe-file --realtime mode feeds the recording at speaking speed, like the microphone
does, so chunking, the speculative tail, retries, joining and the streamed clean-up all run as they do while
dictating, in the release build that ships. No API key is spent and no audio leaves the runner.

It is not the whole app: there is no hotkey, HUD, microphone or paste here (see docs/TESTING-AUDIT.zh-CN.md).
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

import dictation_audio
import fake_provider

WORD_COUNT = 80
TIMEOUT_SECONDS = 240
# The apps never send a chunk longer than this (Chunker MaxSeconds is 28, plus the speech-bound padding).
MAX_CHUNK_SECONDS = 29


def settings(base_url):
    """What a user who picked OpenRouter everywhere, pointed at the fake provider, would have saved."""
    return {
        "sttProvider": "openrouter", "polishProvider": "openrouter", "polishBackupProvider": "openrouter",
        "polishEnabled": True, "polishBackupEnabled": True, "sttBackupEnabled": False, "language": "",
        "baseURLs": {"openrouter": base_url},
    }


def command(executable, audio, values, data_dir):
    arguments = [str(executable), "--transcribe-file", str(audio), "--realtime"]
    env = dict(os.environ, OPENROUTER_API_KEY=fake_provider.API_KEY, OPENTYPELESS_DATA_DIR=str(data_dir),
               OPENTYPELESS_SUPPORT_DIR=str(data_dir))
    if executable.suffix.lower() == ".exe":
        # Windows: settings.json in the data folder OPENTYPELESS_DATA_DIR points at.
        data_dir.mkdir(parents=True, exist_ok=True)
        (data_dir / "settings.json").write_text(json.dumps(values), encoding="utf-8")
    else:
        # macOS: UserDefaults' argument domain (-key value) overrides the saved preferences for this process only.
        for key, value in values.items():
            arguments += [f"-{key}", plist(value)]
    return arguments, env


def plist(value):
    if isinstance(value, bool):
        return "YES" if value else "NO"
    if isinstance(value, dict):
        return "{" + "".join(f'{json.dumps(k)} = {json.dumps(v)};' for k, v in value.items()) + "}"
    return json.dumps(value)


def section(stdout, title):
    match = re.search(rf"^===== {title} \(\d+ chars\) =====\n(.*?)\n\n", stdout, re.S | re.M)
    return match[1] if match else None


def compact(text):
    return re.sub(r"\s+", "", text or "")


def check(returncode, stdout, log, words):
    """Everything wrong with a run, judged from what the app printed and what the fake provider received."""
    errors = []
    if returncode != 0:
        errors.append(f"the app exited with {returncode}")
    expected = "".join(words)
    raw, cleaned = section(stdout, "Raw transcript"), section(stdout, "Cleaned up")
    if raw is None:
        errors.append("no raw transcript in the output")
    elif compact(raw) != expected:
        errors.append(f"raw transcript differs from what was said:\n  said: {' '.join(words)}\n  got:  {raw}")
    if cleaned is None:
        errors.append("no cleaned-up text in the output")
    elif raw is not None and cleaned != fake_provider.polished(raw):
        errors.append(f"cleaned-up text differs from what the provider streamed:\n"
                      f"  sent: {fake_provider.polished(raw)}\n  got:  {cleaned}")
    if unauthorized := [e["path"] for e in log if e["method"] == "POST" and not e.get("authorized")]:
        errors.append(f"requests without the API key: {unauthorized}")
    if bad := [e["error"] for e in log if "error" in e]:
        errors.append(f"malformed requests: {bad}")
    stt = [e for e in log if e["path"].endswith("/audio/transcriptions") and "seconds" in e]
    if not any(e.get("status") == 503 for e in stt):
        errors.append("the injected 503 never happened")
    if len([e for e in stt if e.get("status") == 200]) < 2:
        errors.append(f"expected the minute of audio in at least 2 chunks, got {len(stt)} transcription requests")
    if long := [round(e["seconds"], 1) for e in stt if e["seconds"] > MAX_CHUNK_SECONDS]:
        errors.append(f"chunks longer than {MAX_CHUNK_SECONDS} s: {long}")
    polish = [e for e in log if e["path"].endswith("/chat/completions") and "transcript" in e]
    if not polish:
        errors.append("the clean-up was never requested")
    elif raw is not None and any(e["transcript"] != raw for e in polish):
        errors.append("the clean-up was sent a different transcript from the one printed")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("executable", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    # Windows runners write to a pipe in the ANSI code page, which has no Chinese.
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    executable = args.executable.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    words = dictation_audio.script(WORD_COUNT)
    audio = output / "dictation.wav"
    seconds = dictation_audio.write(audio, words)

    with fake_provider.Server() as server:
        arguments, env = command(executable, audio, settings(server.base_url), output / "data")
        print(f"Dictating {seconds:.0f} s ({len(words)} words) through {executable.name}, provider {server.base_url}",
              flush=True)
        try:
            result = subprocess.run(arguments, env=env, capture_output=True, timeout=TIMEOUT_SECONDS,
                                    encoding="utf-8", errors="replace", check=False)
            returncode, stdout, stderr = result.returncode, result.stdout, result.stderr
        except subprocess.TimeoutExpired as timeout:
            returncode, stdout, stderr = "a timeout", timeout.stdout or "", timeout.stderr or ""
            stdout, stderr = (s.decode("utf-8", "replace") if isinstance(s, bytes) else s for s in (stdout, stderr))
        log = list(server.log)

    (output / "stdout.txt").write_text(stdout, encoding="utf-8")
    (output / "stderr.txt").write_text(stderr, encoding="utf-8")
    (output / "requests.json").write_text(json.dumps(log, ensure_ascii=False, indent=1), encoding="utf-8")
    print(stderr, file=sys.stderr)
    print(stdout)
    errors = check(returncode, stdout, log, words)
    stt = [e for e in log if "seconds" in e]
    report = "### Dictation through the packaged app (fake provider)\n\n"
    chunks = ", ".join(f"{entry['seconds']:.1f}" for entry in stt)
    report += (f"{seconds:.0f} s of synthetic speech fed at speaking speed; {len(stt)} transcription requests "
               f"(audio of {chunks} s, speculative tails included; the first answered 503); streamed clean-up.\n\n")
    report += "\n".join(f"- ❌ {e}" for e in errors) if errors else "✅ Transcript and clean-up exactly as spoken.\n"
    (output / "summary.md").write_text(report, encoding="utf-8")
    print(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as file:
            file.write(report + "\n")
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
