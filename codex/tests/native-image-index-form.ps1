param([string]$ControlFile, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml
$wpfReferences = @([Windows.Window].Assembly.Location, [Windows.Media.Brushes].Assembly.Location, [Windows.Threading.Dispatcher].Assembly.Location, [Windows.Automation.Peers.FrameworkElementAutomationPeer].Assembly.Location, [Windows.Markup.IQueryAmbient].Assembly.Location)
Add-Type -ReferencedAssemblies $wpfReferences -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

public sealed class NativeImageIndexForm : Window {
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out Rect r, int size);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    readonly string controlFile;
    readonly ListBox list = new ListBox();
    readonly DispatcherTimer timer = new DispatcherTimer();
    string command = "";
    bool replacing;
    int hits;
    int selections;
    int stable;
    bool ready;
    Rect priorWindow, priorDwm;
    static bool Same(Rect a, Rect b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }
    public NativeImageIndexForm(string file) {
        controlFile = file;
        Title = "Native image index fixture"; Width = 1800; Height = 500;
        ShowActivated = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000;
        var root = new Grid(); Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var targets = new Grid(); root.Children.Add(targets);
        for (int i = 0; i < 3; i++) targets.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) {
            string name = "Target " + (char)('A' + i);
            var button = new Button { Content = name, Margin = new Thickness(40), MinHeight = 80 };
            AutomationProperties.SetName(button, name); Grid.SetColumn(button, i); targets.Children.Add(button);
            button.Click += delegate { hits++; Title = "Native image " + name + " " + hits; Console.WriteLine("{\"event\":\"hit\",\"target\":\"" + name + "\",\"count\":" + hits + "}"); };
        }
        Grid.SetRow(list, 1); list.Margin = new Thickness(40); AutomationProperties.SetName(list, "Permutation list"); root.Children.Add(list);
        Replace(false);
        list.SelectionChanged += delegate {
            if (!replacing && list.SelectedItem != null) { selections++; Console.WriteLine("{\"event\":\"selection\",\"value\":\"" + ((ListBoxItem)list.SelectedItem).Content + "\"}"); }
        };
        timer.Interval = TimeSpan.FromMilliseconds(161);
        timer.Tick += delegate {
            if (!ready) {
                UpdateLayout(); var h = new WindowInteropHelper(this).Handle; Rect r, d;
                if (!GetWindowRect(h, out r) || DwmGetWindowAttribute(h, 9, out d, 16) != 0) { stable = 0; return; }
                stable = IsMeasureValid && IsArrangeValid && Same(r, priorWindow) && Same(d, priorDwm) ? stable + 1 : 0;
                priorWindow = r; priorDwm = d;
                if (stable < 2) return;
                ready = true;
                Console.WriteLine("{\"ready\":true,\"top\":" + h.ToInt64() + ",\"width\":" + (r.Right-r.Left) + ",\"window\":[" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "],\"dwm\":[" + d.Left + "," + d.Top + "," + d.Right + "," + d.Bottom + "]}");
            }
            string next;
            try { next = File.Exists(controlFile) ? File.ReadAllText(controlFile).Trim() : ""; } catch (IOException) { return; }
            if (next == command) return; command = next;
            if (next == "swap") { Replace(true); Console.WriteLine("{\"event\":\"swapped\"}"); }
            if (next == "state") Console.WriteLine("{\"event\":\"state\",\"hits\":" + hits + ",\"selections\":" + selections + "}");
            if (next == "close") Close();
        };
        ContentRendered += delegate {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowPos(h, new IntPtr(1), 40, 160, 2300, 700, 0x10);
            timer.Start();
        };
        Closed += delegate { timer.Stop(); };
    }
    void Replace(bool reversed) {
        replacing = true;
        try {
            list.Items.Clear();
            foreach (var name in reversed ? new[] { "Beta", "Alpha" } : new[] { "Alpha", "Beta" }) {
                var item = new ListBoxItem { Content = name }; AutomationProperties.SetName(item, name); list.Items.Add(item);
            }
        } finally { replacing = false; }
    }
}
'@
if ($CompileOnly) { [Console]::WriteLine('{"compiled":true}'); exit 0 }
if ([string]::IsNullOrWhiteSpace($ControlFile)) { throw 'ControlFile is required for the live fixture' }
$fixture = [NativeImageIndexForm]::new($ControlFile)
[void]$fixture.ShowDialog()
