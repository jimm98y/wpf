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
#
# There are TWO renderers, and each has to be checked against the sources IT compiles. The desktop
# project excludes Browser/, so comparing the desktop DLL against every .cs under WgpuInterop made a
# browser-only edit unsatisfiable: the desktop build had nothing to rebuild, its DLL kept its old
# timestamp, and the check failed again on the very file it told you to rebuild. Worse, the check it
# should have been making was missing -- the BROWSER dll had no guard at all, so it sat several days
# stale (missing renderer fixes) while every wasm publish quietly shipped it.
WGPU_SRC="$HERE/../../src/Microsoft.DotNet.Wpf/src/WgpuInterop"

check_stale() {
  # $1 = built dll, $2 = project to build, $3 = extra find predicate ("" for none)
  [ -f "$1" ] || return 0
  # shellcheck disable=SC2086
  NEWEST=$(find "$WGPU_SRC" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' $3 -newer "$1" -print -quit 2>/dev/null || true)
  if [ -n "$NEWEST" ]; then
    echo "error: $1 is older than its sources (e.g. $NEWEST)." >&2
    echo "       Packing now would ship a stale renderer. Build it first:" >&2
    echo "         \"$DOTNET\" build \"$2\" -c Release" >&2
    exit 1
  fi
}

# Desktop/unix flavor: everything except the browser-only sources.
check_stale "$WGPU_SRC/bin/Release/net10.0/Microsoft.Wpf.Interop.WebGpu.dll" \
            "$WGPU_SRC/WgpuInterop.csproj" "-not -path */Browser/*"
# Browser flavor: compiles ..\**\*.cs, so every source counts, Browser/ included.
check_stale "$WGPU_SRC/Browser/bin/Release/net10.0/Microsoft.Wpf.Interop.WebGpu.dll" \
            "$WGPU_SRC/Browser/WgpuInterop.Browser.csproj" ""

# Refuse to pack STALE SHIMS, for the same reason and with the same failure mode.
#
# The shims (Registry, SystemEvents, Process) are built into artifacts/bin like everything else and
# packed from there, so editing one and packing without rebuilding ships the previous binary. That is
# not hypothetical: a fix making Process.TotalProcessorTime stop reporting wall-clock as CPU time was
# edited, packed, published and tested -- and the app went on reporting 100% CPU, because the shim on
# disk was 19 hours old. The renderer check below exists for exactly this, and the shims deserve it.
for shim_dir in "$HERE/../../shims"/*/; do
  [ -d "$shim_dir" ] || continue
  shim_name=$(basename "$shim_dir")
  shim_dll="$HERE/../../artifacts/bin/$shim_name/Release/net10.0/$shim_name.dll"
  [ -f "$shim_dll" ] || continue
  NEWEST=$(find "$shim_dir" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -newer "$shim_dll" -print -quit 2>/dev/null || true)
  if [ -n "$NEWEST" ]; then
    echo "error: $shim_dll is older than its sources (e.g. $NEWEST)." >&2
    echo "       Packing now would ship a stale shim. Build it first:" >&2
    echo "         \"$DOTNET\" build \"$shim_dir$shim_name.csproj\" -c Release" >&2
    exit 1
  fi
done

# Pack from scratch every time.
#
# `dotnet pack` decides it is up to date by comparing the .nupkg against the PROJECT, not against the
# hundreds of build outputs the nuspec pulls in from artifacts/bin. Rebuild one of those and the pack
# is skipped, so the feed keeps serving the previous binaries -- silently, with a successful exit code.
# That cost two debugging rounds here: a WindowsBase fix and a Process shim fix were each built,
# "packed", published and tested while the old assemblies were still shipping.
rm -f "$FEED"/WpfWebGpu.Sdk.*.nupkg "$FEED"/WpfWebGpuWasm.Sdk.*.nupkg

for proj in "$HERE/WpfWebGpu.Sdk.csproj" "$HERE/../WpfWebGpuWasm.Sdk/WpfWebGpuWasm.Sdk.csproj"; do
  "$DOTNET" pack "$proj" -c Release -o "$FEED" \
    -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false \
    --nologo -v:m
done
echo "PACKED -> $FEED"
