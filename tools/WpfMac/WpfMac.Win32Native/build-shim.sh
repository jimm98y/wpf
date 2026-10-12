#!/usr/bin/env bash
# Builds libwin32shim.dylib (macOS only) into the directory given:
#   tools/WpfMac/WpfMac.Win32Native/build-shim.sh <out dir> [arm64|x86_64]
# Called by tools/WpfMac/build.sh. Needs clang (Xcode command line tools).

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$1"
ARCH="${2:-arm64}"
if [ "$ARCH" != "arm64" ]; then
  echo "libwin32shim: only arm64 stubs are generated so far" >&2
  exit 1
fi

GEN="$OUT/.win32shim-src"
mkdir -p "$GEN"
dotnet run --project "$HERE/WpfMac.Win32Native.Generator.csproj" -c Release -- "$GEN"

clang -arch "$ARCH" -dynamiclib -O2 -fvisibility=hidden \
  -install_name @rpath/libwin32shim.dylib \
  -o "$OUT/libwin32shim.dylib" \
  "$HERE/win32shim_runtime.c" "$GEN/win32shim_tables.c" "$GEN/win32shim_stubs.s"
cp "$GEN/win32shim_modules.txt" "$OUT/win32shim_modules.txt"
rm -rf "$GEN"
echo "libwin32shim: $(wc -l < "$OUT/win32shim_modules.txt" | tr -d ' ') modules -> $OUT/libwin32shim.dylib"
