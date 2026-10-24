#!/usr/bin/env bash
# Compile PrismRGB.exe (Windows x64, autonome, un seul fichier) depuis macOS ou Linux.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"
dotnet test tests/PrismRGB.Tests
dotnet publish src/PrismRGB.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/PrismRGB
cp LISEZ-MOI.txt dist/PrismRGB/
echo "OK : dist/PrismRGB/PrismRGB.exe"
