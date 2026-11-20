#!/bin/bash
# The package form (docs/aot.md): Plasm's packages, packed from this
# repository into a local feed, are all an application outside the repository
# needs to publish natively with its union fields readable. The consumer is
# copied to a temporary folder with its own package cache, restores Plasm only
# from that feed, and is run under the runtime and as a native binary; the two
# must print the same, and the model must be built; published without Plasm's
# step for union fields, the binary must refuse its union by name. The step's
# build cost is measured: the publish's time with and without it, and the
# step's own. examples/Starter, an F# library with a C# API and C# tests over
# it, is built from the same feed: its API natively, and its tests under the
# runtime and natively (Docker).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
# Its real path: on macOS the temporary folder is under /var, a symlink to
# /private/var, and NuGet's restore of a C# project then loses its reference to
# an F# project (MSBuild names one by each path), and with it the packages.
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-package.XXXXXX")" && pwd -P)"
trap 'rm -rf "$work"' EXIT
feed="$work/feed"; app="$work/app"; export NUGET_PACKAGES="$work/packages"

packages="Plasm.Annotations Plasm Plasm.Postgres Plasm.HotChocolate"
if [ -n "${PLASM_FEED:-}" ]; then
  # A release checks the packages it will push: PLASM_FEED is a folder holding
  # the four, packed already, and nothing is packed here.
  feed="$(cd "$PLASM_FEED" && pwd -P)"
  echo "pack: none; the feed is PLASM_FEED, packed already"
else
  echo "pack: $packages into a local feed, built first and packed without building, as CI packs"
  for p in $packages; do
    dotnet build "$repo/src/$p" -c Release -v q > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
fi
version=$(ls "$feed" | sed -n 's/^Plasm\.Postgres\.\(.*\)\.nupkg$/\1/p' | head -1)
if [ -z "$version" ]; then echo "pack: no Plasm.Postgres package in $feed"; exit 1; fi
for p in $packages; do
  if [ ! -f "$feed/$p.$version.nupkg" ]; then echo "pack: $feed has no $p.$version.nupkg"; exit 1; fi
  # A version on nuget.org cannot be replaced: no build output may ride along.
  leaked=$(unzip -Z1 "$feed/$p.$version.nupkg" | grep -E '(^|/)(obj|bin)/' || true)
  if [ -n "$leaked" ]; then echo "pack: $p carries build output:"; echo "$leaked"; exit 1; fi
done
echo "pack: version $version; no package carries obj/ or bin/"
# The packages depend on the floor, FSharp.Core $(PlasmFSharpCoreFloor) or later,
# whatever the repository's own suites run on (docs/status.md).
floor=$(sed -n 's/.*<PlasmFSharpCoreFloor>\(.*\)<\/PlasmFSharpCoreFloor>.*/\1/p' "$repo/Directory.Build.props")
for p in $packages; do
  declared=$(unzip -p "$feed/$p.$version.nupkg" "$p.nuspec" | sed -n 's/.*<dependency id="FSharp.Core" version="\([^"]*\)".*/\1/p')
  if [ "$declared" != "$floor" ]; then echo "pack: $p depends on FSharp.Core $declared, not the floor $floor"; exit 1; fi
done
echo "pack: every package depends on FSharp.Core $floor or later"
echo "pack: version $version; Plasm's package carries: $(unzip -Z1 "$feed/Plasm.$version.nupkg" | grep -E '^(build|buildTransitive|tools)/' | tr '\n' ' ')"

