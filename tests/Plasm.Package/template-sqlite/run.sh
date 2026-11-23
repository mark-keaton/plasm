#!/bin/bash
# `dotnet new plasm-api --database sqlite` from the packed template: an
# application with no database server. The made project must name no
# PostgreSQL and keep no trace of the Starter's switch; its tests pass under
# the runtime and as a native binary, with no Docker; its API prints the same
# DDL both ways, warning only FSharp.Core's two summary lines. Then the
# database file, as its native API meets it:
# - outside Development, a missing file is refused at start, naming the path
#   it looked for and the command that makes it, and no file is made;
# - `create-database` makes it, and refuses a file that exists, leaving it as
#   it was;
# - in Development, a missing file is made at start, the output naming its full
#   path, and the API serves equipment added over HTTP.
# Run by tests/Plasm.Package/run.sh on the machine, and by
# tests/Plasm.Providers/run-linux.sh in a Linux container; with --pack in place
# of a feed, it packs Plasm.Annotations, Plasm, Plasm.Sqlite and
# Plasm.Templates from the repository first.
#   tests/Plasm.Package/template-sqlite/run.sh <feed> <version>
#   tests/Plasm.Package/template-sqlite/run.sh --pack
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-template-sqlite.XXXXXX")" && pwd -P)"
api=""
trap '[ -n "$api" ] && kill "$api" 2>/dev/null; rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/packages"
if [ "${1:-}" = "--pack" ]; then
  feed="$work/feed"; version="0.0.1-packagecheck"
  for p in Plasm.Annotations Plasm Plasm.Sqlite; do
    dotnet build "$repo/src/$p" -c Release -p:Version="$version" -p:ProduceReferenceAssembly=false -v q -m:1 -nodeReuse:false > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -p:Version="$version" -p:ProduceReferenceAssembly=false -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
  dotnet pack "$repo/src/Plasm.Templates" -c Release -p:Version="$version" -o "$feed" -v q -m:1 -nodeReuse:false > "$work/pack-Plasm.Templates.log" 2>&1 || { cat "$work/pack-Plasm.Templates.log"; exit 1; }
  echo "template-sqlite: packed Plasm.Annotations, Plasm, Plasm.Sqlite and Plasm.Templates at $version"
else
  feed="$(cd "$1" && pwd -P)"; version="$2"
