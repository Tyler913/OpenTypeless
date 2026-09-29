#!/bin/zsh
# Runs the performance budgets (Tests/TypelessCoreTests/PerformanceTests.swift) in a release build, like the app's, and
# prints each timing next to its budget. On GitHub Actions the table also goes to the run's summary page, and a check
# over its budget is flagged on the run page.
set -e
cd "$(dirname "$0")/.."
report="$(mktemp)"
trap 'rm -f "$report"' EXIT

result=0
OPENTYPELESS_PERF=1 OPENTYPELESS_PERF_REPORT="$report" swift test -c release --filter PerformanceTests "$@" || result=$?

table="$(awk -F'\t' 'BEGIN { print "| Check | Time | Budget | |"; print "|---|--:|--:|---|" }
    { printf "| %s | %.1f ms | %d ms | %s |\n", $1, $2, $3, ($2 <= $3 ? "ok" : "OVER BUDGET") }' "$report")"
printf '\n%s\n' "$table"
if [[ -n "$GITHUB_STEP_SUMMARY" ]]; then
    printf '### macOS performance budgets\n\n%s\n' "$table" >> "$GITHUB_STEP_SUMMARY"
fi
if [[ -n "$GITHUB_ACTIONS" ]]; then
    awk -F'\t' '$2 > $3 { printf "::error title=Over its performance budget::%s: %.1f ms (budget %d ms)\n", $1, $2, $3 }' "$report"
fi
exit $result
