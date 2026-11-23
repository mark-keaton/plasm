#!/bin/bash
# Plasm.CSharp from its package (docs/csharp.md): a C# host over an F# data
# layer on Plasm.Sqlite, both restoring Plasm only from the feed $1 (at version
# $2). The C# compiler must treat the package's unions as closed: with the
# Missing arm removed from a switch over Found, the build fails naming
# Plasm.CSharp.Missing. Built whole, the host prints each case it is given
# (Added, Invalid, Found, Missing, and Stale for a typed id and for a key of
# two parts, one holding a "/") as source-generated JSON, and reads a union
# back through a classifier that inherits ByKind. It does so under the runtime
# and as a native binary, which must print the same, warning only
# FSharp.Core's two summary lines.
# Run by tests/Plasm.Package/run.sh; with --pack in place of a feed, it packs
# Plasm.Annotations, Plasm, Plasm.Sqlite and Plasm.CSharp first.
#   tests/Plasm.Package/seam/run.sh <feed> <version>
#   tests/Plasm.Package/seam/run.sh --pack
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
work="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/plasm-seam.XXXXXX")" && pwd -P)"
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/packages"
if [ "${1:-}" = "--pack" ]; then
  feed="$work/feed"; version="0.0.1-packagecheck"
  for p in Plasm.Annotations Plasm Plasm.Sqlite Plasm.CSharp; do
    dotnet build "$repo/src/$p" -c Release -p:Version="$version" -p:ProduceReferenceAssembly=false -v q -m:1 -nodeReuse:false > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
    dotnet pack "$repo/src/$p" -c Release --no-build -p:Version="$version" -p:ProduceReferenceAssembly=false -o "$feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
  done
  echo "seam: packed Plasm.Annotations, Plasm, Plasm.Sqlite and Plasm.CSharp at $version"
else
  feed="$(cd "$1" && pwd -P)"; version="$2"
fi
[ -f "$feed/Plasm.CSharp.$version.nupkg" ] || { echo "seam: $feed has no Plasm.CSharp.$version.nupkg"; exit 1; }
app="$work/app"
mkdir -p "$app"
cp -R "$here/Data" "$here/App" "$app/"
rm -rf "$app/Data/bin" "$app/Data/obj" "$app/App/bin" "$app/App/obj"
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
case "$app" in "$repo"/*) echo "seam: the application is inside the repository"; exit 1;; esac
failed=0
# Red: a switch over Found without its Missing arm is refused, by name.
cp -R "$app" "$work/red"
python3 - "$work/red/App/Program.cs" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
arm = '    Missing m => "missing " + JsonSerializer.Serialize(m, json.Missing),\n};\n\nstring Changed'
assert s.count(arm) == 1, "the Missing arm of Found's switch is not where the check expects it"
open(p, 'w').write(s.replace(arm, '};\n\nstring Changed'))
PY
if dotnet build "$work/red/App" -c Release -p:PlasmVersion="$version" -v q -m:1 -nodeReuse:false > "$work/red.log" 2>&1; then
  echo "seam: a switch over Found without its Missing arm built"; failed=1
elif grep -q "error CS8509.*'Plasm.CSharp.Missing' is not covered" "$work/red.log"; then
  echo "seam: a switch over Found without its Missing arm is refused: CS8509, naming Plasm.CSharp.Missing"
else
  echo "seam: a switch over Found without its Missing arm failed otherwise:"; grep -E ' error ' "$work/red.log" | sed 's/\[.*//' | sort -u | head -5; failed=1
fi
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
dotnet build "$app/App" -c Release -p:PlasmVersion="$version" -o "$work/jit" -v q -m:1 -nodeReuse:false > "$work/build.log" 2>&1 || { echo "seam: the host did not build"; grep -E ' error ' "$work/build.log" | sed 's/\[.*//' | sort -u | head -10; exit 1; }
echo "seam: the host builds, switching over Found, Added and Changed with no default arm"
dotnet "$work/jit/App.dll" "$work/jit.db" > "$work/jit.txt" 2>&1 || true
dotnet publish "$app/App" -c Release -r "$rid" -p:PlasmVersion="$version" -o "$work/native" -v m -m:1 -nodeReuse:false > "$work/native.log" 2>&1 || { tail -30 "$work/native.log"; exit 1; }
"$work/native/App" "$work/native.db" > "$work/native.txt" 2>&1 || true
echo "--- the seam host on $rid, runtime"; cat "$work/jit.txt"
expect() { # what the host must print, under the runtime
  if grep -qxF "$1" "$work/jit.txt"; then echo "seam: $2"; else echo "seam FAILED: $2"; failed=1; fi
}
expect 'added {"id":"01900000-0000-7000-8000-000000000001","title":"Groceries","condition":{"kind":"withdrawn","reason":"kept"}}' "an add is Added, the note as stored, its union written by kind"
expect 'invalid {"problems":[{"field":"title","message":"is already taken"}]}' "an add the database refuses is Invalid, the field camel-cased"
expect 'missing {"what":"note"}' "a read of no note is Missing"
expect 'stale {"what":"Note","key":["01900000-0000-7000-8000-000000000001"]}' "a stale copy is Stale, its typed id unwrapped"
expect 'stale {"what":"Filed","key":["a/b","7"]}' "a stale key of two parts is each part, the \"/\" kept in its part"
expect 'read back: Withdrawn lamp' "a union is read back through a classifier that inherits ByKind"
if diff -u "$work/jit.txt" "$work/native.txt"; then echo "seam: the native host prints what the runtime prints"; else echo "seam: the native host differs from the runtime"; failed=1; fi
others=$(grep 'warning IL' "$work/native.log" | grep -v -E 'IL2104|IL3053' || true)
if [ -n "$others" ]; then echo "seam: the native publish warns beyond FSharp.Core's two summary lines:"; printf '%s\n' "$others" | sed 's/\[.*//' | sort -u | head; failed=1; else echo "seam: the native publish warns only FSharp.Core's two summary lines"; fi
exit $failed
