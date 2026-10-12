#!/usr/bin/env bash
# Builds the fixtures -- ordinary net10.0-windows apps, stock SDK -- into artifacts/wpfmac-fixtures/<name>/,
# where they are run as `wpfmac run artifacts/wpfmac-fixtures/<name>/<name>.dll -- --seconds 5`.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(cd "$HERE/../../../.." && pwd)/artifacts/wpfmac-fixtures"
for proj in "$HERE"/*/*.csproj; do
  name="$(basename "$proj" .csproj)"
  echo ">> $name -> $OUT/$name"
  dotnet build "$proj" -c Release -o "$OUT/$name" -nologo -v q -clp:NoSummary
done
