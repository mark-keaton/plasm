#!/bin/bash
# No host outlives its scenarios (tests/Hosting/Hosting.cs): after each host's
# scenarios run, no process of that host started by the run is left; and a
# test run killed in the middle of a scenario leaves none either, its hosts
# exiting when their standard input closes. Needs Docker (or PLASM_TEST_PG).
set -uo pipefail
here="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$here"
failed=0

# Where each suite's scenarios start their host: the F# host's scenarios from
# their own output (the host is referenced and copied there); the C# host's
# from the host's own output, which their project names (KanbanHostOutput), as
# the scenarios read it. A host is counted by its program's path.
started() {
  case $1 in
    Kanban.CSharpHost.Specs) dotnet msbuild "tests/$1" -getProperty:KanbanHostOutput ;;
    *) echo "$here/tests/$1/bin/" ;;
  esac
}
hosts() { pgrep -f "$1.*Kanban\.Host" | wc -l | tr -d ' '; }

for specs in Kanban.CSharpHost.Specs Kanban.Host.Specs; do
  dotnet build "tests/$specs" -v q > /dev/null 2>&1 || { echo "$specs did not build"; exit 1; }
  dir=$(started "$specs")
  [ -d "$dir" ] || { echo "$specs: no host output at $dir"; exit 1; }
  before=$(hosts "$dir")
  dotnet test --project "tests/$specs" --no-build > /dev/null 2>&1
  after=$(hosts "$dir")
  echo "$specs: $before hosts before its scenarios, $after after"
  [ "$after" -eq 0 ] || failed=1
done

# A run killed while a scenario's host is up.
specs=Kanban.CSharpHost.Specs
dir=$(started "$specs")
dotnet test --project "tests/$specs" --no-build > /dev/null 2>&1 &
for i in $(seq 120); do [ "$(hosts "$dir")" -gt 0 ] && break; sleep 0.5; done
up=$(hosts "$dir")
pkill -9 -f "$here/tests/$specs/bin/Debug/net11.0/$specs" || true
for i in $(seq 20); do [ "$(hosts "$dir")" -eq 0 ] && break; sleep 0.5; done
left=$(hosts "$dir")
echo "$specs killed mid-run: $up host(s) up when killed, $left left after"
[ "$up" -gt 0 ] && [ "$left" -eq 0 ] || failed=1
wait 2> /dev/null
exit $failed
