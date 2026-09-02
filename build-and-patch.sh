#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/VRHFOpenAIAdapter"
MANAGED="$ROOT/VRHandsFrame_Data/Managed"
BUILD="$SRC/build"
mkdir -p "$BUILD"

CECIL="$(find /opt/homebrew/lib/mono/gac/Mono.Cecil /usr/local/lib/mono/gac/Mono.Cecil /usr/lib/mono/gac/Mono.Cecil -path '*/0.11.*/*' -name Mono.Cecil.dll 2>/dev/null | head -n 1 || true)"
if [[ -z "$CECIL" ]]; then
  echo "Mono.Cecil 0.11 was not found. Install Mono first." >&2
  exit 1
fi

mcs -sdk:4.7.2 -langversion:7 -target:library -optimize+ \
  -out:"$MANAGED/VRHF.OpenAIAdapter.dll" \
  -r:"$MANAGED/Newtonsoft.Json.dll" \
  -r:"$MANAGED/netstandard.dll" \
  "$SRC/VRHFOpenAIAdapter.cs"

mcs -sdk:4.7.2 -langversion:7 -target:exe -optimize+ \
  -out:"$BUILD/PatchAssembly.exe" \
  -r:"$CECIL" \
  "$SRC/PatchAssembly.cs"

MONO_PATH="$(dirname "$CECIL")" mono "$BUILD/PatchAssembly.exe" \
  "$MANAGED/Assembly-CSharp.dll" "$MANAGED/VRHF.OpenAIAdapter.dll"

echo "Build and patch completed."
