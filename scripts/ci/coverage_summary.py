#!/usr/bin/env python3
"""Report core-library line coverage; never present it as whole-app coverage."""
import argparse
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET


def swift_rows(path):
    data = json.loads(path.read_text())
    return [(Path(f["filename"]).name, f["summary"]["lines"]["covered"], f["summary"]["lines"]["count"])
            for unit in data["data"] for f in unit["files"]
            if "/Sources/TypelessCore/" in f["filename"]]


def dotnet_rows(path):
    # Cobertura repeats a source file for each class. Merge line numbers instead of double-counting them.
    files = {}
    for package in ET.parse(path).getroot().findall("./packages/package"):
        if package.get("name") != "TypelessCore":
            continue
        for cls in package.findall("./classes/class"):
            name = cls.attrib["filename"].replace("\\", "/")
            lines = files.setdefault(name, {})
            for line in cls.findall("./lines/line"):
                number = int(line.attrib["number"])
                lines[number] = lines.get(number, False) or int(line.attrib["hits"]) > 0
    return [(name, sum(lines.values()), len(lines)) for name, lines in files.items()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("format", choices=("swift", "dotnet"))
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows = (swift_rows if args.format == "swift" else dotnet_rows)(args.report)
    total = sum(row[2] for row in rows)
    covered = sum(row[1] for row in rows)
    if not rows or total == 0:
        parser.exit(1, "No TypelessCore coverage found; refusing an empty report.\n")
    text = f"### {args.format} TypelessCore line coverage: {covered / total:.1%}\n\n"
    text += "Core library only. UI, SessionController, OS integration and updater execution are NOT measured.\n\n"
    text += "| Source | Covered | Executable lines | Coverage |\n|---|---:|---:|---:|\n"
    for name, hit, count in sorted(rows, key=lambda row: row[1] / row[2] if row[2] else 1):
        text += f"| {name} | {hit} | {count} | {hit / count:.1%} |\n" if count else f"| {name} | 0 | 0 | n/a |\n"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding="utf-8")
    print(text)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as file:
            file.write(text + "\n")


if __name__ == "__main__":
    main()
