#!/bin/bash
# stage.sh <site-dir>: builds the koans site as static files in <site-dir>:
# the published .NET app, PGlite, the compiler's reference assemblies and course.json.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
site=$1
mkdir -p "$site"
dotnet publish "$here/Koans.Browser.Host" -c Release -o "$here/obj/publish" -v q
rsync -a "$here/obj/publish/wwwroot/" "$site/"
(cd "$here" && [ -d node_modules/@electric-sql/pglite ] || npm install --no-audit --no-fund --silent)
rsync -a "$here/node_modules/@electric-sql/pglite/dist/" "$site/pglite/"
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
echo "staged $site"
