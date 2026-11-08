using System.Diagnostics;

// Chromium (Chrome, Edge, Electron) in the background, the SHARED recipe of the three NexusWindows profiles (bench of
// 21/10; NexusClaude act_chromium._select_by_typing / _sim_focus, NexusAGY _chromium_simulated_typing): Chromium reads
// the keyboard only while its window is active, and a <select>'s list does not open in the background ("Option did not
// appear after expanding"). SIMULATED activation: WM_NCACTIVATE then WM_ACTIVATE sent to the top-level window (it
// believes itself active without being so, the user's foreground untouched), DOM focus through MSAA accSelect (read
// back), characters through WM_CHAR, effect read back; deactivation POSTED on exit (handled after the characters still
// queued), every potential activation included.
// Under the shared foreground token, within a bounded deadline (Budget after the token, whole path and cleanup
// included). First time for a process: its launch foreground right may have been used (foreground taken then given
// back, reported) and Chromium received a real deactivation: activation sent again and focus taken back before any
// typing. Typing in bounded UNITS: the target's focus read again before each, the value read back after each (nothing
// leaves until what came before is read back; never an arbitrary delay). EXACT comparisons (only line endings
// normalized). Before the first character, any failure = not applicable; after it, an unknown outcome (emergency stop
// included, said "after input"), never replayed. A window already in the foreground: path not applicable, the usual
// techniques apply (Background.cs).
// Failure after at least one character sent: unknown outcome (never turned into "not applicable").
sealed class AfterInputException(string message, Exception inner) : InvalidOperationException(message, inner);

static partial class ChromiumInput
{
    const uint WM_ACTIVATE = 0x0006, WM_NCACTIVATE = 0x0086, WM_CHAR = 0x0102;
    public const int Budget = 9000, Unit = 8, FirstSettleMs = 150;
    // Message text (typeText, WriteClaude): packets of 64 characters, each still read back before the next one. Eight per
    // packet made a short message take one read-back every 8 characters (05/11: about 2 s for a 58-character line).
    public const int TextUnit = 64;
    // Text: historical contract of the Claude profile (NexusClaude server._run, timeout_s=180, typing in slices without a
    // global 9 s cap), for the three MCP profiles since 01/11. Each packet is still read back and guarded (focus,
    // presence, STOP, MCP client cancellation). The JSON request mode without --mcp (Program.cs) keeps Budget.
    public const int McpTextBudget = 170_000;
    public static int TextBudget { get; set; } = McpTextBudget;
    static readonly HashSet<uint> Primed = new();

    public readonly record struct Result(Outcome Outcome, bool Taken, string? GiveBack);

    public static bool SupportsSimulatedActivation(string topClass) => topClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    public static bool IsChromium(nint window) => SupportsSimulatedActivation(Win32.ClassName(Top(window)));
    public readonly record struct HostChild(string Class, uint Pid, long Root);
    public static bool OwnedCef(uint pid, long root, IEnumerable<HostChild> children) => pid != 0 && root != 0 && children.Any(
        child => child.Class == "CefBrowserWindow" && child.Pid == pid && child.Root == root);
    // Steam/CEF in an SDL frame does NOT accept the simulated activation of Chrome's HWND. Publishing and clicks can
    // still handle its web content. An SDL name or a child belonging to another process is not enough.
    public static bool IsCefHost(nint window)
    {
        var top = Top(window); var pid = Win32.ProcessOf(top); var found = false;
        if (top == 0 || pid == 0) return false;
        Win32.EnumChildWindows(top, (child, _) =>
        {
            if (Win32.ClassName(child) != "CefBrowserWindow") return true;
            found = OwnedCef(pid, (long)top, [new HostChild("CefBrowserWindow", Win32.ProcessOf(child), (long)Top(child))]);
            return !found;
        }, 0);
        return found;
    }
    public static bool HasChromiumContent(nint window) => IsChromium(window) || IsCefHost(window);
    public static bool InForeground(nint window) => Top(Win32.GetForegroundWindow()) == Top(window);
    static nint Top(nint window) => Win32.GetAncestor(window, Win32.GA_ROOT) is var root && root != 0 ? root : window;