fi
tpl="$feed/Plasm.Templates.$version.nupkg"
[ -f "$tpl" ] || { echo "template-sqlite: $feed has no Plasm.Templates.$version.nupkg"; exit 1; }
hive="$work/hive"; gen="$work/made"
dotnet new install "$tpl" --debug:custom-hive "$hive" > "$work/install.log" 2>&1 || { cat "$work/install.log"; exit 1; }
dotnet new plasm-api -n Lending -o "$gen" --database sqlite --debug:custom-hive "$hive" > "$work/new.log" 2>&1 || { echo "template-sqlite: plasm-api made no SQLite project:"; cat "$work/new.log"; exit 1; }
failed=0
left=$(grep -r -l -i -E 'starter|postgres|npgsql|testcontainers' "$gen" || true; find "$gen" -iname '*starter*')
if [ -n "$left" ]; then echo "template-sqlite: Starter or PostgreSQL is left in:"; echo "$left"; failed=1; fi
switch=$(grep -r -n -E 'LendingDatabase|STARTER_|LENDING_|#if|#else|#endif|<!--#' "$gen" || true)
if [ -n "$switch" ]; then echo "template-sqlite: the Starter's switch is left in:"; echo "$switch"; failed=1; fi
if ! grep -q "Include=\"Plasm.Sqlite\" Version=\"$version\"" "$gen/Directory.Packages.props"; then echo "template-sqlite: the project does not reference Plasm.Sqlite $version"; cat "$gen/Directory.Packages.props"; failed=1; fi
[ "$failed" = 0 ] && echo "template-sqlite: Lending, from plasm-api --database sqlite: no Starter, no PostgreSQL, no switch, Plasm.Sqlite $version"
cat > "$gen/nuget.config" <<XML
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
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
cd "$gen"
dotnet test --project tests/Lending.Tests > "$work/test.log" 2>&1 || { echo "template-sqlite: the made project's tests failed"; tail -30 "$work/test.log"; failed=1; }
echo "template-sqlite: tests, runtime: $(grep -E '^\s+(total|failed):' "$work/test.log" | tr -s ' ' | tr '\n' ' ')"
dotnet publish tests/Lending.Tests -c Release -r "$rid" -o "$work/tests-native" -v m -m:1 -nodeReuse:false > "$work/tests-native.log" 2>&1 || { tail -30 "$work/tests-native.log"; exit 1; }
"$work/tests-native/Lending.Tests" > "$work/tests-native.txt" 2>&1 || { echo "template-sqlite: the made project's native tests failed"; tail -30 "$work/tests-native.txt"; failed=1; }
echo "template-sqlite: tests, native: $(grep -o 'Total: [0-9]*, Errors: [0-9]*, Failed: [0-9]*' "$work/tests-native.txt" || echo none)"
dotnet build src/Lending.Api -c Release -o "$work/api-jit" -v q -m:1 -nodeReuse:false > "$work/api-build.log" 2>&1 || { cat "$work/api-build.log"; exit 1; }
dotnet publish src/Lending.Api -c Release -r "$rid" -o "$work/api-native" -v m -m:1 -nodeReuse:false > "$work/api-native.log" 2>&1 || { tail -30 "$work/api-native.log"; exit 1; }
dotnet "$work/api-jit/Lending.Api.dll" ddl > "$work/ddl-jit.txt" 2>&1 || true
"$work/api-native/Lending.Api" ddl > "$work/ddl-native.txt" 2>&1 || true
echo "--- the made SQLite project's API, native: its DDL"; cat "$work/ddl-native.txt"
if ! grep -q -i '^create table equipment' "$work/ddl-native.txt"; then echo "template-sqlite: the native API did not print its DDL"; failed=1; fi
if diff -u "$work/ddl-jit.txt" "$work/ddl-native.txt"; then echo "template-sqlite: the native API prints the DDL the runtime prints"; else echo "template-sqlite: the native API's DDL differs from the runtime's"; failed=1; fi
others=$(grep 'warning IL' "$work/api-native.log" | grep -v -E 'IL2104|IL3053' || true)
if [ -n "$others" ]; then echo "template-sqlite: the API's publish warns beyond the summary lines"; printf '%s\n' "$others" | sort -u; failed=1; else echo "template-sqlite: the API publishes natively, warning only FSharp.Core's two summary lines"; fi
bin="$work/api-native/Lending.Api"
cd "$work"
# Every start of the API has a deadline. A host still running at its deadline
# is killed, and the command counts as timed out (124), which fails the
# scenario: a check that hangs is a failed check.
deadline=60
bounded() { # <output file> <command...>
  local out="$1"; shift
  "$@" > "$out" 2>&1 &
  api=$!
  local half=0
  while kill -0 "$api" 2>/dev/null; do
    if [ "$half" -ge $((deadline * 2)) ]; then
      kill "$api" 2>/dev/null; sleep 1; kill -9 "$api" 2>/dev/null || true
      wait "$api" 2>/dev/null || true; api=""
      echo "template-sqlite: still running after ${deadline}s, so killed" >> "$out"
      return 124
    fi
    sleep 0.5; half=$((half + 1))
  done
  local code=0; wait "$api" || code=$?; api=""
  return "$code"
}

# Outside Development, a missing file is refused, and none is made.
mkdir -p "$work/prod" && cd "$work/prod"
set +e; bounded "$work/prod.txt" env ASPNETCORE_ENVIRONMENT=Production "$bin" --urls http://127.0.0.1:0; code=$?; set -e
cd "$work"
echo "--- outside Development, no file (exit $code)"; cat "$work/prod.txt"
out=$(cat "$work/prod.txt")
if [ "$code" != 0 ] && [ "$code" != 124 ] && [[ "$out" == *"$work/prod/lending.db"* ]] && [[ "$out" == *"create-database"* ]] && [ ! -e "$work/prod/lending.db" ]; then
  echo "scenario: outside Development, a missing file is refused, naming its path and create-database, and none is made"
