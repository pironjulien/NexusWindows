using System.Runtime.InteropServices;
using System.Text.Json;

// The helper's OWN real mouse and keyboard (SendInput), without Sky (the user's decision of 22/10: a complete,
// self-sufficient NexusWindows, Sky as a fallback only). Same recipe as nexus-windows for Claude (realinput.py), proven on
// the shared bench: only when the background is impossible and physical input is allowed, the CALLER holding the shared
// foreground token (withForeground) that will give the user's window back at the end.
// 1. the target comes forward (restored if minimized); Windows refuses: nothing is sent;
// 2. before each batch: emergency stop, target still in front, point on a window of the target process;
// 3. the move and the buttons of a click in ONE SINGLE SendInput, absolute coordinates attached to each event (nothing
//    slips in between, the click never lands under the user's cursor);
// 4. cursor put back in its place if we moved it and it has not moved since.
// Real pixels of each screen (app.manifest PerMonitorV2), like the native capture.
static partial class Physical
{
    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion U; }

    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint count, Input[] inputs, int size);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] private static partial nint WindowFromPoint(Win32.Point point);
    [LibraryImport("user32.dll")] private static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Win32.Rect rect, int size);

    static readonly Dictionary<string, (uint Down, uint Up)> Buttons = new()
    {
        ["left"] = (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        ["right"] = (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        ["middle"] = (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
    };

    internal static bool SupportsRelocation(string action) => action is "click" or "move" or "wheel";
    public static int TargetSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        Console.WriteLine(EngineJson.Serialize(input.RootElement.EnumerateArray().Select(e => SupportsRelocation(e.GetString() ?? "")).ToArray()));
        return 0;
    }

    // The target's window: the bound window itself or a window it OWNS (chain of owners: dialog, owned menu). Never a
    // sibling of the same process (Codex's review of 22/10 12:22: two Edge windows), nor a popup merely classed as such
    // (12:57: a foreign non-activating palette covering the point got through).
    static bool OfTarget(nint hwnd, nint target)
    {
        if (hwnd == 0) return false;
        var root = Win32.GetAncestor(hwnd, GA_ROOT);
        if (root == 0) root = hwnd;
        if (root == target) return true;
        for (var (owner, depth) = (Win32.GetWindow(root, Win32.GW_OWNER), 0); owner != 0 && depth < 16; owner = Win32.GetWindow(owner, Win32.GW_OWNER), depth++)
            if (owner == target) return true;
        return false;
    }

    // The target in front, or refused (nothing is sent). Restored if minimized; the foreground thread attached for the
    // time of SetForegroundWindow, like the foreground restore (ForegroundReturn).
    // Bringing to the foreground alone (the session's interaction lease, under its token): without Sky.
    public static object Activate(nint window, Action started)
    {
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        started();
        BringToFront(window);
        return new PhysicalActivateJson(ok: true, technique: "physical:native", mechanism: "SetForegroundWindow", effect: "applied", foregroundTaken: true);
    }

    static void BringToFront(nint window)
    {
        // SetForegroundWindow may update the foreground queue before the target has processed WM_ACTIVATE.
        // A bounded WM_NULL waits for that activation to finish, including the app's layout changes, before any
        // rectangle is re-read. GetForegroundWindow alone is not an activation acknowledgement.
        bool Activated()
        {
            McpServer.NativeInteraction.CheckInputLease();
            if (McpServer.NativeInteraction.AllowsForeground(window, Win32.GetForegroundWindow())) return true;
            if (McpServer.NativeInteraction.HasShellLease(window)) throw new ActionRefusedException("The observed Windows panel no longer owns the foreground; no activation or input sent");
            if (!OfTarget(Win32.GetForegroundWindow(), window)) return false;
            if (Win32.Send(window, 0 /* WM_NULL */) is null)
                throw new ActionRefusedException("The target did not acknowledge activation: nothing was sent");
            EmergencyStop.ThrowIfStopped();
            NativeMcpGuard.CheckInterruption();
            return OfTarget(Win32.GetForegroundWindow(), window);
        }
        if (Activated()) return;
        if (Win32.IsIconic(window)) { EmergencyStop.ThrowIfStopped(); ShowWindow(window, 9 /* SW_RESTORE */); Thread.Sleep(150); }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            EmergencyStop.ThrowIfStopped();
            var fg = Win32.GetForegroundWindow();
            var own = Win32.GetCurrentThreadId();
            var thread = fg == 0 ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
            var attached = thread != 0 && thread != own && Win32.AttachThreadInput(own, thread, true);
            try { Win32.BringWindowToTop(window); Win32.SetForegroundWindow(window); }
            finally { if (attached) Win32.AttachThreadInput(own, thread, false); }
            for (var waited = 0; waited < 500; waited += 25)
            {
                if (Activated()) return;
                Thread.Sleep(25);
            }
        }
        var holder = Win32.GetForegroundWindow();
        throw new ActionRefusedException($"Windows refuses to bring the window to the front (foreground held by {(holder == 0 ? "no window" : Win32.ClassName(holder))}): nothing was sent");
    }

    // Before each batch: emergency stop, target in front, point on the target (a window slipped in between: stop, nothing else).
    static void Guard(nint window, int? x = null, int? y = null)
    {
        EmergencyStop.ThrowIfStopped();
        NativeMcpGuard.CheckInterruption();
        McpServer.NativeInteraction.CheckInputLease();
        if (!OfTarget(Win32.GetForegroundWindow(), window) && !McpServer.NativeInteraction.AllowsForeground(window, Win32.GetForegroundWindow()))
            throw new InvalidOperationException("The target is no longer in the foreground (Julien took over?): stopped, nothing else sent");
        if (x is { } px && y is { } py)
        {
            var hit = WindowFromPoint(new Win32.Point { X = px, Y = py });
            if (!OfTarget(hit, window) && !McpServer.NativeInteraction.AllowsForeground(window, hit))
                throw new InvalidOperationException($"The point ({px}, {py}) does not hit the target window (covered?): stopped, nothing else sent");
        }
    }

    static Input Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        new() { Type = INPUT_MOUSE, U = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Data = unchecked((uint)data), Flags = flags } } };

    // Mouse event AT the point: absolute move on the virtual desktop attached to the button or the wheel.
    static Input At(uint flags, int x, int y, int data = 0)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        var ax = (int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1));
        var ay = (int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1));
        return Mouse(flags | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay, data);
    }

    static Input Key(ushort vk, bool up, ushort scan = 0, bool unicode = false) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion { Keyboard = new KeyboardInput { Vk = unicode ? (ushort)0 : vk, Scan = scan,
            Flags = (up ? KEYEVENTF_KEYUP : 0) | (unicode ? KEYEVENTF_UNICODE : 0) | (!unicode && Keys.IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0) } },
    };

    static void Send(Action started, params Input[] inputs)
    {
        started();
        var inserted = (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (inserted == inputs.Length) return;
        // Batch partly inserted (Codex's review of 22/10 12:22): buttons and keys pressed in the inserted part and not
        // released are released as well as possible, then an unknown outcome, never replayed.
        Release(inputs[..Math.Max(0, inserted)]);
        throw new InvalidOperationException($"Windows inserted {inserted} of {inputs.Length} simulated input events (locked screen, UAC or secure desktop); outcome unknown, do not replay");
    }

    static void Release(Input[] sent)
    {
        var ups = new List<Input>();
        var mouseUps = new Dictionary<uint, uint> { [MOUSEEVENTF_LEFTDOWN] = MOUSEEVENTF_LEFTUP, [MOUSEEVENTF_RIGHTDOWN] = MOUSEEVENTF_RIGHTUP, [MOUSEEVENTF_MIDDLEDOWN] = MOUSEEVENTF_MIDDLEUP };
        foreach (var (down, up) in mouseUps)
        {
            var downs = sent.Count(i => i.Type == INPUT_MOUSE && (i.U.Mouse.Flags & down) != 0);
            var released = sent.Count(i => i.Type == INPUT_MOUSE && (i.U.Mouse.Flags & up) != 0);
            if (downs > released) ups.Add(Mouse(up));
        }
        foreach (var key in sent.Where(i => i.Type == INPUT_KEYBOARD && (i.U.Keyboard.Flags & KEYEVENTF_KEYUP) == 0).Reverse())
        {
            var k = key.U.Keyboard;
            var released = sent.Any(i => i.Type == INPUT_KEYBOARD && (i.U.Keyboard.Flags & KEYEVENTF_KEYUP) != 0 && i.U.Keyboard.Vk == k.Vk && i.U.Keyboard.Scan == k.Scan);
            if (!released) ups.Add(new Input { Type = INPUT_KEYBOARD, U = new InputUnion { Keyboard = k with { Flags = k.Flags | KEYEVENTF_KEYUP } } });
        }
        if (ups.Count > 0) SendInput((uint)ups.Count, [.. ups], Marshal.SizeOf<Input>());
    }

    // `relocate`: target of a native observation, read again UNDER the token after coming to the foreground and just
    // before the input (identity, ancestry, rectangle); returns the center of its visible part (Codex's review of 22/10 12:22).
    public static object Act(nint window, JsonElement request, Action started, Func<(int X, int Y)>? relocate = null)
    {
        int Int(string key) => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string Str(string key, string fallback) => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        var action = Str("action", "");
        if (action is not ("click" or "wheel" or "drag" or "type" or "key" or "move")) throw new InvalidOperationException("Unknown physical action");
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        var (x, y) = (Int("x"), Int("y"));
        // Checks before any input: known button, parsable key, non-empty text.
        var button = Str("button", "left");
        if (action == "click" && !Buttons.ContainsKey(button)) throw new InvalidOperationException("Invalid mouse button");
        (List<int> Mods, int Vk) combo = ([], 0);
        if (action == "key") combo = Keys.ParseFor(Str("key", ""), GetKeyboardLayout(Win32.GetWindowThreadProcessId(window, out _)));
        var text = Str("text", "");
        if (action == "type" && text.Length == 0) throw new InvalidOperationException("Empty text");
        // Point of a native capture: the DWM frame noted at the capture is read again; a moved window = refused (like Pointer).
        void CheckFrame()
        {
            if (!request.TryGetProperty("frame", out var f) || f.ValueKind != JsonValueKind.Array) return;
            var frame = f.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (frame.Length != 4 || DwmGetWindowAttribute(window, 9, out var now, 16) != 0 ||
                now.Left != frame[0] || now.Top != frame[1] || now.Width != frame[2] || now.Height != frame[3])
                throw new ActionRefusedException("Window moved or resized since the native capture: observe again");
        }
        CheckFrame();
        Win32.GetCursorPos(out var cursor);
        var moved = false;
        if (relocate is not null && !SupportsRelocation(action)) throw new ActionRefusedException("Only click, move or wheel may use a native target: observe a native screenshot");
        // Right click: menus open BEFORE, to report the one THIS click opens (like the posted right click).
        var rightClick = action == "click" && button == "right";
        var menusBefore = rightClick ? Win32.LinkedMenuPopups(window).ToHashSet() : [];
        BringToFront(window);
        if (relocate is not null) (x, y) = relocate();
        // Activation/restoration may change the window's placement (or its own Activated handler may resize it).
        // A screenshot point has no semantic target to relocate: refuse before SendInput instead of clicking an old
        // centre that still happens to belong to the same window. The early check above also avoids activation when
        // the capture was already stale before we acquired the foreground.
        CheckFrame();
        string mechanism;
        try
        {
            switch (action)
            {
                case "click":
                {
                    Guard(window, x, y);
                    var (down, up) = Buttons[button];
                    var count = Math.Clamp(Int("count"), 1, 2);
                    var events = new List<Input> { At(0, x, y) };
                    for (var i = 0; i < count; i++) { events.Add(At(down, x, y)); events.Add(At(up, x, y)); }
                    Send(started, [.. events]);
                    moved = true;
                    mechanism = $"SendInput {button} click x{count}";
                    break;
                }
                case "move":
                {
                    // Hover: the cursor stays at the point (that is the gesture's purpose); the foreground is given back as
                    // after any physical gesture.
                    Guard(window, x, y);
                    Send(started, At(0, x, y));
                    mechanism = "SendInput move (hover, cursor left at the point)";
                    break;
                }
                case "wheel":
                {
                    Guard(window, x, y);
                    var (deltaY, deltaX) = (Int("deltaY"), Int("deltaX"));
                    var events = new List<Input> { At(0, x, y) };
                    if (deltaY != 0) events.Add(At(MOUSEEVENTF_WHEEL, x, y, deltaY)); // Windows sign (negative = down), like "point"
                    if (deltaX != 0) events.Add(At(MOUSEEVENTF_HWHEEL, x, y, deltaX));
                    Send(started, [.. events]);
                    moved = true;
                    mechanism = "SendInput wheel";
                    break;
                }
                case "drag":
                {
                    Guard(window, x, y);
                    var (tx, ty) = (Int("toX"), Int("toY"));
                    try
                    {
                        Send(started, At(0, x, y), At(MOUSEEVENTF_LEFTDOWN, x, y));
                        moved = true;
                        const int Steps = 12;
                        for (var i = 1; i <= Steps; i++)
                        {
                            Thread.Sleep(15);
                            Guard(window); // the destination may be another window
                            Send(started, At(0, x + (tx - x) * i / Steps, y + (ty - y) * i / Steps));
                        }
                    }
                    finally { if (moved) Send(started, Mouse(MOUSEEVENTF_LEFTUP)); }
                    mechanism = "SendInput drag";
                    break;
                }
                case "key":
                {
                    McpServer.BeforeKeySequenceInput();
                    Guard(window);
                    var downs = combo.Mods.Select(m => Key((ushort)m, false)).Append(Key((ushort)combo.Vk, false));
                    var ups = new[] { Key((ushort)combo.Vk, true) }.Concat(Enumerable.Reverse(combo.Mods).Select(m => Key((ushort)m, true)));
                    Send(started, [.. downs, .. ups]);
                    mechanism = "SendInput key";
                    break;
                }
                default: // type: UTF-16 units, line breaks through Enter; guard before each character
                {
                    var units = text.Replace("\r\n", "\n");
                    var sent = 0;
                    try
                    {
                        foreach (var ch in units)
                        {
                            Guard(window);
                            if (ch == '\n')
                            {
                                // MCP multiline text owns an in-process submission scope bound by the facade.
                                // A reactive recipient/send control is checked after the typed prefix,
                                // at this actual Enter boundary, before any key event is sent.
                                McpServer.BeforeTextEnter(requireScope: McpServer.Serving);
                                Guard(window); // The contextual read may have taken time: focus/STOP/user again.
                                Send(started, Key(0x0D, false), Key(0x0D, true));
                            }
                            else Send(started, Key(0, false, ch, true), Key(0, true, ch, true));
                            sent++;
                            Thread.Sleep(2);
                        }
                    }
                    // After units were sent: a partial state, never "nothing was sent" (Codex's review of 22/10 12:22).
                    catch (EmergencyStopException) when (sent > 0)
                    {
                        throw new EmergencyStopException($"Emergency stop after input: {sent} of {units.Length} characters sent; outcome unknown, do not replay");
                    }
                    catch (Exception error) when (sent > 0 && error is not EmergencyStopException)
                    {
                        throw new AfterInputException($"Typing interrupted after input: {sent} of {units.Length} characters sent; outcome unknown, do not replay: {error.Message}", error);
                    }
                    mechanism = "SendInput unicode text";
                    break;
                }
            }
        }
        finally
        {
            Thread.Sleep(50);
            if (moved && Win32.GetCursorPos(out var now) && Math.Abs(now.X - (action == "drag" ? Int("toX") : x)) <= 1 &&
                Math.Abs(now.Y - (action == "drag" ? Int("toY") : y)) <= 1)
                SetCursorPos(cursor.X, cursor.Y);
        }
        return new PhysicalActJson(ok: true, technique: "physical:native", mechanism: mechanism, effect: "sent", foregroundTaken: true,
            contextMenu: rightClick ? Program.OpenedMenu(window, menusBefore) : null);
    }
}
