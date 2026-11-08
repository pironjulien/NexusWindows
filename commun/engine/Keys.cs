using System.Runtime.InteropServices;

// Keys and shortcuts in the background (shared bench of the three NexusWindows profiles, 21/10/2026; shortcuts ported
// from NexusClaude and NexusAGY on 25/10, parity bench: Ctrl+K 0/3 here, 3/3 there): WM_KEYDOWN/WM_KEYUP posted to the
// focus of the target's thread; for Ctrl/Shift, the keyboard state of THAT thread is simulated while it processes
// (AttachThreadInput to the target's thread only, never to the foreground one's), held through WaitForInputIdle (2 s at
// most) then at least HoldMs, then only the simulated keys get their value back. WaitForInputIdle is not an
// acknowledgement: the effect is still read back by the session, never announced ("sent"). Tk: simulated focus
// (WM_SETFOCUS sent, WM_KILLFOCUS posted after the keys). Chromium: simulated activation (ChromiumInput.Keys). Alt and
// Windows: refused (system shortcuts expect WM_SYSKEYDOWN). WPF, UWP, Firefox: not applicable (they ignore posted keys,
// measured).
static partial class Keys
{
    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Win32.Rect CaretRect; }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint child);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(byte[] state);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKeyboardState(byte[] state);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyExW(uint code, uint type, nint layout);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial short VkKeyScanExW(char ch, nint layout);
    [LibraryImport("user32.dll")] internal static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")] private static partial uint WaitForInputIdle(nint process, uint ms);
    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassNameW(nint hwnd, [Out] char[] name, int max);

    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeout, out nint result);

    const uint WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
    // Simulated modifiers held at least 0.5 s after sending: at 0.15 s, Ctrl+K posted to a covered Win32 window sometimes
    // arrived after the restore (NexusClaude's measurement of 23/10, bench/miss_probes.py).
    public const int HoldMs = 500;
    internal const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = 0x0D, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["BackSpace"] = 0x08,
        ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Page_Up"] = 0x21, ["Prior"] = 0x21, ["Page_Down"] = 0x22, ["Next"] = 0x22, ["space"] = 0x20, ["Insert"] = 0x2D,
        ["apps"] = 0x5D, ["menu"] = 0x5D, ["printscreen"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14,
        ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
    };
    static readonly HashSet<int> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D];

    static string ClassOf(nint hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return new string(b, 0, Math.Max(0, n)); }

    // "Control_L+s", "ctrl+k", "Return": (modifiers, key). Alt/Windows: refused, nothing is sent.
    // Layout of the TARGET thread (Codex's review, 21/10): the helper's layout turned "1" of a QWERTY keyboard into "&" in
    // an AZERTY target, and its implicit Shift could be wrong.
    static int Modifier(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "control_l" or "control_r" => VK_CONTROL,
        "shift" or "shift_l" or "shift_r" => VK_SHIFT,
        "alt" or "alt_l" or "alt_r" => 0x12,
        "win" or "super" or "meta" or "windows" => 0x5B,
        _ => 0,
    };
    internal static (List<int> Mods, int Vk) ParseCore(string spec, bool physical, Func<char, short> scanCharacter)
    {
        var text = spec.Trim().Replace(" ", "");
        string[] parts = text == "+" ? ["+"] : text.Length > 2 && text.EndsWith("++", StringComparison.Ordinal)
            ? [.. text[..^2].Split('+'), "+"] : text.Split('+');
        if (parts.Any(part => part.Length == 0)) throw new BackgroundNotApplicableException("Incomplete key combination (missing key)");
        var mods = new List<int>();
        void RequireAllowed(int vk)
        {
            if (!physical && vk is 0x12 or 0x5B or 0x5C) throw new BackgroundNotApplicableException("Alt/Windows keys have no safe background technique (system keys)");
        }
        foreach (var m in parts[..^1])
        {
            // Historical physical alias; the Claude adapter canonicalizes the named menu key to apps.
            var modifier = physical && m.Equals("menu", StringComparison.OrdinalIgnoreCase) ? 0x12 : Modifier(m);
            if (modifier == 0) throw new BackgroundNotApplicableException($"Unknown modifier « {m} »");
            RequireAllowed(modifier);
            if (!mods.Contains(modifier)) mods.Add(modifier);
        }
        var key = parts[^1];
        int vk;
        if (parts.Length == 1 && Modifier(key) is var solo && solo != 0) { RequireAllowed(solo); vk = solo; }
        else if (Named.TryGetValue(key, out var named)) vk = named;
        else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = 0x6F + f;
        else if (key.Length == 1)
        {
            var scan = scanCharacter(key[0]);
            if (scan == -1 || ((scan >> 8) & 0x6) != 0) throw new BackgroundNotApplicableException("Key needs AltGr/Ctrl to type: no background technique");
            vk = scan & 0xFF;
            if (((scan >> 8) & 1) != 0 && !mods.Contains(VK_SHIFT)) mods.Add(VK_SHIFT);
        }
        else throw new BackgroundNotApplicableException($"Unknown key « {key} »");
        return (mods, vk);
    }
    internal static (List<int> Mods, int Vk) Parse(string spec, nint layout) => ParseCore(spec, false, ch => VkKeyScanExW(ch, layout));

    // Alt in the background, for a Chromium window only (05/11: alt+n, alt+1 refused « system keys »): its shortcuts are read
    // from WM_SYSKEYDOWN with the Alt context bit and the simulated keyboard state, under the same simulated activation as
    // any key. Never the Windows key, nor what Windows itself handles with Alt (Tab, Esc, Space for the system menu, F4
    // that closes): those keep their physical route.
    internal static readonly int[] AltForWindows = [0x09, 0x1B, 0x20, 0x73];
    public static (List<int> Mods, int Vk, nint Layout) ParseForChromium(nint window, string spec)
    {
        var layout = GetKeyboardLayout(Win32.GetWindowThreadProcessId(window, out _));
        var (mods, vk) = ParseCore(spec, true, ch => VkKeyScanExW(ch, layout));
        if (mods.Contains(0x5B) || mods.Contains(0x5C) || vk is 0x5B or 0x5C) throw new BackgroundNotApplicableException("Windows keys have no safe background technique (system keys)");
        if (vk == 0x12) throw new BackgroundNotApplicableException("Alt alone has no safe background technique (it would open the menu)");
        if (mods.Contains(0x12) && AltForWindows.Contains(vk)) throw new BackgroundNotApplicableException("Alt+Tab, Esc, Space and F4 belong to Windows: no background technique");
        return (mods, vk, layout);
    }

    // Real input supports Alt/Windows too; the background parser still refuses system modifiers before input.
    public static (List<int> Mods, int Vk) ParseFor(string spec, nint layout) => ParseCore(spec, true, ch => VkKeyScanExW(ch, layout));
    public static bool IsExtended(int vk) => Extended.Contains(vk);

    static nint KeyLparam(int vk, bool up, nint layout)
    {
        long lp = 1 | ((long)(MapVirtualKeyExW((uint)vk, 0, layout) & 0xFF) << 16);
        if (Extended.Contains(vk)) lp |= 1L << 24;
        if (up) lp |= (1L << 30) | (1L << 31);
        return (nint)lp;
    }

    static bool InFront(uint thread) { var fg = Win32.GetForegroundWindow(); return fg != 0 && Win32.GetWindowThreadProcessId(fg, out _) == thread; }

    // Keyboard state simulated for the target thread only; on exit, only the simulated keys get their previous value back
    // (their PHYSICAL state if the user took the target meanwhile); unreadable: the whole previous state.
    sealed class SimulatedModifiers : IDisposable
    {
        readonly uint own = Win32.GetCurrentThreadId(), thread, pid;
        readonly List<int> vks;
        readonly byte[] saved = new byte[256];
        bool attached;
        public SimulatedModifiers(nint window, List<int> vks)
        {
            this.vks = vks;
            thread = Win32.GetWindowThreadProcessId(window, out pid);
            if (thread == 0 || InFront(thread)) throw new BackgroundNotApplicableException("The target shares the foreground thread: simulated keyboard state refused");
            if (!Win32.AttachThreadInput(own, thread, true)) throw new BackgroundNotApplicableException("AttachThreadInput refused: nothing was sent");
            attached = true;
            GetKeyboardState(saved);
            var state = (byte[])saved.Clone();
            foreach (var vk in vks) state[vk] = 0x80;
            SetKeyboardState(state);
        }
        public void Dispose()
        {
            if (!attached) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var process = OpenProcess(0x00100000 | 0x1000 /* SYNCHRONIZE | QUERY_LIMITED */, false, pid);
                if (process != 0) { try { _ = WaitForInputIdle(process, 2000); } finally { CloseHandle(process); } }
                var left = HoldMs - (int)clock.ElapsedMilliseconds;
                if (left > 0) Thread.Sleep(left);
            }
            finally
            {
                try
                {
                    var now = new byte[256];
                    if (!GetKeyboardState(now)) Array.Copy(saved, now, 256);
                    var taken = InFront(thread);
                    foreach (var vk in vks) now[vk] = taken ? (byte)(((GetAsyncKeyState(vk) & 0x8000) != 0 ? 0x80 : 0) | (saved[vk] & 1)) : saved[vk];
                    SetKeyboardState(now);
                }
                finally { Win32.AttachThreadInput(own, thread, false); attached = false; }
            }
        }
    }

    // Key or shortcut posted to `target`; modifiers simulated for the thread of `owner` (the targeted window).
    // started() before the first message: past it, any error is an unknown outcome (never replayed).
    internal static string PostCombo(nint owner, nint target, List<int> mods, int vk, nint layout, Action started)
    {
        var sent = 0;
        void Post(uint msg, int key, bool up)
        {
            if (sent == 0) { McpServer.BeforeKeySequenceInput(); McpServer.NativeInteraction.CheckInputLease(); started(); }
            var context = msg is WM_SYSKEYDOWN or WM_SYSKEYUP ? (nint)(1L << 29) : 0;
            try { Win32.Post(target, msg, key, KeyLparam(key, up, layout) | context); }
            catch (Exception error) when (sent > 0) { throw new InvalidOperationException($"Partial key input ({sent} messages posted); outcome unknown", error); }
            sent++;
        }
        using (mods.Count > 0 ? new SimulatedModifiers(owner, mods) : null)
        {
            // With Alt (Chromium only, ParseForChromium): system key messages with the Alt context bit, as Windows sends them.
            var alt = mods.Contains(0x12);
            foreach (var m in mods) Post(alt ? WM_SYSKEYDOWN : WM_KEYDOWN, m, false);
            Post(alt ? WM_SYSKEYDOWN : WM_KEYDOWN, vk, false);
            Post(alt ? WM_SYSKEYUP : WM_KEYUP, vk, true);
            for (var i = mods.Count - 1; i >= 0; i--) Post(alt && mods[i] == 0x12 ? WM_KEYUP : alt ? WM_SYSKEYUP : WM_KEYUP, mods[i], true);
        }
        return mods.Count > 0 ? "WM_KEYDOWN/WM_KEYUP, simulated keyboard state" : "WM_KEYDOWN/WM_KEYUP";
    }

    public static (List<int> Mods, int Vk, nint Layout) ParseForWindow(nint window, string spec)
    {
        var layout = GetKeyboardLayout(Win32.GetWindowThreadProcessId(window, out _));
        var (mods, vk) = Parse(spec, layout);
        return (mods, vk, layout);
    }

    public static Outcome Send(nint window, string spec, Action started)
    {
        var cls = ClassOf(window);
        var tk = cls == "TkTopLevel";
        var classic = !cls.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) && !cls.StartsWith("HwndWrapper", StringComparison.Ordinal) && cls != "ApplicationFrameWindow" &&
            !cls.StartsWith("Windows.UI.", StringComparison.Ordinal) && !tk && !cls.StartsWith("Mozilla", StringComparison.Ordinal);
        if (!classic && !tk) throw new BackgroundNotApplicableException("This interface ignores posted keys (measured): no background technique");
        var (mods, vk, layout) = ParseForWindow(window, spec);
        var thread = Win32.GetWindowThreadProcessId(window, out _);
        if (thread == 0) throw new BackgroundNotApplicableException("Window thread unknown");
        if (InFront(thread)) throw new BackgroundNotApplicableException("The target shares the foreground thread: posted keys refused");
        EmergencyStop.ThrowIfStopped();
        nint Focused()
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && info.Focus != 0 && (info.Focus == window || IsChild(window, info.Focus)) ? info.Focus : window;
        }
        if (!tk) return new("background:messages", PostCombo(window, Focused(), mods, vk, layout, started), "sent");
        // Tk routes the keyboard only to its focused window, kept from WM_SETFOCUS (FocusIn): simulated focus SENT to its
        // window alone (never WM_ACTIVATE), keys posted, then WM_KILLFOCUS POSTED (handled after them). NexusClaude's
        // measurement of 23/10: Ctrl+K received, window covered, foreground and cursor untouched.
        if (SendMessageTimeoutW(window, WM_SETFOCUS, 0, 0, 0x0002 /* ABORTIFHUNG */, 1000, out _) == 0)
            throw new BackgroundNotApplicableException("Tk did not answer WM_SETFOCUS within 1 s: no key was sent");
        try
        {
            if (InFront(thread)) throw new BackgroundNotApplicableException("The Tk window came to the foreground: no key was sent");
            return new("background:messages", PostCombo(window, Focused(), mods, vk, layout, started) + ", simulated focus", "sent");
        }
        finally
        {
            // Taken back meanwhile by the user: their real focus is not undone.
            if (!InFront(thread)) try { Win32.Post(window, WM_KILLFOCUS, 0, 0); } catch (InvalidOperationException) { }
        }
    }
}
