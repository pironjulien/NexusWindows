param([string]$ControlFile, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class NativeOcrCanvas : Control {
    readonly bool upper;
    readonly Action<bool, string, int, int> clicked;
    public bool Changed;
    public NativeOcrCanvas(bool top, Action<bool, string, int, int> callback) {
        upper = top; clicked = callback; AccessibleName = ""; AccessibleRole = AccessibleRole.Pane;
        Text = ""; TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    Rectangle Area(string target) {
        if (target == "ALPHA") return new Rectangle(40, 25, 310, 100);
        if (target == "BRAVO") return new Rectangle(450, 155, 310, 100);
        return new Rectangle(40, 30, 360, 100);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Color.White); e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var font = new Font("Segoe UI", 38, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var pen = new Pen(Color.SteelBlue, 2))
        foreach (var target in upper ? new[] { "ALPHA", "BRAVO" } : new[] { "CHARLIE" }) {
            var area = Area(target); e.Graphics.DrawRectangle(pen, area);
            e.Graphics.DrawString(target == "ALPHA" && Changed ? "DELTA" : target, font, Brushes.Black, area.Left + 24, area.Top + 22);
        }
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e); string hit = "MISS";
        foreach (var target in upper ? new[] { "ALPHA", "BRAVO" } : new[] { "CHARLIE" })
            if (Area(target).Contains(e.Location)) hit = target;
        clicked(upper, hit, e.X, e.Y);
    }
}
public sealed class NativeOcrForm : Form {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    readonly Timer timer = new Timer();
    readonly NativeOcrCanvas upper;
    readonly string controlFile;
    string command = "";
    int total, alpha, bravo, charlie, missed, stable;
    bool ready;
    Rect previous;
    static bool Same(Rect a, Rect b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }
    protected override bool ShowWithoutActivation { get { return true; } }
    public NativeOcrForm(string file) {
        controlFile = file; Text = "Native OCR fixture";
        FormBorderStyle = FormBorderStyle.None; ControlBox = false;
        StartPosition = FormStartPosition.Manual; Location = new Point(80, 80); ClientSize = new Size(840, 500);
        upper = new NativeOcrCanvas(true, Hit) { Dock = DockStyle.Top, Height = 300, Name = "UpperCanvas" };
        var lower = new NativeOcrCanvas(false, Hit) { Dock = DockStyle.Fill, Name = "LowerCanvas" };
        Controls.Add(lower); Controls.Add(upper);
        timer.Interval = 161;
        timer.Tick += delegate {
            if (!ready) {
                Rect current; if (!GetWindowRect(Handle, out current)) return;
                stable = Same(current, previous) ? stable + 1 : 0; previous = current;
                if (stable < 2) return;
                ready = true;
                Console.WriteLine("{\"ready\":true,\"hwnd\":" + Handle.ToInt64() + ",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");
            }
            string next; try { next = File.Exists(controlFile) ? File.ReadAllText(controlFile).Trim() : ""; } catch (IOException) { return; }
            if (next == command) return; command = next;
            var parts = next.Split(':'); if (parts.Length != 2) return;
            int sequence; if (!int.TryParse(parts[1], out sequence)) return;
            if (parts[0] == "mutate") { upper.Changed = true; upper.Refresh(); Console.WriteLine("{\"event\":\"mutated\",\"sequence\":" + sequence + "}"); }
            if (parts[0] == "state") Console.WriteLine("{\"event\":\"state\",\"sequence\":" + sequence + ",\"total\":" + total + ",\"alpha\":" + alpha + ",\"bravo\":" + bravo + ",\"charlie\":" + charlie + ",\"missed\":" + missed + "}");
            if (parts[0] == "close") Close();
        };
        Shown += delegate { timer.Start(); };
        FormClosed += delegate { timer.Stop(); Console.WriteLine("{\"event\":\"closed\"}"); };
    }
    void Hit(bool upperCanvas, string target, int x, int y) {
        total++; if (target == "ALPHA") alpha++; else if (target == "BRAVO") bravo++; else if (target == "CHARLIE") charlie++; else missed++;
        Text = "Native OCR " + target + " " + total;
        Console.WriteLine("{\"event\":\"hit\",\"target\":\"" + target + "\",\"canvas\":\"" + (upperCanvas ? "upper" : "lower") + "\",\"total\":" + total + ",\"x\":" + x + ",\"y\":" + y + "}");
    }
}
'@
if ($CompileOnly) { [Console]::WriteLine('{"compiled":true}'); exit 0 }
if ([string]::IsNullOrWhiteSpace($ControlFile)) { throw 'ControlFile is required for the live OCR fixture' }
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::Run([NativeOcrForm]::new($ControlFile))
