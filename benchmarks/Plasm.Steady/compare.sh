#!/bin/bash
# Before and after (docs/benchmarks.md, "Native translation, before and
# after"): Plasm.Steady built from the commit given and from the working tree,
# both with the working tree's harness (Steady.fs and its project), each under
# the runtime and as a native binary; the four alternated, two rounds, against
# one PostgreSQL 18.4 in Docker seeded as run.sh seeds it. Prints each
# operation's lower median of the two rounds, and the native bytes.
#   benchmarks/Plasm.Steady/compare.sh 0d79f23
set -euo pipefail
before_commit=${1:?the commit to compare the working tree with}
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/plasm-compare.XXXXXX")"
mkdir -p "$work/before" "$work/after" "$work/out"
(cd "$repo" && git archive "$before_commit") | tar -x -C "$work/before"
(cd "$repo" && (git ls-files -z; git ls-files -z --others --exclude-standard) | COPYFILE_DISABLE=1 tar --no-xattrs -cf - --null -T -) | tar -xf - -C "$work/after"
cp "$work/after/benchmarks/Plasm.Steady/Steady.fs" "$work/after/benchmarks/Plasm.Steady/Plasm.Steady.fsproj" "$work/before/benchmarks/Plasm.Steady/"
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
for v in before after; do
  dotnet build "$work/$v/benchmarks/Plasm.Steady" -c Release -v q -o "$work/out/$v-jit" > "$work/out/$v-build.log" 2>&1 || { tail "$work/out/$v-build.log"; exit 1; }
  dotnet publish "$work/$v/benchmarks/Plasm.Steady" -c Release -r "$rid" -p:PublishAot=true -v q -o "$work/out/$v-native" > "$work/out/$v-publish.log" 2>&1 || { tail "$work/out/$v-publish.log"; exit 1; }
done
pg=$(docker run -d -e POSTGRES_PASSWORD=postgres -p 127.0.0.1::5432 postgres:18.4)
trap 'docker rm -f "$pg" > /dev/null' EXIT
for i in $(seq 60); do docker exec "$pg" pg_isready -U postgres > /dev/null 2>&1 && break; sleep 1; done; sleep 2
port=$(docker port "$pg" 5432/tcp | head -1 | sed 's/.*://')
cs="Host=127.0.0.1;Port=$port;Username=postgres;Password=postgres;Database=postgres"
python3 - "$repo" > "$work/out/setup.sql" <<'PY'
import re, sys
repo = sys.argv[1]
def block(path, name):
    text = open(path).read(); start = text.index('let ' + name + ' =')
    body = text[text.index('"""', start) + 3:]; return body[:body.index('"""')]
work = open(repo + '/benchmarks/Plasm.Benchmarks/Workloads.fs').read()
counts = {k: re.search('let ' + k + r' = ([0-9_]+)', work).group(1).replace('_', '') for k in ['orderCount', 'linesPerOrder', 'paymentsPerOrder']}
seed = block(repo + '/benchmarks/Plasm.Benchmarks/Workloads.fs', 'seedSql').replace('%%', '%')
for k, v in counts.items(): seed = seed.replace('{' + k + '}', v)
print(block(repo + '/tests/Plasm.TestDomain/Database.fs', 'schemaSql')); print(seed)
PY
docker exec -i "$pg" psql -q -U postgres -v ON_ERROR_STOP=1 < "$work/out/setup.sql"
uptime
for round in 1 2; do
  dotnet "$work/out/before-jit/Plasm.Steady.dll" "$cs" > "$work/out/before-runtime-$round.txt"
  dotnet "$work/out/after-jit/Plasm.Steady.dll" "$cs" > "$work/out/after-runtime-$round.txt"
  "$work/out/before-native/Plasm.Steady" "$cs" > "$work/out/before-native-$round.txt"
  "$work/out/after-native/Plasm.Steady" "$cs" > "$work/out/after-native-$round.txt"
done
uptime
for f in "$work"/out/*-runtime-*.txt; do grep -qx "mode	runtime" "$f" || { echo "$f did not run as the runtime"; exit 1; }; done
for f in "$work"/out/*-native-*.txt; do grep -qx "mode	native" "$f" || { echo "$f did not run natively"; exit 1; }; done
python3 - "$work/out" <<'PY'
import sys
d = sys.argv[1]
def read(name):
    rows = {}
    for line in open(f"{d}/{name}"):
        p = line.rstrip("\n").split("\t")
        if len(p) == 4: rows[p[0]] = (float(p[1]), float(p[2]), int(p[3]))
    return rows
runs = {k: [read(f"{k}-{r}.txt") for r in (1, 2)] for k in ["before-runtime", "after-runtime", "before-native", "after-native"]}
print("operation | runtime before | runtime after | native before | native after | native bytes before | native bytes after")
for op in runs["before-runtime"][0]:
    m = {k: min(x[op][0] for x in v) for k, v in runs.items()}
    b = {k: v[1][op][2] for k, v in runs.items()}
    print(f"{op} | {m['before-runtime']} | {m['after-runtime']} | {m['before-native']} | {m['after-native']} | {b['before-native']} | {b['after-native']}")
PY
echo "the runs: $work/out"
