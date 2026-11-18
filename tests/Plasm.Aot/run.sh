#!/bin/bash
# The native-AOT check (docs/aot.md): runs tests/Plasm.Aot under the runtime
# and as a native binary against one PostgreSQL, and compares what they print.
# Uses PLASM_TEST_PG (an admin connection string) if set, else starts
# postgres:18.4 in Docker for the run. Exits non-zero when the outputs differ.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d)
container=""
cleanup() {
  rm -rf "$work"
  if [ -n "$container" ]; then docker rm -f "$container" > /dev/null; fi
}
trap cleanup EXIT

if [ -n "${PLASM_TEST_PG:-}" ]; then
  cs="$PLASM_TEST_PG"
else
  container=$(docker run -d -e POSTGRES_PASSWORD=postgres -p 127.0.0.1::5432 postgres:18.4)
  port=$(docker port "$container" 5432/tcp | head -1 | sed 's/.*://')
  for _ in $(seq 1 60); do
    docker exec "$container" pg_isready -U postgres > /dev/null 2>&1 && break
    sleep 1
  done
  sleep 1
  cs="Host=127.0.0.1;Port=$port;Username=postgres;Password=postgres;Database=postgres"
fi

rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
echo "runtime: building"
dotnet build "$here" -c Release -v q -o "$work/jit" > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
echo "native: publishing for $rid (about a minute)"
dotnet publish "$here" -c Release -r "$rid" -p:PublishAot=true -v m -p:PlasmAotReport=high -o "$work/native" > "$work/publish.log" 2>&1 || { cat "$work/publish.log"; exit 1; }
echo "native: $(grep -c 'warning IL' "$work/publish.log" || true) IL warnings"
marked=$(sed -n 's/.*Plasm: \([0-9]*\) F# field getters marked.*/\1/p' "$work/publish.log" | head -1)
echo "native: ${marked:-no} F# field getters marked SpecialName"

jit_mode=$(dotnet "$work/jit/Plasm.Aot.dll" mode)
native_mode=$("$work/native/Plasm.Aot" mode)
echo "runtime: $jit_mode; native: $native_mode"
if [ "$jit_mode" != "dynamic code on" ] || [ "$native_mode" != "dynamic code off" ]; then
  echo "the runtime run is not the runtime as shipped, or the native run is not native"; exit 1
fi
dotnet "$work/jit/Plasm.Aot.dll" "$cs" > "$work/jit.txt" 2>&1 || true
"$work/native/Plasm.Aot" "$cs" > "$work/native.txt" 2>&1 || true

echo "--- runtime"; cat "$work/jit.txt"
echo "--- native"; cat "$work/native.txt"
echo "native binary: $(du -h "$work/native/Plasm.Aot" | cut -f1)"

failed=0
plasm_warnings=$(grep "warning IL" "$work/publish.log" | grep -cE "/src/Plasm|Plasm[A-Za-z.]*\.dll" || true)
echo "native: $plasm_warnings IL warnings naming Plasm"
if [ "$plasm_warnings" -ne 0 ]; then
  echo "the publish has IL warnings naming Plasm"; failed=1
fi
if [ "${marked:-0}" -eq 0 ]; then
  echo "the publish did not apply Plasm's step for F# field getters"; failed=1
fi
if grep -q ": threw " "$work/jit.txt"; then
  echo "a step threw under the runtime"; failed=1
fi
if diff -u "$work/jit.txt" "$work/native.txt"; then
  echo "native binary prints what the runtime prints"
else
  echo "native binary differs from the runtime"; failed=1
fi
# A native binary published without the step for F# union fields refuses
# its union, naming it, its assembly and the step (tests/Plasm.Aot.Unpatched).
echo "--- unpatched: publishing without Plasm's step for F# union fields"
unpatched="$here/../Plasm.Aot.Unpatched"
dotnet publish "$unpatched" -c Release -r "$rid" -v q -o "$work/unpatched" > "$work/unpatched-publish.log" 2>&1 || { cat "$work/unpatched-publish.log"; exit 1; }
set +e
"$work/unpatched/Plasm.Aot.Unpatched" > "$work/unpatched.txt" 2>&1
code=$?
set -e
cat "$work/unpatched.txt"
if [ "$code" -eq 0 ]; then
  echo "the unpatched binary built its model"; failed=1
elif ! grep -q "UnpatchedId.UnpatchedId has fields that this native binary cannot see: its assembly Plasm.Aot.Unpatched was compiled without Plasm's build step" "$work/unpatched.txt"; then
  echo "the unpatched binary did not refuse its union by name, assembly and step"; failed=1
else
  echo "the unpatched binary refuses its union by name, assembly and step"
fi

exit $failed
