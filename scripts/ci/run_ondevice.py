#!/usr/bin/env python3
"""On-device speech-to-text through the packaged Windows CLI, with the real model, on the runner's own CPU.

    run_ondevice.py <OpenTypeless.Cli.exe> <data dir> <output dir>

<data dir> is the app's data folder (OPENTYPELESS_DATA_DIR); CI caches its Models and samples folders between runs,
so the 240 MB model is downloaded once, not on every push. Then:

1. `--download-model`: the app's own downloader fetches the model from its pinned Hugging Face commit (or resumes it),
   checks each file's SHA-256 and loads it with the native sherpa-onnx runtime of this architecture.
2. `--transcribe-file` on the model's own English and Chinese samples (pinned by commit and SHA-256), the English one
   fed at speaking speed like the microphone, with `speechEngine` set to `onDevice` and no API key anywhere.
3. The same with the spoken language set to French, which the model doesn't know: it must fail with that message.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.request

REPOSITORY = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17"
REVISION = "2365baeacb507f821a0c8120fcee3d484dba7a07"
SAMPLES = {
    # name: (SHA-256, words the transcript must have, lower-cased, spaces and punctuation ignored)
    "en.wav": ("eb1eb008904465b74c304aad8342e8c7d3c6e61ffe9f66adcaca9cf0f76a93f4", ["tribalchieftain", "presentedhim"]),
    "zh.wav": ("b77f1794fe374a0ba1ee1dc458bfaf9349496cbbfc32780c50ba3c5a7ad8e373", ["开饭时间", "下午"]),
}
DOWNLOAD_TIMEOUT = 300
TRANSCRIBE_TIMEOUT = 120


def sample(folder, name, sha256):
    """The model repository's test recording, downloaded once and checked."""
    path = folder / name
    if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != sha256:
        folder.mkdir(parents=True, exist_ok=True)
        url = f"https://huggingface.co/{REPOSITORY}/resolve/{REVISION}/test_wavs/{name}"
        with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "OpenTypeless CI"}), timeout=60) as response:
            data = response.read()
        if hashlib.sha256(data).hexdigest() != sha256:
            raise SystemExit(f"{name}: checksum mismatch")
        path.write_bytes(data)
    return path


def settings(data_dir, language):
    data_dir.mkdir(parents=True, exist_ok=True)
    values = {"speechEngine": "onDevice", "polishEnabled": False, "language": language, "appLanguage": "en"}
    (data_dir / "settings.json").write_text(json.dumps(values), encoding="utf-8")


def run(arguments, data_dir, timeout):
    env = dict(os.environ, OPENTYPELESS_DATA_DIR=str(data_dir))
    # No key of any kind: on-device needs none.
    for name in list(env):
        if name.endswith("_API_KEY"):
            del env[name]
    started = time.monotonic()
    try:
        result = subprocess.run(arguments, env=env, capture_output=True, timeout=timeout, encoding="utf-8",
                                errors="replace", check=False)
        code, out, err = result.returncode, result.stdout, result.stderr
    except subprocess.TimeoutExpired as expired:
        code = "a timeout"
        out, err = ((s.decode("utf-8", "replace") if isinstance(s, bytes) else s or "") for s in (expired.stdout, expired.stderr))
    return code, out, err, time.monotonic() - started


def raw_transcript(stdout):
    match = re.search(r"^===== Raw transcript \(\d+ chars\) =====\n(.*?)\n\n", stdout, re.S | re.M)
    return match[1] if match else None


def compact(text):
    return re.sub(r"[\s\W_]+", "", text or "").lower()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("cli", type=Path)
    parser.add_argument("data", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    cli = args.cli.resolve(strict=True)
    data = args.data.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    errors, lines, log = [], [], []

    settings(data, "")
    # The download resumes where it stopped, so a dropped connection gets two more tries.
    for attempt in range(1, 4):
        code, out, err, seconds = run([str(cli), "--download-model"], data, DOWNLOAD_TIMEOUT)
        log.append(f"$ --download-model (attempt {attempt}, {seconds:.1f} s, exit {code})\n{err}{out}")
        print(err, out, sep="\n", flush=True)
        if code == 0:
            break
    if code != 0:
        errors.append(f"--download-model exited with {code}")
    else:
        lines.append(f"model downloaded or found, checked and loaded in {seconds:.1f} s")
        samples = data / "samples"
        for name, (sha256, expected) in SAMPLES.items():
            path = sample(samples, name, sha256)
            arguments = [str(cli), "--transcribe-file", str(path), "--no-polish"]
            if name == "en.wav":
                arguments.append("--realtime")
            code, out, err, seconds = run(arguments, data, TRANSCRIBE_TIMEOUT)
            log.append(f"$ --transcribe-file {name} ({seconds:.1f} s, exit {code})\n{err}{out}")
            print(err, out, sep="\n", flush=True)
            text = raw_transcript(out)
            if code != 0 or text is None:
                errors.append(f"{name}: exit {code}, no transcript")
                continue
            missing = [word for word in expected if word not in compact(text)]
            if missing:
                errors.append(f"{name}: transcript {text!r} lacks {missing}")
            if "STT: on-device" not in err:
                errors.append(f"{name}: not transcribed on-device")
            took = re.search(r"Transcribed: ([\d.]+) s after release", err)
            lines.append(f"{name}: {text!r}" + (f", {took[1]} s after the end of the audio" if took else ""))

        settings(data, "fr")
        code, out, err, seconds = run([str(cli), "--transcribe-file", str(samples / "en.wav"), "--no-polish"], data, TRANSCRIBE_TIMEOUT)
        log.append(f"$ --transcribe-file en.wav, language fr ({seconds:.1f} s, exit {code})\n{err}{out}")
        if code != 2 or "On-device recognition doesn't support this language." not in err:
            errors.append(f"a language the model doesn't know: exit {code}, {err.strip()[-200:]!r}")
        else:
            lines.append("French, which the model doesn't know, fails with the message")
        settings(data, "")

    (output / "ondevice.log").write_text("\n\n".join(log), encoding="utf-8")
    report = "### On-device speech-to-text through the packaged CLI (real model)\n\n"
    report += "".join(f"- {line}\n" for line in lines) + "\n"
    report += "\n".join(f"- ❌ {e}" for e in errors) if errors else "✅ Downloaded, checked, loaded and transcribed on this runner's CPU.\n"
    (output / "summary.md").write_text(report, encoding="utf-8")
    print(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as file:
            file.write(report + "\n")
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
