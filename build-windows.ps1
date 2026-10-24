# Compile PrismRGB.exe directement sous Windows (nécessite le SDK .NET 10).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet test tests/PrismRGB.Tests
dotnet publish src/PrismRGB.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/PrismRGB
Copy-Item LISEZ-MOI.txt dist/PrismRGB/
Write-Host "OK : dist\PrismRGB\PrismRGB.exe"
