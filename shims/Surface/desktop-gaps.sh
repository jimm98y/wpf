#!/usr/bin/env bash
# What of the Windows Desktop Runtime the stack does not have (DesktopGaps/Program.cs): a table per assembly, and the
# member-by-member report.
#   shims/Surface/desktop-gaps.sh [report] [assembly dir...]
# The directories given come first (a WinForms just built: src/Microsoft.DotNet.Wpf/src/WinFormsInterop/bin/Release/
# net10.0), then this machine's overlay (artifacts/wpfrun/<rid>/overlay/wpfrun.payload, tools/WpfWebGpu/build.sh), then
# .NET's own runtime.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
REPORT="${1:-$REPO/artifacts/desktop-gaps.txt}"; [ $# -gt 0 ] && shift
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID=osx-arm64 ;; Darwin-x86_64) RID=osx-x64 ;; Linux-aarch64) RID=linux-arm64 ;; Linux-x86_64) RID=linux-x64 ;;
  *) RID=win-x64 ;;
esac
OVERLAY="$REPO/artifacts/wpfrun/$RID/overlay/wpfrun.payload"
[ -d "$OVERLAY" ] || { echo "error: no $OVERLAY: run tools/WpfWebGpu/build.sh first" >&2; exit 1; }
PKGS="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
REF="$(ls -d "$PKGS"/microsoft.windowsdesktop.app.ref/10.*/ref/net10.0 2>/dev/null | sort -V | tail -1)"
[ -n "$REF" ] || { echo "error: no Microsoft.WindowsDesktop.App.Ref 10 in $PKGS: restore a net10.0-windows project once" >&2; exit 1; }
ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
NETCORE="$(ls -d "$ROOT"/shared/Microsoft.NETCore.App/10.* | sort -V | tail -1)"
TOOL="$(dotnet msbuild "$HERE/DesktopGaps/DesktopGaps.csproj" -getProperty:TargetPath -p:Configuration=Release)"
dotnet build "$HERE/DesktopGaps/DesktopGaps.csproj" -c Release -nologo -v:q | grep -E " error " && exit 1 || true
mkdir -p "$(dirname "$REPORT")"
dotnet "$TOOL" "$REF" "$REPORT" "$@" "$OVERLAY" "$NETCORE"
echo "report: $REPORT"
