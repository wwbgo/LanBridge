[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration
    if (-not $?) { throw 'Build or unit tests failed.' }
    $hostExe = Join-Path $root "src/LanBridge.Host/bin/$Configuration/net10.0-windows/LanBridge.Host.exe"
    & dotnet run --project tests/LanBridge.Acceptance/LanBridge.Acceptance.csproj -c $Configuration -- $hostExe
    if ($LASTEXITCODE -ne 0) { throw 'Real-process local acceptance failed.' }
    Write-Host 'Local verification passed. This does not certify remote LAN/VM/firewall access.'
} finally { Pop-Location }
