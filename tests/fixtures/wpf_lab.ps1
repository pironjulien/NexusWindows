# WPF window of the three-profile behavior test (Windows PowerShell 5.1, .NET Framework WPF): a "Nom" field, a "Secret"
# password field, a "Valider" button and a status text named "Statut" for UI Automation, so "clic N" exists only in its pixels. Shown off-screen at
# the bottom of the Z order, never activated. Prints {"ready":true,"top":<hwnd>} then one {"event":...} line per click.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="NexusWindows behavior test" Width="420" Height="230" ShowActivated="False" WindowStartupLocation="Manual">
  <StackPanel Margin="10">
    <TextBox x:Name="Nom" Margin="0,3" AutomationProperties.Name="Nom"/>
    <PasswordBox x:Name="Secret" Margin="0,3" AutomationProperties.Name="Secret"/>
    <Button x:Name="Valider" Content="Valider" Width="120" Margin="0,6" HorizontalAlignment="Left"/>
    <TextBlock x:Name="Statut" Margin="0,6" FontSize="16" Text="pret" AutomationProperties.Name="Statut"/>
  </StackPanel>
</Window>
'@
$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$window.Left = -32000; $window.Top = -32000
Add-Type -Namespace Fixture -Name Z -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);'
$statut = $window.FindName('Statut')
$script:clics = 0
$window.FindName('Valider').Add_Click({ $script:clics++; $statut.Text = "clic $script:clics"; [Console]::WriteLine('{"event":"clic ' + $script:clics + '"}') })
$window.Add_ContentRendered({
    $h = (New-Object System.Windows.Interop.WindowInteropHelper $window).Handle
    [void][Fixture.Z]::SetWindowPos($h, [IntPtr]1, 0, 0, 0, 0, 0x13)  # HWND_BOTTOM, NOSIZE|NOMOVE|NOACTIVATE
    [Console]::WriteLine('{"ready":true,"top":' + $h.ToInt64() + '}')
})
[void]$window.ShowDialog()
