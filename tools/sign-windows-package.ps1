param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime,
    [ValidateSet('aot','bundled','framework')][string]$Mode,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or !$env:RUNNER_TEMP) { throw 'Use an isolated Windows release runner.' }
$thumbprint = $env:WINDOWS_SIGNING_THUMBPRINT
if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or !$env:WINDOWS_SIGNING_PFX_BASE64 -or !$env:WINDOWS_SIGNING_PFX_PASSWORD) {
    throw 'Configure the Windows signing secrets and publisher thumbprint before releasing.'
}
$stage = Join-Path $env:RUNNER_TEMP ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$personal = $null
$root = $null
try {
    $pfx = Join-Path $stage 'publisher.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_SIGNING_PFX_BASE64))
    $password = ConvertTo-SecureString $env:WINDOWS_SIGNING_PFX_PASSWORD -AsPlainText -Force
    Remove-Item Env:WINDOWS_SIGNING_PFX_BASE64, Env:WINDOWS_SIGNING_PFX_PASSWORD
    $personal = Import-PfxCertificate -FilePath $pfx -Password $password -CertStoreLocation Cert:\CurrentUser\My
    Remove-Item -LiteralPath $pfx
    if (@($personal).Count -ne 1 -or !$personal.HasPrivateKey -or $personal.Thumbprint -ne $thumbprint) {
        throw 'The signing key does not match the configured publisher.'
    }
    # Trust the pinned self-signed leaf only on the disposable runner so that
    # Authenticode can validate the signature and timestamp before publication.
    if ($personal.Subject -eq $personal.Issuer) {
        $public = Join-Path $stage 'publisher.cer'
        Export-Certificate -Cert $personal -FilePath $public | Out-Null
        $root = Import-Certificate -FilePath $public -CertStoreLocation Cert:\CurrentUser\Root
    }
    & "$PSScriptRoot/package.ps1" -Runtime $Runtime -Mode $Mode -Version $Version -SignWindows
} finally {
    if ($root) { Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$($root.Thumbprint)" }
    foreach ($cert in @($personal) | Where-Object { $_ }) { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