else
  echo "scenario FAILED: outside Development, a missing file is refused, naming its path and create-database, and none is made"; failed=1
fi

# create-database makes the file, and refuses it once it exists.
mkdir -p "$work/made-db" && cd "$work/made-db"
set +e; bounded "$work/create.txt" "$bin" create-database; first=$?; set -e
cp lending.db "$work/after-create.db" 2>/dev/null || true
set +e; bounded "$work/again.txt" "$bin" create-database; second=$?; set -e
cd "$work"
echo "--- create-database (exit $first)"; cat "$work/create.txt"
echo "--- create-database again (exit $second)"; cat "$work/again.txt"
if [ "$first" = 0 ] && [ -f "$work/made-db/lending.db" ] && [[ "$(cat "$work/create.txt")" == *"$work/made-db/lending.db"* ]]; then
  echo "scenario: create-database makes the file, naming its full path"
else
  echo "scenario FAILED: create-database makes the file, naming its full path"; failed=1
fi
if [ "$second" != 0 ] && [ "$second" != 124 ] && [[ "$(cat "$work/again.txt")" == *"$work/made-db/lending.db"* ]] && cmp -s "$work/after-create.db" "$work/made-db/lending.db"; then
  echo "scenario: create-database refuses a file that exists, and leaves it as it was"
else
  echo "scenario FAILED: create-database refuses a file that exists, and leaves it as it was"; failed=1
fi
# An empty file, which the DDL would take without an error: the refusal is the
# command's own, not SQLite's on tables that exist.
mkdir -p "$work/empty" && cd "$work/empty" && : > lending.db
set +e; bounded "$work/empty.txt" "$bin" create-database; third=$?; set -e
cd "$work"
echo "--- create-database on an empty file (exit $third)"; cat "$work/empty.txt"
if [ "$third" != 0 ] && [ "$third" != 124 ] && [[ "$(cat "$work/empty.txt")" == *"$work/empty/lending.db exists"* ]] && [ ! -s "$work/empty/lending.db" ]; then
  echo "scenario: create-database refuses an empty file that exists, and leaves it empty"
else
  echo "scenario FAILED: create-database refuses an empty file that exists, and leaves it empty"; failed=1
fi

# In Development a missing file is made at start, and the API serves.
mkdir -p "$work/dev" && cd "$work/dev"
# Port 0: the API takes a free port, and says which.
ASPNETCORE_ENVIRONMENT=Development "$bin" --urls "http://127.0.0.1:0" > "$work/dev.txt" 2>&1 &
api=$!
cd "$work"
up=0; port=""
for _ in $(seq 1 120); do
  port=$(sed -n 's|.*Now listening on: http://127\.0\.0\.1:\([0-9]*\).*|\1|p' "$work/dev.txt" | head -1)
  if [ -n "$port" ] && curl -s -o /dev/null "http://127.0.0.1:$port/equipment/00000000-0000-0000-0000-000000000000"; then up=1; break; fi
  kill -0 "$api" 2>/dev/null || break
  sleep 0.5
done
added=""; read=""
if [ "$up" = 1 ]; then
  added=$(curl -s -X POST -H 'Content-Type: application/json' -d '{"name":"Projector"}' "http://127.0.0.1:$port/equipment")
  id=$(printf '%s' "$added" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')
  read=$(curl -s "http://127.0.0.1:$port/equipment/$id")
fi
kill "$api" 2>/dev/null || true; sleep 1; kill -9 "$api" 2>/dev/null || true; wait "$api" 2>/dev/null || true; api=""
echo "--- Development, no file"; cat "$work/dev.txt"
echo "added: $added"; echo "read: $read"
if [ -f "$work/dev/lending.db" ] && [[ "$(cat "$work/dev.txt")" == *"$work/dev/lending.db"* ]] && [[ "$read" == *'"name":"Projector"'* ]]; then
  echo "scenario: in Development a missing file is made at start, its full path printed, and the API serves"
else
  echo "scenario FAILED: in Development a missing file is made at start, its full path printed, and the API serves"; failed=1
fi
exit $failed
