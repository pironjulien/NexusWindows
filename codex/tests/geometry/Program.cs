using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

// Self-owned surfaces only. Stdout is an event oracle independent from UI Automation.
static class Program
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    sealed class QuietForm : Form { protected override bool ShowWithoutActivation => true; }
    static readonly object Gate = new();
    static void Emit(object item) { lock (Gate) Console.WriteLine(JsonSerializer.Serialize(item)); }

    [STAThread] static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--no-shell-probe")
        {
            // A new private desktop is never displayed or switched to. Its thread has no Explorer broker.
            var desktop = CreateDesktopW("NexusGeometry-" + Guid.NewGuid().ToString("N"), 0, 0, 0, 0x01ff, 0);
            if (desktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var thread = new Thread(() =>
            {
                if (!SetThreadDesktop(desktop)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var started = false;
                try
                {
                    UserApplication.Start(new(Environment.ProcessPath!, "--lifetime-child \"" + args[1] + "\""), () => started = true);
                    Emit(new { refused = false, started });
                }
                catch (UserApplicationUnavailableException) { Emit(new { refused = true, started }); }
            });
            thread.Start(); thread.Join(); CloseDesktop(desktop); return;
        }
        if (args.Length == 2 && args[0] == "--lifetime-child")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { pid = Environment.ProcessId, executable = Environment.ProcessPath }));
            Thread.Sleep(30000); return;
        }
        Application.SetHighDpiMode(args.Contains("--unaware") ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var target = new QuietForm { Text = "Nexus geometry target", StartPosition = FormStartPosition.Manual, Bounds = new(160, 160, 640, 420), BackColor = Color.CornflowerBlue };
        using var overlay = new QuietForm { Text = "Nexus geometry overlay", StartPosition = FormStartPosition.Manual, Bounds = new(160, 160, 640, 420), BackColor = Color.OrangeRed, TopMost = true };
        var label = new Label { Text = "Geometry fixture", AutoSize = true, Location = new(24, 24) };
        target.Controls.Add(label);
        bool resizeOnActivate = false;
        var clicks = 0; var overlayClicks = 0;
        target.MouseDown += (_, e) => Emit(new { eventName = "targetClick", count = ++clicks, x = e.X, y = e.Y });
        overlay.MouseDown += (_, _) => Emit(new { eventName = "overlayClick", count = ++overlayClicks });
        target.Activated += (_, _) =>
        {
            if (resizeOnActivate) { resizeOnActivate = false; target.Width += 161; Emit(new { eventName = "activationResize", width = target.Width }); }
            Emit(new { eventName = "activated" });
        };
        object State(string? id)
        {
            GetWindowRect(target.Handle, out var r);
            return new { id, eventName = "state", hwnd = target.Handle.ToInt64(), clicks, overlayClicks,
                left = r.Left, top = r.Top, width = r.Right - r.Left, height = r.Bottom - r.Top,
                visible = target.Visible, state = target.WindowState.ToString(), awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()),
                logicalClientWidth = target.ClientSize.Width, logicalClientHeight = target.ClientSize.Height, dpi = target.DeviceDpi,
                screens = Screen.AllScreens.Select(s => new { x = s.Bounds.X, y = s.Bounds.Y, width = s.Bounds.Width, height = s.Bounds.Height }).ToArray() };
        }
        target.Shown += (_, _) =>
        {
            SetWindowPos(target.Handle, 1, 0, 0, 0, 0, 0x13); // bottom, no activation
            Emit(State("ready"));
        };
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                using var doc = JsonDocument.Parse(line); var request = doc.RootElement.Clone();
                target.BeginInvoke(() =>
                {
                    var id = request.GetProperty("id").GetString();
                    try
                    {
                        switch (request.GetProperty("command").GetString())
                        {
                            case "resizeOnActivate": resizeOnActivate = true; break;
                            case "maximize": target.WindowState = FormWindowState.Maximized; break;
                            case "minimize": target.WindowState = FormWindowState.Minimized; break;
                            case "normal": target.WindowState = FormWindowState.Normal; break;
                            case "hide": target.Hide(); break;
                            case "show": target.Show(); break;
                            case "overlay": overlay.Bounds = target.Bounds; overlay.Show(); break;
                            case "uncover": overlay.Hide(); break;
                            case "move": target.Location = new(request.GetProperty("x").GetInt32(), request.GetProperty("y").GetInt32()); break;
                            case "close": target.Close(); break;
                        }
                        if (!target.IsDisposed) Emit(State(id));
                    }
                    catch (Exception e) { Emit(new { id, error = e.Message }); }
                });
            }
            try { target.BeginInvoke(target.Close); } catch (InvalidOperationException) { }
        });
        Application.Run(target);
    }
}
