param(
    [Parameter(Mandatory)][string[]]$Paths
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Authenticode signing requires Windows.' }
$thumbprint = $env:WINDOWS_SIGNING_THUMBPRINT
if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Configure the Windows release signer thumbprint.' }
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint"
if (!$certificate.HasPrivateKey) { throw 'The Windows release signer has no private key.' }
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem -LiteralPath $kits -Directory | Where-Object Name -Match '^10\.\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending | ForEach-Object {
        foreach ($arch in @(([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()), 'x64', 'x86')) {
            $candidate = Join-Path $_.FullName "$arch/signtool.exe"
            if (Test-Path -LiteralPath $candidate) { $candidate }
        }
    } | Select-Object -First 1
if (!$signTool) { throw 'Windows SDK SignTool was not found.' }

foreach ($path in $Paths) {
    & $signTool sign /fd SHA256 /sha1 $thumbprint /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $thumbprint -or !$signature.TimeStamperCertificate) {
        throw "Invalid signature or missing timestamp: $path ($($signature.Status))"
    }
}
