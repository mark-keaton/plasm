#!/bin/bash
# Keeps Plasm's trimming suppressions honest (docs/aot.md, "Trimming
# warnings"): publishes the native check with Plasm compiled without
# src/Plasm/Trimming.fs's suppressions, collects each (module, warning) that
# warns, and fails unless that set is exactly the set Trimming.fs suppresses.
# A minute; run it when Plasm's reflection changes, not with every check.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
rid=$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)
echo "publishing without Plasm's suppressions (about a minute)"
dotnet publish "$here" -c Release -r "$rid" -v m -p:PlasmTrimAudit=true -o "$work/native" > "$work/publish.log" 2>&1 || { cat "$work/publish.log"; exit 1; }

# warning ILnnnn: Plasm.Module[.Nested].member...: the top-level module, which
# a suppression scoped to its type covers with everything nested in it (its
# closures, its nested modules and types).
grep "warning IL" "$work/publish.log" | grep "/src/Plasm/" | sed 's/ \[.*//' | sort -u |
  sed -E 's/.*warning (IL[0-9]+): (Plasm\.[A-Za-z]+).*/\2 \1/' |
  sort -u > "$work/warns.txt"

sed -n 's/.*UnconditionalSuppressMessage("[A-Za-z]*", "\(IL[0-9]*\)", Scope = "type", Target = "T:\([^"]*\)".*/\2 \1/p' "$root/src/Plasm/Trimming.fs" |
  sort -u > "$work/suppressed.txt"

echo "$(wc -l < "$work/warns.txt" | tr -d ' ') (module, warning) pairs warn; $(wc -l < "$work/suppressed.txt" | tr -d ' ') are suppressed"
if diff -u "$work/suppressed.txt" "$work/warns.txt"; then
  echo "every warning is suppressed, and every suppression has a warning"
else
  echo "Trimming.fs does not match what warns (- suppressed only, + warns only)"
  exit 1
fi
