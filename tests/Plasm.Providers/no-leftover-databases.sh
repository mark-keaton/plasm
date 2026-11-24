#!/bin/bash
# A run on a PostgreSQL that runs share (PLASM_TEST_PG) leaves no test
# database behind: each test's goes when the test ends, the examples' when the
# program ends, and the next run drops those of a run on this machine whose
# process is gone. It starts a PostgreSQL of its own (Docker) to be that
# server, so no other run is counted.
#   no-leftover-databases.sh            runs the examples and some tests, and counts
#   no-leftover-databases.sh --prove    also leaves an orphan, as a killed run does, and runs again
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
  # A run killed mid-way, as a run that dies is: the next run drops what it left.
  dotnet test --project "$root/tests/Plasm.Specs" --no-build -- --filter-class '*PredictableFunctional*' > /dev/null 2>&1 & pid=$!
  until [ "$(count)" -gt 0 ] || ! kill -0 $pid 2>/dev/null; do sleep 0.1; done
  pkill -9 -f "$root/tests/Plasm.Specs/bin/" 2>/dev/null; pkill -9 -P $pid 2>/dev/null; kill -9 $pid 2>/dev/null; wait $pid 2>/dev/null
  left=$(count); run; after=$(count)
  echo "PostgreSQL runs, one killed mid-run: $left test database(s) left by it, $after after the next run"
  [ "$left" -gt 0 ] && [ "$after" -eq 0 ] || status=1
fi
exit $status
