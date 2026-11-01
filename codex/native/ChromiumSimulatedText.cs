using System.Diagnostics;
using System.Text;
using System.Text.Json;

static partial class ChromiumInput
{
    // Prefix accounting ported from Claude's methods.typed_prefix. Rich empty paragraphs may be represented by UIA
    // as trailing line breaks or one fewer interior break. Each actual Enter is nevertheless read back as a change
    // before any following character, so a missing/late Enter can never be mistaken for an empty paragraph.
    internal static string ComparableText(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00a0', ' ');
    static int? PlainPrefix(string rest, string want)
    {
        if (want.StartsWith(rest, StringComparison.Ordinal)) return rest.Length;
        var core = rest.TrimEnd('\n');
        if (!want.StartsWith(core, StringComparison.Ordinal)) return null;
        var tail = want[core.Length..];
        return core.Length + Math.Min(rest.Length - core.Length, tail.Length - tail.TrimStart('\n').Length);
    }
    static int? RichPrefix(string rest, string want, bool boundedLeft)
    {
        var core = rest.TrimEnd('\n'); var i = 0; var j = 0;
        while (j < core.Length)
        {
            if (i >= want.Length) return null;
            if (want[i] == '\n')
            {
                var run = 0; while (i + run < want.Length && want[i + run] == '\n') run++;
                if (i + run >= want.Length) return null;
                var shown = run >= 2 && (i > 0 || boundedLeft) ? run - 1 : run;
                if (j + shown > core.Length || core.AsSpan(j, shown).IndexOfAnyExcept('\n') >= 0) return null;
                i += run; j += shown;
            }
            else if (core[j] == want[i]) { i++; j++; }
            else return null;
        }
        var tail = want[i..];
        return i + Math.Min(rest.Length - core.Length, tail.Length - tail.TrimStart('\n').Length);
    }
    internal static int? ConfirmedTextPrefix(string value, string text, string before = "", bool append = false)
    {
        var got = ComparableText(value); var want = ComparableText(text); var bases = new List<string> { "" };
        if (append)
        {
            bases.Clear(); var old = ComparableText(before); var core = old.TrimEnd('\n');
            for (var end = old.Length; end >= core.Length; end--) bases.Add(old[..end]);
            if (string.IsNullOrWhiteSpace(old) && !bases.Contains("")) bases.Add("");
        }
        foreach (var basis in bases)
        {
            if (!got.StartsWith(basis, StringComparison.Ordinal)) continue;
            var rest = got[basis.Length..];
            var prefix = PlainPrefix(rest, want) ?? RichPrefix(rest, want, basis.Length > 0 && !basis.EndsWith('\n'));
            if (prefix != null) return prefix;
        }
        return null;
    }
    internal static List<string> SimulatedWriteUnits(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n'); var result = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
            if (i == text.Length || text[i] == '\n')
            {
                if (i > start) result.AddRange(Units(text[start..i]));
                if (i < text.Length) result.Add("\n"); start = i + 1;
            }
        return result;
    }
    readonly record struct SimulatedTextIdentity(string RuntimeId, int ProcessId, long NativeWindow, string Name, string Role, string AutomationId, string Framework);
    static SimulatedTextIdentity TextIdentity(Node node) => new(string.Join(",", node.RuntimeId), node.ProcessId, (long)node.NativeWindow,
        node.Name, node.Role, node.AutomationId, node.FrameworkId);
    static void PostObservedCharacters(string unit, Action provePacket, Action checkPresence, Action<char> post)
    {
        provePacket(); // Fresh focus, target and ancestors once immediately before this bounded packet.
        foreach (var rune in unit.EnumerateRunes())
        {
            checkPresence(); // STOP, user presence and HWND/PID/birth only; no UIA call per rune.
            foreach (var character in rune.ToString()) post(character);
        }
    }
    sealed record SimulatedTextIo(Action Check, Func<string?> Read, Action Prepare, Action Delete, Action<string> Characters,
        Action Enter, Func<bool> TimeLeft, Action Wait);
    static Outcome RunObservedText(string before, string rawText, bool replace, SimulatedTextIo io)
    {
        var text = rawText.Replace("\r\n", "\n").Replace('\r', '\n'); var sent = 0; var changed = false;
        string Read()
        {
            io.Check(); var value = io.Read() ?? throw new InvalidOperationException("Target value became unreadable; no further text sent");
            return ComparableText(value);
        }
        string WaitFor(Func<string, bool> ready)
        {
            while (true)
            {
                var value = Read(); if (ready(value)) return value;
                if (!io.TimeLeft()) throw new InvalidOperationException("Text effect was not read back within the native input budget; no replay");
                io.Wait();
            }
        }
        try
        {
            io.Check(); io.Prepare();
            if (replace && ComparableText(before).TrimEnd('\n').Length > 0)
            {
                io.Check(); changed = true; io.Delete();
                WaitFor(value => value.TrimEnd('\n').Length == 0);
            }
            foreach (var unit in SimulatedWriteUnits(text))
            {
                io.Check();
                var previous = WaitFor(value => ConfirmedTextPrefix(value, text[..sent], before, !replace) == sent);
                // Exactly one dispatch for each unit. A missing acknowledgement is terminal, never a retry.
                changed = true;
                if (unit == "\n") io.Enter(); else io.Characters(unit);
                sent += unit.Length;
                WaitFor(value => ConfirmedTextPrefix(value, text[..sent], before, !replace) == sent && (unit != "\n" || value != previous));
            }
            WaitFor(value => ConfirmedTextPrefix(value, text, before, !replace) == text.Length);
            return new("background:simulated-activation", $"WM_CHAR + Enter keys; {sent} UTF-16 units read back ({(replace ? "replaced" : "appended")}); no foreground activation", "applied");
        }
        catch (EmergencyStopException) when (changed)
        { throw new EmergencyStopException($"Emergency stop after simulated text input ({sent}/{text.Length}); outcome unknown, do not replay"); }
        catch (Exception error) when (changed && error is not (EmergencyStopException or AfterInputException))
        { throw new AfterInputException($"Simulated text stopped after input ({sent}/{text.Length}); outcome unknown, do not replay: {error.Message}", error); }
    }

    // Called only after Program.Execute resolved and guarded the target. The callback repeats that same target and
    // ancestor contract before each packet. It is deliberately not a direct MCP facade or an independent input path.
    public static Result WriteClaude(nint window, Node target, string text, bool replace, int? quietMs, Action started, Action revalidate)
    {
        var timing = new Dictionary<string, (double Milliseconds, int Count)>();
        void RecordTiming(string phase, long stamp)
        {
            var previous = timing.GetValueOrDefault(phase);
            timing[phase] = (previous.Milliseconds + Stopwatch.GetElapsedTime(stamp).TotalMilliseconds, previous.Count + 1);
        }
        T Timed<T>(string phase, Func<T> read)
        {
            var stamp = Stopwatch.GetTimestamp();
            try { return read(); }
            finally { RecordTiming(phase, stamp); }
        }
        if (target.FrameworkId != "Chrome" || target.Role is not ("edit" or "document") || target.Password || target.Disabled || target.RuntimeId.Length == 0)
            throw new ActionRefusedException("Simulated text needs the exact non-secret editable Chromium target");
        using var process = Process.GetProcessById((int)Win32.ProcessOf(window));
        if (GardeFous.BlockReason(process.ProcessName, Win32.ClassName(window), GardeFous.CurrentAgent) is { } blocked) throw new ActionRefusedException(blocked);
        uint? input = null; var windowPid = Win32.ProcessOf(window); var birth = Program.ProcessStartedUtc(windowPid); var windowClass = Win32.ClassName(window);
        var alreadyForeground = InForeground(window);
        if (birth == null) throw new ActionRefusedException("Window process identity unavailable; no simulated text sent");
        void CheckPresence()
        {
            EmergencyStop.ThrowIfStopped(); McpServer.NativeInteraction.CheckInputLease();
            if (!Win32.IsWindow(window) || Win32.ProcessOf(window) != windowPid || Program.ProcessStartedUtc(windowPid) != birth || Win32.ClassName(window) != windowClass)
                throw new ActionRefusedException("Chromium window/process changed; no further text sent");
            input ??= Win32.Probe().LastInput;
            if (Win32.Probe().LastInput != input || InForeground(window) != alreadyForeground || !Win32.IsWindowEnabled(window)) throw new JulienActiveException("The user resumed input, window activation changed or a modal dialog appeared; text stopped");
        }
        void Guard()
        {
            CheckPresence();
            var entered = Program.InputStarted;
            var stamp = Stopwatch.GetTimestamp();
            try { revalidate(); }
            finally { Program.InputStarted |= entered; RecordTiming("guard", stamp); }
        }
        // Bind the same live ancestor chain once. Every worker focus read revalidates this immutable
        // identity around RootWebArea publication; no cached focus bit or descendant is accepted.
        Guard();
        var controlBirth = Program.ProcessStartedUtc((uint)target.ProcessId)
            ?? throw new ActionRefusedException("Chromium field process birth unavailable; no text sent");
        static object FocusIdentity(Node node) => new { runtimeId = node.RuntimeId, processId = node.ProcessId, nativeWindow = (long)node.NativeWindow,
            name = node.Name, role = node.Role, automationId = node.AutomationId, framework = node.FrameworkId };
        object[] focusAncestors; SimulatedTextIdentity[] focusAncestorIdentities;
        using (var identity = ComUia.Shared.ReadIdentity(window, target.RuntimeId, target.NativeWindow, readAncestorText: false))
        {
            var current = identity.Nodes.LastOrDefault();
            if (current == null || !current.RuntimeId.SequenceEqual(target.RuntimeId)) throw new ActionRefusedException("Chromium focus identity disappeared; no text sent");
            focusAncestors = current.Ancestors.Select(index => FocusIdentity(identity.Nodes[index])).ToArray();
            focusAncestorIdentities = current.Ancestors.Select(index => TextIdentity(identity.Nodes[index])).ToArray();
        }
        Guard();
        object FocusRequest(string op) => new { op, windowId = (long)window, runtimeId = target.RuntimeId, nativeWindow = (long)target.NativeWindow,
            expectedWindowPid = windowPid, expectedWindowBirth = birth, expectedWindowClass = windowClass, expectedControlPid = target.ProcessId, expectedControlBirth = controlBirth,
            identity = FocusIdentity(target), ancestors = focusAncestors };
        void TakeBoundFocus(long deadline)
        {
            Guard();
            if (Call(FocusRequest("takeFocus"), deadline).Effect != "applied")
                throw new BackgroundNotApplicableException("Focus without activation not read back on the element: nothing was typed");
        }
        Outcome Perform(nint top, long deadline)
        {
            string? ReadBoundValue()
            {
                var entered = Program.InputStarted;
                try
                {
                    Timed("flush", () => Call(new { op = "flush", windowId = (long)window, runtimeId = Array.Empty<int>() }, deadline));
                    using var identity = Timed("identity", () => ComUia.Shared.ReadIdentity(window, target.RuntimeId, target.NativeWindow, readAncestorText: false));
                    var current = identity.Nodes.SingleOrDefault(n => n.RuntimeId.SequenceEqual(target.RuntimeId));
                    if (current == null || current.Password || current.Disabled || current.ValueTruncated || TextIdentity(current) != TextIdentity(target)
                        || !current.Ancestors.Select(index => TextIdentity(identity.Nodes[index])).SequenceEqual(focusAncestorIdentities))
                        throw new ActionRefusedException("The original text target identity changed or became unreadable");
                    var value = current.Value ?? (current.HasText ? ComUia.DocumentText(current.Element) : null);
                    if (value is { Length: > ComUia.MaxText }) throw new ActionRefusedException("The full original field value cannot be read; text was not assumed complete");
                    return value;
                }
                finally { Program.InputStarted |= entered; }
            }
            void CheckFocus()
            {
                Guard();
                var focus = Timed("focus", () => Call(FocusRequest("hasFocus"), deadline));
                if (focus.Effect != "applied") throw new ActionRefusedException("The exact Chromium field lost keyboard focus; no further text sent");
            }
            void Key(string key)
            {
                CheckFocus(); var parsed = Keys.ParseForWindow(top, key);
                Keys.PostCombo(top, top, parsed.Mods, parsed.Vk, parsed.Layout, () =>
                {
                    Guard();
                    // A reactive field can reveal a send/default control after the preceding characters.
                    // Refresh the bound MCP action at the actual Enter boundary, before its first message.
                    if (key == "enter")
                    {
                        McpServer.BeforeClaudeTextEnter();
                        CheckFocus(); // The fresh full context read may take time: recheck presence/focus before posting.
                    }
                    Program.InputStarted = true;
                });
            }
            var before = ReadBoundValue() ?? throw new BackgroundNotApplicableException("Text value unreadable before writing; nothing was typed");
            try { return RunObservedText(before, text, replace, new SimulatedTextIo(CheckPresence, ReadBoundValue,
                () => { if (replace) Key("ctrl+a"); else { CheckFocus(); Call(new { op = "caretToEnd", windowId = (long)window, runtimeId = target.RuntimeId, nativeWindow = (long)target.NativeWindow }, deadline); } },
                () => Key("delete"), unit => PostObservedCharacters(unit, CheckFocus, CheckPresence, character => Win32.Post(top, WM_CHAR, character, 1)),
                () => Key("enter"), () => Remaining(deadline) > 0, () => { var stamp = Stopwatch.GetTimestamp(); Thread.Sleep(20); RecordTiming("wait", stamp); })); }
            finally
            {
                if (Environment.GetEnvironmentVariable("NEXUS_WINDOWS_TEXT_TIMING") == "1")
                    try { Console.Error.WriteLine("[native-text-timing]" + JsonSerializer.Serialize(timing.ToDictionary(pair => pair.Key, pair => new { ms = Math.Round(pair.Value.Milliseconds, 3), calls = pair.Value.Count }))); }
                    catch { /* Optional numeric diagnostics do not change the input outcome. */ }
            }
        }
        if (!alreadyForeground) return Run(window, target, quietMs, replace ? "replace multiline text" : "append multiline text", () => { Guard(); started(); }, Perform,
            (focusWindow, focusNode, deadline) => TakeBoundFocus(deadline), TextBudget);
        using var token = ForegroundToken.Acquire("Chromium text in already-active window");
        JulienPause.ThrowIfActive(quietMs); EmergencyStop.ThrowIfStopped(); Guard(); started();
        var until = Environment.TickCount64 + TextBudget;
        TakeBoundFocus(until);
        var outcome = Perform(Top(window), until);
        return new(outcome with { Technique = "background:messages", Mechanism = outcome.Mechanism + " (window was already active; no activation requested)" }, false, null);
    }

    internal static object SimulatedPacketGuardSelfTest()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Simulated packet guard selftest: " + message); }
        var packetProofs = 0; var presenceChecks = 0; var posted = 0; var proofDuringRune = 0; var packets = 0;
        foreach (var unit in SimulatedWriteUnits(new string('n', 2000)))
        {
            var insidePacketRunes = false; packets++;
            PostObservedCharacters(unit, () => { packetProofs++; if (insidePacketRunes) proofDuringRune++; },
                () => { insidePacketRunes = true; presenceChecks++; }, _ => posted++);
        }
        Check(packets == 250 && packetProofs == 250, "2000 characters have exactly 250 fresh packet proofs");
        Check(presenceChecks == 2000 && posted == 2000, "presence is checked before every real rune");
        Check(proofDuringRune == 0, "no UIA packet proof is requested from inside the rune loop");
        var unicode = new List<char>(); var unicodeProofs = 0; var unicodePresence = 0;
        PostObservedCharacters("😀", () => unicodeProofs++, () => unicodePresence++, character => unicode.Add(character));
        Check(new string(unicode.ToArray()) == "😀" && unicodeProofs == 1 && unicodePresence == 1 && unicode.Count == 2, "one guarded Unicode rune preserves both UTF-16 halves");
        var sent = 0; var quickChecks = 0;
        try { PostObservedCharacters("refused", () => throw new ActionRefusedException("focus changed"), () => quickChecks++, _ => sent++); }
        catch (ActionRefusedException) { Check(sent == 0 && quickChecks == 0, "a lost focus proof sends no packet"); }
        try { PostObservedCharacters("abcd", () => { }, () => { if (++quickChecks == 3) throw new JulienActiveException("user resumed"); }, _ => sent++); }
        catch (JulienActiveException) { Check(sent == 2 && quickChecks == 3, "user presence interrupts the packet before the next rune without replay"); }
        return new { ok = true, checks, packets, packetProofs, presenceChecks, proofDuringRune, utf16Posted = posted, gui = false, inputSent = false };
    }

