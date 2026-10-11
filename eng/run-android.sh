#!/usr/bin/env bash
# Build, deploy and run an Android head of the WebGPU WPF fork on a device or emulator.
#
# Defaults to the spike (src/Microsoft.DotNet.Wpf/src/WgpuInterop/tests/AndroidSpike). Everything a
# from-scratch machine needs is checked here rather than left to fail deep inside the Android SDK:
#
#   1. the fork's assemblies must be in artifacts/bin -- the head references them by path, it does
#      NOT build them (Arcade does not participate in an Android build). eng/build-sdk.sh makes them.
#   2. wgpu-native for android-arm64 must be under WgpuInterop/native (gitignored, see .gitignore).
#      Without it the app installs and dies at startup with DllNotFoundException: wgpu_native.
#   3. a JDK 17/21 and the Android SDK. `dotnet build -t:InstallAndroidDependencies` provisions both
#      (that is where the default JAVA_HOME below comes from). Android Studio's bundled JBR does NOT
#      work: it reports its version as "25.0.2+-15348964-b329.117", which the SDK fails to parse
#      (error XARSD7004: Version string portion was too short or too long).
#
# Usage:
#   eng/run-android.sh                 build, install, launch, tail the log
#   eng/run-android.sh --no-launch     build and install only
#   eng/run-android.sh --log           just re-print the last run's logs
#   eng/run-android.sh -c Release      build configuration (default Debug)
#
# The WPF Gallery head instead of the spike (it also needs WPF-Samples' WPFGallery built):
#   ANDROID_PROJECT=samples/wpf-gallery-android/WpfGalleryAndroid.csproj \
#   ANDROID_PACKAGE=net.dot.wpf.gallery eng/run-android.sh
#
# Logs come from two places, both printed at the end: logcat (Console.WriteLine lands in the DOTNET
# tag) and the engine's own file log in the app's private storage.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="${ANDROID_PROJECT:-$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/tests/AndroidSpike/AndroidSpike.csproj}"
PKG="${ANDROID_PACKAGE:-net.dot.wpf.androidspike}"
CONFIG="Debug"
launch=1; logonly=0

while [ $# -gt 0 ]; do
  case "$1" in
    --no-launch) launch=0 ;;
    --log) logonly=1 ;;
    -c|--configuration) CONFIG="$2"; shift ;;
    -h|--help) sed -n '2,27p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

# The android workload lives in the repo-local SDK (global.json pins it), not in a system dotnet.
DOTNET="${DOTNET:-$REPO/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET=dotnet

ANDROID_SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
JAVA_SDK="${JAVA_HOME:-$HOME/Library/Developer/Xamarin/jdk/microsoft-jdk-21}"
ADB="$ANDROID_SDK/platform-tools/adb"

dump_logs() {
  echo "==> logcat (DOTNET)"
  "$ADB" logcat -d -s DOTNET:V | tail -60 || true
  echo "==> engine log ($PKG files/wpf-sink.log)"
  "$ADB" shell run-as "$PKG" cat files/wpf-sink.log 2>/dev/null | tail -40 || echo "   (none)"
  echo "==> crashes"
  "$ADB" logcat -d | grep -A 8 "FATAL EXCEPTION" | head -20 || true
}

if [ "$logonly" = 1 ]; then dump_logs; exit 0; fi

WGPU="$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/native/android-arm64/lib/libwgpu_native.so"
[ -f "$WGPU" ] || {
  echo "error: missing $WGPU" >&2
  echo "       fetch it with: pwsh src/Microsoft.DotNet.Wpf/src/WgpuInterop/eng/fetch-wgpu.ps1 -Rid android-arm64" >&2
  exit 1
}
[ -f "$REPO/artifacts/bin/PresentationCore/Release/net10.0/PresentationCore.dll" ] || {
  echo "error: the fork is not built. Run eng/build-sdk.sh first." >&2
  exit 1
}
[ -x "$ADB" ] || { echo "error: adb not found at $ADB (set ANDROID_HOME)" >&2; exit 1; }
[ -d "$JAVA_SDK" ] || {
  echo "error: no JDK at $JAVA_SDK. Provision one with:" >&2
  echo "       $DOTNET build \"$PROJ\" -t:InstallAndroidDependencies \\" >&2
  echo "         -p:AndroidSdkDirectory=$ANDROID_SDK -p:JavaSdkDirectory=$JAVA_SDK -p:AcceptAndroidSDKLicenses=true" >&2
  exit 1
}

"$ADB" wait-for-device

echo "==> building + installing $(basename "$PROJ") ($CONFIG)"
"$DOTNET" build "$PROJ" -c "$CONFIG" -t:Install -v q --nologo \
  -p:JavaSdkDirectory="$JAVA_SDK" -p:AndroidSdkDirectory="$ANDROID_SDK"

if [ "$launch" = 1 ]; then
  # Start fresh: the engine log is appended to, so a stale one reads like this run's.
  "$ADB" shell run-as "$PKG" rm -f files/wpf-sink.log 2>/dev/null || true
  "$ADB" logcat -c
  echo "==> launching $PKG"
  "$ADB" shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1 >/dev/null
  sleep 12
  dump_logs
fi
