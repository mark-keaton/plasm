#!/bin/bash
# An application on Plasm.Sqlite from the packages alone (docs/sqlite.md): it
# restores Plasm only from the feed $1 (at version $2), makes a SQLite file
# from its model's DDL, and writes and reads back a decimal, a time, a list and
# a document, under the runtime and as a native binary, which must print the
# same, with no IL warning naming Plasm, and find SQLite's native library.
# Run by tests/Plasm.Package/run.sh on the machine, and by
# tests/Plasm.Providers/run-linux.sh in a Linux container from a feed packed
# there; with --pack in place of a feed, it packs Plasm.Annotations, Plasm and
# Plasm.Sqlite from the repository first.
#   tests/Plasm.Package/sqlite/run.sh <feed> <version>
#   tests/Plasm.Package/sqlite/run.sh --pack
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-sqlite-consumer.XXXXXX")" && pwd -P)"
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/packages"
if [ "${1:-}" = "--pack" ]; then
  feed="$work/feed"; version="0.0.1-packagecheck"
  for p in Plasm.Annotations Plasm Plasm.Sqlite; do
    dotnet build "$repo/src/$p" -c Release -p:Version="$version" -p:ProduceReferenceAssembly=false -v q -m:1 -nodeReuse:false > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -p:Version="$version" -p:ProduceReferenceAssembly=false -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
  echo "sqlite consumer: packed Plasm.Annotations, Plasm and Plasm.Sqlite at $version"
else
  feed="$(cd "$1" && pwd -P)"; version="$2"
fi
[ -f "$feed/Plasm.Sqlite.$version.nupkg" ] || { echo "sqlite consumer: $feed has no Plasm.Sqlite.$version.nupkg"; exit 1; }
app="$work/app"
mkdir -p "$app"
cp "$here/SqliteConsumer.fsproj" "$here/Program.fs" "$app/"
cat > "$app/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Plasm" /><package pattern="Plasm.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML
case "$app" in "$repo"/*) echo "sqlite consumer: the application is inside the repository"; exit 1;; esac
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet build "$app" -c Release -p:PlasmVersion="$version" -o "$work/jit" -v q -m:1 -nodeReuse:false > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
dotnet "$work/jit/SqliteConsumer.dll" "$work/jit.db" > "$work/jit.txt" 2>&1 || true
dotnet publish "$app" -c Release -r "$rid" -p:PlasmVersion="$version" -o "$work/native" -v m -m:1 -nodeReuse:false > "$work/native.log" 2>&1 || { tail -30 "$work/native.log"; exit 1; }
"$work/native/SqliteConsumer" "$work/native.db" > "$work/native.txt" 2>&1 || true
echo "--- SQLite consumer on $rid, runtime"; cat "$work/jit.txt"
echo "--- SQLite consumer on $rid, native"; cat "$work/native.txt"
failed=0
if ! grep -q '^read: as written$' "$work/jit.txt"; then echo "the SQLite consumer did not read back what it wrote under the runtime"; failed=1; fi
if ! grep -q '^read: as written$' "$work/native.txt"; then echo "the SQLite consumer's native binary did not read back what it wrote"; failed=1; fi
if diff -u "$work/jit.txt" "$work/native.txt"; then echo "the SQLite consumer's native binary prints what the runtime prints"; else echo "the SQLite consumer's native binary differs from the runtime"; failed=1; fi
plasm=$(grep 'warning IL' "$work/native.log" | grep -c 'Plasm' || true)
echo "the SQLite consumer, native: $(grep -c 'warning IL' "$work/native.log" || true) IL warning lines, $plasm naming Plasm"
if [ "$plasm" != 0 ]; then grep 'warning IL' "$work/native.log" | grep 'Plasm' | sort -u; failed=1; fi
case "$rid" in osx-*) lib=libe_sqlite3.dylib;; win-*) lib=e_sqlite3.dll;; *) lib=libe_sqlite3.so;; esac
if [ -f "$work/native/$lib" ]; then echo "SQLite's native library is beside the $rid binary"; else echo "no $lib beside the $rid binary"; failed=1; fi
exit $failed
