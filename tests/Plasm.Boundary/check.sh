#!/bin/bash
# The core holds no PostgreSQL-only SQL (docs/status.md: "Database-specific SQL
# comes from ISqlDialect or is guarded by a capability"). Each construct
# below is searched for in src/Plasm, comments left out; one is allowed only
# where allowed.txt says how many times, in which file, and why. A new one,
# or one more than allowed, fails the check: move it behind the dialect, or
# guard it by a capability and say so in allowed.txt.
#   check.sh [repository root]
set -uo pipefail
root="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
here="$(cd "$(dirname "$0")" && pwd)"
patterns=(
  'unnest\(' '= ANY\(' '<> ALL\(' 'cardinality\(' 'information_schema' 'pg_[a-z_]+'
  'current_schema' 'jsonb' 'ARRAY\[' 'generated always' 'create type' 'alter table'
  'IS (NOT )?DISTINCT FROM' 'AS text' 'CYCLE ' 'SchemaOnly' '42P18' 'ILIKE' '::[a-z]'
  'string_agg' 'format_type'
)
failed=0
for pat in "${patterns[@]}"; do
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    found=$(grep -E -v '^\s*//' "$root/$file" | grep -c -E "$pat" || true)
    allowed=$(grep -F "	$file	$pat	" "$here/allowed.txt" | cut -f1)
    allowed=${allowed:-0}
    if [ "$found" -gt "$allowed" ]; then
      echo "boundary: $file has $found of /$pat/, $allowed allowed:"
      grep -n -E "$pat" "$root/$file" | grep -v -E '^[0-9]+:\s*//' | head -5 | sed 's/^/    /'
      failed=1
    fi
  done < <(cd "$root" && grep -l -E "$pat" src/Plasm/*.fs 2>/dev/null)
done
[ $failed -eq 0 ] && echo "boundary: src/Plasm holds no PostgreSQL-only SQL beyond the $(grep -c -v -E '^(#|$)' "$here/allowed.txt") allowed in tests/Plasm.Boundary/allowed.txt"
exit $failed
