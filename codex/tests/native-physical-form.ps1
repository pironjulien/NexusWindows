# Small real WPF surface: Ctrl+K changes only its title and emits a local event.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -Namespace NativeTest -Name Z -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);'
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Native physical fixture" Width="420" Height="180" ShowActivated="False" WindowStartupLocation="Manual">
  <StackPanel Margin="16"><TextBox x:Name="Nom" AutomationProperties.Name="Nom"/><TextBlock Margin="0,16" Text="Local native acceptance fixture"/><Button Content="Focus"/></StackPanel>
</Window>
'@
$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$window.Left = -32000; $window.Top = -32000
$window.Add_KeyDown({ param($sender, $e)
    if ($e.Key -eq 'K' -and ([Windows.Input.Keyboard]::Modifiers -band [Windows.Input.ModifierKeys]::Control)) {
        $window.Title = 'Native physical shortcut verified'; [Console]::WriteLine('{"event":"physical shortcut"}'); $e.Handled = $true
    }
    if ($e.Key -eq 'System' -and $e.SystemKey -eq 'K' -and ([Windows.Input.Keyboard]::Modifiers -band [Windows.Input.ModifierKeys]::Alt)) {
        $window.Title = 'Native physical alt verified'; [Console]::WriteLine('{"event":"physical alt"}'); $e.Handled = $true
    }
})
$window.Add_ContentRendered({
    $h = (New-Object System.Windows.Interop.WindowInteropHelper $window).Handle
    [void][NativeTest.Z]::SetWindowPos($h, [IntPtr]1, 0, 0, 0, 0, 0x13); $window.Left = 160; $window.Top = 160
    [Console]::WriteLine('{"ready":true,"top":' + $h.ToInt64() + '}')
})
[void]$window.ShowDialog()
