#!/usr/bin/env bash
# Run the cross-platform test suite (Linux/macOS).
#
# Everything this adds over `dotnet test` is DOTNET_ROOT. xunit.v3 requires a native apphost, and an
# apphost resolves the runtime through DOTNET_ROOT or a system install -- neither of which knows
# about this repo's private ./.dotnet, so a bare `./.dotnet/dotnet test` dies with "You must install
# .NET" on a machine that is obviously running it. Where the SDK is installed normally, plain
# `dotnet test` works and this script is unnecessary.
#
# Usage:
#   eng/run-tests.sh                          # everything
#   eng/run-tests.sh --filter Text            # xunit filter (substring of the fully-qualified name)
#   eng/run-tests.sh -- --list-tests          # anything after -- goes to the test platform
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -n "${DOTNET:-}" ] && [ -x "${DOTNET}" ]; then :
elif [ -x "$REPO/.dotnet/dotnet" ]; then DOTNET="$REPO/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
else
  echo "error: no dotnet found. Run ./eng/common/dotnet.sh once to bootstrap .dotnet, or set DOTNET." >&2
  exit 1
fi

# The apphost's runtime probe. Point it at whichever SDK we just resolved.
export DOTNET_ROOT="$(cd "$(dirname "$DOTNET")" && pwd)"

# Both suites. WgpuInterop.Tests covers the renderer (one project reference, builds anywhere);
# Wpf.Platform.Tests covers the per-OS windowing heads and needs the built fork, excluding itself
# with a warning when that is absent.
PROJECTS=(
  "$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/tests/WgpuInterop.Tests/WgpuInterop.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Platform.Tests/Wpf.Platform.Tests.csproj"
)

FILTER=()
PLATFORM_ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --filter) FILTER=(--filter "$2"); shift 2 ;;
    --)       shift; PLATFORM_ARGS+=("$@"); break ;;
    *)        PLATFORM_ARGS+=("$1"); shift ;;
  esac
done

rc=0
for p in "${PROJECTS[@]}"; do
  echo ">> $(basename "$p" .csproj)"
  "$DOTNET" test "$p" -c Release --nologo "${FILTER[@]}" "${PLATFORM_ARGS[@]}" || rc=$?
done
exit $rc
