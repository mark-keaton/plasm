# The .NET SDK with what a native publish needs on Linux (docs/aot.md):
# clang links the binary, zlib is the runtime's compression.
FROM mcr.microsoft.com/dotnet/sdk:11.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
