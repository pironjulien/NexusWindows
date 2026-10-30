# Read-only independent UIA client. FindItemByProperty obtains placeholders; this probe never calls Realize,
# ScrollIntoView, Select, Invoke or SetFocus. It must run outside the WPF UI thread.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][long]$Window,
    [Parameter(Mandatory)][string]$TargetName,
    [Parameter(Mandatory)][string]$DuplicateName,
    [ValidateSet('Before','After')][string]$Stage = 'Before'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
$root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Window)
$condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'VirtualizedItems')
$list = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
if ($null -eq $list) { throw 'The real WPF fixture list is not exposed by UIA.' }
$before = @($list.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    try { $_.Current.Name } catch { '<unavailable>' }
})
$patternObject = $null
$hasContainer = $list.TryGetCurrentPattern([Windows.Automation.ItemContainerPattern]::Pattern, [ref]$patternObject)
$answer = [ordered]@{
    stage = $Stage
    list_framework = $list.Current.FrameworkId
    list_control_type = $list.Current.ControlType.ProgrammaticName
    item_container = $hasContainer
    target_in_tree_before_lookup = $before -ccontains $TargetName
    tree_child_count = $before.Count
    target_found = $false
    target_unique = $false
    target_virtualized_item = $false
    duplicate_count = $null
    duplicate_distinct = $null
    mutations_sent = 0
}
if ($hasContainer) {
    $container = [Windows.Automation.ItemContainerPattern]$patternObject
    $target = $container.FindItemByProperty($null, [Windows.Automation.AutomationElement]::NameProperty, $TargetName)
    $answer.target_found = $null -ne $target
    if ($target) {
        $nextTarget = $container.FindItemByProperty($target, [Windows.Automation.AutomationElement]::NameProperty, $TargetName)
        $answer.target_unique = $null -eq $nextTarget
        $virtualized = $null
        $answer.target_virtualized_item = $target.TryGetCurrentPattern([Windows.Automation.VirtualizedItemPattern]::Pattern, [ref]$virtualized)
    }
    if ($Stage -eq 'Before') {
        $first = $container.FindItemByProperty($null, [Windows.Automation.AutomationElement]::NameProperty, $DuplicateName)
        $second = if ($first) { $container.FindItemByProperty($first, [Windows.Automation.AutomationElement]::NameProperty, $DuplicateName) } else { $null }
        $third = if ($second) { $container.FindItemByProperty($second, [Windows.Automation.AutomationElement]::NameProperty, $DuplicateName) } else { $null }
        $answer.duplicate_count = if ($third) { 3 } elseif ($second) { 2 } elseif ($first) { 1 } else { 0 }
        $answer.duplicate_distinct = $null -ne $second -and -not [Windows.Automation.Automation]::Compare($first, $second)
    }
}
$after = @($list.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    try { $_.Current.Name } catch { '<unavailable>' }
})
$answer.target_in_tree_after_lookup = $after -ccontains $TargetName
$answer.target_selected = $null
$answer.target_offscreen = $null
$answer.vertical_scroll_percent = $null
if ($Stage -eq 'After' -and $answer.target_found) {
    $answer.target_offscreen = $target.Current.IsOffscreen
    $selection = $null
    if ($target.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) {
        $answer.target_selected = ([Windows.Automation.SelectionItemPattern]$selection).Current.IsSelected
    }
    $scroll = $null
    if ($list.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll)) {
        $answer.vertical_scroll_percent = ([Windows.Automation.ScrollPattern]$scroll).Current.VerticalScrollPercent
    }
}
$answer | ConvertTo-Json -Depth 5 -Compress
