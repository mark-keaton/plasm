#!/bin/bash
# Steady-state time per operation under the runtime and as a native binary
# (docs/benchmarks.md): one PostgreSQL 18.4 in Docker, seeded with the
# benchmark dataset (the schema of tests/Plasm.TestDomain/Database.fs and the
# seed of benchmarks/Plasm.Benchmarks/Workloads.fs, read from those files),
# and the same program run twice each way, alternating.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/plasm-steady.XXXXXX")"
pg=$(docker run -d -e POSTGRES_PASSWORD=postgres -p 127.0.0.1::5432 postgres:18.4)
trap 'docker rm -f "$pg" > /dev/null; rm -rf "$work"' EXIT
for i in $(seq 60); do docker exec "$pg" pg_isready -U postgres > /dev/null 2>&1 && break; sleep 1; done; sleep 2
port=$(docker port "$pg" 5432/tcp | head -1 | sed 's/.*://')
cs="Host=127.0.0.1;Port=$port;Username=postgres;Password=postgres;Database=postgres"

python3 - "$repo" > "$work/setup.sql" <<'PY'
import re, sys
repo = sys.argv[1]
def block(path, name):
    text = open(path).read()
    start = text.index('let ' + name + ' =')
    body = text[text.index('"""', start) + 3:]
    return body[:body.index('"""')]
work = open(repo + '/benchmarks/Plasm.Benchmarks/Workloads.fs').read()
counts = {k: re.search('let ' + k + r' = ([0-9_]+)', work).group(1).replace('_', '') for k in ['orderCount', 'linesPerOrder', 'paymentsPerOrder']}
seed = block(repo + '/benchmarks/Plasm.Benchmarks/Workloads.fs', 'seedSql').replace('%%', '%')
for k, v in counts.items(): seed = seed.replace('{' + k + '}', v)
print(block(repo + '/tests/Plasm.TestDomain/Database.fs', 'schemaSql'))
print(seed)
PY
docker exec -i "$pg" psql -q -U postgres -v ON_ERROR_STOP=1 < "$work/setup.sql"
echo "database: $(docker exec "$pg" psql -tA -U postgres -c 'select count(*) from orders') orders, $(docker exec "$pg" psql -tA -U postgres -c 'select count(*) from order_lines') lines, $(docker exec "$pg" psql -tA -U postgres -c 'select count(*) from payments') payments"

rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet build "$here" -c Release -v q -o "$work/jit" > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
dotnet publish "$here" -c Release -r "$rid" -p:PublishAot=true -v q -o "$work/native" > "$work/publish.log" 2>&1 || { tail -20 "$work/publish.log"; exit 1; }
for run in 1 2; do
  dotnet "$work/jit/Plasm.Steady.dll" "$cs" > "$work/jit-$run.txt"
  "$work/native/Plasm.Steady" "$cs" > "$work/native-$run.txt"
done
# Each run says what it ran as: the runtime with dynamic code, or a native binary.
for run in 1 2; do
  grep -qx "mode	runtime" "$work/jit-$run.txt" || { echo "the runtime run did not run as the runtime: $(head -1 "$work/jit-$run.txt")"; exit 1; }
  grep -qx "mode	native" "$work/native-$run.txt" || { echo "the native run did not run natively: $(head -1 "$work/native-$run.txt")"; exit 1; }
done
python3 - "$work" <<'PY'
import sys
work = sys.argv[1]
def read(f):
    rows = {}
    for line in open(f):
        parts = line.rstrip('\n').split('\t')
        if len(parts) == 4: rows[parts[0]] = (float(parts[1]), float(parts[2]), int(parts[3]))
    return rows
jit = [read(f'{work}/jit-{r}.txt') for r in (1, 2)]
nat = [read(f'{work}/native-{r}.txt') for r in (1, 2)]
print('operation | runtime median µs (runs 1, 2) | native median µs (runs 1, 2) | runtime p95 | native p95 | runtime bytes/op | native bytes/op')
for op in jit[0]:
    j = [x[op] for x in jit]; n = [x[op] for x in nat]
    print(f"{op} | {j[0][0]}, {j[1][0]} | {n[0][0]}, {n[1][0]} | {j[0][1]}, {j[1][1]} | {n[0][1]}, {n[1][1]} | {j[1][2]} | {n[1][2]}")
PY
