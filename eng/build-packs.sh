#!/usr/bin/env bash
# Builds everything the Packager needs, from source, into artifacts/:
#
#   artifacts/local-feed/        WpfWebGpu.Sdk and its packages (eng/build-sdk.sh)
#   artifacts/wpfrun/<rid>/      wpfrun for each desktop RID (tools/WpfWebGpu/build.sh -r <rid>)
#   artifacts/packs/             every template the Packager fills: android, ios and visionos (on a Mac), browser,
#                                desktop-<rid> and setup-win-<arch> for each RID, sqlserver-win-x64 and -macos
#   artifacts/Packager-<rid>/    the Packager for each host, the packs beside it, and its .zip / .tar.gz (with
#                                --packager-without-packs an empty packs/ folder: the packs are downloaded apart,
#                                as a release carries them -- a Packager with every pack in it passes 2 GB)
#
# What CI runs (.github/workflows/packs.yml), and what a developer runs to make the same packs locally.
#
# Usage:
#   eng/build-packs.sh                         everything this machine can build
#   eng/build-packs.sh --no-sdk                use the SDK already in artifacts/local-feed
#   eng/build-packs.sh --no-mobile             no android, ios, visionos packs
#   eng/build-packs.sh --rids osx-arm64,linux-x64   only these desktop RIDs (and their Packagers)
#   eng/build-packs.sh --packager-without-packs     the Packagers without the packs (as CI releases them)

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK=1
MOBILE=1
RIDS="osx-arm64,osx-x64,linux-x64,linux-arm64,win-x64,win-arm64"
PACKAGER_ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-sdk) SDK=0; shift ;;
    --no-mobile) MOBILE=0; shift ;;
    --rids) RIDS="$2"; shift 2 ;;
    --packager-without-packs) PACKAGER_ARGS+=(--without-packs); shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# the repo's SDK first, as eng/build-sdk.sh picks it (tools/WpfWebGpu/build.sh calls `dotnet` by name)
if [ -n "${DOTNET:-}" ] && [ -x "${DOTNET}" ]; then :
elif [ -x "$REPO/.dotnet/dotnet" ]; then DOTNET="$REPO/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
else echo "error: no dotnet found. Run ./build.sh once to bootstrap .dotnet, or set DOTNET." >&2; exit 1
fi
export DOTNET PATH="$(dirname "$DOTNET"):$PATH"
echo ">> dotnet: $DOTNET ($("$DOTNET" --version))"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) HOST=osx-arm64 ;; Darwin-x86_64) HOST=osx-x64 ;;
  Linux-aarch64) HOST=linux-arm64 ;; Linux-x86_64) HOST=linux-x64 ;;
  *) echo "error: cannot tell this machine's RID" >&2; exit 2 ;;
esac

if [ "$SDK" = 1 ]; then
  echo ">> the SDK (eng/build-sdk.sh)"
  "$REPO/eng/build-sdk.sh"
fi

# wpfrun for each RID, and for this machine (whose launcher makes the packs)
IFS=',' read -r -a RID_LIST <<< "$RIDS"
for rid in $(printf '%s\n' "${RID_LIST[@]}" "$HOST" | awk '!seen[$0]++'); do
  echo ">> wpfrun for $rid"
  "$REPO/tools/WpfWebGpu/build.sh" -r "$rid"
done
WPFRUN=("$DOTNET" "$REPO/artifacts/wpfrun/$HOST/launcher/wpfrun.dll")
PACKS="$REPO/artifacts/packs"
mkdir -p "$PACKS"

WINDOWS=$(printf '%s\n' "${RID_LIST[@]}" | grep '^win-' | paste -sd, - || true)
echo ">> desktop packs ($RIDS), setup ($WINDOWS), SQL Server"
ARGS=(pack --desktop "$RIDS" --wpfrun "$REPO/artifacts/wpfrun" --sqlserver win-x64,macos --out "$PACKS")
[ -n "$WINDOWS" ] && ARGS+=(--setup "$WINDOWS")
"${WPFRUN[@]}" "${ARGS[@]}"

echo ">> browser pack"
"${WPFRUN[@]}" pack --browser --feed "$REPO/artifacts/local-feed" --out "$PACKS"
if [ "$MOBILE" = 1 ]; then
  echo ">> android pack"
  "${WPFRUN[@]}" pack --android --feed "$REPO/artifacts/local-feed" --out "$PACKS"
  if [ "$(uname -s)" = Darwin ]; then
    echo ">> ios and visionos packs (Xcode)"
    "${WPFRUN[@]}" pack --ios --visionos --feed "$REPO/artifacts/local-feed" --out "$PACKS"
  else
    echo ">> no ios or visionos packs: they are built on a Mac (Xcode)"
  fi
fi

# the Packager for each host, every pack beside it (made after them: it carries what packs/ holds)
echo ">> the Packager ($RIDS)"
"${WPFRUN[@]}" pack --packager "$RIDS" ${PACKAGER_ARGS[@]+"${PACKAGER_ARGS[@]}"} --out "$PACKS"
ls -la "$PACKS"
echo ">> done: $PACKS, and artifacts/Packager-<rid>"
