#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name 'LanBridge' -ErrorAction SilentlyContinue
if (-not $service) { Write-Output 'LanBridge service is not installed.'; return }
if ($PSCmdlet.ShouldProcess('LanBridge', 'Stop and remove Windows service; keep protected binaries, configuration and logs; do not change firewall')) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name 'LanBridge'
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
    }
    & "$env:SystemRoot\System32\sc.exe" delete LanBridge
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed: $LASTEXITCODE" }
    $service.Dispose()
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $remaining = Get-Service -Name 'LanBridge' -ErrorAction SilentlyContinue
        if (-not $remaining) { break }
        $remaining.Dispose()
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (Get-Service -Name 'LanBridge' -ErrorAction SilentlyContinue) { throw 'Service is marked for deletion; close Service Manager and retry verification.' }
    Write-Output 'LanBridge service removed. Protected Program Files/LanBridge and ProgramData/LanBridge were retained. No firewall rules changed.'
}
