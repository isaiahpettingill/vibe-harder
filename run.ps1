$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'src/CodexManager/CodexManager.csproj') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'src/CodexManager/bin/Release/net11.0/VibeHarder.exe')
