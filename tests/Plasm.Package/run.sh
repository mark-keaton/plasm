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
# runtime and natively (Docker). Plasm.Templates is that Starter as a
# `dotnet new` template; a project made from it is tested and published too.
# An application on Plasm.Sqlite writes and reads a SQLite file under the
# runtime and natively, and SQLite's native library arrives for each platform.
# The Starter's tests also run on SQLite, and a project made with
# `--database sqlite` is tested, published, and run against its database file
# (template-sqlite/run.sh). Plasm.CSharp gives a C# host over an F# data
# layer closed unions, refused by name when a switch misses a case, and
# source-generated JSON, natively too (seam/run.sh), and the seam, which
# writes a host's C# side of its data layer at build (seam-tool/run.sh).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
# Its real path: on macOS the temporary folder is under /var, a symlink to
# /private/var, and NuGet's restore of a C# project then loses its reference to
# an F# project (MSBuild names one by each path), and with it the packages.
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-package.XXXXXX")" && pwd -P)"
trap 'rm -rf "$work"' EXIT
feed="$work/feed"; app="$work/app"; export NUGET_PACKAGES="$work/packages"

packages="Plasm.Annotations Plasm Plasm.Postgres Plasm.Sqlite Plasm.HotChocolate Plasm.CSharp"
if [ -n "${PLASM_FEED:-}" ]; then
  # A release checks the packages it will push: PLASM_FEED is a folder holding
  # the six and Plasm.Templates, packed already, and nothing is packed here.
  feed="$(cd "$PLASM_FEED" && pwd -P)"
  echo "pack: none; the feed is PLASM_FEED, packed already"
