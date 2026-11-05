using System.Diagnostics;
using System.Runtime.InteropServices;

// A Chromium window covered by the user's windows does not render a page it loaded while covered: Chrome marks its
// contents hidden (native window occlusion) and the page publishes no accessibility document until the window is
// shown again (05/11, chrome://extensions and the Chrome Web Store dashboard: « publication: no-document » until a pass
// in front). Measured on Chrome 156 and Edge: neither a foreground nor a raise without activation brings it back, but a
// minimize then a restore without activation does, the window staying where it was in the z-order (covered) and a
// maximized window staying maximized (measured).
// A window created behind the user's windows never rendered at all, even with a strip left visible (bench, 05/11): the
// reveal is done whenever the page published nothing; covered, nothing changes on screen, a visible strip blinks once.
// Its minimize/restore animation disabled, under the shared foreground token; Chrome may activate itself on the restore while the user has been
// idle past the foreground lock timeout: the foreground is given back at once, as after a launch.
static class ChromiumReveal
{
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    struct Placement { public int Length, Flags, ShowCommand; public Win32.Point Min, Max; public Win32.Rect Normal; }
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);
    const int WPF_RESTORETOMAXIMIZED = 2;
    const int SW_SHOWMINNOACTIVE = 7, SW_SHOWNOACTIVATE = 4, DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    const uint GW_HWNDPREV = 3, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    internal const int GuardMs = 600;

    // Why the window is left alone (null: it may be revealed).
    internal static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "brave", "vivaldi", "opera", "chromium" };
    internal static string? Refusal(bool visible, bool minimized, bool foreground, bool cloaked, bool covered, bool browser = true) =>
        !browser ? "not a browser window with pages (an app may hide to the tray on a minimize; a dialog is owned)" : !visible ? "hidden window" : minimized ? "minimized by the user" : foreground && !covered ? "already in front and visible" : cloaked ? "cloaked (another virtual desktop)" : null;

    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern int GetWindowRgnBox(nint hwnd, out Win32.Rect rect);
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
    const uint LWA_COLORKEY = 0x1, LWA_ALPHA = 0x2;

    // Whether an exstyle and its layered attributes make a window opaque the way Chrome's occlusion tracker counts it
    // (native_window_occlusion_tracker_win.cc): not click-through, not a tool window, layered only at full alpha without a
    // colour key. Electron and Chrome windows (layered at 255, no redirection bitmap) cover; a shell overlay does not.
    internal static bool Occludes(int exStyle, bool layeredRead, uint layeredFlags, byte alpha) =>
        (exStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) == 0 &&
        ((exStyle & WS_EX_LAYERED) == 0 || layeredRead && (layeredFlags & LWA_COLORKEY) == 0 && ((layeredFlags & LWA_ALPHA) == 0 || alpha == 255));

    // No pixel of the window is visible: the windows above it that Chrome counts as opaque cover its whole frame (a
    // maximized cover has no rounded corners; any other keeps its DWM corner margin, as for the Tk wheel).
    internal static bool Covered(nint hwnd)
    {
        var covers = new List<(Win32.Rect, int)>();
        for (var above = Win32.GetWindow(hwnd, GW_HWNDPREV); above != 0; above = Win32.GetWindow(above, GW_HWNDPREV))
        {
            if (!Win32.IsWindowVisible(above) || Win32.IsIconic(above) || Win32.IsCloaked(above) || GetWindowRgnBox(above, out _) != 0) continue;
            var ex = Win32.ExStyle(above);
            var read = GetLayeredWindowAttributes(above, out _, out var alpha, out var flags);
            if (!Occludes(ex, read, flags, alpha)) continue;
            covers.Add((TkWheel.Frame(above), IsZoomed(above) ? 0 : Math.Max(TkWheel.Corner(above), 1)));
        }
        return TkWheel.Covered(TkWheel.Frame(hwnd), covers);
    }

    // A browser window that hosts pages: top level (a dialog of Chrome, owned by its window, is never minimized) with a page
    // host child (Chrome_RenderWidgetHostHWND).
    static bool BrowserWindow(nint hwnd) => Win32.GetWindow(hwnd, Win32.GW_OWNER) == 0 && Win32.FindWindowExW(hwnd, 0, "Chrome_RenderWidgetHostHWND", null) != 0;

    // True when the window went through a minimize and a restore; the caller reads the document again.
    public static bool TryReveal(nint hwnd)
    {
        var refusal = Refusal(Win32.IsWindowVisible(hwnd), Win32.IsIconic(hwnd), Win32.GetForegroundWindow() == hwnd,
            Win32.IsCloaked(hwnd), Covered(hwnd), Browsers.Contains(Program.ProcessName(Win32.ProcessOf(hwnd))) && BrowserWindow(hwnd));
        if (refusal != null) return false;
        ForegroundToken token;
        try { token = ForegroundToken.Acquire("Chromium page reveal (covered window)"); }
        catch (TimeoutException) { return false; }
        using (token)
        {
            EmergencyStop.ThrowIfStopped();
            var noted = Win32.GetForegroundWindow();
            var input = Win32.Probe().LastInput;
            var previous = Win32.GetWindow(hwnd, GW_HWNDPREV);
            int on = 1, off = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int));
            try
            {
                var maximized = IsZoomed(hwnd);
                ShowWindow(hwnd, SW_SHOWMINNOACTIVE);
                // A maximized window comes back maximized (measured); said again to Windows in case its placement forgot it.
                var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
                if (maximized && GetWindowPlacement(hwnd, ref placement) && (placement.Flags & WPF_RESTORETOMAXIMIZED) == 0)
                {
                    placement.Flags |= WPF_RESTORETOMAXIMIZED; placement.ShowCommand = SW_SHOWMINNOACTIVE; SetWindowPlacement(hwnd, ref placement);
                }
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < GuardMs)
                {
                    var now = Win32.GetForegroundWindow();
                    if (now != noted && now != 0 && Win32.ProcessOf(now) == Win32.ProcessOf(hwnd)) ForegroundReturn.GiveBack(noted, hwnd, input);
                    Thread.Sleep(10);
                }
                // Back under the same window as before (the restore can raise it).
                if (previous != 0 && Win32.IsWindow(previous) && Win32.GetWindow(hwnd, GW_HWNDPREV) != previous)
                    SetWindowPos(hwnd, previous, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
            finally { DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, sizeof(int)); }
            return true;
        }
    }

    internal static int SelfTest()
    {
        var checks = 0;
        void Require(bool value, string why) { checks++; if (!value) throw new InvalidOperationException("Chromium reveal selftest: " + why); }
        Require(Refusal(true, false, false, false, true) == null, "a covered, normal, inactive window may be revealed");
        Require(Refusal(false, false, false, false, true) != null, "a hidden window is left alone");
        Require(Refusal(true, true, false, false, true) != null, "a window the user minimized is never restored");
        Require(Refusal(true, false, true, false, false) != null && Refusal(true, false, true, false, true) == null, "the window in front is left alone, unless something covers it (active but hidden: bench, 05/11)");
        Require(Refusal(true, false, false, true, true) != null, "a cloaked window is left alone");
        Require(Refusal(true, false, false, false, false) == null, "a partly covered window that never rendered its page is revealed too (bench, 05/11)");
        Require(Refusal(true, false, false, false, true, browser: false) != null && Browsers.Contains("chrome") && Browsers.Contains("msedge") && !Browsers.Contains("Claude"), "only browsers: an Electron app is never minimized");
        Require(Occludes(0x200100, false, 0, 0), "a Chrome window (no redirection bitmap) covers");
        Require(Occludes(0x280100, true, LWA_ALPHA, 255), "an Electron window layered at full alpha covers (Claude, 05/11)");
        Require(!Occludes(0x280100, true, LWA_ALPHA, 200), "a translucent window does not cover");
        Require(!Occludes(0x280100, true, LWA_COLORKEY, 0), "a colour-keyed window does not cover");
        Require(!Occludes(0x80000, false, 0, 0), "a window drawn by UpdateLayeredWindow does not cover");
        Require(!Occludes(0x0A0800A8, false, 0, 0), "the shell handwriting canvas (click-through tool window) does not cover");
        var maximized = new Win32.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1380 };
        Require(TkWheel.Covered(maximized, [(maximized, 0)]), "a maximized window covers a maximized window, corners included");
        Require(!TkWheel.Covered(maximized, [(maximized, 12)]), "a rounded cover leaves the corners visible");
        return checks;
    }
}
