using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// "Pure COM" child process (--com-worker): UI Automation patterns with AutoSetFocus = FALSE, without ever loading
// System.Windows.Automation (see Background.cs). One JSON request per line, one answer per line. It lives as long as the
// parent helper keeps its standard input open (the parent's death = the child's end).
static partial class ComWorker
{
    internal const string Uia = "background:uia";

    public static int Run()
    {
        EmergencyStop.Watch();
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            Acted = false; // even if this line fails before Execute (malformed JSON after an earlier input)
            object result;
            try
            {
                using var json = JsonDocument.Parse(line);
                result = Execute(json.RootElement);
            }
            // The send point is what counts, whatever the kind of exception: a prior read can fail without input;
            // conversely an old Data mark must never erase a call already transmitted.
            catch (Exception error) { result = Failure(error); }
            Console.WriteLine(EngineJson.Serialize(result));
            Console.Out.Flush();
        }
        return 0;
    }

    // Boundary of the request: false as long as no call that can act (pattern, focus) has been tried. An emergency stop
    // before it is marked "before any pattern" (Codex's review of 24/10); after it, the outcome stays unknown.
    [ThreadStatic] internal static bool Acted;
    // Named *Json so the shape check of the test suite requires its source-generated metadata: without it the worker
    // died serializing its own failure and every refused click became "worker exited; outcome unknown" (08/11, Gemini).
    internal sealed record WorkerFailureJson(bool ok, string error, string message, bool beforeInput);
    internal static WorkerFailureJson Failure(Exception error) => new(false, error.GetType().Name, error.Message, !Acted);
    static void BeforeAct()
    {
        try { EmergencyStop.ThrowIfStopped(); }
        catch (EmergencyStopException error) when (!Acted) { error.Data["beforeInput"] = true; throw; }
        Acted = true;
    }

    static object Execute(JsonElement request)
    {
        Acted = false;
        try { EmergencyStop.ThrowIfStopped(); }
        catch (EmergencyStopException error) { error.Data["beforeInput"] = true; throw; } // nothing called yet
        var op = request.GetProperty("op").GetString();
        var window = (nint)request.GetProperty("windowId").GetInt64();
        if (op == "realizeNamed") return RealizeNamed(window, request);
        if (op == "resumeStore") return ResumeStore(window, request);
        if (op == "scrollAt") return ScrollAt(window, request);
        if (op == "menuPath") return MenuPath(window, request);
        if (op == "flush") { var publication = ComUia.Shared.Flush(window); return new ComWorkerExecuteJson(ok: true, technique: Uia, mechanism: "ScrollIntoView (document)", effect: publication == "requested" ? "sent" : "none", publication: publication); }
        if (op is "takeFocus" or "hasFocus" && request.TryGetProperty("expectedWindowBirth", out _)) RequireBoundChromiumWindow(window, request);
        var runtimeId = request.GetProperty("runtimeId").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        string Text(string key) => request.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        // Before the pattern, any failure (window or element gone, provider absent) comes before the input.
        nint element;
        var hint = request.TryGetProperty("nativeWindow", out var h) && h.ValueKind == JsonValueKind.Number ? (nint)h.GetInt64() : 0;
        try
        {
            EmergencyStop.ThrowIfStopped();
            element = ComUia.Shared.FindByRuntimeId(window, runtimeId, hint);
        }
        catch (Exception error) when (error is BackgroundNotApplicableException or EmergencyStopException)
        {
            // Target gone or emergency stop before any pattern: the same mark (Codex's review of 24/10).
            error.Data["beforeInput"] = true;
            throw;
        }
        catch (Exception error)
        {
            throw Before("UI Automation element unavailable before input: " + error.Message);
        }
        try
        {
            var outcome = op switch
            {
                "click" => Click(element, request.GetProperty("order").EnumerateArray().Select(e => e.GetString() ?? "").ToArray()),
                "invoke" => WithPattern(element, ComUia.InvokePatternId, p => { BeforeAct(); ComUia.CallSlot(p, 3); return new Outcome(Uia, "Invoke", "sent"); },
                    () => throw Before("No InvokePattern")),
                "scrollIntoView" => WithPattern(element, ComUia.ScrollItemPatternId, p => { BeforeAct(); ComUia.CallSlot(p, 3); return new Outcome(Uia, "ScrollItem.ScrollIntoView", "sent"); }, () => throw Before("No ScrollItemPattern")),
                "setFocus" => request.TryGetProperty("expectedWindowBirth", out _) ? SetBoundFocus(element, window, request) : SetFocus(element),
                "takeFocus" => request.TryGetProperty("expectedWindowBirth", out _) ? BoundChromiumFocus(element, window, request, true) : TakeFocus(element, window),
                "caretToEnd" => CaretToEnd(element),
                "hasFocus" => request.TryGetProperty("expectedWindowBirth", out _) ? BoundChromiumFocus(element, window, request, false) : new Outcome(Uia, "HasKeyboardFocus", ComUia.HasKeyboardFocus(element) ? "applied" : "none"),
                "setValue" => SetValue(element, Text("value")),
                "appendValue" => AppendValue(window, element, request, Text("text")),
                "expandCollapse" => ExpandCollapse(element, Text("action"), request.TryGetProperty("invokeFallback", out var fb) && fb.ValueKind == JsonValueKind.True),
                "selectItem" => SelectItem(window, element, request.GetProperty("containerRuntimeId").EnumerateArray().Select(e => e.GetInt32()).ToArray()),
                "selectOption" => SelectOption(element, Text("option"), request.TryGetProperty("combo", out var c) && c.GetBoolean()),
                _ => throw new InvalidOperationException("Unknown COM worker operation"),
            };
            return new ProgramKeyActionJson(ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect);
        }
        finally { ComUia.Release(element); }
    }
    // Focus without activation (MSAA accSelect), read back for up to 500 ms: "applied" if the element has the keyboard
    // focus, otherwise "sent" (focus requested but not read back; nothing is typed afterwards).
    static Outcome TakeFocus(nint element, nint window = 0)
    {
        BeforeAct();
        ComUia.TakeFocus(element);
        // A Chromium page behind other windows publishes its focus change only at an accessibility request (the same
        // ScrollIntoView of its document the bound text route makes): without it a focused web field read « not focused »
        // (05/11: a click in a page field, « Focus without activation not read back »).
        var chromium = window != 0 && ChromiumInput.HasChromiumContent(window);
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 500)
        {
            if (ComUia.HasKeyboardFocus(element)) return new(Uia, "accSelect(SELFLAG_TAKEFOCUS)", "applied");
            if (chromium && ComUia.Shared.Flush(window) == "stopped") throw new EmergencyStopException("Emergency stop during Chromium focus publication");
            Thread.Sleep(20);
        }
        return new(Uia, "accSelect(SELFLAG_TAKEFOCUS)", "sent");
    }
    static Outcome CaretToEnd(nint element)
    {
        BeforeAct();
        ComUia.CaretToEnd(element);
        return new(Uia, "TextRange caret to end", "applied");
    }
    static Outcome SetFocus(nint element)
    {
        BeforeAct();
        ComUia.SetFocus(element);
        return new("foreground:uia", "SetFocus", ComUia.HasKeyboardFocus(element) ? "applied" : "sent");
    }

    // WinForms menu (MenuStrip/ToolStrip, ContextMenuStrip; no Win32 menu bar) by its path in the bound window (shared
    // bench of 21/10: the UIA identities of ToolStrip items change at each read, "Target is no longer present"): each
    // segment {name, optional automation id} is searched DIRECTLY under the previous one (ComUia.MenuChild: never through
    // another menu item); a submenu whose items are not exposed yet is expanded (ExpandCollapse); the final command
    // through UIA Invoke. An absent item, a namesake, an incomplete read or no Invoke: not applicable, nothing is invoked,
    // and the submenus we expanded are collapsed and their closing READ BACK; an unproven closing is an unknown outcome
    // (Codex's review of 22/10).
    static ProgramKeyActionJson MenuPath(nint window, JsonElement request)
    {
        var segments = request.GetProperty("path").EnumerateArray()
            .Select(p => (Name: p.GetProperty("name").GetString() ?? "", AutomationId: p.TryGetProperty("automationId", out var a) ? a.GetString() : null)).ToArray();
        if (segments.Length == 0 || segments.Any(p => p.Name.Length == 0)) throw new BackgroundNotApplicableException("Invalid menu path");
        var held = new List<nint>();
        var expanded = new List<nint>();
        var scope = ComUia.Shared.WindowElement(window);
        held.Add(scope);
        var invoked = false;
        try
        {
            foreach (var (name, automationId) in segments)
            {
                var item = ComUia.Shared.MenuChild(scope, name, automationId);
                if (item == 0 && held.Count > 1)
                {
                    var expand = ComUia.Pattern(scope, ComUia.ExpandCollapsePatternId);
                    if (expand != 0)
                    {
                        try
                        {
                            BeforeAct();
                            ComUia.CallSlot(expand, 3); // Expand
                            expanded.Add(scope);
                            WaitState(expand, s => s is 1 or 2);
                        }
                        finally { ComUia.Release(expand); }
                        item = ComUia.Shared.MenuChild(scope, name, automationId);
                    }
                }
                if (item == 0) throw new BackgroundNotApplicableException($"No background technique applies: menu item « {name} » not found at this level of the menu");
                held.Add(item);
                scope = item;
            }
            var outcome = WithPattern(scope, ComUia.InvokePatternId, p => { BeforeAct(); invoked = true; ComUia.CallSlot(p, 3); return new Outcome(Uia, "Invoke (menu path)", "sent"); },
                () => throw new BackgroundNotApplicableException("No background technique applies: the menu command has no Invoke"));
            return new ProgramKeyActionJson(ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect);
        }
        finally
        {
            try
            {
                if (!invoked)
                    for (var i = expanded.Count - 1; i >= 0; i--)
                        Collapse(expanded[i]);
            }
            finally { foreach (var element in held) ComUia.Release(element); }
        }
    }

    // Collapse of a submenu we expanded, closing read back; unproven: unknown outcome (terminal, never swallowed).
    static void Collapse(nint item)
    {
        var pattern = ComUia.Pattern(item, ComUia.ExpandCollapsePatternId);
        if (pattern == 0) throw new InvalidOperationException("A menu opened for the search could not be closed; outcome unknown");
        try { CollapseVerified(() => { BeforeAct(); ComUia.CallSlot(pattern, 4); }, () => WaitState(pattern, s => s == 0)); }
        finally { ComUia.Release(pattern); }
    }
    public static void CollapseVerified(Action collapse, Func<int> state)
    {
        int after;
        try { collapse(); after = state(); }
        catch (Exception error) { throw new InvalidOperationException("A menu opened for the search could not be closed; outcome unknown: " + error.Message, error); }
        if (after != 0) throw new InvalidOperationException("A menu opened for the search is still open; outcome unknown");
    }

    // Background wheel of an interface that ignores the posted mouse (WPF, shared bench of 21/10, Codex's recipe A):
    // SEMANTIC scrolling of the smallest UI Automation container scrollable on that axis under the point, searched in the
    // subtree of the target window (ComUia.ScrollContainerAt). One small step per notch; position read back for up to
    // 1 s: changed = "applied", unchanged = "sent" (never a second technique). Already at the REAL end (0 or 100 %):
    // nothing is sent ("none"). Any failure from the first step on: unknown outcome. This is not a wheel event: a canvas
    // without ScrollPattern stays not applicable.
    static ProgramKeyActionJson ScrollAt(nint window, JsonElement request)
    {
        int Int(string key) => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var (x, y, deltaY, deltaX) = (Int("x"), Int("y"), Int("deltaY"), Int("deltaX"));
        var previous = Win32.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2: real pixels, like the capture
        nint element = 0, pattern = 0;
        try
        {
            var plan = ScrollPlan.Of(deltaY, deltaX);
            (element, pattern) = ComUia.Shared.ScrollContainerAt(window, x, y, plan.Vertical);
            if (pattern == 0) throw new BackgroundNotApplicableException("No scrollable UI Automation container of the window under the point on this axis");
            var slot = plan.Vertical ? 6 : 5; // get_CurrentVertical/HorizontalScrollPercent
            var before = ComUia.ReadDouble(pattern, slot);
            if (plan.AtEnd(before))
                return new ProgramKeyActionJson(ok: true, technique: Uia, mechanism: $"ScrollPattern: already at the end ({before} %)", effect: "none");
            EmergencyStop.ThrowIfStopped();
            try
            {
                for (var i = 0; i < plan.Notches; i++)
                {
                    BeforeAct();
                    if (plan.Vertical) ComUia.Scroll(pattern, 2, plan.Amount); else ComUia.Scroll(pattern, plan.Amount, 2);
                }
                var after = before;
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1000)
                {
                    after = ComUia.ReadDouble(pattern, slot);
                    if (after != before) break;
                    Thread.Sleep(50);
                }
                return new ProgramKeyActionJson(ok: true, technique: Uia, mechanism: $"ScrollPattern {plan.Notches}x ({before:0.##} % -> {after:0.##} %)", effect: after != before ? "applied" : "sent");
            }
            catch (Exception error)
            {
                // At least one step may have left: never "nothing was sent", never a fallback.
                throw new InvalidOperationException("Semantic scroll interrupted after input; outcome unknown, do not replay: " + error.Message, error);
            }
        }
        finally
        {
            ComUia.Release(pattern);
            ComUia.Release(element);
            if (previous != 0) Win32.SetThreadDpiAwarenessContext(previous);
        }
    }

    // Refusal raised before any pattern call of this request: marked, passed to the helper (beforeInput), which reports it
    // as "nothing sent" (Codex's review of 24/10).
    internal static BackgroundNotApplicableException Before(string message)
    {
        var error = new BackgroundNotApplicableException(message);
        if (!Acted) error.Data["beforeInput"] = true;
        return error;
    }
    static T WithPattern<T>(nint element, int patternId, Func<nint, T> action, Func<T> missing)
    {
        var pattern = ComUia.Pattern(element, patternId);
        if (pattern == 0) return missing();
        try { return action(pattern); } finally { ComUia.Release(pattern); }
    }
    static Outcome Toggle(nint pattern)
    {
        var before = ComUia.ReadInt(pattern, 4);
        BeforeAct();
        ComUia.CallSlot(pattern, 3);
        return new(Uia, "Toggle", EffectAfterCall(ComUia.ReadInt(pattern, 4) != before));
    }
    static Outcome Select(nint pattern)
    {
        BeforeAct();
        ComUia.CallSlot(pattern, 3);
        return new(Uia, "SelectionItem.Select", EffectAfterCall(ComUia.ReadInt(pattern, 6) != 0));
    }
    static Outcome Click(nint element, string[] order) => ChooseClick(order, name => name switch
    {
        "toggle" => WithPattern<Outcome?>(element, ComUia.TogglePatternId, p => Toggle(p), () => null),
        "invoke" => WithPattern<Outcome?>(element, ComUia.InvokePatternId, p => { BeforeAct(); ComUia.CallSlot(p, 3); return new(Uia, "Invoke", "sent"); }, () => null),
        "default" => WithPattern<Outcome?>(element, ComUia.LegacyIAccessiblePatternId, p => { BeforeAct(); ComUia.CallSlot(p, 4); return new(Uia, "LegacyIAccessible.DoDefaultAction", "sent"); }, () => null),
        "select" => WithPattern<Outcome?>(element, ComUia.SelectionItemPatternId, p => Select(p), () => null),
        _ => null,
    });
    // The next pattern is consulted only if the previous one does not exist (null). An input without a read-back effect,
    // a lost ACK or an error after BeforeAct stay terminal: neither another pattern nor another click.
    internal static Outcome ChooseClick(string[] order, Func<string, Outcome?> attempt)
    {
        foreach (var name in order)
        {
            var outcome = attempt(name);
            if (outcome is { } done) return done;
        }
        throw Before("No background click pattern for this control"); // no pattern called
    }
    static Outcome SetValue(nint element, string value) => WithPattern(element, ComUia.ValuePatternId, pattern =>
    {
        if (ComUia.ReadInt(pattern, 5) != 0) throw Before("Read-only value");
        BeforeAct();
        ComUia.SetValue(pattern, value);
        var after = ComUia.Value(pattern);
        return new Outcome(Uia, "Value.SetValue", EffectAfterCall(after == value));
    }, () => throw Before("No writable ValuePattern"));
    // State read through the ExpandCollapse pattern (0 collapsed, 1 expanded, 2 partial, 3 leaf), waited for 0.5 s at most:
    // some providers (WPF) open their popup asynchronously.
    static int WaitState(nint pattern, Func<int, bool> reached)
    {
        var state = ComUia.ReadInt(pattern, 5);
        for (var i = 0; i < 10 && !reached(state); i++) { Thread.Sleep(50); state = ComUia.ReadInt(pattern, 5); }
        return state;
    }
    // A call already transmitted never becomes "none" because its effect is not published yet. Even a COM refusal after
    // the call does not prove the provider scheduled nothing. Invoke is a fallback only if the ExpandCollapse pattern is
    // ABSENT before any call, never after Expand/Collapse or after a failed read.
    static Outcome ExpandCollapse(nint element, string action, bool invokeFallback) => ChooseExpandCollapse(action, invokeFallback,
        () => WithPattern<Outcome?>(element, ComUia.ExpandCollapsePatternId, pattern =>
        {
            Func<int, bool> reached = action == "Expand" ? s => s is 1 or 2 : s => s == 0;
            return ExpandCollapseOnce(action,
                () => { BeforeAct(); ComUia.CallSlot(pattern, action == "Expand" ? 3 : 4); },
                () => reached(WaitState(pattern, reached)));
        }, () => null),
        () => WithPattern<Outcome?>(element, ComUia.InvokePatternId, pattern =>
        {
            BeforeAct();
            ComUia.CallSlot(pattern, 3);
            return new Outcome(Uia, "Invoke (ExpandCollapsePattern absent)", "sent");
        }, () => null));

    internal static string EffectAfterCall(bool reached) => reached ? "applied" : "sent";
    internal static Outcome ChooseExpandCollapse(string action, bool invokeFallback, Func<Outcome?> expand, Func<Outcome?> invoke)
    {
        if (expand() is { } first) return first;
        if (action == "Expand" && invokeFallback && invoke() is { } fallback) return fallback;
        throw Before("No applicable ExpandCollapsePattern or permitted InvokePattern");
    }
    internal static Outcome ExpandCollapseOnce(string action, Action apply, Func<bool> reached)
    {
        string mechanism = action;
        try { apply(); }
        catch (COMException error) when (IsRefusal(error.HResult))
        {
            mechanism += $" refused by the provider ({error.Message}), state read back; no replay";
        }
        return new(Uia, mechanism, EffectAfterCall(reached()));
    }
    // Known COM refusals whose state can be read back. They still count as transmitted: no other pattern after the call.
    internal static bool IsRefusal(int hresult) => hresult is unchecked((int)0x80131509) or unchecked((int)0x80040200);

    // Option of a combo box (container) picked through SelectionItem, then the list closed again: a list left open is a
    // popup above the user's windows (a click from them would pick an option there).
    static Outcome SelectItem(nint window, nint item, int[] containerRuntimeId)
    {
        var container = ComUia.Shared.FindByRuntimeId(window, containerRuntimeId);
        try
        {
            var selected = WithPattern(item, ComUia.SelectionItemPatternId, Select, () => throw Before("Option is not selectable"));
            return Close(container, selected with { Mechanism = "SelectionItem.Select" });
        }
        finally { ComUia.Release(container); }
    }
    // UIA list or combo box (WPF, XAML, Chromium): an option already published under the container, otherwise expanding
    // without focus, choice through SelectionItem, then collapsing (always for a combo box, otherwise if it was collapsed).
    // Option matching: a unique exact match, then a unique case-insensitive one; never partial.
    static Outcome SelectOption(nint container, string option, bool combo)
    {
        var expand = ComUia.Pattern(container, ComUia.ExpandCollapsePatternId);
        try
        {
            nint item;
            try { item = FindOption(container, option, allowAbsent: expand != 0); }
            catch (BackgroundNotApplicableException error) { error.Data["beforeInput"] = true; throw; } // nothing expanded yet
            var opened = false;
            if (item == 0)
            {
                if (ComUia.ReadInt(expand, 5) != 0) throw Before("Option is absent from the list"); // nothing expanded yet
                BeforeAct();
                ComUia.CallSlot(expand, 3);
                opened = true;
                for (var i = 0; i < 10 && item == 0; i++)
                {
                    item = FindOption(container, option, allowAbsent: true);
                    if (item == 0) Thread.Sleep(50);
                }
                if (item == 0)
                {
                    Close(container, new Outcome(Uia, "ExpandCollapse", "sent"));
                    throw new InvalidOperationException("Option did not appear after expanding the list (list collapsed again); outcome unknown");
                }
            }
            try
            {
                Outcome selected;
                try { selected = WithPattern(item, ComUia.SelectionItemPatternId, Select, () => throw Before("Option is not selectable")); }
                catch (BackgroundNotApplicableException error) when (opened) { throw new InvalidOperationException(error.Message + " (list was expanded; outcome unknown)"); }
                selected = selected with { Mechanism = (opened ? "ExpandCollapse+" : "") + "SelectionItem.Select" };
                return combo || opened ? Close(container, selected) : selected;
            }
            finally { ComUia.Release(item); }
        }
        finally { ComUia.Release(expand); }
    }
    static nint FindOption(nint container, string option, bool allowAbsent)
    {
        var items = ComUia.Shared.SelectableItems(container);
        try
        {
            int index;
            try { index = Program.MatchOption(items.Select(i => (string?)i.Name).ToList(), option); }
            catch (BackgroundNotApplicableException error) when (error.Message.Contains("absent") && allowAbsent) { return 0; }
            var found = items[index].Element;
            items[index] = (0, "");
            return found;
        }
        finally { foreach (var (element, _) in items) ComUia.Release(element); }
    }
    // Collapsing read back after the choice; the choice already made is never replayed. A list left open: effect "sent".
    static Outcome Close(nint container, Outcome selected)
    {
        var expand = ComUia.Pattern(container, ComUia.ExpandCollapsePatternId);
        if (expand == 0) return selected;
        try
        {
            // Some lists close by themselves after the choice: a late Collapse may then fail.
            if (ComUia.ReadInt(expand, 5) is 1 or 2)
            {
                try { BeforeAct(); ComUia.CallSlot(expand, 4); } catch (System.Runtime.InteropServices.COMException) { }
            }
            var closed = WaitState(expand, s => s == 0) == 0;
            return selected with { Mechanism = selected.Mechanism + "+Collapse", Effect = closed ? selected.Effect : "sent" };
        }
        finally { ComUia.Release(expand); }
    }
}

