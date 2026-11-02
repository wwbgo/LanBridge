[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId, [Parameter(Mandatory=$true)][int]$ListenPort)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$window = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId))
if (-not $window) { throw 'Isolated App window not found.' }
function Find-Name([string]$name) {
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
}
function Assert-True([bool]$value, [string]$message) { if (-not $value) { throw $message } }
function Get-Row {
    $list = Find-Name '转发规则列表'
    $row = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    Assert-True ($null -ne $row -and $row.Current.Name.StartsWith('isolated-tray')) 'Refusing to mutate anything except the isolated fixture rule.'
    return $row
}
function Wait-Until([scriptblock]$condition, [string]$message) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 30) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 150
    }
    $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Output ('UI FAILURE STATE: ' + (($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -Unique) -join ' | '))
    throw $message
}
$row = Get-Row
([System.Windows.Automation.SelectionItemPattern]$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
foreach($step in @(@('停止选中规则','启动选中规则','已停止'), @('启动选中规则','停止选中规则','监听中'), @('停止选中规则','启动选中规则','已停止'))) {
    $button = Find-Name $step[0]
    Assert-True ($null -ne $button -and $button.Current.IsEnabled) ('Toggle unavailable before ' + $step[0])
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Wait-Until { $next = Find-Name $step[1]; $null -ne $next -and $next.Current.IsEnabled -and $null -ne (Find-Name $step[2]) } ('Selection/action/runtime state lost after ' + $step[0])
    Assert-True ($null -eq (Find-Name '操作已提交，状态刷新失败')) 'Successful mutation was mislabeled as refresh failure.'
    $row = Get-Row
    $identity = $row.GetRuntimeId() -join ','
    Start-Sleep -Seconds 7
    $current = Get-Row
    Assert-True (($current.GetRuntimeId() -join ',') -eq $identity) 'Polling repeatedly rebuilt an unchanged rule row.'
    $selected = [System.Windows.Automation.SelectionItemPattern]$current.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    Assert-True $selected.Current.IsSelected 'Polling lost single selection.'
    Assert-True ((Find-Name $step[1]).Current.IsEnabled) 'Polling disabled the next toggle action.'
    if ($step[2] -eq '监听中') {
        $payload = [Text.Encoding]::UTF8.GetBytes('ui-toggle-echo')
        $tcp = [Net.Sockets.TcpClient]::new()
        $udp = [Net.Sockets.UdpClient]::new()
        try {
            $connection = $tcp.ConnectAsync('127.0.0.1', $ListenPort)
            Assert-True ($connection.Wait(5000)) 'TCP reconnect timed out after UI Start.'
            $stream = $tcp.GetStream(); $stream.ReadTimeout = 5000; $stream.WriteTimeout = 5000
            $stream.Write($payload, 0, $payload.Length)
            $received = [byte[]]::new($payload.Length)
            $offset = 0
            while ($offset -lt $received.Length) {
                $count = $stream.Read($received, $offset, $received.Length - $offset)
                Assert-True ($count -gt 0) 'TCP echo closed early.'
                $offset += $count
            }
            Assert-True ([Convert]::ToBase64String($received) -eq [Convert]::ToBase64String($payload)) 'TCP echo differs after UI Start.'
            $udp.Client.ReceiveTimeout = 5000
            $udp.Connect('127.0.0.1', $ListenPort)
            [void]$udp.Send($payload, $payload.Length)
            $remote = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $reply = $udp.Receive([ref]$remote)
            Assert-True ([Convert]::ToBase64String($reply) -eq [Convert]::ToBase64String($payload)) 'UDP echo differs after UI Start.'
            Write-Output 'PASS UI Start restores real TCP and UDP byte delivery'
        } finally { $tcp.Dispose(); $udp.Dispose() }
    } else {
        $tcp = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Tcp)
        $udp = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork, [Net.Sockets.SocketType]::Dgram, [Net.Sockets.ProtocolType]::Udp)
        try {
            $tcp.ExclusiveAddressUse = $true; $udp.ExclusiveAddressUse = $true
            $endpoint = [Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $ListenPort)
            $tcp.Bind($endpoint); $udp.Bind($endpoint)
            Write-Output 'PASS UI Stop releases both TCP and UDP listeners'
        } finally { $tcp.Dispose(); $udp.Dispose() }
    }
    Write-Output ('PASS real single-selection toggle and stable polling: ' + $step[0])
}
Write-Output 'PASS stop/start/stop commits preserve single selection, row identity and enabled controls across polling'
