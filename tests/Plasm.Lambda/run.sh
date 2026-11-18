#!/bin/bash
# A function shaped like a deployed one, end to end on this machine
# (docs/aot.md): Plasm packed from this repository into a local feed, the
# function (tests/Plasm.Lambda/function) published natively for linux-arm64
# inside Amazon Linux 2023, the image Lambda's provided.al2023 runtime runs,
# and invoked through the Lambda runtime interface emulator against
# PostgreSQL in a second container. No AWS account is used: the connection
# string is an environment variable. It runs twice, with the first connection
# and the repository's start in the function's init and in its first
# invocation, each on a fresh database, and fails on any answer that is not
# the expected one. Needs Docker; an arm64 machine runs it natively.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
began=$(date +%s)
work="$(mktemp -d "${TMPDIR:-/tmp}/plasm-lambda.XXXXXX")"
net="plasm-lambda-$$"
containers=()
cleanup() { for c in "${containers[@]:-}"; do [ -n "$c" ] && docker rm -f "$c" > /dev/null 2>&1; done; docker network rm "$net" > /dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
image=public.ecr.aws/amazonlinux/amazonlinux:2023

# A version of its own, so that no package cache holds an older Plasm of it.
suffix="alpha.lambda$(date +%s)"
version="0.1.0-$suffix"
echo "pack: Plasm.Annotations, Plasm, Plasm.Postgres $version into a local feed"
export NUGET_PACKAGES="$work/host-packages"
for p in Plasm.Annotations Plasm Plasm.Postgres; do
  dotnet build "$repo/src/$p" -c Release -p:VersionSuffix="$suffix" -v q > "$work/build-$p.log" 2>&1 || { cat "$work/build-$p.log"; exit 1; }
  dotnet pack "$repo/src/$p" -c Release --no-build -p:VersionSuffix="$suffix" -o "$work/feed" -v q > "$work/pack-$p.log" 2>&1 || { cat "$work/pack-$p.log"; exit 1; }
done
mkdir -p "$work/app" "$work/out" "$work/rie"
cp "$here/function/Function.fsproj" "$here/function/Program.fs" "$work/app/"
cat > "$work/app/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="/work/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Plasm" /><package pattern="Plasm.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
XML

echo "publish: inside $image (the SDK and packages are kept in Docker volumes between runs)"
docker run --rm -v "$work:/work" -v plasm-lambda-sdk-arm64:/opt/dotnet -v plasm-lambda-nuget-arm64:/root/.nuget/packages \
  -e PLASM_VERSION="$version" "$image" bash -c '
  set -e
  dnf install -y -q clang gcc zlib-devel tar gzip findutils binutils libicu > /dev/null 2>&1
  if [ ! -x /opt/dotnet/dotnet ]; then
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh
    bash /tmp/di.sh --version 11.0.100 --install-dir /opt/dotnet > /dev/null 2>&1
  fi
  export PATH=/opt/dotnet:$PATH DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  echo "publish: $(ldd --version | head -1), SDK $(dotnet --version)"
  cd /work/app
  dotnet publish -c Release -r linux-arm64 -p:PlasmVersion=$PLASM_VERSION -o /work/out -v m > /work/publish.log 2>&1 || { grep -E " error " /work/publish.log | head -20; tail -5 /work/publish.log; exit 1; }
  echo "publish: $(grep -c "warning IL" /work/publish.log || true) IL warnings, $(grep "warning IL" /work/publish.log | grep -c Plasm || true) naming Plasm"
  echo "publish: bootstrap $(stat -c %s /work/out/bootstrap) bytes, newest glibc symbol $(objdump -T /work/out/bootstrap | grep -o "GLIBC_[0-9.]*" | sort -V | tail -1)"
'
glibc=$(docker run --rm -v "$work/out:/out" "$image" bash -c 'ldd --version | head -1' | grep -o '[0-9.]*$')
echo "publish: Amazon Linux 2023 has glibc $glibc"

echo "emulator: the Lambda runtime interface emulator for arm64"
curl -sSL -o "$work/rie/aws-lambda-rie" https://github.com/aws/aws-lambda-runtime-interface-emulator/releases/latest/download/aws-lambda-rie-arm64
chmod +x "$work/rie/aws-lambda-rie"

docker network create "$net" > /dev/null
failed=0
for mode in ${PLASM_LAMBDA_MODES:-init invocation}; do
  pg=$(docker run -d --network "$net" -e POSTGRES_PASSWORD=postgres postgres:18.4); containers+=("$pg")
  for i in $(seq 60); do docker exec "$pg" pg_isready -U postgres > /dev/null 2>&1 && break; sleep 1; done; sleep 2
  docker exec -i "$pg" psql -q -U postgres -v ON_ERROR_STOP=1 < "$here/schema.sql"
  pgname=$(docker inspect -f '{{.Name}}' "$pg" | tr -d /)
  fn=$(docker run -d --network "$net" -p 127.0.0.1::8080 -v "$work/out:/var/task" -v "$work/rie:/rie" \
    -e PLASM_CS="Host=$pgname;Username=postgres;Password=postgres;Database=postgres" -e PLASM_INIT="$mode" \
    "$image" /rie/aws-lambda-rie /var/task/bootstrap); containers+=("$fn")
  port=$(docker port "$fn" 8080/tcp | head -1 | sed 's/.*://')
  for i in $(seq 50); do curl -s -o /dev/null "http://127.0.0.1:$port/" && break; sleep 0.2; done
  echo "--- PLASM_INIT=$mode"
  ask() { # event, expected answer
    local got secs
    got=$(curl -s -w '\n%{time_total}' -d "$1" "http://127.0.0.1:$port/2015-03-31/functions/function/invocations")
    secs=$(echo "$got" | tail -1); got=$(echo "$got" | sed '$d')
    if [ "$got" = "$2" ]; then echo "ok   $1 -> $got ($(python3 -c "print(round($secs * 1000))") ms round trip)"; else echo "FAIL $1"; echo "     expected $2"; echo "     got      $got"; failed=1; fi
  }
  ask '{"ticket":1}' '{"ticket":1,"title":"Ship it","stage":"todo","version":1,"customer":"Ada","notes":["call back","check logs"]}'
  ask '{"ticket":1,"start":"grace"}' '{"ticket":1,"title":"Ship it","stage":"doing by grace","version":2,"customer":"Ada","notes":["call back","check logs"]}'
  ask '{"ticket":1,"start":"linus"}' '{"refused":"grace has started it"}'
  ask '{"ticket":99}' '{"missing":"ticket 99"}'
  ask '{"ticket":99,"start":"ada"}' '{"missing":"ticket 99"}'
  echo "the function's log:"
  # During an invocation, the runtime prefixes what a function logs.
  docker logs "$fn" 2>&1 | grep -oE "(to main|model|repo start[^0-9]*|first read|invocation) [0-9.]+ ms" | sed 's/^/  /' || true
  docker logs "$fn" 2>&1 | grep -oE "INIT REPORT\(durationMs: [0-9.]+" | sed 's/.*: /  emulator: init /; s/$/ ms/' || true
  docker logs "$fn" 2>&1 | grep "REPORT RequestId" | awk -F'\t' '{ for (i = 1; i <= NF; i++) if ($i ~ /^Duration: /) print "  emulator: invocation " substr($i, 11) }' || true
  docker rm -f "$fn" "$pg" > /dev/null
done
echo "took $(( $(date +%s) - began )) s"
exit $failed