    static object SimulatedTextSelfTest(JsonElement test)
    {
        static string S(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
        if (test.TryGetProperty("prefix", out var prefix)) return new { confirmed = ConfirmedTextPrefix(S(prefix, "value"), S(prefix, "text"), S(prefix, "before"), prefix.TryGetProperty("append", out var append) && append.GetBoolean()) };
        if (test.TryGetProperty("units", out var units)) return SimulatedWriteUnits(units.GetString()!);
        var value = S(test, "before"); var text = S(test, "text"); var replace = test.TryGetProperty("replace", out var r) && r.GetBoolean();
        var events = new List<string>(); var checks = 0; var waits = 0; var inputPackets = 0;
        var loseAfter = test.TryGetProperty("loseAfter", out var lost) ? lost.GetInt32() : int.MaxValue;
        var noAck = test.TryGetProperty("noAck", out var no) && no.GetBoolean();
        var changeAfterEnter = test.TryGetProperty("suppressEnter", out var suppress) && suppress.GetBoolean();
        var revealSend = test.TryGetProperty("revealSendAfter", out var reveal) ? reveal.GetString() : null;
        void Check() { checks++; if (inputPackets >= loseAfter) throw new ActionRefusedException("Fixture focus changed"); }
        var io = new SimulatedTextIo(Check, () => value, () => events.Add(replace ? "select-all" : "caret-end"),
            () => { events.Add("delete"); inputPackets++; if (!noAck) value = ""; },
            unit => { events.Add("chars:" + unit); inputPackets++; if (!noAck) value += unit; },
            () =>
            {
                if (revealSend != null && value.Contains(revealSend, StringComparison.Ordinal))
                {
                    events.Add("enter-refused:outward-context-changed");
                    throw new ActionRefusedException("A send control appeared after the text prefix; no exact action accord, no Enter sent");
                }
                events.Add("enter"); inputPackets++; if (!noAck && !changeAfterEnter) value += "\n";
            },
            () => waits < 3, () => waits++);
        try { var result = RunObservedText(value, text, replace, io); return new { ok = true, result, value, events, checks, waits, inputPackets }; }
        catch (Exception error) { return new { ok = false, error = error.GetType().Name, message = error.Message, value, events, checks, waits, inputPackets }; }
    }
}

static partial class Program
{
    // WM_CHAR is delivered to the already-proved DOM focus, not to screen coordinates. A growing text area may
    // change its rectangle legitimately; process/window, target runtime, selector and every ancestor stay binding.
    internal static void RevalidateClaudeTextTarget(nint window, Node original, JsonElement request)
    {
        using var tree = ComUia.Shared.ReadIdentity(window, original.RuntimeId, original.NativeWindow, readAncestorText: false);
        var target = tree.Nodes.SingleOrDefault(n => n.RuntimeId.SequenceEqual(original.RuntimeId));
        if (target == null || target.Disabled || target.Password || target.Offscreen || target.FrameworkId != "Chrome" || target.ProcessId != original.ProcessId
            || target.NativeWindow != original.NativeWindow || target.Role != original.Role || target.Name != original.Name || target.AutomationId != original.AutomationId)
            throw new ActionRefusedException("The observed Chromium field identity changed; no further text sent");
        if (request.TryGetProperty("selector", out var selector) && !Matches(target, selector)) throw new ActionRefusedException("Chromium text selector changed; no further text sent");
        if (request.TryGetProperty("ancestry", out var ancestors) && ancestors.ValueKind == JsonValueKind.Array && !SameAncestry(tree.Nodes, target, ancestors.EnumerateArray().ToList()))
            throw new ActionRefusedException("Chromium text target ancestry changed; no further text sent");
    }
}
