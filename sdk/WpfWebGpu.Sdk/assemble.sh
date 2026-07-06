#!/bin/sh
# Pack WpfWebGpu.Sdk + WpfWebGpuWasm.Sdk into the local feed (artifacts/local-feed).
# All layout logic lives in the pack csprojs (files are packed directly from the
# repo's build outputs), so this is just `dotnet pack` twice — see assemble.cmd
# for the Windows equivalent. Rerun after rebuilding the fork.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
FEED="$HERE/../../artifacts/local-feed"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET=dotnet

for proj in "$HERE/WpfWebGpu.Sdk.csproj" "$HERE/../WpfWebGpuWasm.Sdk/WpfWebGpuWasm.Sdk.csproj"; do
  "$DOTNET" pack "$proj" -c Release -o "$FEED" \
    -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false \
    --nologo -v:m
done
echo "PACKED -> $FEED"
