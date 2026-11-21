#!/bin/bash
# The SQLite run leaves no database file behind: each test's file (with its
# -wal, -shm and -journal) goes when the test ends, the run's directory when
# it ends, and a run killed mid-way has its directory removed by the next.
#   no-leftover-files.sh            counts what is left in the temp directory
#   no-leftover-files.sh --prove    also kills a run mid-way and runs again
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
tmp="${TMPDIR:-/tmp}"; tmp="${tmp%/}"
count() { find "$tmp" -maxdepth 2 -path "$tmp/plasm-sqlite-*" -type f 2>/dev/null | wc -l | tr -d ' '; }
run() { env -u PLASM_TEST_PG PLASM_TEST_PROVIDER=sqlite dotnet test "$root/tests/Plasm.Specs" --no-build -- --filter-class '*PredictableFunctional*' > /dev/null 2>&1; }
before=$(count); run; after=$(count)
echo "SQLite run: $before file(s) before its scenarios, $after after"
status=0; [ "$after" -eq 0 ] || status=1
if [ "${1:-}" = "--prove" ]; then
  run & pid=$!
  until [ "$(count)" -gt 0 ] || ! kill -0 $pid 2>/dev/null; do sleep 0.2; done
  # The test host is dotnet test's child, not this shell's: killed by its path,
  # as a run that dies is, so it leaves its files behind.
  up=$(count); pkill -9 -f "$root/tests/Plasm.Specs/bin/" 2>/dev/null; pkill -9 -P $pid 2>/dev/null; kill -9 $pid 2>/dev/null; wait $pid 2>/dev/null
  left=$(count); run; after=$(count)
  echo "SQLite run killed mid-run: $up file(s) when killed, $left left by it, $after after the next run"
  [ "$left" -gt 0 ] && [ "$after" -eq 0 ] || status=1
fi
exit $status
