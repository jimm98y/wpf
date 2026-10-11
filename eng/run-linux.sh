#!/usr/bin/env bash
# Build and run a WPF app on the Linux/Wayland head.
#
# The Linux twin of eng/run-gallery.sh (macOS) and eng/run-android.sh. Same three
# easy-to-miss steps are automated: rebuild the renderer, rebuild the fork, and run against the
# artifacts rather than a stale NuGet cache.
#
# Usage:
#   eng/run-linux.sh                        the in-repo smoke app (samples/wpf-linux-smoke)
#   eng/run-linux.sh --gallery              the shared code-only gallery (samples/wpf-gallery-linux)
#   eng/run-linux.sh --media -- FILE        MediaElement over GStreamer (samples/media-linux)
#   eng/run-linux.sh --project <path>       any other app project
#   eng/run-linux.sh --no-rebuild           skip the fork build (fast iteration on the app)
#
# Flags:
#   --scale N        force a backing scale (WPF_LINUX_FORCE_SCALE), for HiDPI testing
#   --cpu-raster     force the CPU path rasterizer (WPF_WEBGPU_CPU_RASTER=1)
#   --gpu-raster     force the GPU path rasterizer, even on backends known to mis-render it
#   --backend B      pin the wgpu backend: vulkan | gl | all (WPF_WEBGPU_BACKEND)
#   --poll-pump      drive the dispatcher with the periodic slice instead of the wayland fd
#   --a11y           export the accessibility tree over AT-SPI2 and trace it (WPF_A11Y_LOG). The
#                    bridge normally waits for org.a11y.Status.IsEnabled, which GNOME leaves false
#                    until a screen reader starts; this points it straight at the a11y bus instead,
#                    so the tree can be walked with an AT-SPI client without turning Orca on.
#   --wayland-log    trace connection/globals setup
#   --wayland-debug  WAYLAND_DEBUG=1: decode every protocol request and event
#   --wgpu-log       wgpu-native's own debug log
#   --dump FILE      re-render the composed frame offscreen to a PNG (WPF_WEBGPU_SINK_DUMP)
#   --surf-dump FILE read the REAL swapchain back to a PNG (WF_SURF_DUMP). Diff it against --dump to
#                    prove the presented buffer matches what was rendered. Needs a surface that
#                    allows CopySrc: the GL backend does not, so pair it with --backend vulkan
#                    (which picks the llvmpipe CPU adapter here -- slow, but it is a one-frame probe).
#   --perf           per-frame timing to the console

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -n "${DOTNET:-}" ] && [ -x "${DOTNET}" ]; then :
elif [ -x "$REPO/.dotnet/dotnet" ]; then DOTNET="$REPO/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
else
  echo "error: no dotnet found. Run ./eng/common/dotnet.sh once to bootstrap .dotnet, or set DOTNET." >&2
  exit 1
fi

PROJECT="$REPO/samples/wpf-linux-smoke/WpfLinuxSmoke.csproj"
REBUILD=1
APP_ARGS=()

while [ $# -gt 0 ]; do
  case "$1" in
    --gallery)      PROJECT="$REPO/samples/wpf-gallery-linux/WpfGalleryLinux.csproj"; shift ;;
    --media)        PROJECT="$REPO/samples/media-linux/MediaLinux.csproj"; shift ;;
    --project)      PROJECT="$2"; shift 2 ;;
    --no-rebuild)   REBUILD=0; shift ;;
    --scale)        export WPF_LINUX_FORCE_SCALE="$2"; shift 2 ;;
    --cpu-raster)   export WPF_WEBGPU_CPU_RASTER=1; shift ;;
    --gpu-raster)   export WPF_WEBGPU_CPU_RASTER=0; shift ;;
    --backend)      export WPF_WEBGPU_BACKEND="$2"; shift 2 ;;
    --poll-pump)    export WPF_LINUX_POLL_PUMP=1; shift ;;
    --a11y)
      export WPF_A11Y_LOG=1
      # Ask the session bus where the accessibility bus is, exactly as the bridge would. Setting
      # AT_SPI_BUS_ADDRESS is also what makes the bridge skip the IsEnabled gate.
      if [ -z "${AT_SPI_BUS_ADDRESS:-}" ] && command -v gdbus >/dev/null 2>&1; then
        A11Y_ADDR="$(gdbus call --session --dest org.a11y.Bus --object-path /org/a11y/bus \
                     --method org.a11y.Bus.GetAddress 2>/dev/null | sed "s/^('//; s/',)$//")"
        [ -n "$A11Y_ADDR" ] && export AT_SPI_BUS_ADDRESS="$A11Y_ADDR"
      fi
      if [ -z "${AT_SPI_BUS_ADDRESS:-}" ]; then
        echo "warning: no accessibility bus (org.a11y.Bus.GetAddress failed); --a11y will do nothing" >&2
      fi
      shift ;;
    --wayland-log)  export WPF_WAYLAND_LOG=1; shift ;;
    --wayland-debug) export WAYLAND_DEBUG=1; shift ;;
    --wgpu-log)     export WPF_WEBGPU_WGPU_LOG=debug; shift ;;
    --dump)         export WPF_WEBGPU_SINK_DUMP="$2"; shift 2 ;;
    --surf-dump)    export WF_SURF_DUMP="$2"; shift 2 ;;
    --perf)         export WPF_WEBGPU_PERF_CONSOLE=1; shift ;;
    --)             shift; APP_ARGS+=("$@"); break ;;
    *)              APP_ARGS+=("$1"); shift ;;
  esac
done

# The wgpu backend has to be staged before anything can run; it is not committed.
RID="$("$DOTNET" --version >/dev/null 2>&1 && uname -m | sed 's/^aarch64$/linux-arm64/; s/^x86_64$/linux-x64/')"
NATIVE="$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/native/$RID/lib/libwgpu_native.so"
if [ ! -f "$NATIVE" ]; then
  echo "error: $NATIVE is missing. Stage it with:" >&2
  echo "  pwsh $REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/eng/fetch-wgpu.ps1 -Rid $RID" >&2
  exit 1
fi

if [ -z "${WAYLAND_DISPLAY:-}" ]; then
  echo "error: WAYLAND_DISPLAY is not set. This head is Wayland-native; there is no X11 fallback." >&2
  exit 1
fi

if [ "$REBUILD" = "1" ]; then
  echo ">> building the fork (eng/build-sdk.sh)"
  DOTNET="$DOTNET" "$REPO/eng/build-sdk.sh" -p:SkipBrowser=true -p:SkipPack=true
fi

echo ">> building $PROJECT"
"$DOTNET" build "$PROJECT" -c Release -v q --nologo -p:WgpuRid="$RID"

# Unset DISPLAY so an accidental XWayland path cannot masquerade as a working Wayland head.
unset DISPLAY

export WPF_USE_WEBGPU_COMPOSITION=1
echo ">> running (backend=${WPF_WEBGPU_BACKEND:-auto} raster=${WPF_WEBGPU_CPU_RASTER:-auto})"
exec "$DOTNET" run --project "$PROJECT" -c Release --no-build -p:WgpuRid="$RID" -- "${APP_ARGS[@]:-}"