else
  # At a version of the check's own, unlike the default a local pack makes
  # (0.1.0-alpha) and unlike the Starter's, so nothing passes because two
  # versions happen to be the same.
  checked="0.0.1-packagecheck"
  # Without reference assemblies: a build at another version (the AOT check's,
  # at the default) leaves ref/ assemblies that a version alone does not
  # replace, and Plasm would be compiled against Plasm.Annotations 0.1.0.0.
  noref="-p:ProduceReferenceAssembly=false"
  echo "pack: $packages and Plasm.Templates into a local feed at $checked, built first and packed without building, as CI packs"
  for p in $packages; do
    dotnet build "$repo/src/$p" -c Release -p:Version="$checked" $noref -v q > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -p:Version="$checked" $noref -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
  # The template has no build output; it packs the Starter's files.
  dotnet pack "$repo/src/Plasm.Templates" -c Release -p:Version="$checked" -o "$feed" -v q > "$work/pack-Plasm.Templates.log" 2>&1 || { cat "$work/pack-Plasm.Templates.log"; exit 1; }
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
# The feed is exactly the seven pushed: the six libraries and the template.
pushed=$(cd "$feed" && ls *.nupkg | sed "s/\.$version\.nupkg$//" | sort | tr '\n' ' ')
expected=$(printf '%s\n' $packages Plasm.Templates | sort | tr '\n' ' ')
if [ "$pushed" != "$expected" ]; then echo "pack: the feed holds $pushed, not $expected"; exit 1; fi
if [ "$(echo $expected | wc -w | tr -d ' ')" != 7 ]; then echo "pack: $expected is not seven packages"; exit 1; fi
echo "pack: the feed is exactly the seven packages $expected"
# Plasm.Sqlite: its assembly, its documentation and the readme, and nothing
# else; it depends on Plasm at the version packed and on Microsoft.Data.Sqlite
# at the repository's version, which brings SQLite's native library (checked
# below, for each platform, from a consumer's restore).
sq="$feed/Plasm.Sqlite.$version.nupkg"
sqfiles=$(unzip -Z1 "$sq" | grep -v -E '^(_rels/|package/|\[Content_Types\]\.xml$|Plasm\.Sqlite\.nuspec$)' | sort | tr '\n' ' ')
if [ "$sqfiles" != "README.md lib/net11.0/Plasm.Sqlite.dll lib/net11.0/Plasm.Sqlite.xml " ]; then echo "pack: Plasm.Sqlite carries $sqfiles"; exit 1; fi
sqspec=$(unzip -p "$sq" Plasm.Sqlite.nuspec)
mds=$(sed -n 's/.*Include="Microsoft.Data.Sqlite" Version="\([^"]*\)".*/\1/p' "$repo/Directory.Packages.props")
deps=$(printf '%s\n' "$sqspec" | grep -o '<dependency id="[^"]*" version="[^"]*"' | sed 's/<dependency id="\([^"]*\)" version="\([^"]*\)"/\1 \2/' | sort | tr '\n' ';')
floor0=$(sed -n 's/.*<PlasmFSharpCoreFloor>\(.*\)<\/PlasmFSharpCoreFloor>.*/\1/p' "$repo/Directory.Build.props")
want_deps=$(printf '%s\n' "FSharp.Core $floor0" "Microsoft.Data.Sqlite $mds" "Plasm $version" | sort | tr '\n' ';')
if [ "$deps" != "$want_deps" ]; then echo "pack: Plasm.Sqlite depends on $deps, not $want_deps"; exit 1; fi
if [[ "$sqspec" != *"Plasm is new"* ]]; then echo "pack: Plasm.Sqlite's release notes do not say Plasm is new"; exit 1; fi
# In the shell, not by a pipe into grep -q (as the template's check, below).
sqreadme=$(unzip -p "$sq" README.md)
if [[ "$sqreadme" =~ [0-9]+\.[0-9]+\.[0-9]+ ]]; then echo "pack: Plasm.Sqlite's readme names a version: ${BASH_REMATCH[0]}"; exit 1; fi
if [[ "$sqreadme" != *"Plasm is new"* ]]; then echo "pack: Plasm.Sqlite's readme does not say Plasm is new"; exit 1; fi
echo "pack: Plasm.Sqlite carries $sqfiles; depends on $deps"
# Plasm.CSharp: its assembly, its documentation and the readme, and the seam:
# its build targets (build/, buildTransitive/), the tool they run, built
# with what it needs to run (tools/seam/), and its analyzer; it depends on Plasm alone (and
# FSharp.Core), so a host of either provider takes it.
cs="$feed/Plasm.CSharp.$version.nupkg"
csfiles=$(unzip -Z1 "$cs" | grep -v -E '^(_rels/|package/|\[Content_Types\]\.xml$|Plasm\.CSharp\.nuspec$)' | sort | tr '\n' ' ')
want_csfiles="README.md analyzers/dotnet/cs/Plasm.Seam.Analyzers.dll build/Plasm.CSharp.targets buildTransitive/Plasm.CSharp.targets lib/net11.0/Plasm.CSharp.dll lib/net11.0/Plasm.CSharp.xml tools/seam/FSharp.Core.dll tools/seam/Microsoft.CodeAnalysis.CSharp.dll tools/seam/Microsoft.CodeAnalysis.dll tools/seam/Plasm.Seam.deps.json tools/seam/Plasm.Seam.dll tools/seam/Plasm.Seam.runtimeconfig.json tools/seam/System.Reflection.MetadataLoadContext.dll "
if [ "$csfiles" != "$want_csfiles" ]; then echo "pack: Plasm.CSharp carries $csfiles"; exit 1; fi
csdeps=$(unzip -p "$cs" Plasm.CSharp.nuspec | grep -o '<dependency id="[^"]*" version="[^"]*"' | sed 's/<dependency id="\([^"]*\)" version="\([^"]*\)"/\1 \2/' | sort | tr '\n' ';')
want_csdeps=$(printf '%s\n' "FSharp.Core $floor0" "Plasm $version" | sort | tr '\n' ';')
if [ "$csdeps" != "$want_csdeps" ]; then echo "pack: Plasm.CSharp depends on $csdeps, not $want_csdeps"; exit 1; fi
echo "pack: Plasm.CSharp carries $csfiles; depends on $csdeps"
# The template's projects reference Plasm at the template's version: its
# template.json replaces the Starter's version with this one.
tplconfig=$(unzip -p "$feed/Plasm.Templates.$version.nupkg" content/Starter/.template.config/template.json 2>/dev/null || true)
starterversion=$(sed -n 's/.*Include="Plasm.Postgres" Version="\([^"]*\)".*/\1/p' "$repo/examples/Starter/Directory.Packages.props")
# Matched in the shell, not by a pipe into grep -q: under pipefail, grep -q
# leaving early can fail the pipe, and the check would answer the wrong way.
if [[ "$tplconfig" != *"\"value\": \"$version\""* || "$tplconfig" != *"\"replaces\": \"$starterversion\""* ]]; then
  echo "pack: Plasm.Templates' template.json does not replace the Starter's $starterversion with $version:"; printf '%s\n' "$tplconfig" | grep -E '"(value|replaces)"'; exit 1
