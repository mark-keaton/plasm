#!/bin/bash
# The SQLite checks on Linux, in a container, as CI would run them: the SQLite
# run of the features sqlite-features.txt lists, the leftover files check with
# a run killed mid-way, and Plasm.Sqlite's native-AOT check.
#   tests/Plasm.Providers/run-linux.sh          linux-arm64 (or the machine's own)
#   tests/Plasm.Providers/run-linux.sh x64      linux-x64, under emulation on arm64
# The repository's files (tracked, and untracked but not ignored) are copied to
# a temporary directory the container builds in; from a copy that is no git
# checkout (a tree exported for its check), every file but bin and obj. No PostgreSQL is started: the
# SQLite run must not need one. The build runs in one MSBuild node, none kept: under x64 emulation
# on arm64, its worker nodes have hung the build for hours.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
arch="${1:-arm64}"
case "$arch" in
  arm64) platform=linux/arm64 ;;
  x64) platform=linux/amd64 ;;
  *) echo "usage: $0 [arm64|x64]"; exit 2 ;;
esac
image="plasm-sqlite-linux:$arch"
copy=$(mktemp -d)
trap 'rm -rf "$copy"' EXIT
start=$(date +%s)
echo "linux-$arch: building the SDK image"
docker build -q --platform "$platform" -t "$image" "$here/linux" > /dev/null
cd "$root"
if git rev-parse --is-inside-work-tree > /dev/null 2>&1; then
  git ls-files -z --cached --others --exclude-standard |
    COPYFILE_DISABLE=1 tar --no-xattrs --null -T - -cf - | tar -xf - -C "$copy"
else
  COPYFILE_DISABLE=1 tar --no-xattrs --exclude bin --exclude obj -cf - . | tar -xf - -C "$copy"
fi
code=0
docker run --rm --platform "$platform" \
  -v "$copy:/work" -w /work \
  -v "plasm-aot-nuget-$arch:/root/.nuget/packages" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$image" bash -c 'uname -m && dotnet build Plasm.slnx -v q -m:1 -nodeReuse:false 2>&1 | grep -E " error |Build succeeded" | sort -u && bash tests/Plasm.Providers/sqlite-run.sh /tmp/sqlite-logs && echo "sqlite-run exit 0" && bash tests/Plasm.Providers/no-leftover-files.sh --prove && echo "no-leftover-files exit 0" && bash tests/Plasm.Aot.Sqlite/run.sh | grep -E "^runtime:|IL warnings|prints what|differs|threw|cancelled|native binary:"' || code=$?
echo "linux-$arch: $(( $(date +%s) - start )) s"
exit $code
