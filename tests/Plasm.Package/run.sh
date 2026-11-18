#!/bin/bash
# The package form (docs/aot.md): Plasm's packages, packed from this
# repository into a local feed, are all an application outside the repository
# needs to publish natively with its union fields readable. The consumer is
# copied to a temporary folder with its own package cache, restores Plasm only
# from that feed, and is run under the runtime and as a native binary; the two
# must print the same, and the model must be built; published without Plasm's
# step for union fields, the binary must refuse its union by name. The step's
# build cost is measured: the publish's time with and without it, and the
# step's own.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/plasm-package.XXXXXX")"
trap 'rm -rf "$work"' EXIT
feed="$work/feed"; app="$work/app"; export NUGET_PACKAGES="$work/packages"

echo "pack: Plasm.Annotations, Plasm, Plasm.Postgres, Plasm.HotChocolate into a local feed, built first and packed without building, as CI packs"
for p in Plasm.Annotations Plasm Plasm.Postgres Plasm.HotChocolate; do
  dotnet build "$repo/src/$p" -c Release -v q > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
  dotnet pack "$repo/src/$p" -c Release --no-build -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
done
version=$(ls "$feed" | sed -n 's/^Plasm\.Postgres\.\(.*\)\.nupkg$/\1/p' | head -1)
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