fi
echo "pack: Plasm.Templates replaces the Starter's Plasm.Postgres $starterversion with $version"
# The packages depend on the floor, FSharp.Core $(PlasmFSharpCoreFloor) or later,
# whatever the repository's own suites run on (docs/status.md).
floor=$(sed -n 's/.*<PlasmFSharpCoreFloor>\(.*\)<\/PlasmFSharpCoreFloor>.*/\1/p' "$repo/Directory.Build.props")
for p in $packages; do
  declared=$(unzip -p "$feed/$p.$version.nupkg" "$p.nuspec" | sed -n 's/.*<dependency id="FSharp.Core" version="\([^"]*\)".*/\1/p')
  if [ "$declared" != "$floor" ]; then echo "pack: $p depends on FSharp.Core $declared, not the floor $floor"; exit 1; fi
done
echo "pack: every package depends on FSharp.Core $floor or later"
# A packed XML doc describes only what a caller can reach: no internal or
# private member (src/reachable-docs.fsx, which the pack filters each with).
docs=0
for p in $packages; do
  mkdir "$work/docs-$p"
  unzip -q "$feed/$p.$version.nupkg" "lib/net11.0/$p.dll" "lib/net11.0/$p.xml" -d "$work/docs-$p"
  dotnet fsi --quiet "$repo/src/reachable-docs.fsx" check "$work/docs-$p/lib/net11.0/$p.dll" "$work/docs-$p/lib/net11.0/$p.xml" || { echo "pack: $p's XML doc describes members no caller can reach"; exit 1; }
  docs=$((docs + $(grep -c '<member ' "$work/docs-$p/lib/net11.0/$p.xml")))
done
echo "pack: every package's XML doc describes only members a caller can reach ($docs in all)"
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
rsync -a --exclude bin --exclude obj --exclude TestResults --exclude packages --exclude .nuget --exclude "Seam/*.g.cs" "$repo/examples/Starter/" "$st/"
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
sed -i.orig -E 's#(Include="Plasm\.(Postgres|Sqlite|CSharp)" Version=")[^"]*"#\1'"$version"'"#' "$st/Directory.Packages.props"
rm "$st/Directory.Packages.props.orig"
for p in Postgres Sqlite CSharp; do
  if ! grep -q "Include=\"Plasm.$p\" Version=\"$version\"" "$st/Directory.Packages.props"; then echo "Starter's Directory.Packages.props names no Plasm.$p version to replace"; exit 1; fi
done
cd "$st"
# The contract the Starter commits is the one its build writes, for each
# database: a build that may not change seam.lock (as on a CI server) passes.
for db in postgres sqlite; do
  dotnet build src/Starter.Api -p:StarterDatabase=$db -p:PlasmSeamDrift=fail -v q > "$work/st-lock-$db.log" 2>&1 || { echo "Starter's $db build changes its committed contract:"; grep -E ' error ' "$work/st-lock-$db.log" | sed 's/\[.*//' | sort -u | head; failed=1; }
done
[ "$failed" = 0 ] && echo "Starter's committed contracts (seam.lock, seam.sqlite.lock) are what its builds write"
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
others=$(grep 'warning IL' "$work/st-native.log" | grep -v -E 'IL2104|IL3053' || true)
if [ -n "$others" ]; then echo "Starter's publish warns beyond the summary lines"; failed=1; fi
if diff -u "$work/st-jit.txt" "$work/st-native.txt"; then echo "Starter's native binary prints what the runtime prints"; else echo "Starter's native binary differs from the runtime"; failed=1; fi
if grep -q "EquipmentId.EquipmentId has fields that this native binary cannot see" "$work/st-without.txt"; then echo "without the step, Starter's binary refuses its union by name"; else echo "without the step, Starter's binary did not refuse its union by name"; cat "$work/st-without.txt"; failed=1; fi
dotnet test --project tests/Starter.Tests > "$work/st-test.log" 2>&1 || { echo "Starter's tests failed"; tail -30 "$work/st-test.log"; failed=1; }
echo "Starter's tests, runtime: $(grep -E '^\s+(total|failed):' "$work/st-test.log" | tr -s ' ' | tr '\n' ' ')"
dotnet publish tests/Starter.Tests -c Release -r "$rid" -o "$work/st-tests-native" -v m > "$work/st-tests-native.log" 2>&1 || { tail -30 "$work/st-tests-native.log"; exit 1; }
"$work/st-tests-native/Starter.Tests" > "$work/st-tests-native.txt" 2>&1 || { echo "Starter's native tests failed"; tail -30 "$work/st-tests-native.txt"; failed=1; }
echo "Starter's tests, native: $(grep -o 'Total: [0-9]*, Errors: [0-9]*, Failed: [0-9]*' "$work/st-tests-native.txt")"
# The same copy built for SQLite (README: -p:StarterDatabase=sqlite), as CI's
# Starter job also tests it: a file, no Docker.
dotnet test --project tests/Starter.Tests -p:StarterDatabase=sqlite > "$work/st-sqlite-test.log" 2>&1 || { echo "Starter's tests on SQLite failed"; tail -30 "$work/st-sqlite-test.log"; failed=1; }
echo "Starter's tests on SQLite, runtime: $(grep -E '^\s+(total|failed):' "$work/st-sqlite-test.log" | tr -s ' ' | tr '\n' ' ')"
cd "$repo"

