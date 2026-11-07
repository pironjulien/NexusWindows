using System.Runtime.InteropServices;

// Tk in the background (NexusClaude's recipe of 23/10, reviewed by Codex in six rounds; ported to NexusAGY 3.0.2,
// reviewed in five rounds). Tk reads the button state through GetKeyState when it handles the message
// (TkWinGetModifierState): a click posted without a "pressed" button is ignored. Press SENT with the button simulated
// for Tk's thread only, by an auxiliary thread that owns both press AND release (SendMessageTimeout's delay is not
// guaranteed under AttachThreadInput); one simulated press at a time per thread; a release without an answer: the
// thread is closed to simulated presses while the window exists. Wheel: Tk sends it to the window under the point
// (WindowFromPoint), lost if it is covered; a list scrolls through its native bar (tk.Scrollbar) ATTACHED to the
// targeted widget, position read back (GetScrollInfo).
static partial class TkInput
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(byte[] state);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKeyboardState(byte[] state);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetScrollInfo(nint hwnd, int bar, ref ScrollInfo info);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Win32.Rect rect);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassNameW(nint hwnd, [Out] char[] name, int max);
    [StructLayout(LayoutKind.Sequential)] struct ScrollInfo { public uint Size, Mask; public int Min, Max; public uint Page; public int Pos, TrackPos; }

    const uint WM_MOUSEMOVE = 0x0200, WM_VSCROLL = 0x0115, WM_HSCROLL = 0x0114;
    const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_ENDSCROLL = 8, SB_CTL = 2, SBS_VERT = 1, LinesPerNotch = 3;
    static readonly HashSet<uint> Pending = [];
    static readonly object Gate = new();
    static readonly Dictionary<string, (uint Down, uint Up, int Mk, uint Dbl, int Vk)> Buttons = new()
    {
        ["left"] = (0x0201, 0x0202, 0x0001, 0x0203, 0x01),
        ["right"] = (0x0204, 0x0205, 0x0002, 0x0206, 0x02),
        ["middle"] = (0x0207, 0x0208, 0x0010, 0x0209, 0x04),
    };

    static string ClassOf(nint hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return new string(b, 0, Math.Max(0, n)); }
    static uint ThreadOf(nint hwnd) => Win32.GetWindowThreadProcessId(hwnd, out _);
    static bool InFront(uint thread) { var fg = Win32.GetForegroundWindow(); return fg != 0 && ThreadOf(fg) == thread; }
    static bool Send(nint hwnd, uint msg, nint wparam, nint lparam) => SendMessageTimeoutW(hwnd, msg, wparam, lparam, 0x0002 /* ABORTIFHUNG */, 1000, out _) != 0;

    // Key `vk` "pressed" for the thread of `target` only; on exit, it alone gets its previous value back (its physical
    // state if the user took the window); unreadable: the whole previous state.
    sealed class SimulatedKey : IDisposable
    {
        readonly uint own = Win32.GetCurrentThreadId(), thread;
        readonly int vk;
        readonly byte[] saved = new byte[256];
        bool attached;
        public SimulatedKey(nint target, int vk)
        {
            this.vk = vk;
            thread = ThreadOf(target);
            if (thread == 0 || InFront(thread)) throw new BackgroundNotApplicableException("The Tk window is in the foreground: nothing simulated");
            if (!Win32.AttachThreadInput(own, thread, true)) throw new BackgroundNotApplicableException("AttachThreadInput refused");
            attached = true;
            GetKeyboardState(saved);
            var state = (byte[])saved.Clone();
            state[vk] = 0x80;
            SetKeyboardState(state);
        }
        public void Dispose()
        {
            if (!attached) return;
            try
            {
                var now = new byte[256];
                if (!GetKeyboardState(now)) Array.Copy(saved, now, 256);
                now[vk] = InFront(thread) ? (byte)(((GetAsyncKeyState(vk) & 0x8000) != 0 ? 0x80 : 0) | (saved[vk] & 1)) : saved[vk];
                SetKeyboardState(now);
            }
            finally { Win32.AttachThreadInput(own, thread, false); attached = false; }
        }
    }

    static void HeldPress(nint target, int vk, uint down, uint up, int mk, nint lp)
    {
        var thread = ThreadOf(target);
        lock (Gate) if (!Pending.Add(thread)) throw new ActionRefusedException("A previous simulated press in this application is not finished");
        var sync = new object();
        var abandoned = false; // under `sync`: giving up and the decision to press exclude each other (Codex's review of 24/10)
        var done = new ManualResetEventSlim();
        Exception? error = null;
        var worker = new Thread(() =>
        {
            var pressed = false;
            try
            {
                lock (sync) if (abandoned) return;
                using (new SimulatedKey(target, vk))
                {
                    EmergencyStop.ThrowIfStopped(); // read again just before the press
                    lock (sync)
                    {
                        if (abandoned) return; // the working thread has already returned: no more press
                        pressed = true;
                    }
                    if (!Send(target, down, mk, lp)) error ??= new InvalidOperationException("The press got no answer within 1 s; outcome unknown");
                }
            }
            catch (Exception e) { error ??= e; }
            finally
            {
                try
                {
                    if (pressed && !Send(target, up, 0, lp)) // cleanup release, state already restored: always sent
                    {
                        error ??= new InvalidOperationException("The release got no answer within 1 s; outcome unknown");
                        done.Set();
                        while (Win32.IsWindow(target)) Thread.Sleep(1000); // never proven settled: thread closed to simulated presses
                    }
                }
                finally { lock (Gate) Pending.Remove(thread); done.Set(); }
            }
        }) { IsBackground = true, Name = "nexus-held-press" };
        worker.Start();
        var deadline = Environment.TickCount64 + 1000;
        try
        {
            while (!done.Wait(20))
            {
                EmergencyStop.ThrowIfStopped(); // emergency stop: given up, the auxiliary thread that has not left sends nothing more
                if (Environment.TickCount64 >= deadline) throw new InvalidOperationException("The press got no answer within 1 s; outcome unknown");
            }
        }
        catch { lock (sync) abandoned = true; throw; }
        if (error != null) throw error;
    }

    // Tk hover: WM_MOUSEMOVE SENT (Tk handles it as a real move: <Enter>, <Motion>), cursor untouched.
    public static Outcome Hover(nint target, nint lp, Action started)
    {
        if (InFront(ThreadOf(target))) throw new BackgroundNotApplicableException("The Tk window is in the foreground: nothing simulated");
        EmergencyStop.ThrowIfStopped();
        started();
        if (!Send(target, WM_MOUSEMOVE, 0, lp)) throw new InvalidOperationException("Tk did not answer the hover; outcome unknown");
        return new("background:messages", "sent WM_MOUSEMOVE (hover)", "sent");
    }

    // Invoke of a Tk button: BN_CLICKED (0) posted to the button itself, non-zero wParam (ButtonProc in tkWinButton.c
    // ignores a 0 without a parameter, the WM_NULL "ping"). Tk itself refuses a disabled button (STATE_DISABLED): the
    // effect is read back by the session's postcondition, never announced.
    public static Outcome Invoke(nint button, Action started)
    {
        if (!Win32.IsWindowEnabled(button)) throw new ActionRefusedException("Button is disabled");
        EmergencyStop.ThrowIfStopped();
        started();
        Win32.Post(button, 0 /* BN_CLICKED, read by Tk's ButtonProc */, 1, button);
        return new("background:messages", "Tk button invoke (BN_CLICKED posted to the button, tkWinButton.c)", "sent");
    }

    // Tk click: hover sent then, for each press, the button simulated for Tk's thread only. started() before the first
    // send: past it, any error is an unknown outcome (never replayed, never a real mouse on top).
    public static Outcome Click(nint target, nint lp, string button, int count, Action started)
    {
        if (!Buttons.TryGetValue(button, out var b)) throw new InvalidOperationException("Invalid mouse button");
        var thread = ThreadOf(target);
        if (InFront(thread)) throw new BackgroundNotApplicableException("The Tk window is in the foreground: nothing simulated");
        lock (Gate) if (Pending.Contains(thread)) throw new ActionRefusedException("A previous simulated press in this application is not finished");
        EmergencyStop.ThrowIfStopped();
        started();
        if (!Send(target, WM_MOUSEMOVE, 0, lp)) throw new InvalidOperationException("Tk did not answer the hover; outcome unknown");
        for (var i = 0; i < Math.Clamp(count, 1, 2); i++)
        {
            EmergencyStop.ThrowIfStopped();
            HeldPress(target, b.Vk, i == 1 ? b.Dbl : b.Down, b.Up, b.Mk, lp);
        }
        return new("background:messages", count == 2 ? "sent double click, button marked pressed for Tk's thread" : $"sent {button} click, button marked pressed for Tk's thread", "sent");
    }

    // A visible native bar of the right direction, sibling of the widget under the point, ATTACHED to it over its whole
    // height (width), and attached to no other sibling widget; exactly one, otherwise 0 (nothing guessed).
    static nint BarBeside(nint under, bool vertical)
    {
        if (under == 0 || ClassOf(under).Equals("ScrollBar", StringComparison.OrdinalIgnoreCase)) return 0;
        var parent = Win32.GetParent(under);
        var siblings = new List<nint>();
        Win32.EnumChildWindows(parent, (h, _) => { if (Win32.GetParent(h) == parent && Win32.IsWindowVisible(h)) siblings.Add(h); return true; }, 0);
        GetWindowRect(under, out var box);
        bool Touches(Win32.Rect bar, Win32.Rect w, bool whole) => vertical
            ? (Math.Abs(bar.Left - w.Right) <= 4 || Math.Abs(w.Left - bar.Right) <= 4) &&
              (whole ? bar.Top <= w.Top + 4 && bar.Bottom >= w.Bottom - 4 : bar.Top < w.Bottom && w.Top < bar.Bottom)
            : (Math.Abs(bar.Top - w.Bottom) <= 4 || Math.Abs(w.Top - bar.Bottom) <= 4) &&
              (whole ? bar.Left <= w.Left + 4 && bar.Right >= w.Right - 4 : bar.Left < w.Right && w.Left < bar.Right);
        var found = new List<nint>();
        foreach (var bar in siblings)
        {
            if (!ClassOf(bar).Equals("ScrollBar", StringComparison.OrdinalIgnoreCase) || ((Win32.GetWindowLongW(bar, -16) & SBS_VERT) != 0) != vertical) continue;
            GetWindowRect(bar, out var r);
            if (!Touches(r, box, true)) continue;
            var shared = siblings.Any(h => h != bar && h != under && !ClassOf(h).Equals("ScrollBar", StringComparison.OrdinalIgnoreCase) &&
                GetWindowRect(h, out var o) && Touches(r, o, false));
            if (!shared) found.Add(bar);
        }
        return found.Count == 1 ? found[0] : 0;
    }

    static ScrollInfo? State(nint bar)
    {
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x1 | 0x2 | 0x4 };
        return GetScrollInfo(bar, SB_CTL, ref info) ? info : null;
    }

    public static bool HasBar(nint under, bool vertical) => BarBeside(under, vertical) != 0;

    // Scrolling a Tk list through its native bar: a single axis; no bar, or already at the end: refused before any
    // input; lines sent like a click on the arrow, position read back for 1 s at most ("applied" if it moved).
    public static Outcome Scroll(nint under, int deltaY, int deltaX, Action started)
    {
        if ((deltaY == 0) == (deltaX == 0)) throw new BackgroundNotApplicableException("Tk scroll bar: one axis at a time");
        var vertical = deltaY != 0;
        var forward = vertical ? deltaY < 0 : deltaX > 0; // negative deltaY = down (WHEEL_DELTA)
        var bar = BarBeside(under, vertical);
        if (bar == 0) throw new BackgroundNotApplicableException("No native scroll bar drives what is under the point (Tk routes the wheel to the window on top)");
        var before = State(bar) ?? throw new BackgroundNotApplicableException("The scroll bar position cannot be read");
        var end = Math.Max(before.Min, before.Max - Math.Max((int)before.Page, 1) + 1);
        if (forward ? before.Pos >= end : before.Pos <= before.Min) throw new BackgroundNotApplicableException("The scroll bar is already at the " + (forward ? "end" : "start"));
        // At most 50 notches (150 lines) per gesture, like NexusClaude: never thousands of messages at once.
        var lines = Math.Min(50, Math.Max(1, Math.Abs(vertical ? deltaY : deltaX) / 120)) * LinesPerNotch;
        var parent = Win32.GetParent(bar);
        var msg = vertical ? WM_VSCROLL : WM_HSCROLL;
        EmergencyStop.ThrowIfStopped();
        started();
        Exception? pending = null;
        try
        {
            for (var i = 0; i < lines; i++)
            {
                if (i > 0) EmergencyStop.ThrowIfStopped(); // emergency stop between two lines: the next one does not leave
                Win32.Post(parent, msg, forward ? SB_LINEDOWN : SB_LINEUP, bar);
            }
        }
        catch (Exception error) { pending = error; }
        // The end of scrolling is always signalled; its failure never hides the current error (emergency stop included,
        // Codex's review of 24/10): it is raised alone only if everything else succeeded.
        try { Win32.Post(parent, msg, SB_ENDSCROLL, bar); }
        catch (Exception cleanup) when (pending is not null) { System.Diagnostics.Trace.WriteLine("SB_ENDSCROLL: " + cleanup.Message); }
        if (pending is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pending).Throw();
        var deadline = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
            if (State(bar) is { } after && after.Pos != before.Pos)
                return new("background:messages", $"Tk scroll bar lines ({before.Pos} → {after.Pos})", "applied");
        }
        return new("background:messages", "Tk scroll bar lines (position not seen moving within 1 s)", "sent");
    }
}
