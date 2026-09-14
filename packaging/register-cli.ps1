param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$directory = $PSScriptRoot
$key = 'HKCU:\Software\CodexManager'
$path = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($path -split ';' | Where-Object { $_ -ne '' })
$ownsEntry = (Get-ItemProperty -LiteralPath $key -Name CliPathAdded -ErrorAction SilentlyContinue).CliPathAdded -eq $directory
if ($Remove) {
    if (!$ownsEntry) { exit 0 }
    $parts = @($parts | Where-Object { $_.TrimEnd('\') -ine $directory.TrimEnd('\') })
} else {
    if ($parts.Where({ $_.TrimEnd('\') -ieq $directory.TrimEnd('\') }).Count -gt 0) { exit 0 }
    $parts += $directory
}
[Environment]::SetEnvironmentVariable('Path', ($parts -join ';'), 'User')
if ($Remove) { Remove-ItemProperty -LiteralPath $key -Name CliPathAdded }
else {
    if (!(Test-Path -LiteralPath $key)) { New-Item -Path $key | Out-Null }
    Set-ItemProperty -LiteralPath $key -Name CliPathAdded -Value $directory
}
