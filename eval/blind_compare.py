#!/usr/bin/env python3
"""Blind comparison of two evaluation runs: which output reads more like what the speaker would have typed?

The automatic checks say whether a clean-up kept what it must and dropped what it must; they can't say whether it reads
naturally. For that, run the same cases twice with `--eval-polish` (say, the old prompt with `--prompt-file` and the
new one built in), then:

    python3 eval/blind_compare.py pair eval/polish-holdout.json OLD.json NEW.json --out /private/blind.csv --key /private/key.json

writes one row per case, model and round that both runs answered: the transcript and the two outputs as "1" and "2", in
an order shuffled per row. Fill in the `vote` column with 1, 2 or = (no difference), by hand or with another model as the
judge (give it the CSV without the key), then:

    python3 eval/blind_compare.py score /private/blind.csv /private/key.json

prints how often each run won, overall and per model. Only the win rate counts, not a score. Rows whose outputs are the
same are left out. Like the runs' own output, both files hold your text: keep them outside the repository.
"""

import argparse
import csv
import json
import os
import random
import sys
from collections import Counter, defaultdict

REPOSITORY = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def outside_repository(path):
    resolved = os.path.realpath(path)
    if resolved == REPOSITORY or resolved.startswith(REPOSITORY + os.sep):
        sys.exit(f"{path}: must be outside the repository, since it holds your text")
    return path


def answers(path):
    """(case, model, round) → output, for the attempts of a run that came back without an error."""
    with open(path, encoding="utf-8") as f:
        results = json.load(f)
    return {
        (r["id"], r["model"], r["round"]): r["attempt"]["output"]
        for r in results
        if r["attempt"].get("error") is None and r["attempt"].get("output")
    }


def pair(args):
    with open(args.cases, encoding="utf-8") as f:
        inputs = {c["id"]: c["input"] for c in json.load(f)}
    a, b = answers(args.a), answers(args.b)
    rng = random.Random(args.seed)
    rows, key = [], {}
    for index, item in enumerate(sorted(set(a) & set(b))):
        if a[item] == b[item]:
            continue
        flipped = rng.random() < 0.5
        first, second = (b[item], a[item]) if flipped else (a[item], b[item])
        row = f"r{index}"
        rows.append({"row": row, "case": item[0], "transcript": inputs.get(item[0], ""), "output 1": first,
                     "output 2": second, "vote": ""})
        key[row] = {"case": item[0], "model": item[1], "round": item[2], "1": "b" if flipped else "a",
                    "2": "a" if flipped else "b"}
    rng.shuffle(rows)
    with open(outside_repository(args.out), "w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=["row", "case", "transcript", "output 1", "output 2", "vote"])
        writer.writeheader()
        writer.writerows(rows)
    with open(outside_repository(args.key), "w", encoding="utf-8") as f:
        json.dump({"a": args.a, "b": args.b, "rows": key}, f, ensure_ascii=False, indent=2)
    print(f"{len(rows)} pairs to judge ({len(set(a) & set(b)) - len(rows)} identical left out)")


def score(args):
    with open(args.key, encoding="utf-8") as f:
        key = json.load(f)
    totals, by_model = Counter(), defaultdict(Counter)
    with open(args.votes, encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            vote = row["vote"].strip()
            entry = key["rows"].get(row["row"])
            if entry is None or vote not in ("1", "2", "="):
                continue
            winner = "tie" if vote == "=" else entry[vote]
            totals[winner] += 1
            by_model[entry["model"]][winner] += 1

    def line(label, counts):
        judged = sum(counts.values())
        if judged == 0:
            return f"{label}: nothing judged"
        decided = counts["a"] + counts["b"]
        rate = f"{counts['b'] / decided:.0%}" if decided else "-"
        return (f"{label}: B won {counts['b']}, A won {counts['a']}, {counts['tie']} the same"
                f" · B's win rate {rate} of {decided} decided")

    print(f"A = {key['a']}\nB = {key['b']}")
    print(line("all", totals))
    for model in sorted(by_model):
        print(line(model, by_model[model]))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("pair", help="write the pairs to judge, shuffled, and the key to them")
    p.add_argument("cases", help="the case file both runs used, for the transcripts")
    p.add_argument("a", help="--out of the first run (the baseline)")
    p.add_argument("b", help="--out of the second run (the change)")
    p.add_argument("--out", required=True, help="the CSV to judge, outside the repository")
    p.add_argument("--key", required=True, help="which output is which, outside the repository")
    p.add_argument("--seed", type=int, default=None, help="for a shuffle that can be repeated")
    p.set_defaults(run=pair)
    s = commands.add_parser("score", help="count the votes")
    s.add_argument("votes", help="the CSV, with its vote column filled in")
    s.add_argument("key", help="the key written with it")
    s.set_defaults(run=score)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
