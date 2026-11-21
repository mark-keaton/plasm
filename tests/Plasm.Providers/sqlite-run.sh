#!/bin/bash
# The SQLite run (PLASM_TEST_PROVIDER=sqlite, PLASM_TEST_PG unset) of the
# features sqlite-features.txt lists, as the full check runs it: each class's
# totals, and the provider the run said it used.
#   sqlite-run.sh <log directory>
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
logs="$1"; mkdir -p "$logs"; status=0
summary="$logs/sqlite-provider.txt"; : > "$summary"
while IFS=$'\t' read -r feature class; do
  case "$feature" in ''|'#'*) continue;; esac
  [ -f "$root/$feature" ] || { echo "sqlite-features.txt names $feature, which is no feature file"; status=1; continue; }
  log="$logs/sqlite-$(basename "$feature" .feature).log"
  env -u PLASM_TEST_PG PLASM_TEST_PROVIDER=sqlite PLASM_TEST_SUMMARY="$summary" \
    dotnet test "$root/tests/Plasm.Specs" --no-build --output Detailed -- --filter-class "$class" > "$log" 2>&1 || status=1
  echo "SQLite $feature: $(grep -E '^\s+total:|^\s+failed:' "$log" | tr -s ' ' | tr '\n' ' ')"
  grep -E "^\s*failed [A-Za-z]" "$log" | head -5
done < "$root/tests/Plasm.Providers/sqlite-features.txt"
listed=$(grep -cvE '^(#|$)' "$root/tests/Plasm.Providers/sqlite-features.txt")
all=$(ls "$root"/features/*.feature | wc -l | tr -d ' ')
echo "SQLite run: $listed of $all feature files; $(sort -u "$summary" | tr '\n' ' ')"
grep -q "^provider: SQLite " "$summary" || { echo "the SQLite run did not say it ran on SQLite"; status=1; }
exit $status
