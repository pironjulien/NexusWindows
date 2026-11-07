using System.Runtime.InteropServices;

// Click, double click, right click and wheel in the background at a point of a NATIVE capture (shared bench of the three
// NexusWindows profiles, 21/10/2026, Codex's plan step 5): messages posted to the deepest child under the point,
// foreground and cursor untouched. The DWM frame noted at the capture is read again just before the input: a moved or
// resized window = refused (observe again). Win32 and WinForms; Chromium: posted click, wheel through DevTools if the
// target has a debugging port (Cdp.cs; a posted wheel is ignored, measured); WPF: wheel through UI Automation semantic
// scrolling (ComWorker scrollAt); Tk: click sent with the button simulated for its thread, list scrolled through its
// native bar (TkInput.cs). UWP and the rest of WPF: not applicable (posted mouse ignored). Hover (26/10, parity with
// Windows-MCP): a single WM_MOUSEMOVE posted to the control under the point (Win32, WinForms; sent to Tk), cursor
// untouched; elsewhere not applicable (the helper's real mouse as a fallback). Point of a selector: without a capture
// frame, the point (center read in a fresh tree) only has to be inside the window.
static partial class Pointer
{
    [LibraryImport("user32.dll")] private static partial nint ChildWindowFromPointEx(nint parent, Win32.Point point, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hwnd, ref Win32.Point point);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassNameW(nint hwnd, [Out] char[] name, int max);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Win32.Rect rect, int size);
    [LibraryImport("user32.dll")] private static partial nint WindowFromPoint(Win32.Point point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Win32.Rect CaretRect;
    }

    // Trace of the last posted click (shared bench: WinForms double click without effect, Codex's plan of 22/10): targeted
    // window (HWND, class, thread), client point, posted messages, the window really under the point (WinForms raises
    // Click and DoubleClick only if WindowFromPoint of the release point is the control itself: Control.WmMouseUp) and the
    // thread's state after sending (capture, focus). Receipt of posted messages cannot be observed without a hook in the
    // target process: it is inferred from that state, never asserted.
    [ThreadStatic] public static object? LastTrace;

    internal static void InRecipientContext(Func<nint> enter, Action send, Action<nint> restore)
    {
        var previous = enter();
        if (previous == 0) throw new ActionRefusedException("Recipient DPI context unavailable; no pointer message sent");
        try { send(); }
        finally { restore(previous); }
    }

    internal static object RecipientContextSelfTest()
    {
        var checks = 0;
        void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("Pointer DPI context selftest failed"); }
        var sent = 0; var restored = new List<nint>();
        InRecipientContext(() => 42, () => sent++, previous => restored.Add(previous));
        Check(sent == 1 && restored.SequenceEqual(new nint[] { 42 }));
        try { InRecipientContext(() => 43, () => { sent++; throw new IOException("partial message"); }, previous => restored.Add(previous)); }
        catch (IOException) { Check(sent == 2 && restored.Last() == 43); }
        var before = sent;
        try { InRecipientContext(() => 0, () => sent++, previous => restored.Add(previous)); }
        catch (ActionRefusedException) { Check(sent == before && restored.Count == 2); }
        var contexts = new Stack<nint>();
        InRecipientContext(() => 1, () => InRecipientContext(() => 2, () => contexts.Push(3), previous => contexts.Push(previous)), previous => contexts.Push(previous));
        Check(contexts.SequenceEqual(new nint[] { 1, 2, 3 }));
        return new ComWorkerChromiumFocusSelfTestJson(ok: true, checks: checks, gui: false, inputSent: false);
    }

    const uint WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    static readonly Dictionary<string, (uint Down, uint Up, int Mk, uint Dbl)> Buttons = new()
    {
        ["left"] = (0x0201, 0x0202, 0x0001, 0x0203),
        ["right"] = (0x0204, 0x0205, 0x0002, 0x0206),
        ["middle"] = (0x0207, 0x0208, 0x0010, 0x0209),
    };

    static string ClassOf(nint hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return new string(b, 0, Math.Max(0, n)); }
    static nint Lparam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    static nint DeepestChild(nint top, int x, int y)
    {
        var current = top;
        for (var i = 0; i < 32; i++)
        {
            var p = new Win32.Point { X = x, Y = y };
            ScreenToClient(current, ref p);
            var child = ChildWindowFromPointEx(current, p, 0x0001 | 0x0002 | 0x0004); // CWP_SKIPINVISIBLE | SKIPDISABLED | SKIPTRANSPARENT
            if (child == 0 || child == current) break;
            current = child;
        }
        return current;
    }

    static PointerThreadStateJson? ThreadState(uint thread)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? new PointerThreadStateJson(capture: (long)info.Capture, focus: (long)info.Focus, active: (long)info.Active) : null;
    }

    // Interfaces where a posted click has no effect (WPF, UWP/WinUI, Firefox; measured): a click at a point there is either
    // the UIA gesture of the single actionable element under it (McpSession) or the brief foreground input.
    internal static bool IgnoresPostedClick(nint window)
    {
        var cls = ClassOf(window);
        return cls.StartsWith("HwndWrapper", StringComparison.Ordinal) || cls == "ApplicationFrameWindow" || cls.StartsWith("Windows.UI.", StringComparison.Ordinal) || cls.StartsWith("Mozilla", StringComparison.Ordinal);
    }

    public static Outcome Act(nint window, string action, int x, int y, int[] frame, string button, int count, int deltaY, int deltaX, Action started, int toX = 0, int toY = 0)
    {
        // Real pixels of each screen, like the capture (Capture.cs): point, frame and client origin in the same space.
        var previous = Win32.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        try { return ActPhysicalPixels(window, action, x, y, frame, button, count, deltaY, deltaX, started, toX, toY); }
        finally { if (previous != 0) Win32.SetThreadDpiAwarenessContext(previous); }
    }

    static Outcome ActPhysicalPixels(nint window, string action, int x, int y, int[] frame, string button, int count, int deltaY, int deltaX, Action started, int toX = 0, int toY = 0)
    {
        var cls = ClassOf(window);
        var chromium = cls.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
        var wpf = cls.StartsWith("HwndWrapper", StringComparison.Ordinal);
        var tk = cls == "TkTopLevel";
        var classic = !chromium && !wpf && cls != "ApplicationFrameWindow" && !cls.StartsWith("Windows.UI.", StringComparison.Ordinal) &&
            cls != "TkTopLevel" && !cls.StartsWith("Mozilla", StringComparison.Ordinal);
        if (action is not ("click" or "wheel" or "hover" or "drag")) throw new InvalidOperationException("Unknown pointer action");
        // Drag and drop (01/11): only a Chromium or CEF page with a proven DevTools port receives it in the background.
        // Elsewhere Windows' drag loop (OLE DoDragDrop) reads the real mouse: posted messages would make it drop what it
        // carries under the user's real cursor. Never posted: real mouse only, with foreground:true.
        if (action == "drag" && !((chromium || ChromiumInput.IsCefHost(window)) && Cdp.HasPort(window)))
            throw new BackgroundNotApplicableException("drag and drop follows the real mouse in this interface (Windows drag loop)");
        if (!classic && !(chromium && action is "click" or "wheel" or "drag") && !(wpf && action == "wheel") && !(tk && action is "click" or "wheel" or "hover"))
            throw new BackgroundNotApplicableException("This interface ignores posted mouse input (measured): no background technique");
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        if (Win32.IsIconic(window)) throw new ActionRefusedException("Window is minimized: observe again");
        // Geometry read again: the image's point targets the same pixel only if the frame has not moved since the capture.
        if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var now, 16) != 0 ||
            (frame.Length != 4 && !(action == "hover" && frame.Length == 0)) ||
            (frame.Length == 4 && (now.Left != frame[0] || now.Top != frame[1] || now.Width != frame[2] || now.Height != frame[3])))
            throw new ActionRefusedException("Window moved or resized since the native capture: observe again");
        if (x < now.Left || y < now.Top || x >= now.Right || y >= now.Bottom) throw new ActionRefusedException("Point is outside the window");
        if (action == "drag")
        {
            if (toX < now.Left || toY < now.Top || toX >= now.Right || toY >= now.Bottom) throw new ActionRefusedException("Drop point is outside the window");
            return Cdp.Drag(window, x, y, toX, toY, started);
        }
        // A single technique per gesture: the interface's, never a second one after a send (shared bench of 21/10).
        // Chromium without a DevTools port (the user's own browser, a long web page): the posted wheel is ignored, so the
        // page's UI Automation ScrollPattern under the point scrolls it, read back like WPF (Chrome Web Store, 31/10).
        if (action == "wheel" && (wpf || chromium && !Cdp.HasPort(window)))
        {
            EmergencyStop.ThrowIfStopped();
            // Potential input from this call on: a failure afterwards is an unknown outcome (never "nothing was sent"),
            // and the foreground watch under the token applies (Codex's review, 21/10). The COM process raises "not
            // applicable" only before the first step.
            started();
            return ComBridge.Call(new PointerActPhysicalPixelsJson(op: "scrollAt", windowId: (long)window, runtimeId: Array.Empty<int>(), x: x, y: y, deltaY: deltaY, deltaX: deltaX));
        }
        // Hosted CEF (Steam SDL): the same strict CDP proof as Chromium. Without an attributable port/PID/page/surface,
        // Cdp refuses BEFORE sending; never a posted wheel silently in its place. Click/hover keep their path.
        if (action == "wheel" && (chromium || ChromiumInput.IsCefHost(window))) return Cdp.Wheel(window, x, y, deltaY, deltaX, started);
        var target = DeepestChild(window, x, y);
        // Tk: native bar attached to the widget first; without it, a FULLY covered window moved off screen for the time
        // of the wheel (TkWheel.cs, recipe of NexusWindows Claude 3.7.0 of 27/10).
        if (tk && action == "wheel")
            return TkInput.HasBar(target, deltaY != 0) ? TkInput.Scroll(target, deltaY, deltaX, started) : TkWheel.Wheel(window, x, y, deltaY, deltaX, DeepestChild, started);
        // Tk button (Button, Checkbutton, Radiobutton): a window of class BUTTON but driven by Tk's ButtonProc
        // (tkWinButton.c, DefWindowProc); the simulated mouse does not take there (real check of 27/10: a click at the OCR
        // point of "Valider" without effect) and a WM_COMMAND posted to the parent comes back to the button without
        // invoking it (tkWinX.c, unchanged reflection; Codex's review of 27/10). Tk invokes only on BN_CLICKED (= 0)
        // received BY the button with a non-zero parameter: that is its own invoke (command, the checkbox's variable),
        // posted, in the background.
        if (tk && action == "click" && button == "left" && count == 1 && Win32.BaseClass(target) == "button")
            return TkInput.Invoke(target, started);
        if (tk)
        {
            var at = new Win32.Point { X = x, Y = y };
            ScreenToClient(target, ref at);
            var ratio = Capture.Scale(target);
            if (ratio != 1.0) at = new Win32.Point { X = (int)Math.Round(at.X / ratio), Y = (int)Math.Round(at.Y / ratio) };
            return action == "hover" ? TkInput.Hover(target, Lparam(at.X, at.Y), started) : TkInput.Click(target, Lparam(at.X, at.Y), button, count, started);
        }
        // WinForms raises DoubleClick only if the window at the point (hit-test) is the control itself: a covered point,
        // a posted double click without effect (shared bench of 22/10, the three tools); nothing is sent, physical input
        // (a brief foreground) handles it if it is allowed.
        if (count >= 2 && action == "click" && ClassOf(target).StartsWith("WindowsForms10.", StringComparison.Ordinal) &&
            WindowFromPoint(new Win32.Point { X = x, Y = y }) != target)
            throw new BackgroundNotApplicableException("WinForms double click at a covered point has no effect by messages: nothing was sent");
        EmergencyStop.ThrowIfStopped();
        var sent = 0;
        void Post(uint msg, nint wparam, nint lparam)
        {
            if (sent == 0) started(); // past this, any error follows an input: unknown result, never replayed
            try { Win32.Post(target, msg, wparam, lparam); }
            catch (Exception error) when (sent > 0) { throw new InvalidOperationException($"Partial pointer input ({sent} messages posted); outcome unknown", error); }
            sent++;
        }
        if (action == "wheel")
        {
            if (deltaY != 0) Post(WM_MOUSEWHEEL, (nint)((deltaY & 0xFFFF) << 16), Lparam(x, y));
            if (deltaX != 0) Post(WM_MOUSEHWHEEL, (nint)((deltaX & 0xFFFF) << 16), Lparam(x, y));
            return new("background:messages", "WM_MOUSEWHEEL (screen point)", "sent");
        }
        if (!Buttons.TryGetValue(button, out var b)) throw new InvalidOperationException("Invalid mouse button");
        var client = new Win32.Point { X = x, Y = y };
        var moveOnly = action == "hover";
        ScreenToClient(target, ref client);
        // Frame in which the target READS the message: logical pixels for a window that is not DPI aware (or system DPI);
        // the point is already converted: posting in the recipient's exact context avoids a second conversion on
        // receipt (OCR canvas proof: 544,200 became 435,160 at 125 %).
        var scale = Capture.Scale(target);
        if (scale != 1.0) client = new Win32.Point { X = (int)Math.Round(client.X / scale), Y = (int)Math.Round(client.Y / scale) };
        var lp = Lparam(client.X, client.Y);
        var hit = WindowFromPoint(new Win32.Point { X = x, Y = y });
        var thread = Win32.GetWindowThreadProcessId(target, out _);
        var posted = new List<string>();
        void Trace(string state) => LastTrace = new PointerActPhysicalPixelsJson2(
            target: (long)target, className: ClassOf(target), thread: thread, client: new[] { client.X, client.Y }, screen: new[] { x, y },
            posted: posted.ToArray(), state: state, hitWindow: (long)hit, hitClass: hit == 0 ? "" : ClassOf(hit), hitIsTarget: hit == target,
            threadAfter: ThreadState(thread)
        );
        try
        {
            var recipientContext = Win32.GetWindowDpiAwarenessContext(target);
            if (recipientContext == 0) throw new ActionRefusedException("Recipient DPI identity unavailable; no pointer message sent");
            InRecipientContext(() => Win32.SetThreadDpiAwarenessContext(recipientContext), () =>
            {
                Post(WM_MOUSEMOVE, 0, lp); posted.Add("WM_MOUSEMOVE");
                for (var i = 0; !moveOnly && i < Math.Clamp(count, 1, 2); i++)
                {
                    Post(i == 1 ? b.Dbl : b.Down, b.Mk, lp);
                    posted.Add($"0x{(i == 1 ? b.Dbl : b.Down):X4}");
                    Post(b.Up, 0, lp); posted.Add($"0x{b.Up:X4}");
                }
            }, previous =>
            {
                if (Win32.SetThreadDpiAwarenessContext(previous) == 0) throw new InvalidOperationException("Pointer DPI context could not be restored");
            });
        }
        catch { Trace("partial"); throw; }
        Trace("posted");
        if (moveOnly) return new("background:messages", "posted WM_MOUSEMOVE (hover)", "sent");
        return new("background:messages", count == 2 ? "posted double click" : $"posted {button} click", "sent");
    }
}
