#!/bin/bash
# The native-AOT check (run.sh) on Linux, in a container (docs/aot.md):
#   tests/Plasm.Aot/run-linux.sh          linux-arm64 (or the machine's own)
#   tests/Plasm.Aot/run-linux.sh x64      linux-x64, under emulation on arm64
# The repository's files (tracked, and untracked but not ignored) are copied
# to a temporary directory the container builds in, so nothing it builds
# lands in the working tree.
# PostgreSQL runs in a second container on a network of their own. Packages
# are kept in the Docker volume plasm-aot-nuget-<arch> between runs.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
arch="${1:-arm64}"
case "$arch" in
  arm64) platform=linux/arm64 ;;
  x64) platform=linux/amd64 ;;
  *) echo "usage: $0 [arm64|x64]"; exit 2 ;;
esac

image="plasm-aot-linux:$arch"
net="plasm-aot-$$"
pg="plasm-aot-pg-$$"
copy=$(mktemp -d)
cleanup() {
  rm -rf "$copy"
  docker rm -f "$pg" > /dev/null 2>&1 || true
  docker network rm "$net" > /dev/null 2>&1 || true
}
trap cleanup EXIT

start=$(date +%s)
echo "linux-$arch: building the SDK image"
docker build -q --platform "$platform" -t "$image" "$here/linux" > /dev/null

docker network create "$net" > /dev/null
docker run -d --name "$pg" --network "$net" -e POSTGRES_PASSWORD=postgres postgres:18.4 > /dev/null
for _ in $(seq 1 60); do
  docker exec "$pg" pg_isready -U postgres > /dev/null 2>&1 && break
  sleep 1
done
sleep 1

cd "$root"
# No extended attributes: macOS's tar would add ._ files for them, among them
# a ._Plasm.Aot.fsproj MSBuild takes for a second project.
git ls-files -z --cached --others --exclude-standard |
  COPYFILE_DISABLE=1 tar --no-xattrs --null -T - -cf - | tar -xf - -C "$copy"

code=0
docker run --rm --platform "$platform" --network "$net" \
  -v "$copy:/work" -w /work \
  -v "plasm-aot-nuget-$arch:/root/.nuget/packages" \
  -e PLASM_TEST_PG="Host=$pg;Username=postgres;Password=postgres;Database=postgres" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$image" bash -c 'uname -m && bash tests/Plasm.Aot/run.sh' || code=$?
echo "linux-$arch: $(( $(date +%s) - start )) s"
exit $code
