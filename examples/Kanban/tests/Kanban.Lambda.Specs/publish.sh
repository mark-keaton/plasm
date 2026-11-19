#!/bin/bash
# Publishes Kanban's C# function (src/Lambda) natively for linux-arm64 inside
# Amazon Linux 2023, the image Lambda's provided.al2023 runtime runs, as
# tests/Plasm.Lambda/run.sh publishes its function, and fetches the Lambda
# runtime interface emulator beside it. Run by the scenarios'
# steps (features/lambda), once a test run, into $1: the sources it needs are
# copied to $1/repo at the paths they have here, the binary goes to $1/out and
# the emulator to $1/rie. The SDK and packages are kept in Docker volumes
# between runs. Prints what the steps check; exits non-zero if the publish
# fails. Needs Docker; an arm64 machine runs it natively.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
work="$1"
image=public.ecr.aws/amazonlinux/amazonlinux:2023
mkdir -p "$work/repo" "$work/out" "$work/rie"

# Only what the function's build reads, without this machine's bin and obj.
cd "$repo"
for p in Directory.Build.props Directory.Packages.props global.json README.md src/Directory.Build.props \
         src/Plasm src/Plasm.Annotations src/Plasm.Postgres src/Plasm.FieldGetters \
         examples/Kanban/Directory.Packages.props examples/Kanban/src/Shared examples/Kanban/src/Data examples/Kanban/src/Lambda; do
  rsync -a --relative --exclude bin --exclude obj "$p" "$work/repo/"
done

docker run --rm -v "$work:/work" -v plasm-lambda-sdk-arm64:/opt/dotnet -v plasm-lambda-nuget-arm64:/root/.nuget/packages "$image" bash -c '
  set -e
  dnf install -y -q clang gcc zlib-devel tar gzip findutils binutils libicu > /dev/null 2>&1
  if [ ! -x /opt/dotnet/dotnet ]; then
    curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh
    bash /tmp/di.sh --version 11.0.100 --install-dir /opt/dotnet > /dev/null 2>&1
  fi
  export PATH=/opt/dotnet:$PATH DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  echo "publish: $(ldd --version | head -1), SDK $(dotnet --version)"
  cd /work/repo/examples/Kanban/src/Lambda
  dotnet publish -c Release -r linux-arm64 -o /work/out -v m > /work/publish.log 2>&1 || { grep -E " error " /work/publish.log | sort -u | head -20; tail -5 /work/publish.log; exit 1; }
  echo "publish: $(grep "warning IL" /work/publish.log | sort -u | wc -l) IL warnings, $(grep "warning IL" /work/publish.log | sort -u | grep -c Plasm || true) naming Plasm"
  echo "publish: bootstrap $(stat -c %s /work/out/bootstrap) bytes, newest glibc symbol $(objdump -T /work/out/bootstrap | grep -o "GLIBC_[0-9.]*" | sort -V | tail -1 | sed s/GLIBC_//)"
  echo "publish: Amazon Linux 2023 has glibc $(ldd --version | head -1 | grep -o "[0-9.]*$")"
'

curl -sSL -o "$work/rie/aws-lambda-rie" https://github.com/aws/aws-lambda-runtime-interface-emulator/releases/latest/download/aws-lambda-rie-arm64
chmod +x "$work/rie/aws-lambda-rie"
echo "emulator: $(stat -f %z "$work/rie/aws-lambda-rie" 2> /dev/null || stat -c %s "$work/rie/aws-lambda-rie") bytes"
