#!/bin/bash
# The seam from Plasm.CSharp's package: a C# host with a Seam.cs over an F#
# data project, both restoring Plasm only from the feed $1 (at version $2).
# Its build writes the C# side into the host's Seam folder (the views, the
# mapping, a class over the F# Orders, the JSON context) and seam.lock, and
# compiles them with the host. Built again unchanged, the seam is skipped and
# no file is rewritten; with a member added to the F# type, it runs again and
# says what changed in the contract; in CI (ContinuousIntegrationBuild=true)
# such a change stops the build instead, naming it. The host prints what it is given as the
# seam's source-generated JSON, under the runtime and as a native binary,
# which must print the same, warning only FSharp.Core's two summary lines.
# Two builds must fail, though the host does not treat warnings as errors: a
# switch that misses a case of the seam's union (CS8509), and an endpoint
# whose body is a record no JSON context lists (SEAM030).
# Run by tests/Plasm.Package/run.sh; with --pack in place of a feed, it packs
# Plasm.Annotations, Plasm and Plasm.CSharp first.
#   tests/Plasm.Package/seam-tool/run.sh <feed> <version>
#   tests/Plasm.Package/seam-tool/run.sh --pack
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-seam-tool.XXXXXX")" && pwd -P)"
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/packages"
if [ "${1:-}" = "--pack" ]; then
  feed="$work/feed"; version="0.0.1-packagecheck"
  for p in Plasm.Annotations Plasm Plasm.CSharp; do
    dotnet build "$repo/src/$p" -c Release -p:Version="$version" -p:ProduceReferenceAssembly=false -v q -m:1 -nodeReuse:false > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -p:Version="$version" -p:ProduceReferenceAssembly=false -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
  echo "seam-tool: packed Plasm.Annotations, Plasm and Plasm.CSharp at $version"
else
  feed="$(cd "$1" && pwd -P)"; version="$2"
