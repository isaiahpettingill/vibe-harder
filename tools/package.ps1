param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','linux-musl-x64','linux-musl-arm64','osx-x64','osx-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.0.0',
    [switch]$Managed,
    [ValidateSet('aot','bundled','framework')][string]$Mode = 'aot'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$mode = if ($Managed) { 'bundled' } else { $Mode }
$isAot = $mode -eq 'aot'
$publish = Join-Path $repo "artifacts/publish/$Runtime-$mode"
$packages = Join-Path $repo 'artifacts/packages'
$cache = Join-Path $repo 'artifacts/downloads'
New-Item -ItemType Directory -Force $publish, $packages, $cache | Out-Null
foreach ($obsolete in @('CodexManager.exe','CodexManager.dll','CodexManager.deps.json','CodexManager.runtimeconfig.json')) {
    $oldFile = Join-Path $publish $obsolete
    if (Test-Path -LiteralPath $oldFile) { Remove-Item -LiteralPath $oldFile }
}
if ($isAot -and (($Runtime.StartsWith('osx') -and !$IsMacOS) -or ($Runtime.StartsWith('win') -and !$IsWindows) -or ($Runtime.StartsWith('linux') -and !$IsLinux))) {
    throw 'Native AOT requires building on the target OS. Use its CI runner, or explicitly pass -Managed for a managed cross-build.'
}
dotnet publish (Join-Path $repo 'src/CodexManager') -c Release -r $Runtime "--self-contained=$($mode -ne 'framework')" "-p:PublishAot=$isAot" -p:StripSymbols=true -o $publish
if ($LASTEXITCODE) { throw 'Publish failed' }
# Symbols stay in the build tree, not in distributed packages.
Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Extension -in '.pdb', '.dbg' } | Remove-Item

# Ship the official Node distribution, including npm/npx and its license.
# Its SHA256 is verified before extraction; no global install or PATH changes.
$nodeVersion = 'v22.23.2'
$nodePlatform = if ($Runtime.StartsWith('osx')) { 'darwin' } elseif ($Runtime.StartsWith('win')) { 'win' } else { 'linux' }
$arch = $Runtime.Split('-')[-1]
$stem = "node-$nodeVersion-$nodePlatform-$arch"
$extension = if ($nodePlatform -eq 'win') { 'zip' } else { 'tar.gz' }
$archiveName = "$stem.$extension"
$archive = Join-Path $cache $archiveName
$baseUrl = "https://nodejs.org/dist/$nodeVersion"
if (!$Runtime.Contains('musl')) {
$checksums = (Invoke-WebRequest "$baseUrl/SHASUMS256.txt").Content
$line = ($checksums -split "`n" | Where-Object { $_.Trim().EndsWith(" $archiveName", [StringComparison]::Ordinal) })
if (@($line).Count -ne 1) { throw "Missing checksum for $archiveName" }
$expected = ($line.Trim() -split '\s+')[0]
if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    Invoke-WebRequest "$baseUrl/$archiveName" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Node checksum mismatch' }
$node = Join-Path $publish 'runtime/node'
New-Item -ItemType Directory -Force $node | Out-Null
if ($extension -eq 'zip') {
    $extracted = Join-Path $cache $stem
    Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
    Copy-Item -Path (Join-Path $extracted '*') -Destination $node -Recurse -Force
} else {
    # Preserve npm/npx's relative symlinks; Copy-Item dereferences them.
    tar -xzf $archive --strip-components=1 -C $node
    if ($LASTEXITCODE) { throw 'Node extraction failed' }
}
}
# The app-only SSH server uses pure JS ssh2 (optional native helpers are omitted).
Set-Content -LiteralPath (Join-Path $publish 'package.json') -Value '{"private":true,"dependencies":{"ssh2":"1.17.0"}}'
npm install --prefix $publish --omit=dev --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE) { throw 'SSH server dependency installation failed' }
Copy-Item -LiteralPath (Join-Path $repo 'packaging/README.md') -Destination (Join-Path $publish 'INSTALL.md')
if (Test-Path (Join-Path $repo 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $publish }
Set-Content -LiteralPath (Join-Path $publish 'runtime.txt') -Value $Runtime -NoNewline

if ($Runtime.StartsWith('win')) {
    $compiler = Get-Command makensis -ErrorAction SilentlyContinue
    if (!$compiler) { throw 'Install NSIS (https://nsis.sourceforge.io/Download) and add makensis to PATH, then rerun.' }
    $output = Join-Path $packages "VibeHarder-$Version-$Runtime-$mode-Setup.exe"
    & $compiler.Source "/DPUBLISH=$publish" "/DOUTPUT=$output" "/DVERSION=$Version" (Join-Path $repo 'packaging/windows.nsi')
    if ($LASTEXITCODE) { throw 'Installer compilation failed' }
} elseif ($Runtime.StartsWith('linux')) {
    if (!$IsLinux) { throw 'Build Linux packages on Linux to preserve executable modes and symlinks.' }
    Copy-Item -LiteralPath (Join-Path $repo 'packaging/install-linux.sh') -Destination (Join-Path $publish 'install.sh')
    chmod +x (Join-Path $publish 'VibeHarder') (Join-Path $publish 'install.sh')
    $output = Join-Path $packages "VibeHarder-$Version-$Runtime-$mode.tar.gz"
    tar -czf $output -C $publish .
    if ($LASTEXITCODE) { throw 'Linux packaging failed' }
} else {
    if (!$IsMacOS -and !$IsLinux) { throw 'Build macOS bundles on macOS or Linux to preserve executable modes and symlinks.' }
    $staging = Join-Path $repo ("artifacts/bundles/$Runtime-$mode-" + [Guid]::NewGuid().ToString('N'))
    $bundle = Join-Path $staging 'Vibe Harder.app'
    $macos = Join-Path $bundle 'Contents/MacOS'
    $resources = Join-Path $bundle 'Contents/Resources'
    New-Item -ItemType Directory -Force $macos, $resources | Out-Null
    cp -a "$publish/." "$macos/"
    if ($LASTEXITCODE) { throw 'App bundle copy failed' }
    Copy-Item -LiteralPath (Join-Path $repo 'src/CodexManager/Assets/app.icns') -Destination (Join-Path $resources 'app.icns')
    (Get-Content -LiteralPath (Join-Path $repo 'packaging/Info.plist') -Raw).Replace('@VERSION@', $Version) | Set-Content -LiteralPath (Join-Path $bundle 'Contents/Info.plist')
    chmod +x (Join-Path $macos 'VibeHarder')
    # Ad-hoc signing makes a local bundle runnable. Distributors may set a Developer ID.
    $output = Join-Path $packages "VibeHarder-$Version-$Runtime-$mode.zip"
    if ($IsMacOS) {
        $identity = if ($env:CODEX_MANAGER_SIGN_IDENTITY) { $env:CODEX_MANAGER_SIGN_IDENTITY } else { '-' }
        codesign --force --deep --sign $identity $bundle
        if ($LASTEXITCODE) { throw 'App signing failed' }
        codesign --verify --deep --strict $bundle
        if ($LASTEXITCODE) { throw 'App signature verification failed' }
        ditto -c -k --sequesterRsrc --keepParent $bundle $output
    } else {
        Push-Location $staging
        try { zip -qry $output 'Vibe Harder.app' }
        finally { Pop-Location }
        Write-Warning 'Cross-built macOS bundle: native validation, signing, and notarization still require macOS.'
    }
    if ($LASTEXITCODE) { throw 'macOS packaging failed' }
}
Write-Output "Created $output"
