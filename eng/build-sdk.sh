#!/usr/bin/env bash
# Build the WPF-on-WebGPU fork and pack WpfWebGpu.Sdk into artifacts/local-feed.
# macOS/Linux wrapper around eng/build-sdk.proj (eng/build-sdk.cmd is the Windows twin);
# all of the actual logic lives in the .proj so the three platforms cannot drift apart.
#
# Usage:
#   eng/build-sdk.sh                       build everything, pack, evict the NuGet cache
#   eng/build-sdk.sh -p:SkipBrowser=true   skip the wasm flavor (needs the wasm-tools workload)
#   eng/build-sdk.sh -p:SkipPack=true      build only
#   eng/build-sdk.sh -p:Configuration=Debug
#
# Any extra arguments are forwarded to msbuild verbatim.
#
# After this, eng/run-gallery.sh --no-rebuild runs the WPF Gallery against what was packed.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Prefer the repo-local SDK: global.json pins 10.0.109 and ./build.sh installs it into
# .dotnet. A system dotnet on a lower feature band cannot load these projects at all.
if [ -n "${DOTNET:-}" ] && [ -x "${DOTNET}" ]; then :
elif [ -x "$REPO/.dotnet/dotnet" ]; then DOTNET="$REPO/.dotnet/dotnet"
elif [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET="$HOME/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
else
  echo "error: no dotnet found. Run ./build.sh once to bootstrap .dotnet, or set DOTNET." >&2
  exit 1
fi

echo ">> dotnet: $DOTNET ($("$DOTNET" --version))"

exec "$DOTNET" msbuild "$REPO/eng/build-sdk.proj" -nologo -v:m \
  -p:DotNetTool="$DOTNET" "$@"