    // Click on a Chromium editable surface: focus without activation, read back.
    // started: called just before the first activation message (RunAction: "input left" only from there on).
    public static Result Focus(nint window, Node target, int? quietMs, Action? started = null) =>
        Run(window, target, quietMs, "focus", started, (_, _) => new("background:simulated-activation", "accSelect focus (simulated activation)", "applied"));

    // Chromium <select>: focus then typing of the label (one unit); keyboard search picks the option.
    public static Result Select(nint window, Node target, string option, int? quietMs, Action? started = null)
    {
        if (option.Length == 0 || option.Length > 64) throw new BackgroundNotApplicableException("Option label must be 1 to 64 characters to be typed");
        return Run(window, target, quietMs, "select", started, (top, deadline) =>
            TypeUnits(window, target, top, option, deadline, [option], _ => null, value => Same(value, option), "option typed"));
    }

    // Text appended at the end of a Chromium field or rich editor (caret put at the end, read back unit by unit).
    public static Result Type(nint window, Node target, string text, int? quietMs, Action? started = null)
    {
        if (text.Length == 0) throw new BackgroundNotApplicableException("Nothing to type");
        if (text.Contains('\n') || text.Contains('\r')) throw new BackgroundNotApplicableException("Line breaks are typed by the physical fallback (Chromium inserts them only from the key)");
        return Run(window, target, quietMs, "type", started, (top, deadline) =>
        {
            var before = ReadValue(window, target, deadline) ?? throw new BackgroundNotApplicableException("Field value unreadable: nothing was typed");
            Call(new ProgramForegroundActionJson(op: "caretToEnd", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow), deadline);
            var units = Units(text);
            return TypeUnits(window, target, top, text, deadline, units, typed => before + typed, value => Same(value, before + text), "text typed at the end");
        }, budget: TextBudget);
    }

    // Chromium key or shortcut (Ctrl+K): simulated activation, keys posted to the top-level window with the keyboard
    // state simulated for its thread (Keys.PostCombo), deactivation posted; no targeted element, the page receives the
    // keys at its DOM focus. NexusClaude's recipe (methods.simulated_activation, "simulated activation, simulated
    // keyboard": Ctrl+K received by the page, the user's foreground untouched). Effect read back by the session ("sent").
    public static Result SendKeys(nint window, string spec, int? quietMs, Action? started = null)
    {
        var (mods, vk, layout) = Keys.ParseForChromium(window, spec);
        if (EditingKey(mods, vk)) throw new BackgroundNotApplicableException("A Chromium editor does not apply editing keys while its window is inactive (measured 05/11: Backspace, Delete, Home and Ctrl+A had no effect in Codex): no background technique");
        return Run(window, null, quietMs, "keys", started, (top, _) =>
        {
            var posted = false;
            try
            {
                var how = Keys.PostCombo(top, top, mods, vk, layout, () => posted = true);
                return new("background:simulated-activation", how + " (simulated activation)", "sent");
            }
            catch (Exception error) when (posted && error is not EmergencyStopException)
            {
                throw new AfterInputException($"Keys interrupted after input; outcome unknown, do not replay: {error.Message}", error);
            }
        });
    }

    // Keys that edit the text or move its caret or selection: Backspace, Delete, Home, End, Page Up/Down, arrows, and
    // Ctrl+A, C, V, X, Y, Z. Command keys an app handles itself (Enter, Escape, Ctrl+K, F keys) still go in the background.
    internal static bool EditingKey(IReadOnlyCollection<int> mods, int vk)
    {
        if (mods.Contains(0x12) || mods.Contains(0x5B) || mods.Contains(0x5C)) return false; // Alt and Windows shortcuts are commands
        if (vk is 0x08 or 0x2E or (>= 0x21 and <= 0x28)) return true;
        return mods.Contains(0x11) && vk is 'A' or 'C' or 'V' or 'X' or 'Y' or 'Z';
    }

    // Exact equality, only line endings normalized (Codex's review of 22/10: case and spaces count).
    public static bool Same(string? actual, string expected) => actual is not null && actual.Replace("\r\n", "\n") == expected.Replace("\r\n", "\n");

