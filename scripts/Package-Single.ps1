[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '0.1.0',
    [string]$OutputRoot = 'artifacts',
    [string]$IsccPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Assert-Version([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be stable major.minor.patch (received '$Value')."
    }
}

function Find-Iscc([string]$RequestedPath) {
    if ($RequestedPath) {
        if (-not (Test-Path $RequestedPath -PathType Leaf)) { throw "ISCC.exe was not found at '$RequestedPath'." }
        return (Resolve-Path $RequestedPath).Path
    }

    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidates = @(@(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        $(if ($env:ChocolateyInstall) { Join-Path $env:ChocolateyInstall 'bin/ISCC.exe' })
    ) | Where-Object { $_ -and (Test-Path $_ -PathType Leaf) })
    if ($candidates.Count -gt 0) { return (Resolve-Path $candidates[0]).Path }
    throw 'ISCC.exe was not found. Install Inno Setup or pass -IsccPath explicitly.'
}

Assert-Version $Version
$outputPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    Join-Path $root $OutputRoot
}
$stage = Join-Path $outputPath '.staging/LanBridge-single'
$appOut = Join-Path $outputPath 'LanBridge-win-x64'
$hostOut = Join-Path $stage 'host'
$installerOut = $outputPath
$installer = Join-Path $installerOut 'LanBridge-Setup.exe'
$iss = Join-Path $root 'scripts/LanBridge.iss'
$versionProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.0",
    "-p:FileVersion=$Version.0",
    "-p:InformationalVersion=$Version"
)

Push-Location $root
try {
    if ((Test-Path $appOut) -and -not $Force) { throw "Refusing to overwrite existing payload: $appOut. Use -Force explicitly." }
    if ((Test-Path $installer) -and -not $Force) { throw "Refusing to overwrite existing installer: $installer. Use -Force explicitly." }
    if ($Force) {
        Remove-Item $stage, $appOut, $installer -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $appOut, $hostOut, $installerOut -Force | Out-Null

    & dotnet publish src/LanBridge.App/LanBridge.App.csproj -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true -p:PublishSingleFile=false @versionProperties -o $appOut
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }

    & dotnet publish src/LanBridge.Host/LanBridge.Host.csproj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true @versionProperties -o $hostOut
    if ($LASTEXITCODE -ne 0) { throw 'Host single-file publish failed.' }

    Copy-Item (Join-Path $hostOut 'LanBridge.Host.exe') $appOut -Force
    Copy-Item (Join-Path $root 'LICENSE') $appOut -Force
    Copy-Item (Join-Path $root 'README.md') $appOut -Force
    Copy-Item (Join-Path $root 'scripts') (Join-Path $appOut 'scripts') -Recurse -Force

    $iscc = Find-Iscc $IsccPath
    & $iscc "/DSourceDir=$appOut" "/DOutputDir=$installerOut" "/DMyAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) { throw 'Single-file installer build failed.' }
    if (-not (Test-Path $installer -PathType Leaf)) { throw "Inno Setup did not produce '$installer'." }

    Write-Host "Ready: $installer"
    Write-Host "Portable payload: $appOut"
} finally {
    Pop-Location
}
