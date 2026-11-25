#!/bin/bash
# A run on a PostgreSQL that runs share (PLASM_TEST_PG) leaves no test
# database behind: each test's goes when the test ends, the examples' when the
# program ends, and the next run drops those of a run on this machine whose
# process is gone. It starts a PostgreSQL of its own (Docker) to be that
# server, so no other run is counted.
#   no-leftover-databases.sh            runs the examples and some tests, and counts
#   no-leftover-databases.sh --prove    also leaves orphans (an older run's, a killed run's, ones whose
#                                        process id is reused) and runs again
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
if ! docker info > /dev/null 2>&1; then echo "PostgreSQL runs: no Docker here, so not checked"; exit 0; fi
pg=$(docker run -d --rm -e POSTGRES_PASSWORD=plasm -p 127.0.0.1::5432 postgres:18 -c max_connections=300 -c max_prepared_transactions=10)
trap 'docker stop "$pg" > /dev/null 2>&1' EXIT
for _ in $(seq 1 120); do docker exec "$pg" pg_isready -U postgres > /dev/null 2>&1 && break; sleep 0.5; done
sleep 1
port=$(docker port "$pg" 5432/tcp | sed -n 's/.*:\([0-9]*\)$/\1/p' | head -1)
export PLASM_TEST_PG="Host=127.0.0.1;Port=$port;Username=postgres;Password=plasm"
# A test database's name begins plasm_t; plasm_template, which each run makes
# again, is not one.
count() { docker exec "$pg" psql -U postgres -tAc "select count(*) from pg_database where datname like 'plasm\_t%' and datname <> 'plasm_template'"; }
run() {
  dotnet run --project "$root/examples/Plasm.Examples" --no-build > /dev/null 2>&1
  dotnet test --project "$root/tests/Plasm.Specs" --no-build -- --filter-class '*PredictableFunctional*' > /dev/null 2>&1
}
before=$(count); run; after=$(count)
echo "PostgreSQL runs: $before test database(s) before the examples and the scenarios, $after after"
status=0; [ "$after" -eq 0 ] || status=1
if [ "${1:-}" = "--prove" ]; then
  # An orphan as a run before machines were named left one, its process gone:
  # the next run drops it.
  docker exec "$pg" psql -U postgres -qc "create database plasm_t999999_1" > /dev/null
  planted=$(count); run; after=$(count)
  echo "PostgreSQL runs beside an orphan of a run before machines were named: $planted then, $after after the next run"
  [ "$planted" -eq 1 ] && [ "$after" -eq 0 ] || status=1
  # A run killed mid-way, as a run that dies is: the next run drops what it
  # left. PLASM_TEST_HOLD_DROP holds its first test before it drops its
  # database, so the run is killed with one up, whatever the timing.
  PLASM_TEST_HOLD_DROP=1 dotnet test --project "$root/tests/Plasm.Specs" --no-build -- --filter-class '*PredictableFunctional*' > /dev/null 2>&1 & pid=$!
  # Killed once a database of this run's is up: the query that sees it reads
  # its name, which says this machine's part (plasm_t<machine>_<pid>_<n>).
  newest() { docker exec "$pg" psql -U postgres -tAc "select datname from pg_database where datname ~ '^plasm_t[0-9a-f]{8}_[0-9]+_[0-9]+$' limit 1"; }
  seen=""
  until [ -n "$seen" ] || ! kill -0 $pid 2>/dev/null; do seen=$(newest); [ -n "$seen" ] || sleep 0.05; done
  pkill -9 -f "$root/tests/Plasm.Specs/bin/" 2>/dev/null; pkill -9 -P $pid 2>/dev/null; kill -9 $pid 2>/dev/null; wait $pid 2>/dev/null
  left=$(count); run; after=$(count)
  echo "PostgreSQL runs, one killed mid-run: $left test database(s) left by it, $after after the next run"
  [ "$left" -gt 0 ] && [ "$after" -eq 0 ] || status=1
  # Process ids are reused. A database whose id now belongs to a process
  # started after it was made is an orphan, of either form of name; one whose
  # process started before it (this shell) is a run's, and kept. The killed
  # run's name gave this machine's part.
  machine=$(echo "$seen" | sed -n 's/^plasm_t\([0-9a-f]\{8\}\)_[0-9]*_[0-9]*$/\1/p')
  if [ -z "$machine" ]; then
    echo "PostgreSQL runs, process ids reused: not checked, and failed: the killed run ended before a database of its was seen, so this machine's part of a name is unknown"
    exit 1
  fi
  echo "PostgreSQL runs: this machine's part of a test database's name is $machine (from $seen)"
  for d in reuse_a reuse_b; do docker exec "$pg" psql -U postgres -qc "create database plasm_$d" > /dev/null; done
  docker exec "$pg" psql -U postgres -qc "create database plasm_t${machine}_$$_1" > /dev/null
  sleep 2; sleep 900 & later=$!
  docker exec "$pg" psql -U postgres -qc "alter database plasm_reuse_a rename to plasm_t${later}_1" > /dev/null
  docker exec "$pg" psql -U postgres -qc "alter database plasm_reuse_b rename to plasm_t${machine}_${later}_1" > /dev/null
  planted=$(count); run; after=$(count)
  kept=$(docker exec "$pg" psql -U postgres -tAc "select count(*) from pg_database where datname = 'plasm_t${machine}_$$_1'")
  kill $later 2>/dev/null; wait $later 2>/dev/null
  echo "PostgreSQL runs beside databases whose process id a later process holds, and one whose process is this check: $planted then, $after after the next run, the check's kept: $kept"
  [ "$planted" -eq 3 ] && [ "$after" -eq 1 ] && [ "$kept" -eq 1 ] || status=1
fi
exit $status
