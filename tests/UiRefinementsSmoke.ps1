[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function U([string]$s) { [regex]::Unescape($s) }
function Root { $script:app.Refresh(); [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle) }
function Find($root,[string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name)
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function Need($root,[string]$name) {
    $e=Find $root $name
    if($null -eq $e){throw "Missing control: $name"}; return $e
}
function Show($e) {
    $p=$null
    if($e.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern,[ref]$p)){$p.ScrollIntoView();Start-Sleep -Milliseconds 150}
}
function EditableValue($combo) {
    $editType=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)
    $edit=$combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$editType)
    if($null -eq $edit){ throw "Editable text child missing: $($combo.Current.Name)" }
    $pattern=$null
    if(-not $edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pattern) -or $pattern.Current.IsReadOnly){ throw "Editable text child is read-only: $($combo.Current.Name)" }
    return $pattern
}
$script:app=$null
try {
    if(-not (Get-Process -Name LanBridge.Host -ErrorAction SilentlyContinue)){throw 'Existing backend required; refusing implicit backend lifecycle changes.'}
    $script:app=Start-Process -FilePath $AppPath -PassThru
    $add=$null
    for($i=0;$i -lt 40;$i++){
        Start-Sleep -Milliseconds 300; $script:app.Refresh()
        if($script:app.HasExited){throw "App exited: $($script:app.ExitCode)"}
        if($script:app.MainWindowHandle -ne [IntPtr]::Zero){$root=Root;$add=Find $root (U '\u6dfb\u52a0\u89c4\u5219');if($add -and $add.Current.IsEnabled){break}}
    }
    if(-not $add -or -not $add.Current.IsEnabled){throw 'App not ready'}
    if(Find $root (U '\u8fde\u63a5 / \u542f\u52a8\u540e\u53f0')){throw 'Redundant connect/start button still visible'}
    Write-Output 'PASS no connect/start button; startup connects automatically'
    $add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500; $root=Root
    $source=Need $root (U '\u9009\u62e9\u76ee\u6807\u53d1\u73b0\u6765\u6e90')
    $discover=Need $root (U '\u53d1\u73b0\u76ee\u6807')
    Show $source; $root=Root
    $source=Need $root (U '\u9009\u62e9\u76ee\u6807\u53d1\u73b0\u6765\u6e90')
    $discover=Need $root (U '\u53d1\u73b0\u76ee\u6807')
    $s=$source.Current.BoundingRectangle; $d=$discover.Current.BoundingRectangle
    if($s.IsEmpty -or $d.IsEmpty -or $s.Right -gt $d.Left -or $s.Bottom -lt $d.Top -or $d.Bottom -lt $s.Top){throw "Source is not left of Discover on same row: source=$s discover=$d"}
    Write-Output 'PASS source selector is left of Discover on the same row'
    foreach($name in @((U '\u672c\u673a\u7aef\u53e3'),(U '\u76ee\u6807\u7aef\u53e3'),(U '\u672c\u673a\u76d1\u542c\u5730\u5740'))){
        $e=Need (Root) $name
        if($e.Current.ControlType -ne [System.Windows.Automation.ControlType]::ComboBox){throw "Not a dropdown: $name"}
        Show $e
        $value=EditableValue $e
        $sample=if($name -eq (U '\u672c\u673a\u76d1\u542c\u5730\u5740')){'127.0.0.2'}else{'49152'}
        try { $value.SetValue($sample) } catch {
            $e.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { Write-Output "EDIT DIAG $($_.Current.ControlType.ProgrammaticName) $($_.Current.Name) enabled=$($_.Current.IsEnabled)" }
            throw
        }
        if($value.Current.Value -ne $sample){throw "Custom value not retained for $name"}
        $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 150
        $list=(Root).FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
        if($list.Count -eq 0){throw "No dropdown options: $name"}
        $first=$list.Item(0)
        $first.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 150
        $value=EditableValue $e
        if([string]::IsNullOrWhiteSpace($value.Current.Value)){throw "Selecting a dropdown item did not populate text: $name"}
        Write-Output "PASS selected dropdown item populates text: $name = $($value.Current.Value)"
        $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Start-Sleep -Milliseconds 200
        Write-Output "PASS editable dropdown with options and retained custom input: $name"
    }
    $protocol=Need (Root) (U '\u4f20\u8f93\u534f\u8bae'); Show $protocol
    $protocol.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 150
    foreach($name in @('TCP','UDP','TCP/UDP')){$item=Need (Root) $name}
    $protocol.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 200
    Write-Output 'PASS TCP, UDP and combined TCP/UDP are selectable'
    (Need (Root) (U '\u53d6\u6d88')).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 200
    if(-not (Need (Root) (U '\u6dfb\u52a0\u89c4\u5219')).Current.IsEnabled){throw 'Editor did not cancel'}
    Write-Output 'PASS cancel without saving or changing forwarding rules'
    (Root).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if(-not $script:app.WaitForExit(5000)){throw 'Test-owned UI failed to close'}
} finally {
    if($script:app -and -not $script:app.HasExited){Stop-Process -Id $script:app.Id -ErrorAction SilentlyContinue}
}
