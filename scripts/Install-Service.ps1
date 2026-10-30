#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string] $PublishDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^S-1-5-21-\d+-\d+-\d+-\d+$')][string] $OwnerSid
)
$ErrorActionPreference = 'Stop'
$serviceName = 'LanBridge'
if (-not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit Windows PowerShell.' }
$installDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'LanBridge'
$dataDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LanBridge'
$owner = New-Object System.Security.Principal.SecurityIdentifier($OwnerSid)
if (-not $owner.IsAccountSid()) { throw 'OwnerSid must be an account SID.' }
$source = (Resolve-Path -LiteralPath $PublishDirectory).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $source 'LanBridge.Host.exe') -PathType Leaf)) { throw 'PublishDirectory must contain the published LanBridge.Host.exe and all dependencies.' }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'LanBridge already exists. Stop/uninstall it explicitly before reinstalling.' }
function Assert-NoReparse([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not allowed: $($item.FullName)" }
        $item = $item.Parent
    }
}
Assert-NoReparse $source
foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish tree must not contain symbolic links or junctions.' }
}
foreach ($path in @($installDirectory, $dataDirectory)) {
    Assert-NoReparse $path
    if (Test-Path -LiteralPath $path) {
        foreach ($item in Get-ChildItem -LiteralPath $path -Recurse -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Destination must not contain symbolic links or junctions.' }
        }
    }
}
if ($source.TrimEnd('\').Equals($installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish from a separate directory; installation copies binaries into protected Program Files.' }
function Set-ProtectedAcl([string] $Path, [bool] $Data) {
    $items = @((Get-Item -LiteralPath $Path -Force)) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    foreach ($item in $items) {
        if ($item.PSIsContainer) { $acl = New-Object System.Security.AccessControl.DirectorySecurity }
        else { $acl = New-Object System.Security.AccessControl.FileSecurity }
        $acl.SetAccessRuleProtection($true, $false)
        $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
        $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
        $acl.SetOwner($admins)
        foreach ($entry in @(@($admins, 'FullControl'), @($system, 'FullControl'), @($owner, 'ReadAndExecute'))) {
            if ($item.PSIsContainer) {
                $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($entry[0], $entry[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            } else {
                $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($entry[0], $entry[1], 'Allow')
            }
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}
if ($PSCmdlet.ShouldProcess('LanBridge', "Copy published binaries to $installDirectory, restrict ACLs, install and start LocalSystem service for $OwnerSid (no firewall change)")) {
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    Set-ProtectedAcl $installDirectory $false
    Set-ProtectedAcl $dataDirectory $true
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $installDirectory -Recurse -Force
    }
    Set-ProtectedAcl $installDirectory $false
    $executable = Join-Path $installDirectory 'LanBridge.Host.exe'
    $binaryPath = '"' + $executable + '" --service --owner-sid "' + $OwnerSid + '"'
    New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'LanBridge Forwarding Host' -StartupType Automatic -Description 'Per-owner local named-pipe TCP/UDP forwarding; no automatic firewall changes.' | Out-Null
    Start-Service -Name $serviceName
    $installed = Get-CimInstance Win32_Service -Filter "Name='LanBridge'"
    if ($installed.PathName -ne $binaryPath -or $installed.State -ne 'Running') { throw 'Service read-back verification failed. Inspect Event Viewer and ProgramData/LanBridge/host.log.' }
    Write-Output "LanBridge installed: $($installed.PathName)"
    Write-Output "Configuration retained at $dataDirectory. No firewall rules were modified."
}
