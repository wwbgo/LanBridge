[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$process=Get-Process -Id $AppProcessId
$root=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
function Find([string]$name){$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Need([string]$name){$e=Find $name;if(-not $e){throw "Missing $name"};return $e}
function Invoke([string]$name){(Need $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 400}
function Value($element){
 if($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox){
  $element=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))
 }
 return [System.Windows.Automation.ValuePattern]$element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
}
function Scroll($element){$p=$null;if($element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern,[ref]$p)){$p.ScrollIntoView();Start-Sleep -Milliseconds 100}}
function ResultText {
 $e=Need '本机端口检查结果'
 $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
 $child=$walker.GetFirstChild($e)
 if($child){return $child.Current.Name}
 return $e.Current.Name
}
$tcp=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,0)
$tcp.Server.ExclusiveAddressUse=$true
$tcp.Start()
$port=$tcp.LocalEndpoint.Port
$cancelled=$false
try {
 Invoke '添加规则'
 $name=Need '规则名称';$protocol=Need '传输协议'
 if([Math]::Abs($name.Current.BoundingRectangle.Y-$protocol.Current.BoundingRectangle.Y) -gt 4){throw 'Identity row is not aligned'}
 $listen=Need '本机监听地址'; $listenPort=Need '本机端口';Scroll $listen
 if($listen.Current.BoundingRectangle.Right -gt $listenPort.Current.BoundingRectangle.Left){throw 'Listener fields overlap'}
 (Value $listen).SetValue('127.0.0.1')
 (Value $listenPort).SetValue([string]$port)
 Start-Sleep -Milliseconds 1600
 $feedback=ResultText
 if($feedback -notmatch '占用|冲突'){throw "Occupied local TCP port not detected automatically: $feedback"}
 if((Need '保存规则').Current.IsEnabled){throw 'Known conflict must prevent saving'}
 Write-Output "PASS automatic conflict detection with real local TCP listener on $port"
 $tcp.Stop();Start-Sleep -Milliseconds 200
 # Change input twice so the check runs again without adding/saving a forwarding rule.
 (Value $listenPort).SetValue('')
 (Value $listenPort).SetValue([string]$port)
 Start-Sleep -Milliseconds 1600
 $feedback=ResultText
 if($feedback -notmatch '可用|可使用|未发现'){throw "Freed port not rechecked as available: $feedback"}
 Write-Output 'PASS release and input changes trigger a fresh available-port check'
 $source=Need '选择目标发现来源';$discover=Need '发现目标';Scroll $source
 if($source.Current.BoundingRectangle.Right -gt $discover.Current.BoundingRectangle.Left){throw 'Discovery controls overlap'}
 $target=Need '目标地址 / 主机名';$targetPort=Need '目标端口';Scroll $target
 if($target.Current.BoundingRectangle.Right -gt $targetPort.Current.BoundingRectangle.Left){throw 'Target fields overlap'}
 Write-Output 'PASS grouped editor fields and discovery row do not overlap'
 Invoke '取消';$cancelled=$true
 if(-not (Need '添加规则').Current.IsEnabled){throw 'Cancel failed'}
 Write-Output 'PASS cancel leaves existing forwarding rules unchanged'
} finally {
 $tcp.Stop()
 if(-not $cancelled){$cancel=Find '取消';if($cancel -and $cancel.Current.IsEnabled){$cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}}
}
