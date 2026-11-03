# Builds publish\VirtualStreamDeck.exe (one file, nothing else to install).
# Requires the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet run --project tests/VirtualStreamDeck.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

dotnet publish src/VirtualStreamDeck.App -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Write-Host "`nHotovo: $PSScriptRoot\publish\VirtualStreamDeck.exe" -ForegroundColor Green
