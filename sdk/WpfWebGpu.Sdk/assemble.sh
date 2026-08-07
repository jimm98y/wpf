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

# Refuse to pack a STALE renderer.
#
# This script packs whatever is already sitting in the build outputs; it does not build anything.
# That is fine when you have just built, and silently wrong when you have not -- and "have not" is
# easy to reach by accident. The way it actually happened: an A/B experiment edited
# WgpuSceneRenderer.cs, `dotnet run` on a test rebuilt the renderer from the edited source, and the
# experiment then restored the .cs but NOT the DLL. Packing after that shipped the experiment's
# binary inside the SDK, so an app built against the feed showed the very bug that had just been
# fixed in the source -- with nothing anywhere on disk to suggest a mismatch.
#
# Comparing the newest renderer source against the built assembly costs nothing and turns that into
# an error at the point of packing rather than a mystery three builds later.
WGPU_SRC="$HERE/../../src/Microsoft.DotNet.Wpf/src/WgpuInterop"
WGPU_DLL="$WGPU_SRC/bin/Release/net10.0/Microsoft.Wpf.Interop.WebGpu.dll"
if [ -f "$WGPU_DLL" ]; then
  NEWEST=$(find "$WGPU_SRC" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -newer "$WGPU_DLL" -print -quit 2>/dev/null || true)
  if [ -n "$NEWEST" ]; then
    echo "error: $WGPU_DLL is older than its sources (e.g. $NEWEST)." >&2
    echo "       Packing now would ship a stale renderer. Build it first:" >&2
    echo "         \"$DOTNET\" build \"$WGPU_SRC/WgpuInterop.csproj\" -c Release" >&2
    exit 1
  fi
fi

for proj in "$HERE/WpfWebGpu.Sdk.csproj" "$HERE/../WpfWebGpuWasm.Sdk/WpfWebGpuWasm.Sdk.csproj"; do
  "$DOTNET" pack "$proj" -c Release -o "$FEED" \
    -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false \
    --nologo -v:m
done
echo "PACKED -> $FEED"
