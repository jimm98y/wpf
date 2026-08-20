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

# Every suite. WgpuInterop.Tests covers the renderer (one project reference, builds anywhere); the
# rest need the built fork and exclude themselves with a warning when it is absent, so running this
# without eng/build-sdk.sh first is quiet rather than broken.
#
# Keep this list complete. It listed two of six for a while, and the printing suite was one of the
# four missing -- which is how a Linux print path that segfaulted on the first call to CUPS stayed
# green: the suite that would have caught it was never run.
PROJECTS=(
  "$REPO/src/Microsoft.DotNet.Wpf/src/WgpuInterop/tests/WgpuInterop.Tests/WgpuInterop.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Platform.Tests/Wpf.Platform.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Accessibility.Tests/Wpf.Accessibility.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Dialog.Tests/Wpf.Dialog.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Document.Tests/Wpf.Document.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Geometry.Tests/Wpf.Geometry.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Imaging.Tests/Wpf.Imaging.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Input.Tests/Wpf.Input.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Printing.Tests/Wpf.Printing.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.Text.Tests/Wpf.Text.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.WebView.Tests/Wpf.WebView.Tests.csproj"
  "$REPO/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/Wpf.WinFormsInterop.Tests/Wpf.WinFormsInterop.Tests.csproj"
)

# Keeping the list complete by ASKING rather than by remembering. The comment above has said "keep
# this list complete" since the printing suite went missing, and three more suites (dialog, imaging,
# WinForms interop) had gone missing again by the time anyone looked -- so a suite written, reviewed
# and committed never ran here, which is indistinguishable from a suite that passes.
#
# Anything under tests/CrossPlatform is a suite this script owes a run. WgpuInterop.Tests lives
# elsewhere and is listed by hand, so it is not swept up here.
_missing=""
for _found in "$REPO"/src/Microsoft.DotNet.Wpf/tests/CrossPlatform/*/*.csproj; do
  case " ${PROJECTS[*]} " in
    *" $_found "*) ;;
    *) _missing="$_missing
  $(basename "$_found")" ;;
  esac
done
if [ -n "$_missing" ]; then
  echo "error: these test projects exist but this script does not run them:$_missing" >&2
  echo "       Add them to PROJECTS in $(basename "${BASH_SOURCE[0]}")." >&2
  exit 1
fi

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
  # The ${a[@]+"${a[@]}"} dance is not noise: macOS ships bash 3.2, where an EMPTY array counts
  # as unset, so a plain "${FILTER[@]}" under `set -u` aborts the script before the first test
  # runs. That is what "PLATFORM_ARGS[@]: unbound variable" was -- the whole suite was
  # unrunnable on macOS with the stock shell whenever no extra arguments were passed.
  "$DOTNET" test "$p" -c Release --nologo \
    ${FILTER[@]+"${FILTER[@]}"} ${PLATFORM_ARGS[@]+"${PLATFORM_ARGS[@]}"} || rc=$?
done
exit $rc
