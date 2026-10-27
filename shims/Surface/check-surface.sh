#!/usr/bin/env bash
# Verifies a built shim has exactly the public surface of the package it stands in for:
#   shims/Surface/check-surface.sh <ShimDir> <PackageId> <Version>
# or of the shared framework's assembly of that name (its reference assembly):
#   shims/Surface/check-surface.sh <ShimDir> <AssemblyName> framework
# Both assemblies are written out by GenAPI, with no exclusions, and compared. Exit 1 on a difference.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SHIM="$(cd "$1" && pwd)"; ID="$2"; VER="$3"
PKGS="${NUGET_PACKAGES:-$HOME/.nuget/packages}/"
lower="$(echo "$ID" | tr '[:upper:]' '[:lower:]')"
GOT="$(dotnet msbuild "$(ls "$SHIM"/*.csproj | head -1)" -getProperty:TargetPath -p:Configuration=Release)"
[ -f "$GOT" ] || { echo "error: build $SHIM (Release) first" >&2; exit 1; }
ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
LIB="$(ls -d "$ROOT"/packs/Microsoft.NETCore.App.Ref/10.* | sort -V | tail -1)/ref/net10.0"
if [ "$VER" = "framework" ]; then WANT="$LIB/$ID.dll"; else WANT="$PKGS$lower/$VER/lib/net10.0/$ID.dll"; fi
for d in "$PKGS"system.codedom/"$VER"/lib/net10.0 "$PKGS"system.diagnostics.eventlog/"$VER"/lib/net10.0; do [ -d "$d" ] && LIB="$LIB%3B$d"; done
OUT="$(mktemp -d)"
gen() { dotnet msbuild "$HERE/GenSurface.proj" -t:Gen -nologo -v:q -p:NuGetPackageRoot="$PKGS" -p:In="$1" -p:Lib="$LIB" -p:Out="$2" -p:Exclude= -p:Message=x >/dev/null; }
gen "$WANT" "$OUT/want.cs"
gen "$GOT" "$OUT/got.cs"
# The header names the tool version only; [CompilerGenerated] on an auto-property's accessors and the
# nullable annotations are not part of the binding contract.
norm() {
  grep -v '^//' "$1" | sed 's/\[System.Runtime.CompilerServices.CompilerGeneratedAttribute\] //g' \
    | perl -ne "print unless /^\\s*$NULLABLE\\s*\$/" | perl -pe "s/$NULLABLE ?//g; s/\\?(?=[ ,)>\\]])//g"
}
NULLABLE='\[(return: )?System\.Runtime\.CompilerServices\.Nullable(Context)?Attribute\((?:[^()]|\([^()]*\))*\)\]' 
if diff <(norm "$OUT/want.cs") <(norm "$OUT/got.cs") > "$OUT/diff.txt"; then
  echo "$ID: surface matches $VER"
else
  echo "$ID: surface differs from $VER (< package, > shim):"; cat "$OUT/diff.txt"; exit 1
fi