# Plasm.Templates (`dotnet new plasm-api`): its content is examples/Starter's
# own files, byte for byte, and nothing else. Installed into a hive of its own,
# a project is made from it under another name outside the repository; the
# names are replaced and it references Plasm at the template's version, from
# nuget.org alone. The check, not the project, then adds this feed (the
# consumer's nuget.config), since the version is not on nuget.org yet; its
# tests pass and its API publishes natively.
tpl="$feed/Plasm.Templates.$version.nupkg"
if [ ! -f "$tpl" ]; then echo "template: $feed has no Plasm.Templates.$version.nupkg"; exit 1; fi
# The Starter's tracked files; in an exported tree, with no git, its files
# but what a build or a run leaves (bin, obj, TestResults, packages, .nuget,
# and the C# the seam writes, Seam/*.g.cs).
if git -C "$repo" rev-parse --is-inside-work-tree > /dev/null 2>&1; then
  want=$(git -C "$repo" ls-files examples/Starter | sed 's|^examples/Starter/||' | sort)
else
  want=$(cd "$repo/examples/Starter" && find . -type f -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/TestResults/*' -not -path './packages/*' -not -path './.nuget/*' -not -path './src/*/Seam/*.g.cs' | sed 's|^\./||' | sort)
fi
have=$(unzip -Z1 "$tpl" | sed -n 's|^content/Starter/||p' | grep -v '^\.template\.config/' | grep -v '/$' | sort)
if [ "$want" != "$have" ]; then echo "template: its files are not the Starter's"; diff <(echo "$want") <(echo "$have"); exit 1; fi
for f in $want; do
  if ! unzip -p "$tpl" "content/Starter/$f" | cmp -s - "$repo/examples/Starter/$f"; then echo "template: content/Starter/$f differs from examples/Starter/$f"; exit 1; fi
done
echo "template: version $version; its $(echo "$want" | wc -l | tr -d ' ') files are examples/Starter's"
hive="$work/hive"; gen="$work/made"
dotnet new install "$tpl" --debug:custom-hive "$hive" > "$work/tpl-install.log" 2>&1 || { cat "$work/tpl-install.log"; exit 1; }
dotnet new plasm-api -n Lending -o "$gen" --debug:custom-hive "$hive" > "$work/tpl-new.log" 2>&1 || { cat "$work/tpl-new.log"; exit 1; }
left=$(grep -r -l -i starter "$gen" || true; find "$gen" -iname '*starter*')
if [ -n "$left" ]; then echo "template: Starter is left in:"; echo "$left"; exit 1; fi
if ! grep -q "Include=\"Plasm.CSharp\" Version=\"$version\"" "$gen/Directory.Packages.props"; then echo "template: the project does not reference Plasm.CSharp $version"; exit 1; fi
if ! grep -q "Include=\"Plasm.Postgres\" Version=\"$version\"" "$gen/Directory.Packages.props"; then echo "template: the project does not reference Plasm.Postgres $version"; cat "$gen/Directory.Packages.props"; exit 1; fi
if [ "$(grep -c '<add key=' "$gen/nuget.config")" != 1 ] || ! grep -q 'api.nuget.org' "$gen/nuget.config"; then echo "template: the project's nuget.config is not nuget.org alone"; cat "$gen/nuget.config"; exit 1; fi
# Made for PostgreSQL, the default: no SQLite, and nothing of the Starter's
# switch between the two.
other=$(grep -r -l -i -E 'sqlite' "$gen" || true)
if [ -n "$other" ]; then echo "template: SQLite is left in the PostgreSQL project:"; echo "$other"; exit 1; fi
switch=$(grep -r -n -E 'LendingDatabase|STARTER_|LENDING_|#if|#else|#endif|<!--#' "$gen" || true)
if [ -n "$switch" ]; then echo "template: the Starter's switch is left in:"; echo "$switch"; exit 1; fi
echo "template: Lending, from plasm-api: no Starter left, no SQLite and no switch, Plasm.Postgres $version, nuget.org alone"
cp "$app/nuget.config" "$gen/nuget.config"
cd "$gen"
# The project comes with its contract: a build that may not change seam.lock passes.
if dotnet build src/Lending.Api -p:PlasmSeamDrift=fail -v q > "$work/tpl-lock.log" 2>&1; then echo "template: the made project's seam.lock is the contract its build writes"; else echo "template: the made project's build changes its seam.lock:"; grep -E ' error ' "$work/tpl-lock.log" | sed 's/\[.*//' | sort -u | head; failed=1; fi
dotnet test --project tests/Lending.Tests > "$work/tpl-test.log" 2>&1 || { echo "the made project's tests failed"; tail -30 "$work/tpl-test.log"; failed=1; }
echo "the made project's tests: $(grep -E '^\s+(total|failed):' "$work/tpl-test.log" | tr -s ' ' | tr '\n' ' ')"
dotnet publish src/Lending.Api -c Release -r "$rid" -o "$work/tpl-native" -v m > "$work/tpl-native.log" 2>&1 || { tail -30 "$work/tpl-native.log"; exit 1; }
"$work/tpl-native/Lending.Api" ddl > "$work/tpl-native.txt" 2>&1 || true
if ! grep -q '^create table equipment' "$work/tpl-native.txt"; then echo "the made project's native binary did not print its DDL"; cat "$work/tpl-native.txt"; failed=1; fi
others=$(grep 'warning IL' "$work/tpl-native.log" | grep -v -E 'IL2104|IL3053' || true)
echo "--- the made project's API, native ($(grep -c 'warning IL' "$work/tpl-native.log" || true) IL warning lines, $(printf '%s' "$others" | grep -c . || true) other than the summary lines)"
if [ -n "$others" ]; then echo "the made project's publish warns beyond the summary lines"; grep 'warning IL' "$work/tpl-native.log" | sort -u; failed=1; else echo "the made project's API publishes natively, warning only FSharp.Core's two summary lines"; fi
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

# A SQLite application on Plasm.Sqlite, from the same feed
# (tests/Plasm.Package/sqlite/run.sh): a file made from the model's DDL, a
# decimal, a time, a list and a document written and read back, under the
# runtime and as a native binary with no warning naming Plasm, SQLite's native
# library beside it. run-linux.sh runs the same on Linux, from a feed packed
# there; here the library is also checked to arrive in a publish for each
# other platform Plasm is checked on.
bash "$here/sqlite/run.sh" "$feed" "$version" || failed=1

# `dotnet new plasm-api --database sqlite`, from the same feed
# (tests/Plasm.Package/template-sqlite/run.sh): its tests, its API natively,
# and the database file as the API meets it, missing or there.
bash "$here/template-sqlite/run.sh" "$feed" "$version" || failed=1

# Plasm.CSharp, from the same feed (tests/Plasm.Package/seam/run.sh): a C#
# switch over FindResult without its Missing arm is refused by name; the host's
# cases, Stale's key parts among them, as JSON, the same natively.
bash "$here/seam/run.sh" "$feed" "$version" || failed=1

# The seam, from the same feed (tests/Plasm.Package/seam-tool/run.sh): a C#
# host's build writes its side of the data layer and seam.lock, skips the
# seam when nothing changed, says what changed when the F# did, and prints
# the same as JSON under the runtime and natively.
bash "$here/seam-tool/run.sh" "$feed" "$version" || failed=1
sqa="$work/sqlite-app"
mkdir -p "$sqa"
cp "$here/sqlite/SqliteConsumer.fsproj" "$here/sqlite/Program.fs" "$app/nuget.config" "$sqa/"
native_lib() { case "$1" in osx-*) echo libe_sqlite3.dylib;; *) echo libe_sqlite3.so;; esac; }
for other in osx-arm64 linux-x64 linux-arm64; do
  [ "$other" = "$rid" ] && continue
  dotnet publish "$sqa" -c Release -r "$other" --self-contained false -p:PublishAot=false -p:PlasmVersion="$version" -o "$work/sq-$other" -v q > "$work/sq-$other.log" 2>&1 || { tail -30 "$work/sq-$other.log"; exit 1; }
  if [ -f "$work/sq-$other/$(native_lib "$other")" ]; then echo "SQLite's native library arrives for $other"; else echo "no $(native_lib "$other") in the $other publish"; failed=1; fi
done
exit $failed
