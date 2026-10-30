# Dependency-free PowerShell tests; system firewall is never touched.
$ErrorActionPreference = 'Stop'
$script:captured = $null
$script:existing = $null
function Get-NetFirewallRule { param($Name, $ErrorAction) return $script:existing }
function New-NetFirewallRule {
    param($Name,$DisplayName,$Group,$Direction,$Action,$Enabled,$Protocol,$LocalAddress,$LocalPort,$RemoteAddress,$Profile,$ErrorAction)
    $script:captured = $PSBoundParameters
}
function Remove-NetFirewallRule { param($Name,$ErrorAction) $script:captured = $PSBoundParameters }
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$helper = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts/Set-LanBridgeFirewall.ps1'
$id = [Guid]::NewGuid()
& $helper -RuleId $id -ListenAddress '127.0.0.1' -Port 18080 -Protocol TCP
Assert ($null -ne $script:captured) 'No firewall rule was requested.'
Assert ($script:captured.Name -eq "LanBridge-$id") 'Unowned rule name.'
Assert ($script:captured.Group -eq 'LanBridge') 'Missing ownership group.'
Assert ($script:captured.Profile -eq 'Private') 'Public profile must never be opened implicitly.'
Assert ($script:captured.RemoteAddress -eq 'LocalSubnet') 'Sources must be limited to local subnet.'
Assert ($script:captured.LocalAddress -eq '127.0.0.1') 'Must preserve exact listening IP.'
Assert ($script:captured.LocalPort -eq 18080) 'Must preserve exact port.'
Assert ($script:captured.Protocol -eq 'TCP') 'Must preserve protocol.'
Write-Host 'PASS firewall add is owned and scoped to Private/LocalSubnet/exact address+port'
$script:captured = $null
& $helper -RuleId $id -ListenAddress '127.0.0.1' -Port 18080 -WhatIf
Assert ($null -eq $script:captured) 'WhatIf changed firewall.'
Write-Host 'PASS WhatIf does not change firewall'
$script:existing = [pscustomobject]@{Group='OtherApp'}
$rejected = $false
try { & $helper -RuleId $id -Remove } catch { $rejected = $true }
Assert $rejected 'Must refuse to remove an unowned firewall rule.'
Write-Host 'PASS unowned firewall rule removal rejected'
$script:existing = [pscustomobject]@{Group='LanBridge'}
& $helper -RuleId $id -Remove
Assert ($script:captured.Name -eq "LanBridge-$id") 'Deletion must target exact owned rule.'
Write-Host 'PASS deletion targets only the requested owned rule'
$rejected = $false
try { & $helper -RuleId $id -ListenAddress 'not-an-ip' -Port 18080 } catch { $rejected = $true }
Assert $rejected 'Invalid listen address accepted.'
Write-Host 'PASS invalid IP rejected'
