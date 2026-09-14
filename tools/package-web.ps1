param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/CodexManager.Browser'
$publishOptions = @()
if ($ArtifactsPath) { $publishOptions += @('--artifacts-path', $ArtifactsPath) }
dotnet publish $project -c Release "-p:Version=$Version" @publishOptions
if ($LASTEXITCODE) { throw 'Web publish failed' }
$bundle = Join-Path $project 'bin/Release/net10.0-browser/browser-wasm/publish/wwwroot'
if (!(Test-Path -LiteralPath (Join-Path $bundle 'index.html'))) {
    $bundle = Join-Path $project 'bin/Release/net10.0-browser/publish/wwwroot'
}
if ($ArtifactsPath) { $bundle = Join-Path $ArtifactsPath 'publish/CodexManager.Browser/release/wwwroot' }
if (!(Test-Path -LiteralPath (Join-Path $bundle 'index.html')) -or !(Test-Path -LiteralPath (Join-Path $bundle '_framework/dotnet.js'))) { throw 'Incomplete web bundle' }
$packages = Join-Path $repo 'artifacts/packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null
$archive = Join-Path $packages 'VibeHarder-Web.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output $archive
