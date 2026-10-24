# Compile PrismRGB.exe directement sous Windows (nécessite le SDK .NET 10), puis le place à la racine.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet test tests/PrismRGB.Tests
dotnet publish src/PrismRGB.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
Copy-Item dist/PrismRGB.exe ./PrismRGB.exe -Force
Write-Host "OK : PrismRGB.exe"
