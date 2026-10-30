[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0',
    [string]$OutputRoot = 'artifacts',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Assert-Version([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be stable major.minor.patch (received '$Value')."
    }
}

Assert-Version $Version
$outputPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    Join-Path $root $OutputRoot
}
$appOut = Join-Path $outputPath 'LanBridge-win-x64'
$hostOut = Join-Path $outputPath '.staging/host-win-x64'
$versionProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.0",
    "-p:FileVersion=$Version.0",
    "-p:InformationalVersion=$Version"
)

Push-Location $root
try {
    if ((Test-Path $appOut) -and -not $Force) { throw "Refusing to overwrite existing payload: $appOut. Use -Force explicitly." }
    if ($Force) {
        Remove-Item $appOut, $hostOut -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $appOut, $hostOut -Force | Out-Null

    & dotnet publish src/LanBridge.App/LanBridge.App.csproj -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true @versionProperties -o $appOut
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
    & dotnet publish src/LanBridge.Host/LanBridge.Host.csproj -c $Configuration -r win-x64 --self-contained true @versionProperties -o $hostOut
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
    Copy-Item (Join-Path $hostOut '*') $appOut -Recurse -Force
    Copy-Item (Join-Path $root 'LICENSE') $appOut -Force
    Copy-Item (Join-Path $root 'README.md') $appOut -Force
    Copy-Item (Join-Path $root 'scripts') (Join-Path $appOut 'scripts') -Recurse -Force
    Write-Host "Ready: $(Join-Path $appOut 'LanBridge.App.exe')"
} finally {
    Pop-Location
}
