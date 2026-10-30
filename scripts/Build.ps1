[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$Version = '0.1.0',
    [string]$ArtifactsRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Assert-Version([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be stable major.minor.patch (received '$Value')."
    }
}

Assert-Version $Version
$artifactPath = if ([IO.Path]::IsPathRooted($ArtifactsRoot)) {
    [IO.Path]::GetFullPath($ArtifactsRoot)
} else {
    Join-Path $root $ArtifactsRoot
}
$testResults = Join-Path $artifactPath 'test-results'
New-Item -ItemType Directory -Path $testResults -Force | Out-Null
$versionProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.0",
    "-p:FileVersion=$Version.0",
    "-p:InformationalVersion=$Version"
)

Push-Location $root
try {
    if (-not $SkipTests) {
        & dotnet test tests/LanBridge.Core.Tests/LanBridge.Core.Tests.csproj -c $Configuration @versionProperties --logger 'trx;LogFileName=core.trx' --results-directory $testResults
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        & dotnet test tests/LanBridge.Host.Tests/LanBridge.Host.Tests.csproj -c $Configuration @versionProperties --logger 'trx;LogFileName=host.trx' --results-directory $testResults
        if ($LASTEXITCODE -ne 0) { throw 'Host tests failed.' }
        & dotnet test tests/LanBridge.Discovery.Tests/LanBridge.Discovery.Tests.csproj -c $Configuration @versionProperties --logger 'trx;LogFileName=discovery.trx' --results-directory $testResults
        if ($LASTEXITCODE -ne 0) { throw 'Discovery tests failed.' }
        & dotnet run --project tests/LanBridge.App.Tests/LanBridge.App.Tests.csproj -c $Configuration @versionProperties
        if ($LASTEXITCODE -ne 0) { throw 'UI presentation tests failed.' }
        & dotnet run --project tests/LanBridge.Editor.Tests/LanBridge.Editor.Tests.csproj -c $Configuration @versionProperties
        if ($LASTEXITCODE -ne 0) { throw 'Editor tests failed.' }
        & dotnet run --project tests/LanBridge.Lifecycle.Tests/LanBridge.Lifecycle.Tests.csproj -c $Configuration @versionProperties
        if ($LASTEXITCODE -ne 0) { throw 'Tray lifecycle tests failed.' }
    }

    & dotnet build src/LanBridge.Host/LanBridge.Host.csproj -c $Configuration @versionProperties
    if ($LASTEXITCODE -ne 0) { throw 'Host build failed.' }
    & dotnet build src/LanBridge.App/LanBridge.App.csproj -c $Configuration -p:Platform=x64 @versionProperties
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }

    Write-Host "Build succeeded for version $Version. Test results: $testResults"
} finally {
    Pop-Location
}
