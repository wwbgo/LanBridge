[CmdletBinding()]
param(
    [string]$TagName,
    [string]$Version = '0.1.1',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputRoot = 'artifacts/release',
    [string]$IsccPath,
    [switch]$ValidateOnly,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

function Get-ReleaseVersion([string]$Tag, [string]$RequestedVersion) {
    if ($Tag) {
        if ($Tag -notmatch '^v(?<version>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$') {
            throw "Release tag must match vX.Y.Z (received '$Tag')."
        }
        $tagVersion = $Matches.version
        if ($PSBoundParameters.ContainsKey('RequestedVersion') -and $RequestedVersion -and $RequestedVersion -ne '0.1.1' -and $RequestedVersion -ne $tagVersion) {
            throw "Version '$RequestedVersion' does not match tag '$Tag'."
        }
        return $tagVersion
    }

    if ($RequestedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be stable major.minor.patch (received '$RequestedVersion')."
    }
    return $RequestedVersion
}

function Resolve-RepoPath([string]$PathValue) {
    if ([IO.Path]::IsPathRooted($PathValue)) { return [IO.Path]::GetFullPath($PathValue) }
    return Join-Path $root $PathValue
}

function Write-Sha256Sums([string]$Directory) {
    $names = @('LanBridge-Setup.exe', 'LanBridge-win-x64.zip')
    $lines = foreach ($name in $names) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path $path -PathType Leaf)) { throw "Missing release asset: $path" }
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
        '{0}  {1}' -f $hash, $name
    }
    $output = Join-Path $Directory 'SHA256SUMS'
    [IO.File]::WriteAllLines($output, $lines, [Text.UTF8Encoding]::new($false))
    return $output
}

$resolvedVersion = Get-ReleaseVersion $TagName $Version
if ($ValidateOnly) {
    Write-Output $resolvedVersion
    exit 0
}

$outputPath = Resolve-RepoPath $OutputRoot
if ((Test-Path $outputPath) -and -not $Force) {
    throw "Refusing to overwrite existing release directory: $outputPath. Use -Force explicitly."
}
if ($Force) { Remove-Item $outputPath -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$packageScript = Join-Path $root 'scripts/Package-Single.ps1'
& $packageScript -Configuration $Configuration -Version $resolvedVersion -OutputRoot $outputPath -IsccPath $IsccPath -Force
if ($LASTEXITCODE -ne 0) { throw 'Versioned package creation failed.' }

$appPath = Join-Path $outputPath 'LanBridge-win-x64'
$zipPath = Join-Path $outputPath 'LanBridge-win-x64.zip'
if (-not (Test-Path (Join-Path $appPath 'LanBridge.App.exe') -PathType Leaf)) {
    throw "Published app is missing from '$appPath'."
}

Add-Type -AssemblyName System.IO.Compression
[IO.Compression.ZipFile]::CreateFromDirectory($appPath, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($archive.Entries.Count -eq 0) { throw 'Portable archive is empty.' }
    if ($archive.Entries | Where-Object { $_.FullName.StartsWith('LanBridge-win-x64/', [StringComparison]::OrdinalIgnoreCase) }) {
        throw 'Portable archive must contain the app directory at its flat root.'
    }
} finally {
    $archive.Dispose()
}

$sumsPath = Write-Sha256Sums $outputPath
Write-Host "Release assets for version $resolvedVersion"
Write-Host "  Installer: $([IO.Path]::GetFullPath((Join-Path $outputPath 'LanBridge-Setup.exe')))"
Write-Host "  Portable:  $([IO.Path]::GetFullPath($zipPath))"
Write-Host "  Checksums: $([IO.Path]::GetFullPath($sumsPath))"
