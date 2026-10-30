$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$existingHosts = @(Get-Process LanBridge.Host -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$repoRoot = Split-Path $PSScriptRoot -Parent
$appPath = Join-Path $repoRoot 'artifacts/LanBridge-win-x64/LanBridge.App.exe'
$app = Start-Process -FilePath $appPath -PassThru
$ownHosts = @()
function Find-ByName($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
try {
    $root = $null
    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.HasExited) { throw "App exited: $($app.ExitCode)" }
        if ($app.MainWindowHandle -ne [IntPtr]::Zero) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
            $button = Find-ByName $root ([regex]::Unescape('\u6dfb\u52a0\u89c4\u5219'))
            if ($button -and $button.Current.IsEnabled) { break }
        }
    }
    if (-not $button -or -not $button.Current.IsEnabled) { throw 'App did not connect and enable Add rule.' }
    $ownHosts = @(Get-Process LanBridge.Host -ErrorAction SilentlyContinue | Where-Object { $existingHosts -notcontains $_.Id })
    Write-Output "PASS published WinUI app is running and connected (PID $($app.Id))"
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    $names = @($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    Write-Output 'DIALOG UIA TEXT:'
    $names | Select-Object -Unique | ForEach-Object { Write-Output $_ }
    $comboType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $combos = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$comboType)
    Write-Output "COMBO COUNT $($combos.Count)"
    # Source combo is identified by accessibility name; expand only the source selector, no runtime discovery.
    foreach ($combo in $combos) {
        if ($combo.Current.Name -match 'WSL|Docker|Podman' -or $combo.Current.Name -eq ([regex]::Unescape('\u9009\u62e9\u76ee\u6807\u53d1\u73b0\u6765\u6e90'))) {
            $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            Start-Sleep -Milliseconds 300
            $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'Hyper-V|WSL|Docker|Podman' } | Select-Object -Unique | ForEach-Object { Write-Output "SOURCE $_" }
            $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
            break
        }
    }
    Start-Sleep -Milliseconds 400
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $cancel = Find-ByName $root ([regex]::Unescape('\u53d6\u6d88'))
    if (-not $cancel) { throw 'Cancel button missing.' }
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    if (-not (Find-ByName $root ([regex]::Unescape('\u6dfb\u52a0\u89c4\u5219'))).Current.IsEnabled) { throw 'Editor did not close.' }
    Write-Output 'PASS actual editor opens and cancels without creating a rule'
    $ownHosts = @(Get-Process LanBridge.Host -ErrorAction SilentlyContinue | Where-Object { $existingHosts -notcontains $_.Id })
    $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $app.WaitForExit(5000)) { throw 'App did not close.' }
    foreach ($backend in $ownHosts) {
        $backend.Refresh()
        if ($backend.HasExited) { throw 'Closing GUI stopped its backend.' }
    }
    Write-Output 'PASS closing GUI leaves launched backend running'
} finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id }
    foreach ($backend in $ownHosts) {
        if (-not $backend.HasExited) { Stop-Process -Id $backend.Id }
    }
}
