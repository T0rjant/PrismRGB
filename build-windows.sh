#!/usr/bin/env bash
# Compile PrismRGB.exe (Windows x64, autonome, un seul fichier) depuis macOS ou Linux,
# puis le place à la racine du projet.
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH"
dotnet test tests/PrismRGB.Tests
dotnet publish src/PrismRGB.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
cp dist/PrismRGB.exe ./PrismRGB.exe
echo "OK : PrismRGB.exe"
