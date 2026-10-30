[CmdletBinding()]
param([Parameter(Mandatory=$true)][int]$AppProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$pidCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppProcessId)
$window = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCondition)
if (-not $window) { throw 'Published App window not found.' }
function Find-Name([string]$name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Assert-True([bool]$value, [string]$message) { if (-not $value) { throw $message } }
function Visible([string]$name) {
    $element = Find-Name $name
    return ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.BoundingRectangle.Width -gt 0)
}
function Set-Batch([bool]$enabled) {
    $checkbox = Find-Name '批量选择规则'
    $toggle = [System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -ne $enabled) {
        $toggle.Toggle(); Start-Sleep -Milliseconds 300
    }
}
function Check-Columns {
    $list = Find-Name '转发规则列表'
    $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $row = $list.FindFirst([System.Windows.Automation.TreeScope]::Children, $rowCondition)
    Assert-True ($null -ne $row) 'At least one existing rule is required; no test rules will be created.'
    $textCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    Assert-True ($texts.Count -ge 5) 'Rule row lacks name/protocol/listen/target/status.'
    $headers = @('规则','本机监听','转发目标','状态')
    $positions = @(0,2,3,4)
    for($i=0; $i -lt 4; $i++) {
        $header = Find-Name $headers[$i]
        $delta = [Math]::Abs($header.Current.BoundingRectangle.X - $texts[$positions[$i]].Current.BoundingRectangle.X)
        Assert-True ($delta -le 8) "Column $($headers[$i]) misaligned by $delta pixels."
    }
}
$transform = [System.Windows.Automation.TransformPattern]$window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
$original = $window.Current.BoundingRectangle
try {
    Set-Batch $false
    $batch = Find-Name '批量选择规则'
    $batchText = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetFirstChild($batch)
    Assert-True ($null -ne $batchText) 'Batch selection must expose its content in the raw accessibility tree.'
    $right = $batchText.Current.BoundingRectangle.Right
    $listRight = (Find-Name '转发规则列表').Current.BoundingRectangle.Right
    Assert-True ([Math]::Abs($right - $listRight) -le 4) "Batch selection content is not flush right: content=$right list=$listRight"
    Write-Output 'PASS batch-selection content aligns with the rule-list right edge'
    $list = Find-Name '转发规则列表'
    $selection = [System.Windows.Automation.SelectionPattern]$list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    Assert-True (-not $selection.Current.CanSelectMultiple) 'Default mode must be single selection.'
    Assert-True (-not (Visible '全选转发规则')) 'Select-all should be hidden in single mode.'
    Assert-True (-not (Visible '批量操作')) 'Batch actions should be hidden in single mode.'
    Check-Columns
    Write-Output 'PASS published default single-select, quiet toolbar and aligned columns'
    Set-Batch $true
    Assert-True ($selection.Current.CanSelectMultiple) 'Batch mode must enable multiple selection.'
    Assert-True (Visible '全选转发规则') 'Batch mode must reveal select-all.'
    Assert-True (Visible '已选 0 条') 'Batch mode must show selected count.'
    Check-Columns
    Write-Output 'PASS published batch mode reveals controls and keeps column alignment'
    $transform.Resize(760,600); Start-Sleep -Milliseconds 400
    Check-Columns
    $more = Find-Name '更多'
    $batch = Find-Name '批量选择规则'
    Assert-True ($more.Current.BoundingRectangle.Right -lt $batch.Current.BoundingRectangle.Left) 'Toolbar commands overlap batch controls at minimum width.'
    $all = Find-Name '全选转发规则'
    Assert-True ($all.Current.BoundingRectangle.Right -lt $window.Current.BoundingRectangle.Right) 'Select-all extends beyond window.'
    Write-Output 'PASS minimum-size batch toolbar has no overlap and columns remain aligned'
    Set-Batch $false
    Assert-True (-not $selection.Current.CanSelectMultiple) 'Leaving batch mode must restore single selection.'
    Assert-True ($selection.Current.GetSelection().Count -eq 0) 'Leaving batch mode must clear selection.'
    Write-Output 'PASS leaving batch mode restores single selection without changing rules'
} finally {
    Set-Batch $false
    $transform.Resize($original.Width,$original.Height)
}