fi
[ -f "$feed/Plasm.CSharp.$version.nupkg" ] || { echo "seam-tool: $feed has no Plasm.CSharp.$version.nupkg"; exit 1; }
app="$work/app"
mkdir -p "$app"
cp -R "$here/Data" "$here/Api" "$app/"
rm -rf "$app/Data/bin" "$app/Data/obj" "$app/Api/bin" "$app/Api/obj" "$app/Api/Seam" "$app/Api/seam.lock"
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
case "$app" in "$repo"/*) echo "seam-tool: the application is inside the repository"; exit 1;; esac
failed=0
check() { # a condition, and what it shows
  if eval "$1"; then echo "seam-tool: $2"; else echo "seam-tool FAILED: $2"; failed=1; fi
}
build() { # a log name; the host built at the feed's version, with the seam's lines kept
  dotnet build "$app/Api" -c Release -p:PlasmVersion="$version" -v n -m:1 -nodeReuse:false > "$work/$1.log" 2>&1 || { echo "seam-tool: the host did not build ($1)"; grep -E ' error ' "$work/$1.log" | sed 's/\[.*//' | sort -u | head -10; exit 1; }
}
times() { (cd "$app/Api" && ls -l --time-style=+%s.%N Seam/*.g.cs seam.lock 2>/dev/null || stat -f '%m %N' Seam/*.g.cs seam.lock); }

build first
check '[ -f "$app/Api/Seam/Views.g.cs" ] && [ -f "$app/Api/Seam/SeamMap.g.cs" ] && [ -f "$app/Api/Seam/SeamJson.g.cs" ] && [ -f "$app/Api/Seam/Orders.g.cs" ]' \
  "the first build writes Views, SeamMap, SeamJson and Orders into the host's Seam folder"
check 'grep -q "Plasm.Seam: wrote .*seam.lock, the contract of the generated C#. Commit it." "$work/first.log"' "and writes seam.lock, saying so"
check 'grep -qF "public sealed record OrderView(" "$app/Api/Seam/Views.g.cs" && grep -qF "public union StatusView(Open, Shipped, Cancelled);" "$app/Api/Seam/Views.g.cs"' \
  "Views.g.cs is readable C#: a record per F# record, a union per F# union"
check '! grep -q "SeamHttp" "$work/first.log" && [ ! -f "$app/Api/Seam/SeamHttp.g.cs" ]' "a host that is not a web project gets no HTTP helper"

before=$(times)
build again
check 'grep -q "Skipping target \"PlasmSeamGenerate\" because all output files are up-to-date" "$work/again.log"' "built again unchanged, the seam is skipped"
check '[ "$(times)" = "$before" ]' "and no file in Seam, nor seam.lock, is rewritten"

python3 - "$app/Data/Orders.fs" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
end = '    member _.OrdersAsync(cancellationToken: CancellationToken) : Task<Order list> = Task.FromResult orders\n'
assert s.count(end) == 1, "OrdersAsync is not where the check expects it"
open(p, 'w').write(s.replace(end, end + '\n    member _.CountAsync(cancellationToken: CancellationToken) : Task<int> = Task.FromResult orders.Length\n'))
PY
build changed
check 'grep -q "Plasm.Seam: the contract changed, and seam.lock is rewritten (1 change). Commit it." "$work/changed.log"' "with a member added to the F# type, the seam runs again and rewrites seam.lock"
check 'grep -qF "Plasm.Seam:   added to class Orders = Shop.Orders: Task<int> CountAsync(CancellationToken cancellationToken)" "$work/changed.log"' "naming what changed"
check 'grep -qF "public Task<int> CountAsync(CancellationToken cancellationToken)" "$app/Api/Seam/Orders.g.cs"' "and the C# class has the new member"

# Red: a switch over the seam's union without one case does not build, though
# the host does not treat warnings as errors: the package makes CS8509 one.
cp -R "$app" "$work/red-switch"
python3 - "$work/red-switch/Api/Program.cs" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
arm = '    Open => "open",\n'
assert s.count(arm) == 1, "the Open arm is not where the check expects it"
open(p, 'w').write(s.replace(arm, ''))
PY
if dotnet build "$work/red-switch/Api" -c Release -p:PlasmVersion="$version" -v q -m:1 -nodeReuse:false > "$work/red-switch.log" 2>&1; then
  echo "seam-tool FAILED: a switch over StatusView without its Open arm built"; failed=1
elif grep -q "error CS8509.*'Shop.Api.Open' is not covered" "$work/red-switch.log"; then
  echo "seam-tool: a switch over StatusView without its Open arm is refused: error CS8509, naming Shop.Api.Open, with warnings not errors"
else
  echo "seam-tool FAILED: the switch without its Open arm failed otherwise:"; grep -E ' error ' "$work/red-switch.log" | sed 's/\[.*//' | sort -u | head -5; failed=1
fi
# Red: an endpoint whose body is a record no JSON context lists does not
# build (SEAM030, Plasm.CSharp's analyzer); listed in Seam.cs, it builds.
cp -R "$app" "$work/red-json"
cat > "$work/red-json/Api/Endpoints.cs" <<'CS'
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Shop.Api;

public static class Endpoints
{
    public static void Map(WebApplication app) =>
        app.MapPost("/orders", (NewOrder input, CancellationToken ct) => Results.Ok());
}

public sealed record NewOrder(string Customer);
CS
python3 - "$work/red-json/Api/Api.csproj" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
end = '  <ItemGroup>\n    <ProjectReference'
assert s.count(end) == 1
open(p, 'w').write(s.replace(end, '  <ItemGroup>\n    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n  </ItemGroup>\n' + end))
PY
if dotnet build "$work/red-json/Api" -c Release -p:PlasmVersion="$version" -v q -m:1 -nodeReuse:false > "$work/red-json.log" 2>&1; then
  echo "seam-tool FAILED: a POST whose body NewOrder no JSON context lists built"; failed=1
elif grep -q "Endpoints.cs(9,42): error SEAM030: NewOrder is the body of POST /orders, and no JSON context lists it" "$work/red-json.log"; then
  echo "seam-tool: a POST whose body no JSON context lists is refused at build: SEAM030 at the parameter, naming NewOrder"
else
  echo "seam-tool FAILED: the unlisted body failed otherwise:"; grep -E ' error ' "$work/red-json.log" | sed 's/\[.*//' | sort -u | head -5; failed=1
fi
printf '\n[assembly: SeamJson(typeof(Shop.Api.NewOrder))]\n' >> "$work/red-json/Api/Seam.cs"
if dotnet build "$work/red-json/Api" -c Release -p:PlasmVersion="$version" -v q -m:1 -nodeReuse:false > "$work/green-json.log" 2>&1; then
  echo "seam-tool: listed in Seam.cs with SeamJson, it builds"
else
  echo "seam-tool FAILED: with NewOrder listed in Seam.cs, the host did not build:"; grep -E ' error ' "$work/green-json.log" | sed 's/\[.*//' | sort -u | head -5; failed=1
fi

# In CI (ContinuousIntegrationBuild=true) a change to the contract stops the
# build, naming it, and seam.lock is left as it was.
python3 - "$app/Data/Orders.fs" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
end = '    member _.CountAsync(cancellationToken: CancellationToken) : Task<int> = Task.FromResult orders.Length\n'
assert s.count(end) == 1, "CountAsync is not where the check expects it"
open(p, 'w').write(s.replace(end, end + '\n    member _.EmptyAsync(cancellationToken: CancellationToken) : Task<bool> = Task.FromResult orders.IsEmpty\n'))
PY
cp "$app/Api/seam.lock" "$work/seam.lock.before"
if dotnet build "$app/Api" -c Release -p:PlasmVersion="$version" -p:ContinuousIntegrationBuild=true -v q -m:1 -nodeReuse:false > "$work/ci.log" 2>&1; then
  echo "seam-tool FAILED: a CI build that changes the contract built"; failed=1
elif grep -qF "seam.lock : error SEAM020: the contract would change: added to class Orders = Shop.Orders: Task<bool> EmptyAsync(CancellationToken cancellationToken)" "$work/ci.log"; then
  echo "seam-tool: a CI build that would change the contract stops, naming the change (SEAM020)"
else
  echo "seam-tool FAILED: the CI build failed otherwise:"; grep -E ' error ' "$work/ci.log" | sed 's/\[.*//' | sort -u | head -5; failed=1
fi
check 'cmp -s "$app/Api/seam.lock" "$work/seam.lock.before"' "and leaves seam.lock as it was"

rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet build "$app/Api" -c Release -p:PlasmVersion="$version" -o "$work/jit" -v q -m:1 -nodeReuse:false > "$work/jit-build.log" 2>&1 || { echo "seam-tool: the host did not build"; grep -E ' error ' "$work/jit-build.log" | sed 's/\[.*//' | sort -u | head; exit 1; }
dotnet "$work/jit/Api.dll" > "$work/jit.txt" 2>&1 || true
dotnet publish "$app/Api" -c Release -r "$rid" -p:PlasmVersion="$version" -o "$work/native" -v m -m:1 -nodeReuse:false > "$work/native.log" 2>&1 || { tail -30 "$work/native.log"; exit 1; }
"$work/native/Api" > "$work/native.txt" 2>&1 || true
echo "--- the seam host on $rid, runtime"; cat "$work/jit.txt"
expect() { # what the host must print, under the runtime
  if grep -qxF "$1" "$work/jit.txt"; then echo "seam-tool: $2"; else echo "seam-tool FAILED: $2"; failed=1; fi
}
expect 'found {"id":"01900000-0000-7000-8000-000000000001","customer":"Ada","note":"by the door","lines":[{"part":"bolt","quantity":4}],"status":{"kind":"shipped","at":"2026-11-24T09:00:00+00:00"},"version":2}' \
  "a read is Found of the view: the typed id its Guid, the option its value, the list an array, the union by kind"
expect 'missing {"what":"order"}' "a read of none is Missing"
expect 'all [{"id":"01900000-0000-7000-8000-000000000001","customer":"Ada","note":"by the door","lines":[{"part":"bolt","quantity":4}],"status":{"kind":"shipped","at":"2026-11-24T09:00:00+00:00"},"version":2},{"id":"01900000-0000-7000-8000-000000000002","customer":"Grace","note":null,"lines":[],"status":{"kind":"cancelled","reason":"out of stock"},"version":1}]' \
  "an F# list of records is an array of views, an option of none null"
expect 'added {"id":"01900000-0000-7000-8000-000000000003","customer":"Edsger","note":null,"lines":[],"status":{"kind":"open"},"version":1}' "a C# null is an F# None, and a case without fields is its kind alone"
expect 'invalid {"problems":[{"field":"customer","message":"is required"}]}' "Invalid crosses as it is"
expect 'read back: cancelled lost' "a union is read back by its kind"
if diff -u "$work/jit.txt" "$work/native.txt"; then echo "seam-tool: the native host prints what the runtime prints"; else echo "seam-tool: the native host differs from the runtime"; failed=1; fi
others=$(grep 'warning IL' "$work/native.log" | grep -v -E 'IL2104|IL3053' || true)
if [ -n "$others" ]; then echo "seam-tool: the native publish warns beyond FSharp.Core's two summary lines:"; printf '%s\n' "$others" | sed 's/\[.*//' | sort -u | head; failed=1; else echo "seam-tool: the native publish warns only FSharp.Core's two summary lines"; fi
exit $failed
