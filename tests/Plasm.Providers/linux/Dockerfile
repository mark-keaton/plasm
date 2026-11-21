# The .NET SDK with what the SQLite checks need on Linux: clang and zlib for a
# native publish (as tests/Plasm.Aot/linux), procps for the leftover check's
# process tests (pkill).
FROM mcr.microsoft.com/dotnet/sdk:11.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev procps \
 && rm -rf /var/lib/apt/lists/*
