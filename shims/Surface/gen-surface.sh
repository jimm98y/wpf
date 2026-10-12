#!/usr/bin/env bash
# Regenerates a shim's Surface.g.cs from the package it stands in for:
#   shims/Surface/gen-surface.sh <ShimDir> <PackageId> <Version>
# The shim's implemented.txt lists the doc IDs it implements by hand (T:, M:, P:, E:, F:); they are
# left out of the generated file. Everything else throws PlatformNotSupportedException, exactly as
# the package's own non-Windows build does.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SHIM="$(cd "$1" && pwd)"; ID="$2"; VER="$3"
PKGS="${NUGET_PACKAGES:-$HOME/.nuget/packages}/"
lower="$(echo "$ID" | tr '[:upper:]' '[:lower:]')"
IN="$PKGS$lower/$VER/lib/net10.0/$ID.dll"
[ -f "$IN" ] || { echo "error: $IN not found; restore $ID $VER first" >&2; exit 1; }
ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
REF="$(ls -d "$ROOT"/packs/Microsoft.NETCore.App.Ref/10.* | sort -V | tail -1)/ref/net10.0"
# Its dependencies outside the framework (System.CodeDom, System.Diagnostics.EventLog, ...).
LIB="$REF"
for d in "$PKGS"system.codedom/"$VER"/lib/net10.0 "$PKGS"system.diagnostics.eventlog/"$VER"/lib/net10.0; do [ -d "$d" ] && LIB="$LIB%3B$d"; done
EXCL="$SHIM/implemented.txt"; [ -f "$EXCL" ] || EXCL=""
dotnet msbuild "$HERE/GenSurface.proj" -t:Gen -nologo -v:m -p:NuGetPackageRoot="$PKGS" \
  -p:In="$IN" -p:Lib="$LIB" -p:Out="$SHIM/Surface.g.cs" -p:Exclude="$EXCL" \
  -p:Message="$ID is not supported on this platform."

# A type whose constructors the shim implements: GenAPI, finding no constructor left to emit, adds an
# internal parameterless one so the compiler does not; the shim's own constructors replace it.
if [ -n "$EXCL" ]; then
  for t in $(grep -o '^M:[A-Za-z0-9_.]*\.#ctor' "$EXCL" | sed -E 's/^M:.*\.([A-Za-z0-9_]+)\.#ctor$/\1/' | sort -u); do
    perl -i -ne "print unless /^\s+internal $t\(\) \{ \}\s*\$/" "$SHIM/Surface.g.cs"
  done
fi

# Nullable annotations are compiler-reserved attributes; they cannot be written in source.
NULLABLE='\[(return: )?System\.Runtime\.CompilerServices\.Nullable(Context)?Attribute\((?:[^()]|\([^()]*\))*\)\]'
perl -i -ne "print unless /^\\s*$NULLABLE\\s*\$/" "$SHIM/Surface.g.cs"
perl -i -pe "s/$NULLABLE ?//g" "$SHIM/Surface.g.cs"