mkdir -p "$app"
cp "$here/consumer/Consumer.fsproj" "$here/consumer/Program.fs" "$app/"
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
case "$app" in "$repo"/*) echo "the consumer is inside the repository"; exit 1;; esac

rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
echo "runtime: building the consumer"
dotnet build "$app" -c Release -p:PlasmVersion="$version" -o "$work/jit" -v q > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
dotnet "$work/jit/Consumer.dll" > "$work/jit.txt" 2>&1 || true

publish() { # name, extra properties; the native compile runs afresh each time
  local start end
  rm -rf "$app/obj/Release/net11.0/$rid/native" "$app/obj/Release/net11.0/$rid/plasm-aot"
  start=$(python3 -c 'import time; print(time.time())')
  dotnet publish "$app" -c Release -r "$rid" -p:PlasmVersion="$version" "${@:2}" -o "$work/$1" -clp:PerformanceSummary -v m > "$work/$1.log" 2>&1 || { tail -30 "$work/$1.log"; exit 1; }
  end=$(python3 -c 'import time; print(time.time())')
  python3 -c "print(round($end - $start, 1))"
}
# Restored and built once, so the two timed publishes differ only by the step.
dotnet publish "$app" -c Release -r "$rid" -p:PlasmVersion="$version" -o "$work/warm" -v q > "$work/warm.log" 2>&1 || { tail -30 "$work/warm.log"; exit 1; }
without=$(publish without -p:PlasmFieldGetters=false)
with=$(publish native)
step=$(grep -E '^ +[0-9]+ ms +PlasmFieldGetters +1 calls' "$work/native.log" | awk '{print $1}' | head -1 || true)
echo "native: publish ${with} s with Plasm's step, ${without} s without; the step itself ${step:-no} ms"
"$work/native/Consumer" > "$work/native.txt" 2>&1 || true
"$work/without/Consumer" > "$work/without.txt" 2>&1 || true

echo "--- runtime"; cat "$work/jit.txt"
echo "--- native"; cat "$work/native.txt"
failed=0
if ! grep -q "^model: built" "$work/native.txt"; then echo "the native binary did not build its model"; failed=1; fi
echo "--- native, without Plasm's step"; cat "$work/without.txt"
if ! grep -q "TicketId.TicketId has fields that this native binary cannot see" "$work/without.txt"; then echo "without the step, the binary did not refuse its union by name"; failed=1; fi
if diff -u "$work/jit.txt" "$work/native.txt"; then echo "native binary prints what the runtime prints"; else echo "native binary differs from the runtime"; failed=1; fi

# examples/Starter (docs/aot.md, "From the packages"): an F# library on
# Plasm.Postgres's package, and a C# minimal API and C# tests that reference
# only it, with PublishAot and no Plasm reference of their own; the library's
# package carries Plasm's build step to their publishes. Copied as a user
# would have it, except that it restores Plasm from this feed, copied into its
# packages/, at the feed's version, in place of nuget.org's.
st="$work/starter"
rsync -a --exclude bin --exclude obj --exclude TestResults --exclude packages --exclude .nuget "$repo/examples/Starter/" "$st/"
mkdir -p "$st/packages"
cp "$feed"/*.nupkg "$st/packages/"
cat > "$st/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Plasm" />
      <package pattern="Plasm.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML
sed -i.orig -E 's|(Include="Plasm.Postgres" Version=")[^"]*"|\1'"$version"'"|' "$st/Directory.Packages.props"
rm "$st/Directory.Packages.props.orig"
if ! grep -q "Include=\"Plasm.Postgres\" Version=\"$version\"" "$st/Directory.Packages.props"; then echo "Starter's Directory.Packages.props names no Plasm.Postgres version to replace"; exit 1; fi
cd "$st"
dotnet build src/Starter.Api -c Release -o "$work/st-jit" -v q > "$work/st-build.log" 2>&1 || { cat "$work/st-build.log"; exit 1; }
dotnet "$work/st-jit/Starter.Api.dll" ddl > "$work/st-jit.txt" 2>&1 || true
dotnet publish src/Starter.Api -c Release -r "$rid" -o "$work/st-native" -v m > "$work/st-native.log" 2>&1 || { tail -30 "$work/st-native.log"; exit 1; }
rm -rf "src/Starter.Api/obj/Release/net11.0/$rid/native" "src/Starter.Api/obj/Release/net11.0/$rid/plasm-aot"
dotnet publish src/Starter.Api -c Release -r "$rid" -p:PlasmFieldGetters=false -o "$work/st-without" -v m > "$work/st-without.log" 2>&1 || { tail -30 "$work/st-without.log"; exit 1; }
"$work/st-native/Starter.Api" ddl > "$work/st-native.txt" 2>&1 || true
"$work/st-without/Starter.Api" ddl > "$work/st-without.txt" 2>&1 || true
echo "--- Starter's API, runtime: its DDL"; cat "$work/st-jit.txt"
echo "--- Starter's API, native ($(grep 'warning IL' "$work/st-native.log" | sort -u | wc -l | tr -d ' ') IL warnings, $(grep 'warning IL' "$work/st-native.log" | grep -v -E 'IL2104|IL3053' | sort -u | wc -l | tr -d ' ') other than the summary lines)"
if ! grep -q '^create table equipment' "$work/st-native.txt"; then echo "Starter's native binary did not print its DDL"; cat "$work/st-native.txt"; failed=1; fi
if grep 'warning IL' "$work/st-native.log" | grep -v -E 'IL2104|IL3053' | grep -q .; then echo "Starter's publish warns beyond the summary lines"; failed=1; fi
if diff -u "$work/st-jit.txt" "$work/st-native.txt"; then echo "Starter's native binary prints what the runtime prints"; else echo "Starter's native binary differs from the runtime"; failed=1; fi
if grep -q "EquipmentId.EquipmentId has fields that this native binary cannot see" "$work/st-without.txt"; then echo "without the step, Starter's binary refuses its union by name"; else echo "without the step, Starter's binary did not refuse its union by name"; cat "$work/st-without.txt"; failed=1; fi
dotnet test --project tests/Starter.Tests > "$work/st-test.log" 2>&1 || { echo "Starter's tests failed"; tail -30 "$work/st-test.log"; failed=1; }
echo "Starter's tests, runtime: $(grep -E '^\s+(total|failed):' "$work/st-test.log" | tr -s ' ' | tr '\n' ' ')"
dotnet publish tests/Starter.Tests -c Release -r "$rid" -o "$work/st-tests-native" -v m > "$work/st-tests-native.log" 2>&1 || { tail -30 "$work/st-tests-native.log"; exit 1; }
"$work/st-tests-native/Starter.Tests" > "$work/st-tests-native.txt" 2>&1 || { echo "Starter's native tests failed"; tail -30 "$work/st-tests-native.txt"; failed=1; }
echo "Starter's tests, native: $(grep -o 'Total: [0-9]*, Errors: [0-9]*, Failed: [0-9]*' "$work/st-tests-native.txt")"
cd "$repo"

# A GraphQL application on Plasm.HotChocolate, from the same feed.
gql="$work/graphql"
mkdir -p "$gql"
cp "$here/graphql/GraphQLConsumer.fsproj" "$here/graphql/Program.fs" "$app/nuget.config" "$gql/"
dotnet build "$gql" -c Release -p:PlasmVersion="$version" -o "$work/gql-out" -v q > "$work/gql-build.log" 2>&1 || { cat "$work/gql-build.log"; exit 1; }
dotnet "$work/gql-out/GraphQLConsumer.dll" > "$work/gql.txt" 2>&1 || true
echo "--- GraphQL consumer"; cat "$work/gql.txt"
expected='{"data":{"sql":"SELECT \"t\".\"id\", \"t\".\"title\", \"t\".\"points\" FROM \"tickets\" AS \"t\" WHERE \"t\".\"points\" > @p0 ORDER BY \"t\".\"points\" DESC"}}'
if [ "$(cat "$work/gql.txt")" = "$expected" ]; then echo "the GraphQL consumer's where and order are Plasm's SQL"; else echo "the GraphQL consumer answered otherwise"; failed=1; fi
exit $failed
