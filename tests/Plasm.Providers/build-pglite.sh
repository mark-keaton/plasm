#!/bin/bash
# Plasm.PGlite compiled, nothing run. Plasm.slnx leaves it out (the browser
# koans build it, and run only when a browser path changes), so a change to
# the provider interfaces could break it unseen: this builds it alone, from
# any copy of the repository, on every full check.
#   tests/Plasm.Providers/build-pglite.sh
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
log=$(mktemp)
trap 'rm -f "$log"' EXIT
dotnet build "$root/src/Plasm.PGlite" -v q -nologo > "$log" 2>&1
status=$?
grep -E " error " "$log" | sed "s|$root/||g" | sort -u | head -20
echo "pglite: $(grep -cE ' error ' "$log" | tr -d ' ') error lines, $(grep -E '[0-9]+ Warning\(s\)' "$log" | tail -1 | tr -s ' ')"
exit $status
