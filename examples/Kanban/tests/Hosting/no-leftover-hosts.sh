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

# No host is ever stopped (state T) by the run that stops it: a host is
# stopped with one SIGKILL. Process.Kill(entireProcessTree: true) stops it
# first (SIGSTOP) and lists its children before it kills, and a run that dies
# in that window leaves it stopped for good. A poller samples the C# hosts'
# states while their scenarios run.
specs=Kanban.CSharpHost.Specs
dir=$(started "$specs")
seen=$(mktemp "${TMPDIR:-/tmp}/kanban-host-states.XXXXXX")
dotnet test --project "tests/$specs" --no-build > /dev/null 2>&1 &
run=$!
while kill -0 $run 2> /dev/null; do
  pids=$(pgrep -f "$dir.*Kanban\.Host" | tr '\n' ',' | sed 's/,$//')
  [ -n "$pids" ] && ps -o stat= -p "$pids" >> "$seen" 2> /dev/null
done
wait $run 2> /dev/null
stopped=$(grep -c '^T' "$seen" || true)
echo "$specs: $(wc -l < "$seen" | tr -d ' ') host states sampled, $stopped stopped (T)"
[ "$stopped" -eq 0 ] || failed=1
rm -f "$seen"

# A run killed while a scenario's host is up: killed only once a host is
# seen. A run that starts no host within the wait has tested nothing, which
# is its own failure ("no host started"), shown with the run's own output,
# not a host left behind. The run is built above, so the wait is the
# scenarios' own: their database starting, then their first host.
# KANBAN_LEFTOVER_PLANT=leak, =stopped or =never makes each failure happen, to
# prove the check tells them apart (never set it otherwise).
specs=Kanban.CSharpHost.Specs
dir=$(started "$specs")
wait_s=${KANBAN_LEFTOVER_WAIT:-180}
run_log=$(mktemp "${TMPDIR:-/tmp}/kanban-killed-run.XXXXXX")
case "${KANBAN_LEFTOVER_PLANT:-}" in
  never) sleep "$wait_s" > "$run_log" 2>&1 & ;;
  *) dotnet test --project "tests/$specs" --no-build > "$run_log" 2>&1 & ;;
esac
run=$!
up=0
for i in $(seq $((wait_s * 2))); do up=$(hosts "$dir"); [ "$up" -gt 0 ] && break; sleep 0.5; done
if [ "$up" -eq 0 ]; then
  kill -9 $run 2> /dev/null
  pkill -9 -f "$here/tests/$specs/bin/Debug/net11.0/$specs" 2> /dev/null
  echo "$specs killed mid-run: no host started within $wait_s s; the run's output until then:"
  tail -30 "$run_log" | sed 's/^/    /'
  failed=1
else
  # A host the check plants, which ignores its standard input: a leak. Or the
  # running host stopped (SIGSTOP) before the run is killed, as a tree-kill
  # interrupted between its SIGSTOP and its SIGKILL leaves one.
  case "${KANBAN_LEFTOVER_PLANT:-}" in
    leak) (exec -a "$dir/Kanban.Host.planted" sleep 120) & ;;
    stopped) pgrep -f "$dir.*Kanban\.Host" | xargs kill -STOP ;;
  esac
  pkill -9 -f "$here/tests/$specs/bin/Debug/net11.0/$specs" || true
  for i in $(seq 20); do [ "$(hosts "$dir")" -eq 0 ] && break; sleep 0.5; done
  left=$(hosts "$dir")
  echo "$specs killed mid-run: $up host(s) up when killed, $left left after"
  if [ "$left" -ne 0 ]; then
    # Each with its state: T is a host stopped (SIGSTOP), deaf to SIGTERM.
    echo "a host was left:"
    for p in $(pgrep -f "$dir.*Kanban\.Host"); do ps -o pid=,stat=,command= -p $p | cut -c1-200 | sed 's/^/    /'; done
    if [ "${KANBAN_LEFTOVER_PLANT:-}" = stopped ]; then
      pgrep -f "$dir.*Kanban\.Host" | xargs kill -TERM; sleep 2
      echo "after SIGTERM: $(hosts "$dir") left, state $(ps -o stat= -p $(pgrep -f "$dir.*Kanban\.Host" | head -1) 2>/dev/null)"
      pgrep -f "$dir.*Kanban\.Host" | xargs kill -CONT; sleep 2
      echo "after SIGCONT: $(hosts "$dir") left"
      pgrep -f "$dir.*Kanban\.Host" | xargs kill -9 2> /dev/null
    fi
    pkill -9 -f "$dir.*Kanban\.Host.planted" 2> /dev/null
    failed=1
  fi
fi
wait 2> /dev/null
rm -f "$run_log"
exit $failed
