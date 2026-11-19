#!/bin/sh
# Builds the programs for people to download: dist/<runtime>/ with Uplift (the window) and uplift-cli (the command line), each
# self-contained (no .NET needed on the machine). Usage: scripts/publish.sh [runtime...] (default: linux-x64 win-x64 osx-arm64).
set -eu
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-dotnet}
runtimes=${*:-linux-x64 win-x64 osx-arm64}
for rid in $runtimes; do
  out=dist/uplift-$rid
  rm -rf "$out"
  "$DOTNET" publish src/Uplift.Gui/Uplift.Gui.csproj -c Release -r "$rid" --self-contained -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "$out" --disable-build-servers -nologo -v:q
  "$DOTNET" publish src/Uplift.Cli/Uplift.Cli.csproj -c Release -r "$rid" --self-contained -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -o "$out" --disable-build-servers -nologo -v:q
  mkdir -p "$out/plugins"
  rm -f "$out"/*.pdb "$out"/*.xml
  echo "$out"
done