    // Typing units: at most Unit UTF-16 units, without splitting a surrogate pair.
    public static List<string> Units(string text, int size = Unit)
    {
        var units = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            var length = Math.Min(size, text.Length - i);
            if (i + length < text.Length && char.IsHighSurrogate(text[i + length - 1])) length--;
            units.Add(text.Substring(i, length));
            i += length;
        }
        return units;
    }

    static int Remaining(long deadline) => (int)Math.Max(0, deadline - Environment.TickCount64);
    static Outcome Call(object request, long deadline)
    {
        var left = Remaining(deadline);
        if (left <= 0) throw new TimeoutException("Chromium input budget exhausted");
        return ComBridge.Call(request, Math.Min(left, ComBridge.TimeoutMs));
    }

    static string? ReadValue(nint window, Node target, long deadline)
    {
        Call(new ChromiumInputReadValueJson(op: "flush", windowId: (long)window, runtimeId: Array.Empty<int>()), deadline);
        using var tree = ComUia.Shared.ReadIdentity(window, target.RuntimeId, target.NativeWindow);
        return tree.Nodes.Count > 0 ? tree.Nodes[^1].Value : null;
    }

    // `expectedSoFar(typed)`: expected value after the units already sent (null: read back only at the end).
    static Outcome TypeUnits(nint window, Node target, nint top, string text, long deadline, List<string> units,
        Func<string, string?> expectedSoFar, Func<string?, bool> done, string what)
    {
        var sent = 0;
        var typed = "";
        string Partial() => $"{sent} of {text.Length} characters sent";
        try
        {
            foreach (var unit in units)
            {
                EmergencyStop.ThrowIfStopped();
                if (Top(Win32.GetForegroundWindow()) == top)
                    throw sent == 0 ? new BackgroundNotApplicableException("Julien switched to the window: nothing was typed")
                        : new InvalidOperationException("Julien switched to the window while typing: typing stopped");
                var focus = Call(new ProgramForegroundActionJson(op: "hasFocus", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow), deadline);
                if (focus.Effect != "applied")
                    throw sent == 0 ? new BackgroundNotApplicableException("The field lost the focus: nothing was typed")
                        : new InvalidOperationException("The field lost the focus while typing: typing stopped");
                foreach (var ch in unit) { Win32.Post(top, WM_CHAR, ch, 1); sent++; }
                typed += unit;
                if (expectedSoFar(typed) is not { } expected || typed.Length == text.Length) continue;
                // Nothing leaves until this unit is read back (no arbitrary delay: a deadline only).
                while (!Same(ReadValue(window, target, deadline), expected))
                {
                    if (Remaining(deadline) <= 0) throw new InvalidOperationException("Typed unit not read back in time: typing stopped");
                    Thread.Sleep(20);
                }
            }
            while (Remaining(deadline) > 0)
            {
                if (done(ReadValue(window, target, deadline))) return new("background:simulated-activation", $"WM_CHAR {sent} ({what}, simulated activation)", "applied");
                Thread.Sleep(20);
            }
            return new("background:simulated-activation", $"WM_CHAR {sent} ({what}, simulated activation)", "sent");
        }
        catch (EmergencyStopException) when (sent > 0)
        {
            // Emergency stop after characters (typing or read-back): a partial state, never "nothing was sent".
            throw new EmergencyStopException($"Emergency stop after input: {Partial()}; outcome unknown, do not replay");
        }
        catch (Exception error) when (sent > 0 && error is not EmergencyStopException)
        {
            throw new AfterInputException($"Typing interrupted after input: {Partial()}; outcome unknown, do not replay: {error.Message}", error);
        }
    }

    static Result Run(nint window, Node? target, int? quietMs, string purpose, Action? started, Func<nint, long, Outcome> act,
        Action<nint, Node, long>? focusTarget = null, int? budget = null)
    {
        var top = Top(window);
        if (!SupportsSimulatedActivation(Win32.ClassName(top)))
            throw new BackgroundNotApplicableException("Not a top-level Chromium window: simulated activation not applicable");
        if (Top(Win32.GetForegroundWindow()) == top)
            throw new BackgroundNotApplicableException("The window is in the foreground: simulated activation not needed");
        using var token = ForegroundToken.Acquire("Chromium simulated activation (" + purpose + ")");
        var deadline = Environment.TickCount64 + (budget ?? Budget);
        var pid = Win32.ProcessOf(top);
        var first = !Primed.Contains(pid);
        // Julien keeps working while the agent works (05/11): posted messages to a window that is not in front never meet
        // his keyboard or mouse, so they do not wait for him to stop. Only a process's first simulated activation can use
        // its launch right to the foreground (handled below): that one still waits for a quiet moment.
        // Measured 05/11: Codex running for an hour still came in front for 17 ms at a new engine's first activation, so
        // every first activation waits for a quiet moment, whatever the age of the process.
        if (first) JulienPause.ThrowIfActive(quietMs);
        EmergencyStop.ThrowIfStopped();
        var noted = Win32.GetForegroundWindow();
        var input = Win32.Probe().LastInput;
        var taken = false;
        string? giveBack = null;
        var activated = false; // no activation message before: nothing to undo on exit
        void TakeTargetFocus()
        {
            if (target is null) return;
            if (focusTarget is null) Focus(window, target, deadline); else focusTarget(window, target, deadline);
        }
        try
        {
            started?.Invoke();
            activated = true; // preconditions, token, pause and STOP passed: the first activation message leaves
            Activate(top);
            TakeTargetFocus();
            if (first)
            {
                // First simulated activation of this process: its launch foreground right may have been used during the
                // focus. Foreground given back to the user, then activation sent again and focus taken back; nothing typed yet.
                Thread.Sleep(FirstSettleMs);
                if (Top(Win32.GetForegroundWindow()) == top)
                {
                    taken = true;
                    giveBack = ForegroundReturn.GiveBack(noted, window, input);
                    if (giveBack != "restored") throw new BackgroundNotApplicableException($"First activation took the foreground ({giveBack}): nothing was typed");
                    Activate(top);
                    TakeTargetFocus();
                }
                Primed.Add(pid);
            }
            if (Top(Win32.GetForegroundWindow()) == top)
                throw new BackgroundNotApplicableException("Julien switched to the window: nothing was typed");
            return new(act(top, deadline), taken, giveBack);
        }
        catch (Exception error) when (error is not (BackgroundNotApplicableException or EmergencyStopException or ActionRefusedException or JulienActiveException or AfterInputException))
        {
            // Before the first character (focus, delay, read): nothing was typed.
            throw new BackgroundNotApplicableException($"Chromium input impossible before typing ({error.GetType().Name}: {error.Message}): nothing was typed");
        }
        finally
        {
            if (!activated) { }
            else if (Top(Win32.GetForegroundWindow()) != top)
            {
                try { Win32.Post(top, WM_ACTIVATE, 0 /* WA_INACTIVE */, 0); Win32.Post(top, WM_NCACTIVATE, 0, 0); } catch (InvalidOperationException) { }
                Thread.Sleep(30);
            }
            else if (Win32.Probe().LastInput == input)
            {
                taken = true;
                giveBack = ForegroundReturn.GiveBack(noted, window, input);
            }
            LastTaken = taken;
            LastGiveBack = giveBack;
        }
    }

    // Taking and giving back the foreground of the last path (read by RunAction, after an error too).
    [ThreadStatic] public static bool LastTaken;
    [ThreadStatic] public static string? LastGiveBack;

    static void Activate(nint top)
    {
        if (Win32.Send(top, WM_NCACTIVATE, 1) is null || Win32.Send(top, WM_ACTIVATE, 1 /* WA_ACTIVE */) is null)
            throw new BackgroundNotApplicableException("The window does not answer activation messages: nothing was typed");
    }

    static void Focus(nint window, Node target, long deadline)
    {
        var focus = Call(new ProgramForegroundActionJson(op: "takeFocus", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow), deadline);
        if (focus.Effect != "applied") throw new BackgroundNotApplicableException("Focus without activation not read back on the element: nothing was typed");
    }
}
