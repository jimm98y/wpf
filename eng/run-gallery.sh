#!/usr/bin/env bash
# Build and run the WPF Gallery on macOS against the from-source WebGPU WPF fork.
#
# The gallery is NOT in this repo -- it is WPF-Samples/Sample Applications/WPFGallery,
# a XAML app that builds with Sdk="WpfWebGpu.Sdk/<ver>". That SDK package BAKES IN the
# fork's assemblies (lib/wpf/*.dll, lib/wpf-desktop/Microsoft.Wpf.Interop.WebGpu.dll),
# so building the gallery alone silently uses whatever was packed last. Three steps are
# easy to miss and each one leaves you running stale bits with no error:
#
#   1. repack the SDK (sdk/WpfWebGpu.Sdk/assemble.sh) after rebuilding the fork;
#   2. EVICT ~/.nuget/packages/wpfwebgpu.sdk -- the package version does not change, so
#      NuGet serves the cached copy and the repack is invisible;
#   3. build with the pinned SDK (~/.dotnet/dotnet). WPF-Samples/global.json wants
#      10.0.301; a system dotnet on 10.0.300 fails to load the project at all.
#
# The Windows equivalent is samples/wpf-webgpu-gallery/run-gallery.ps1 (a different
# head: net10.0-windows against inbox WPF). On macOS the from-source fork IS the WPF.
#
# Usage:
#   eng/run-gallery.sh                     build everything, run
#   eng/run-gallery.sh --no-rebuild        skip the fork rebuild + repack
#   eng/run-gallery.sh --scale 2           force a 2x DPI render
#   eng/run-gallery.sh --dump out.png      read back the real swapchain surface to a PNG
#   eng/run-gallery.sh --perf              print per-frame renderer counters
#   eng/run-gallery.sh --cpu-raster        use the CPU scanline rasterizer instead of the GPU one
#   eng/run-gallery.sh --devtools [port]   attach the CDP visual-tree inspector (default 9222)
#   eng/run-gallery.sh --dump-tree FILE    write the visual tree to FILE a moment after startup
#   eng/run-gallery.sh --tree-props MODE   detail for --dump-tree: local | set | all
#
# --dump is the way to SEE the output from a terminal: macOS blocks `screencapture`
# without a TCC grant, and WF_SURF_DUMP reads back the actual presented surface anyway.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GALLERY_DIR="${WPF_GALLERY_DIR:-$REPO/../WPF-Samples/Sample Applications/WPFGallery}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET=dotnet

rebuild=1; scale=""; dump=""; perf=""; cpu=""; devtools=""; treedump=""; treeprops=""
while [ $# -gt 0 ]; do
  case "$1" in
    --no-rebuild) rebuild=0 ;;
    --scale) scale="$2"; shift ;;
    --dump) dump="$2"; shift ;;
    --perf) perf=1 ;;
    --cpu-raster) cpu=1 ;;
    # The port is optional, so only swallow the next argument when it looks like one.
    --devtools) devtools=9222
                case "${2:-}" in [0-9]*) devtools="$2"; shift ;; esac ;;
    --dump-tree) treedump="$2"; shift ;;
    --tree-props) treeprops="$2"; shift ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

[ -d "$GALLERY_DIR" ] || { echo "gallery not found at: $GALLERY_DIR" >&2
                           echo "set WPF_GALLERY_DIR to override." >&2; exit 1; }

if [ "$rebuild" = 1 ]; then
  echo ">> building WgpuInterop (the renderer)"
  "$DOTNET" build "$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/WgpuInterop.csproj" -c Release -v q --nologo

  echo ">> repacking WpfWebGpu.Sdk into the local feed"
  "$REPO/sdk/WpfWebGpu.Sdk/assemble.sh" >/dev/null

  echo ">> evicting the cached SDK package (version is unchanged, so NuGet would reuse it)"
  rm -rf "$HOME/.nuget/packages/wpfwebgpu.sdk" "$HOME/.nuget/packages/wpfwebgpuwasm.sdk"
fi

echo ">> building the gallery"
"$DOTNET" build "$GALLERY_DIR/WPFGallery.csproj" -c Release -v q --nologo

OUT="$GALLERY_DIR/bin/Release/net10.0"
# Fail loudly rather than launching something built from bits we did not just produce.
DEPLOYED="$OUT/Microsoft.Wpf.Interop.WebGpu.dll"
SOURCE="$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/bin/Release/net10.0/Microsoft.Wpf.Interop.WebGpu.dll"
if [ "$rebuild" = 1 ] && ! cmp -s "$DEPLOYED" "$SOURCE"; then
  echo "ERROR: the deployed renderer differs from the one just built." >&2
  echo "  deployed: $DEPLOYED" >&2
  echo "  built:    $SOURCE" >&2
  echo "The SDK repack or the NuGet cache eviction did not take effect." >&2
  exit 1
fi

# Same check for the inspector: it is loaded by NAME at runtime, so a stale or missing copy
# next to the app is silent -- WPF_DEVTOOLS would simply appear to do nothing.
if [ -n "$devtools$treedump" ]; then
  DEVTOOLS_DEPLOYED="$OUT/Microsoft.Wpf.DevTools.dll"
  DEVTOOLS_SOURCE="$REPO/src/Microsoft.DotNet.Wpf/src/WpfDevTools/bin/Release/net10.0/Microsoft.Wpf.DevTools.dll"
  if [ ! -f "$DEVTOOLS_DEPLOYED" ]; then
    echo "ERROR: the inspector was requested but is not deployed at $DEVTOOLS_DEPLOYED." >&2
    echo "Build it and repack: \"$DOTNET\" build \"$REPO/src/Microsoft.DotNet.Wpf/src/WpfDevTools/WpfDevTools.csproj\" -c Release" >&2
    exit 1
  fi
  if [ "$rebuild" = 1 ] && ! cmp -s "$DEVTOOLS_DEPLOYED" "$DEVTOOLS_SOURCE"; then
    echo "ERROR: the deployed inspector differs from the one just built." >&2
    echo "  deployed: $DEVTOOLS_DEPLOYED" >&2
    echo "  built:    $DEVTOOLS_SOURCE" >&2
    exit 1
  fi
fi

export WPF_USE_WEBGPU_COMPOSITION=1
[ -n "$scale" ] && export WPF_MAC_FORCE_SCALE="$scale"
[ -n "$dump" ]  && export WF_SURF_DUMP="$dump"
[ -n "$perf" ]  && export WPF_WEBGPU_PERF_CONSOLE=1
[ -n "$cpu" ]   && export WPF_WEBGPU_CPU_RASTER=1
# --dump-tree on its own is enough to want the inspector loaded.
[ -n "$treedump" ] && [ -z "$devtools" ] && devtools=9222
[ -n "$devtools" ]  && export WPF_DEVTOOLS="$devtools"
[ -n "$treedump" ]  && export WPF_DEVTOOLS_DUMP="$treedump"
[ -n "$treeprops" ] && export WPF_DEVTOOLS_DUMP_PROPS="$treeprops"

echo ">> running (WPF_USE_WEBGPU_COMPOSITION=1${scale:+, scale=$scale}${cpu:+, cpu raster}${devtools:+, devtools=$devtools})"
cd "$OUT"
exec "$DOTNET" WPFGallery.dll
