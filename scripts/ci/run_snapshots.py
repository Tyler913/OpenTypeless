#!/usr/bin/env python3
"""Run the packaged native app in isolated rendering scenarios (each language × light/dark), with a hard timeout."""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import random
import subprocess

from check_snapshots import check, expected_files

# German too: its long words are the first to show text that is cut off or doesn't fit.
LANGUAGES = {"en": "English", "zh": "Chinese", "de": "German"}
THEMES = ("light", "dark")
# A scenario takes about 10 s. Six hung ones at this limit still end before the workflow step's 15 minutes, so the report
# says which scenario and page hung instead of the step being killed silently.
TIMEOUT = 120

# What the dark scenarios' Home, History and panel show: a few dictations (text, no recordings). The light scenarios
# start from an empty folder, so both the first-run pages and the pages in use are drawn.
SAMPLE_DICTATIONS = (
    (0.4, 12, "Let's move the launch review to Friday at 3, and invite the design team."),
    (1.2, 14, "我想在设置页加一个 toggle，用户可以在 light mode 和 dark mode 之间切换，默认跟随 system。"),
    (2.1, 38, "A few things for the release:\n\n- Update the changelog\n- Tag the build\n- Post the notes in the channel"),
    (26.0, 95, "Weekly sync notes. The onboarding flow is done, the billing migration is on track, and the "
               "search rewrite needs one more week."),
    (28.5, 23, "Can you refactor the handleSubmit function, pull the validation logic out, and add tests for the empty case?"),
    (33.0, 41, "Thanks for the detailed review. I've addressed the comments on the retry logic and pushed a new commit."),
)


def seed_sample_data(data, now=None):
    """Writes half a year of usage and a few dictations into a scenario's data folder, in the format both apps read
    (`usage.json`, and `Sessions/<id>/session.json`: see UsageLedger and DictationRecord)."""
    now = now or datetime.now().astimezone()
    data.mkdir(parents=True, exist_ok=True)
    dice = random.Random(7)  # the same picture on every run
    days = {}
    for age in range(182):
        day = now - timedelta(days=age)
        # Quieter weekends, and the odd day off.
        if dice.random() < (0.55 if day.weekday() >= 5 else 0.12):
            continue
        dictations = dice.randint(2, 28)
        words = sum(dice.randint(8, 160) for _ in range(dictations))
        days[day.strftime("%Y-%m-%d")] = {
            "words": words, "dictations": dictations, "seconds": round(words / dice.uniform(2.2, 2.7), 1),
            "sttCost": round(words * 0.000012, 6), "polishCost": round(words * 0.000007, 6), "unpriced": 0,
        }
    (data / "usage.json").write_text(json.dumps({"version": 1, "days": days}, indent=2), encoding="utf-8")
    for hours, seconds, text in SAMPLE_DICTATIONS:
        date = (now - timedelta(hours=hours)).astimezone(timezone.utc)
        record = date.strftime("%Y%m%d-%H%M%S") + "-demo"
        folder = data / "Sessions" / record
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "session.json").write_text(json.dumps({
            "id": record, "date": date.strftime("%Y-%m-%dT%H:%M:%SZ"), "duration": seconds, "status": "done",
            "rawText": text, "polishedText": text, "chunkTexts": {}, "cost": round(seconds * 0.00005, 6), "counted": True,
        }, ensure_ascii=False, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    errors = []
    hung = False
    for language in LANGUAGES:
        for theme in THEMES:
            scenario = f"{language}-{theme}"
            if hung:
                # One hang means the rest hang the same way; the report comes sooner without them.
                errors.append(f"{scenario}: not run after the hang above")
                continue
            print(f"{scenario}: rendering…", flush=True)
            directory = output / scenario
            # Never accept screenshots from a previous process/run as evidence of success.
            directory.mkdir()
            data = output / f"{scenario}-data"
            if theme == "dark":
                seed_sample_data(data)
            env = dict(os.environ, OPENTYPELESS_DATA_DIR=str(data), OPENTYPELESS_SUPPORT_DIR=str(data), OPENTYPELESS_DEBUG="1")
            with (directory / "process.log").open("w", encoding="utf-8") as log:
                try:
                    result = subprocess.run(
                        [str(executable), "--snapshot-ui", str(directory), "--lang", language, "--" + theme, "--demo"],
                        env=env, stdout=log, stderr=subprocess.STDOUT, timeout=TIMEOUT, check=False,
                    )
                    if result.returncode:
                        errors.append(f"{scenario}: app exited with {result.returncode}")
                except subprocess.TimeoutExpired:
                    # The images written so far show which page it was on.
                    done = sorted(directory.glob("*.png"), key=lambda path: path.stat().st_mtime)
                    errors.append(f"{scenario}: app exceeded {TIMEOUT} seconds after {len(done)} images "
                                  f"(last written: {done[-1].name if done else 'none'})")
                    hung = True
            errors.extend(f"{scenario}: {error}" for error in check(directory, language))
    per_scenario = len(expected_files("en"))
    scenarios = len(LANGUAGES) * len(THEMES)
    report = "### Native UI rendering smoke test\n\n"
    report += (f"{' + '.join(LANGUAGES.values())} × light + dark; {per_scenario} images per scenario. "
               "This does not test clicks or compare visual baselines.\n\n")
    report += ("\n".join(f"- {error}" for error in errors) if errors
               else f"All {per_scenario * scenarios} images decoded and contained visible content.\n")
    (output / "summary.md").write_text(report, encoding="utf-8")
    print(report)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as file:
            file.write(report + "\n")
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
