#!/usr/bin/env bash
# Builds wpfmac into artifacts/wpfmac/<rid>/ (build.cmd is the Windows twin):
#
#   launcher/            wpfmac itself: `dotnet launcher/wpfmac.dll run <App.dll>`, or launcher/wpfmac
#   overlay/wpfmac.payload/   the WebGPU stack an app runs on (the published WpfMac.Payload)
#
# That folder is all of wpfmac. Removing it plus `wpfmac clean --all` leaves nothing behind.
#
# The overlay is built against the WpfWebGpu.Sdk package in artifacts/local-feed, so build and pack the
# fork first (eng/build-sdk.sh). Usage:
#   tools/WpfMac/build.sh                  for this machine's RID
#   tools/WpfMac/build.sh -r osx-arm64     for another

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
cd "$HERE"

RID=""
while [ $# -gt 0 ]; do
  case "$1" in
    -r|--rid) RID="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Linux-x86_64) RID=linux-x64 ;;
    *) echo "cannot tell this machine's RID; pass -r" >&2; exit 2 ;;
  esac
fi

OUT="$REPO/artifacts/wpfmac/$RID"
if [ ! -f "$REPO/artifacts/local-feed/WpfWebGpu.Sdk.0.1.1.nupkg" ]; then
  echo "error: artifacts/local-feed has no WpfWebGpu.Sdk; run eng/build-sdk.sh first." >&2
  exit 1
fi

# The SDK package version never changes between builds, so NuGet would keep serving the copy it
# extracted the first time; take the one just packed.
rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}/wpfwebgpu.sdk" "${NUGET_PACKAGES:-$HOME/.nuget/packages}/wpfwebgpu.fonts"

rm -rf "$OUT"
echo ">> launcher -> $OUT/launcher"
dotnet publish "$HERE/WpfMac.Launcher" -c Release -o "$OUT/launcher" -nologo -v q
echo ">> overlay ($RID) -> $OUT/overlay/wpfmac.payload"
dotnet publish "$HERE/WpfMac.Payload" -c Release -r "$RID" --self-contained false \
  -o "$OUT/overlay/wpfmac.payload" -nologo -v q -clp:NoSummary -p:WarningLevel=0 -nowarn:MSB3277
case "$RID" in
  osx-*)
    # The Win32 trampoline an app's own P/Invokes into user32, kernel32, gdi32... land in. Windows
    # has the real DLLs, and Linux would need ELF stubs, which are not generated yet.
    echo ">> libwin32shim ($RID) -> $OUT/overlay/wpfmac.payload"
    "$HERE/WpfMac.Win32Native/build-shim.sh" "$OUT/overlay/wpfmac.payload" "$( [ "${RID#osx-}" = x64 ] && echo x86_64 || echo arm64 )" | tail -1
    ;;
esac
echo ">> done: dotnet $OUT/launcher/wpfmac.dll run <App.dll>"
