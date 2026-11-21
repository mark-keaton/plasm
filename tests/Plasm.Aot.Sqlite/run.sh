#!/bin/bash
# Plasm.Sqlite's native-AOT check (docs/aot.md): runs tests/Plasm.Aot.Sqlite
# under the runtime and as a native binary, each on a SQLite file of its own,
# and compares what they print. Exits non-zero when the outputs differ, a step
# throws, or the publish warns naming Plasm.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
echo "runtime: building"
dotnet build "$here" -c Release -v q -o "$work/jit" > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
echo "native: publishing for $rid"
dotnet publish "$here" -c Release -r "$rid" -p:PublishAot=true -v m -o "$work/native" > "$work/publish.log" 2>&1 || { cat "$work/publish.log"; exit 1; }
jit_mode=$(dotnet "$work/jit/Plasm.Aot.Sqlite.dll" mode)
native_mode=$("$work/native/Plasm.Aot.Sqlite" mode)
echo "runtime: $jit_mode; native: $native_mode"
if [ "$jit_mode" != "dynamic code on" ] || [ "$native_mode" != "dynamic code off" ]; then
  echo "the runtime run is not the runtime as shipped, or the native run is not native"; exit 1
fi
dotnet "$work/jit/Plasm.Aot.Sqlite.dll" "$work/jit.db" > "$work/jit.txt" 2>&1 || true
"$work/native/Plasm.Aot.Sqlite" "$work/native.db" > "$work/native.txt" 2>&1 || true
echo "--- runtime"; cat "$work/jit.txt"
echo "--- native"; cat "$work/native.txt"
echo "native binary: $(du -h "$work/native/Plasm.Aot.Sqlite" | cut -f1)"
failed=0
echo "native: $(grep -c 'warning IL' "$work/publish.log" || true) IL warnings"
plasm_warnings=$(grep "warning IL" "$work/publish.log" | grep -cE "/src/Plasm|Plasm[A-Za-z.]*\.dll" || true)
echo "native: $plasm_warnings IL warnings naming Plasm"
[ "$plasm_warnings" -eq 0 ] || { echo "the publish has IL warnings naming Plasm"; failed=1; }
grep -q ": threw " "$work/jit.txt" && { echo "a step threw under the runtime"; failed=1; }
if diff -u "$work/jit.txt" "$work/native.txt"; then
  echo "native binary prints what the runtime prints"
else
  echo "native binary differs from the runtime"; failed=1
fi
exit $failed
