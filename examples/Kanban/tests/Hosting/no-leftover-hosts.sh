#!/bin/bash
# No host outlives its scenarios (tests/Hosting/Hosting.cs): after each host's
# scenarios run, no process of that host started by the run is left; and a
# test run killed in the middle of a scenario leaves none either, its hosts
# exiting when their standard input closes. Needs Docker (or PLASM_TEST_PG).
set -uo pipefail
here="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$here"
failed=0

hosts() { pgrep -f "$here/tests/$1/bin/.*Kanban\.Host" | wc -l | tr -d ' '; }

for specs in Kanban.CSharpHost.Specs Kanban.GraphQLHost.Specs; do
  dotnet build "tests/$specs" -v q > /dev/null 2>&1 || { echo "$specs did not build"; exit 1; }
  before=$(hosts "$specs")
  dotnet test --project "tests/$specs" --no-build > /dev/null 2>&1
  after=$(hosts "$specs")
  echo "$specs: $before hosts before its scenarios, $after after"
  [ "$after" -eq 0 ] || failed=1
done

# A run killed while a scenario's host is up.
specs=Kanban.CSharpHost.Specs
dotnet test --project "tests/$specs" --no-build > /dev/null 2>&1 &
for i in $(seq 120); do [ "$(hosts "$specs")" -gt 0 ] && break; sleep 0.5; done
up=$(hosts "$specs")
pkill -9 -f "$here/tests/$specs/bin/Debug/net11.0/$specs" || true
for i in $(seq 20); do [ "$(hosts "$specs")" -eq 0 ] && break; sleep 0.5; done
left=$(hosts "$specs")
echo "$specs killed mid-run: $up host(s) up when killed, $left left after"
[ "$up" -gt 0 ] && [ "$left" -eq 0 ] || failed=1
wait 2> /dev/null
exit $failed
