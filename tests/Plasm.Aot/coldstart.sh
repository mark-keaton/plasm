#!/bin/bash
# Cold start (docs/aot.md): runs tests/Plasm.Aot's coldstart mode as fresh
# processes, under the runtime and natively, with every query checked at start
# and with none, and prints the median of each phase in milliseconds.
# Needs PLASM_TEST_PG, a PostgreSQL on localhost, so the connection is a floor.
# SCHEMA=1 adds the schema check as a phase; RUNS sets the runs per column.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cs="${PLASM_TEST_PG:?set PLASM_TEST_PG to a localhost PostgreSQL}"
runs="${RUNS:-11}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet build "$here" -c Release -v q -o "$work/jit" > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
dotnet publish "$here" -c Release -r "$rid" -p:PublishAot=true -v q -o "$work/native" > "$work/publish.log" 2>&1 || { cat "$work/publish.log"; exit 1; }
"$work/native/Plasm.Aot" "$cs" > /dev/null 2>&1 || true   # creates the check's schema and rows

for kind in runtime native; do
  for queries in check ignore; do
    for i in $(seq 1 "$runs"); do
      if [ "$kind" = runtime ]; then
        dotnet "$work/jit/Plasm.Aot.dll" "$cs" coldstart "$queries" ${SCHEMA:+schema} 2>> "$work/$kind-$queries.txt" > /dev/null
      else
        "$work/native/Plasm.Aot" "$cs" coldstart "$queries" ${SCHEMA:+schema} 2>> "$work/$kind-$queries.txt" > /dev/null
      fi
    done
  done
done

python3 - "$work" <<'PY'
import sys, statistics, collections
work = sys.argv[1]
cols = ["runtime-check", "runtime-ignore", "native-check", "native-ignore"]
data = {}
order = []
for c in cols:
    d = collections.defaultdict(list)
    for line in open(f"{work}/{c}.txt"):
        phase, ms = line.rsplit(" ", 1)
        if phase not in order: order.append(phase)
        d[phase].append(float(ms))
    data[c] = d
print("| Phase (median ms) | " + " | ".join(cols) + " |")
print("|---|" + "---:|" * len(cols))
for p in order:
    print(f"| {p} | " + " | ".join(f"{statistics.median(data[c][p]):.1f}" if data[c][p] else "" for c in cols) + " |")
PY
echo "native binary: $(du -h "$work/native/Plasm.Aot" | cut -f1)"
