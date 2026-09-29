#!/usr/bin/env python3
"""Checks i18n/strings.json against the L("中文", "English") calls in both apps.

Every user-facing string is written inline as L("中文", "English"). Chinese and English live in the code; the
other languages live in strings.json, keyed by the English text with each interpolation replaced by {0}, {1}, …
in order of appearance:

    L("有新版本 \\(version)", "Version \\(version) is available")      (Swift)
    L($"有新版本 {version}", $"Version {version} is available")       (C#)
      → "Version {0} is available": { "ja": "新しいバージョン {0} があります", … }

A translation may reorder the placeholders but must use each of them exactly as the key does.

    python3 i18n/check.py            report missing, unused and malformed entries (exit 1 if any)
    python3 i18n/check.py --list     print every English key found in the code, with its Chinese text
"""

import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LANGUAGES = ["ja", "ko", "es", "pt", "ru", "fr", "de"]
PLACEHOLDER = re.compile(r"\{(\d+)\}")


def string_literal(src, i, swift):
    """Parses a string literal starting at src[i]. Returns (key, end) or None if it isn't one."""
    interpolated = False
    if not swift and src[i] == "$":
        interpolated, i = True, i + 1
    if src[i] != '"' or src.startswith('"""', i):
        return None
    i += 1
    key, holes = [], 0
    while True:
        c = src[i]
        if c == '"':
            return "".join(key), i + 1
        if c == "\\":
            n = src[i + 1]
            if swift and n == "(":
                i = skip_balanced(src, i + 1, "(", ")")
                key.append("{%d}" % holes)
                holes += 1
                continue
            if swift and n == "u":
                end = src.index("}", i)
                key.append(chr(int(src[i + 3:end], 16)))
                i = end + 1
                continue
            key.append({"n": "\n", "t": "\t", "0": "\0"}.get(n, n))
            i += 2
            continue
        if interpolated and c == "{":
            if src[i + 1] == "{":
                key.append("{")
                i += 2
                continue
            i = skip_balanced(src, i, "{", "}")
            key.append("{%d}" % holes)
            holes += 1
            continue
        if interpolated and c == "}" and src[i + 1] == "}":
            key.append("}")
            i += 2
            continue
        key.append(c)
        i += 1


def skip_balanced(src, i, open_, close):
    """src[i] is `open_`; returns the index just past its matching `close`, skipping nested string literals."""
    depth = 0
    while True:
        c = src[i]
        if c == '"':
            i += 1
            while src[i] != '"':
                i += 2 if src[i] == "\\" else 1
        elif c == open_:
            depth += 1
        elif c == close:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1


def arguments(src, i):
    """src[i] is the "(" of a call; returns the raw text of each top-level argument."""
    end = skip_balanced(src, i, "(", ")")
    args, depth, start, j = [], 0, i + 1, i + 1
    while j < end - 1:
        c = src[j]
        if c == '"':
            j += 1
            while src[j] != '"':
                j += 2 if src[j] == "\\" else 1
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            args.append((start, j))
            start = j + 1
        j += 1
    args.append((start, end - 1))
    return args


def scan():
    """Yields (path, line, zh, en) for every L(…) call; zh/en are None when an argument isn't a plain literal."""
    for base, swift in (("macos/Sources", True), ("windows/src", False)):
        for folder, _, files in os.walk(os.path.join(ROOT, base)):
            for name in sorted(files):
                if not name.endswith(".swift" if swift else ".cs"):
                    continue
                path = os.path.join(folder, name)
                src = open(path, encoding="utf-8").read()
                for match in re.finditer(r"(?<![\w.])L\(", src):
                    line_start = src.rfind("\n", 0, match.start()) + 1
                    if "//" in src[line_start:match.start()] or re.match(r"L\((_ )?\w+ zh", src[match.start():]):
                        continue  # a comment or the definition of L itself
                    parsed = []
                    for start, end in arguments(src, match.end() - 1):
                        k = start
                        while src[k].isspace():
                            k += 1
                        literal = string_literal(src, k, swift)
                        parsed.append(literal[0] if literal and not src[literal[1]:end].strip() else None)
                    line = src.count("\n", 0, match.start()) + 1
                    rel = os.path.relpath(path, ROOT)
                    if len(parsed) != 2:
                        yield rel, line, None, None
                    else:
                        yield rel, line, parsed[0], parsed[1]


def main():
    calls = list(scan())
    if "--list" in sys.argv:
        seen = {}
        for _, _, zh, en in calls:
            if en is not None and en not in seen:
                seen[en] = zh
        json.dump(seen, sys.stdout, ensure_ascii=False, indent=2)
        print()
        return 0

    problems = []
    for path, line, zh, en in calls:
        if en is None:
            problems.append(f"{path}:{line}: the English argument of L() isn't a plain string literal")
    keys = {en for _, _, _, en in calls if en is not None}

    with open(os.path.join(ROOT, "i18n", "strings.json"), encoding="utf-8") as f:
        table = json.load(f)

    for key in sorted(keys - table.keys()):
        problems.append(f"missing: {key!r}")
    for key in sorted(table.keys() - keys):
        problems.append(f"unused: {key!r}")
    for key, translations in table.items():
        expected = sorted(PLACEHOLDER.findall(key))
        for language in LANGUAGES:
            text = translations.get(language)
            if not text:
                problems.append(f"{key!r}: no {language} translation")
            elif sorted(PLACEHOLDER.findall(text)) != expected:
                problems.append(f"{key!r}: the {language} translation has different placeholders")
        for language in translations.keys() - set(LANGUAGES):
            problems.append(f"{key!r}: unknown language {language!r}")

    for problem in problems:
        print(problem)
    print(f"{len(keys)} strings, {len(LANGUAGES)} languages, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
