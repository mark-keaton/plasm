#!/bin/bash
# Runs the reproduction under the runtime, as a native binary, and as a native
# binary of the same IL with only SpecialName set on the property getters.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
cd "$here"
rm -rf bin obj out patched
echo "--- runtime"; dotnet run -c Release -v q
echo "--- native"; dotnet publish -c Release -r "$rid" -o out -v q > /dev/null; ./out/Repro
echo "--- native, getters marked SpecialName"
dotnet build -c Release -r "$rid" -v q > /dev/null
dotnet fsi patch.fsx "obj/Release/net11.0/$rid/Repro.dll"
dotnet publish -c Release -r "$rid" --no-build -o patched -v q > /dev/null; ./patched/Repro
