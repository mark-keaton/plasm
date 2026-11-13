#!/bin/bash
# stage.sh <site-dir>: builds the koans site as static files in <site-dir>:
# the published .NET app, PGlite, the compiler's reference assemblies and course.json.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
site=$1
mkdir -p "$site"
# A fresh publish folder: publishing into an old one keeps the old hashed files.
pub=$(mktemp -d)
trap 'rm -rf "${pub:?}"' EXIT
dotnet publish "$here/Koans.Browser.Host" -c Release -o "$pub" -v q
rsync -a --delete "$pub/wwwroot/" "$site/"
(cd "$here" && [ -d node_modules/@electric-sql/pglite ] || npm install --no-audit --no-fund --silent)
rsync -a --delete --exclude "*.map" --exclude "*.tar.gz" "$here/node_modules/@electric-sql/pglite/dist/" "$site/pglite/"
mkdir -p "$site/refs"
cp ~/.dotnet/packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0/*.dll "$site/refs/"
# The compiler references what the page runs: FSharp.Core, Plasm and the koans' runtime.
# (Published assemblies carry a content hash: <name>.<hash>.dll.)
for dll in FSharp.Core Plasm Plasm.Koans.Core; do
  file=$(ls "$site/_framework" | grep -E "^${dll//./\\.}\.[a-z0-9]+\.dll$")
  cp "$site/_framework/$file" "$site/refs/$dll.dll"
done
(cd "$site/refs" && ls *.dll > list.txt)
dotnet run --project "$root/koans/Plasm.Koans" -- --course "$site/course.json" > /dev/null
# Plasm's acceptance specification, which each koan links to.
mkdir -p "$site/spec" && cp "$root"/features/*.feature "$site/spec/"
# Precompress what publish did not, and list every file for offline use.
node "$here/compress.mjs" "$site" > /dev/null
echo "staged $site"
