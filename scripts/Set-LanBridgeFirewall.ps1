#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][Guid]$RuleId,
    [Parameter(Mandatory = $true, ParameterSetName = 'Add')]
    [ValidateScript({ $parsed = $null; [System.Net.IPAddress]::TryParse($_, [ref]$parsed) })]
    [string]$ListenAddress,
    [Parameter(Mandatory = $true, ParameterSetName = 'Add')]
    [ValidateRange(1, 65535)][int]$Port,
    [Parameter(ParameterSetName = 'Add')][ValidateSet('TCP','UDP')][string]$Protocol = 'TCP',
    [Parameter(Mandatory = $true, ParameterSetName = 'Remove')][switch]$Remove
)
$ErrorActionPreference = 'Stop'
$name = "LanBridge-$RuleId"
$existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
if ($existing -and @($existing | Where-Object { $_.Group -ne 'LanBridge' }).Count -gt 0) {
    throw "Refusing to modify a rule not owned by LanBridge: $name"
}
if ($Remove) {
    if ($existing -and $PSCmdlet.ShouldProcess($name, 'Remove owned LanBridge firewall rule')) {
        Remove-NetFirewallRule -Name $name -ErrorAction Stop
    }
    return
}
if ($existing) { throw "Rule $name already exists. Remove the old owned rule explicitly before replacing it." }
if ($PSCmdlet.ShouldProcess("$ListenAddress`:$Port/$Protocol (Private, LocalSubnet)", 'Allow inbound traffic')) {
    New-NetFirewallRule -Name $name -DisplayName "LanBridge $ListenAddress`:$Port/$Protocol" `
        -Group 'LanBridge' -Direction Inbound -Action Allow -Enabled True `
        -Protocol $Protocol -LocalAddress $ListenAddress -LocalPort $Port `
        -RemoteAddress LocalSubnet -Profile Private -ErrorAction Stop
}
