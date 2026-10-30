# Real WPF data virtualization fixture. No custom AutomationPeer or fake UIA provider.
# It creates ordinary data items, never pre-created ListBoxItems (which disable useful virtualization).
[CmdletBinding()]
param([int]$X = 180, [int]$Y = 180)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[AppContext]::SetSwitch('Switch.UseLegacyAccessibilityFeatures', $false)
[AppContext]::SetSwitch('Switch.UseLegacyAccessibilityFeatures.2', $false)
[AppContext]::SetSwitch('Switch.UseLegacyAccessibilityFeatures.3', $false)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -TypeDefinition @'
public sealed class NexusVirtualItem {
    public string Key { get; set; }
    public string Label { get; set; }
    public override string ToString() { return Label; }
}
'@
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NexusVirtualWindow {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
[xml]$markup = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Native virtualized WPF fixture"
 Width="580" Height="410" ShowActivated="False" WindowStartupLocation="Manual">
 <DockPanel Margin="12">
  <TextBlock DockPanel.Dock="Top" Text="Real WPF ListBox / ItemContainer / VirtualizedItem" Margin="0,0,0,8"/>
  <Button x:Name="Commit" DockPanel.Dock="Bottom" Height="30" Content="Valider la selection" Margin="0,8,0,0"/>
  <TextBlock x:Name="Result" DockPanel.Dock="Bottom" Text="Aucune selection validee" AutomationProperties.Name="Resultat fixture"/>
  <ListBox x:Name="Items" AutomationProperties.Name="Liste virtualisee Nexus"
   AutomationProperties.AutomationId="VirtualizedItems" SelectionMode="Single"
   ScrollViewer.CanContentScroll="True" ScrollViewer.VerticalScrollBarVisibility="Auto"
   VirtualizingStackPanel.IsVirtualizing="True" VirtualizingStackPanel.VirtualizationMode="Recycling">
   <ListBox.ItemsPanel><ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate></ListBox.ItemsPanel>
   <ListBox.ItemContainerStyle>
    <Style TargetType="ListBoxItem">
     <Setter Property="Height" Value="26"/>
     <Setter Property="AutomationProperties.Name" Value="{Binding Label}"/>
     <Setter Property="AutomationProperties.AutomationId" Value="{Binding Key}"/>
    </Style>
   </ListBox.ItemContainerStyle>
   <ListBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding Label}"/></DataTemplate></ListBox.ItemTemplate>
  </ListBox>
 </DockPanel>
</Window>
'@
$window = [Windows.Markup.XamlReader]::Load([Xml.XmlNodeReader]::new($markup))
$list = $window.FindName('Items')
$result = $window.FindName('Result')
$targetIndex = 2048
$targetLabel = 'Cible virtualis' + [char]0x00e9 + 'e ' + [char]0x00e9 + 't' + [char]0x00e9 + ' ' + [char]0x03a9 + ' 2048'
$duplicateLabel = 'Doublon virtuel Nexus'
$items = [Collections.Generic.List[NexusVirtualItem]]::new()
for ($index = 0; $index -lt 3000; $index++) {
    $item = [NexusVirtualItem]::new()
    $item.Key = 'item-' + $index
    $item.Label = if ($index -eq $targetIndex) { $targetLabel } elseif ($index -in 2500,2800) { $duplicateLabel } else { 'Ligne ' + $index.ToString('D4') }
    $items.Add($item)
}
$list.ItemsSource = $items
$script:commits = 0
$script:wasRealized = $false
function Write-FixtureEvent([hashtable]$Event) {
    [Console]::WriteLine(($Event | ConvertTo-Json -Depth 5 -Compress))
    [Console]::Out.Flush()
}
function Get-FixtureState {
    $targetContainer = $list.ItemContainerGenerator.ContainerFromIndex($targetIndex)
    $viewer = Find-VisualChild $list ([Windows.Controls.ScrollViewer])
    $inViewport = $false
    if ($null -ne $targetContainer) {
        $bounds = $targetContainer.TransformToAncestor($list).TransformBounds([Windows.Rect]::new(0,0,$targetContainer.ActualWidth,$targetContainer.ActualHeight))
        $inViewport = $bounds.IntersectsWith([Windows.Rect]::new(0,0,$list.ActualWidth,$list.ActualHeight))
    }
    $realized = 0
    for ($i = 0; $i -lt $items.Count; $i++) {
        if ($null -ne $list.ItemContainerGenerator.ContainerFromIndex($i)) { $realized++ }
    }
    return @{
        target_realized = $null -ne $targetContainer
        target_visible = $null -ne $targetContainer -and $targetContainer.IsVisible
        target_in_viewport = $inViewport
        realized_count = $realized
        vertical_offset = $(if ($viewer) { $viewer.VerticalOffset } else { $null })
        selected_key = $(if ($list.SelectedItem) { $list.SelectedItem.Key } else { $null })
        commits = $script:commits
    }
}
function Find-VisualChild($Parent, [Type]$Type) {
    for ($i = 0; $i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Parent); $i++) {
        $child = [Windows.Media.VisualTreeHelper]::GetChild($Parent, $i)
        if ($child -is $Type) { return $child }
        $found = Find-VisualChild $child $Type
        if ($found) { return $found }
    }
    return $null
}
$list.Add_SelectionChanged({
    $state = Get-FixtureState
    $state.event = 'selected'
    Write-FixtureEvent $state
})
$list.Add_LayoutUpdated({
    $realized = $null -ne $list.ItemContainerGenerator.ContainerFromIndex($targetIndex)
    if ($realized -and -not $script:wasRealized) {
        $script:wasRealized = $true
        $state = Get-FixtureState
        $state.event = 'target-realized'
        Write-FixtureEvent $state
    }
})
$window.FindName('Commit').Add_Click({
    $script:commits++
    $selected = if ($list.SelectedItem) { $list.SelectedItem.Key } else { 'none' }
    $result.Text = 'Valide ' + $selected + ' / ' + $script:commits
    $window.Title = 'Native virtualized committed ' + $selected + ' ' + $script:commits
    $state = Get-FixtureState
    $state.event = 'committed'
    Write-FixtureEvent $state
})
$window.Add_ContentRendered({
    $handle = [Windows.Interop.WindowInteropHelper]::new($window).Handle
    # Hide initial placement from the user's foreground, then put only this local fixture behind existing windows.
    [void][NexusVirtualWindow]::SetWindowPos($handle, [IntPtr]1, $X, $Y, 0, 0, 0x11)
    $state = Get-FixtureState
    $state.ready = $true
    $state.top = $handle.ToInt64()
    $state.count = $items.Count
    $state.target_name = $targetLabel
    $state.target_key = $items[$targetIndex].Key
    $state.duplicate_name = $duplicateLabel
    $state.target_data_count = @($items | Where-Object Label -CEQ $targetLabel).Count
    $state.duplicate_data_count = @($items | Where-Object Label -CEQ $duplicateLabel).Count
    $state.is_virtualizing = [Windows.Controls.VirtualizingStackPanel]::GetIsVirtualizing($list)
    Write-FixtureEvent $state
})
$window.Left = -32000
$window.Top = -32000
$window.Add_Closed({ Write-FixtureEvent @{ event = 'closed'; commits = $script:commits } })
[void]$window.ShowDialog()