// Managed helper side: a reused --com-worker child, one request at a time, bounded delay. The child is attached to a
// Job Object (KILL_ON_JOB_CLOSE): if the helper dies (delay exceeded on the Node side, which kills it), Windows kills the
// child too, which therefore can no longer act afterwards on an action already reported "unknown".
static partial class ComBridge
{
    static Process? child;
    public const int TimeoutMs = 10000; // bound of a call to the UI Automation patterns process
    static nint job;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateJobObjectW(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimit info, int size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
    const int JobObjectExtendedLimitInformation = 9;
    const uint KillOnJobClose = 0x2000;

    // Job of the helper process, created once; its handle is never closed before the helper ends (closing = the
    // children's death).
    static nint Job()
    {
        if (job != 0) return job;
        var created = CreateJobObjectW(0, null);
        if (created == 0) throw new BackgroundNotApplicableException($"Job Object unavailable (error {Marshal.GetLastWin32Error()}); COM worker not started");
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(created, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimit>()))
            throw new BackgroundNotApplicableException($"Job Object limit refused (error {Marshal.GetLastWin32Error()}); COM worker not started");
        return job = created;
    }

    public static Outcome Call(object request) => Call(request, TimeoutMs);
    public static Outcome Call(object request, int timeoutMs)
    {
        var reply = CallData(request, timeoutMs);
        string Text(string key) => reply.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return new(Text("technique"), Text("mechanism"), Text("effect"));
    }
    public static JsonElement CallData(object request, int timeoutMs = TimeoutMs)
    {
        // Start, Job Object and serialization come before any request is written: their failures consume no input. A
        // WriteLine/Flush error, on the other hand, may follow a partial send: never reclassified.
        var process = BeforeRequest(() => child is { HasExited: false } ? child : Start());
        var requestLine = BeforeRequest(() => EngineJson.Serialize(request));
        string? line;
        try
        {
            process.StandardInput.WriteLine(requestLine);
            process.StandardInput.Flush();
            var read = process.StandardOutput.ReadLineAsync();
            if (!read.Wait(timeoutMs)) { Stop(); throw new InvalidOperationException("COM UI Automation worker timed out; outcome unknown"); }
            line = read.Result;
        }
        catch (IOException error) { Stop(); throw new InvalidOperationException("COM UI Automation worker lost; outcome unknown: " + error.Message); }
        if (line == null) { Stop(); throw new InvalidOperationException("COM UI Automation worker exited; outcome unknown"); }
        using var json = JsonDocument.Parse(line);
        var reply = json.RootElement;
        string Text(string key) => reply.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (reply.GetProperty("ok").GetBoolean()) return reply.Clone();
        var message = Text("message");
        var before = reply.TryGetProperty("beforeInput", out var b) && b.ValueKind == JsonValueKind.True;
        throw ReplyError(Text("error"), message, before);
    }
    internal static T BeforeRequest<T>(Func<T> prepare)
    {
        try { return prepare(); }
        catch (Exception error) { throw ReplyError(error.GetType().Name, error.Message, true); }
    }
    // Phase attested by the worker: a failure to get/read the pattern before the call allows another path.
    // A firm refusal and STOP keep their nature. After the call, any failure is terminal, even if it carried an old
    // refusal marker or a preparatory mark from a sub-step.
    internal static Exception ReplyError(string error, string message, bool beforeInput)
    {
        if (error == nameof(EmergencyStopException)) return Stop(message, beforeInput);
        if (!beforeInput) return new InvalidOperationException(message + " (outcome unknown)");
        Exception refusal = error switch
        {
            nameof(GuardRailException) => new GuardRailException(message),
            nameof(ActionRefusedException) => new ActionRefusedException(message),
            _ => new BackgroundNotApplicableException(message),
        };
        refusal.Data["beforeInput"] = true;
        return refusal;
    }
    // Emergency stop of the COM process, with its "before any pattern" mark if it had one (FirstCall reads it).
    static EmergencyStopException Stop(string message, bool beforeInput)
    {
        var error = new EmergencyStopException(message);
        if (beforeInput) error.Data["beforeInput"] = true;
        return error;
    }

    static Process Start()
    {
        Stop();
        var start = new ProcessStartInfo(Program.HelperExecutable is { Length: > 0 } helper ? helper : throw new InvalidOperationException("Helper path unavailable"), "--com-worker")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        var jobHandle = Job();
        child = Process.Start(start) ?? throw new BackgroundNotApplicableException("COM UI Automation worker did not start");
        // Before any request: a child not bound to the helper could outlive it. Nothing has been sent.
        if (!AssignProcessToJobObject(jobHandle, child.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            Stop();
            throw new BackgroundNotApplicableException($"COM worker could not be bound to the helper (Job Object, error {error}); nothing was sent");
        }
        child.ErrorDataReceived += (_, _) => { }; // drains stderr without keeping it
        child.BeginErrorReadLine();
        return child;
    }
    static void Stop()
    {
        var current = child; child = null;
        try { if (current is { HasExited: false }) current.Kill(); } catch (InvalidOperationException) { }
        current?.Dispose();
    }
}
