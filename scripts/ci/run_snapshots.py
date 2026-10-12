#!/usr/bin/env python3
"""Run the packaged native app in isolated rendering scenarios (each language × light/dark), with a hard timeout."""
import argparse
import os
from pathlib import Path
import subprocess

from check_snapshots import check, expected_files

# German too: its long words are the first to show text that is cut off or doesn't fit.
LANGUAGES = {"en": "English", "zh": "Chinese", "de": "German"}
THEMES = ("light", "dark")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    executable = args.executable.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    errors = []
    for language in LANGUAGES:
        for theme in THEMES:
            scenario = f"{language}-{theme}"
            directory = output / scenario
            # Never accept screenshots from a previous process/run as evidence of success.
            directory.mkdir()
            data = output / f"{scenario}-data"
            env = dict(os.environ, OPENTYPELESS_DATA_DIR=str(data), OPENTYPELESS_SUPPORT_DIR=str(data), OPENTYPELESS_DEBUG="1")
            with (directory / "process.log").open("w", encoding="utf-8") as log:
                try:
                    result = subprocess.run(
                        [str(executable), "--snapshot-ui", str(directory), "--lang", language, "--" + theme, "--demo"],
                        env=env, stdout=log, stderr=subprocess.STDOUT, timeout=180, check=False,
                    )
                    if result.returncode:
                        errors.append(f"{scenario}: app exited with {result.returncode}")
                except subprocess.TimeoutExpired:
                    errors.append(f"{scenario}: app exceeded 180 seconds")
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
