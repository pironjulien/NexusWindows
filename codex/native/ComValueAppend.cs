using System.Text.Json;

static partial class Program
{
    // Claude's UIA append reads the value in the action worker, never from the preceding observation. Chromium
    // keeps its event-producing text route; the fixed policies of Codex and Antigravity are unchanged.
    static Outcome UiaAppendValue(nint window, Node target, List<Node> nodes, string text)
    {
        InputStarted = false;
        if (GardeFous.CurrentAgent != "claude" || target.FrameworkId == "Chrome" || target.Password || target.Disabled || target.ValueTruncated)
            throw new ActionRefusedException("The exact non-secret writable UIA field is required for Claude append");
        var pid = Win32.ProcessOf(window);
        var birth = ProcessStartedUtc(pid) ?? throw new ActionRefusedException("Window process birth unavailable; no text sent");
        var controlBirth = ProcessStartedUtc((uint)target.ProcessId) ?? throw new ActionRefusedException("Field process birth unavailable; no text sent");
        static object Identity(Node node) => new { runtimeId = node.RuntimeId, processId = node.ProcessId, nativeWindow = (long)node.NativeWindow,
            name = node.Name, role = node.Role, automationId = node.AutomationId, framework = node.FrameworkId };
        var ancestors = target.Ancestors.Select(i => Identity(nodes[i])).ToArray();
        InputStarted = true; // A lost worker reply can follow its single SetValue; only its beforeInput proof resets this.
        return FirstCall(() => ComBridge.Call(new { op = "appendValue", windowId = (long)window, runtimeId = target.RuntimeId,
            nativeWindow = (long)target.NativeWindow, expectedWindowPid = pid, expectedWindowBirth = birth,
            expectedControlPid = target.ProcessId, expectedControlBirth = controlBirth, identity = Identity(target), ancestors, text }));
    }
}

unsafe sealed partial class ComUia
{
    internal static string CurrentAppendValue(nint element, nint pattern) => ComWorker.AppendableValue(
        ReadInt(element, 35) != 0, ReadInt(element, 28) != 0, ReadInt(pattern, 5) != 0,
        // Do not read a value until the live password/enabled/readonly checks have passed.
        () => Value(pattern));
}

static partial class ComWorker
{
    internal static string AppendableValue(bool password, bool enabled, bool readOnly, Func<string?> read)
    {
        if (password) throw new ActionRefusedException("Password values are refused; no text sent");
        if (!enabled) throw new ActionRefusedException("The field became disabled; no text sent");
        if (readOnly) throw new ActionRefusedException("The field is read-only; no text sent");
        var value = read() ?? throw new ActionRefusedException("The complete current value cannot be read; no text sent");
        if (value.Length > ComUia.MaxText) throw new ActionRefusedException("The current field value exceeds the complete observation bound; no text sent");
        return value;
    }
    static bool AppendIdentityMatches(Node node, JsonElement expected) =>
        node.RuntimeId.SequenceEqual(expected.GetProperty("runtimeId").EnumerateArray().Select(v => v.GetInt32()))
        && node.ProcessId == expected.GetProperty("processId").GetInt32() && (long)node.NativeWindow == expected.GetProperty("nativeWindow").GetInt64()
        && node.Name == expected.GetProperty("name").GetString() && node.Role == expected.GetProperty("role").GetString()
        && node.AutomationId == expected.GetProperty("automationId").GetString() && node.FrameworkId == expected.GetProperty("framework").GetString();
    static Outcome AppendValue(nint window, nint element, JsonElement request, string text)
    {
        var runtimeId = request.GetProperty("runtimeId").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        void Validate()
        {
            RequireWindowIdentity(window, request);
            var pid = request.GetProperty("expectedControlPid").GetInt32();
            if (ComUia.ProcessIdOf(element) != pid || Program.ProcessStartedUtc((uint)pid) != request.GetProperty("expectedControlBirth").GetString())
                throw new ActionRefusedException("Field process identity changed; no text sent");
            using var tree = ComUia.Shared.ReadIdentity(window, runtimeId, (nint)request.GetProperty("nativeWindow").GetInt64());
            var target = tree.Nodes.LastOrDefault();
            var ancestors = request.GetProperty("ancestors").EnumerateArray().ToArray();
            if (target == null || !ComUia.Shared.SameElement(element, target.Element) || !AppendIdentityMatches(target, request.GetProperty("identity"))
                || target.FrameworkId == "Chrome" || target.Password || target.Disabled || target.ValueTruncated
                || target.Role is not ("edit" or "document" or "combobox") || target.Ancestors.Length != ancestors.Length)
                throw new ActionRefusedException("The original UIA append target changed or became unreadable; no text sent");
            for (var i = 0; i < ancestors.Length; i++)
                if (!AppendIdentityMatches(tree.Nodes[target.Ancestors[i]], ancestors[i]))
                    throw new ActionRefusedException("The original UIA append ancestry changed; no text sent");
        }
        Validate();
        return WithPattern(element, ComUia.ValuePatternId, pattern => AppendValueOnce(text,
            () => ComUia.CurrentAppendValue(element, pattern), Validate, BeforeAct,
            value => ComUia.SetValue(pattern, value), () => ComUia.Value(pattern)),
            () => throw Before("No writable ValuePattern for a semantic append"));
    }
    static Outcome AppendValueOnce(string text, Func<string> read, Action validate, Action beforeAct, Action<string> write, Func<string?> readAfter)
    {
        validate(); var before = read();
        if (text.Length > ComUia.MaxText - before.Length) throw new ActionRefusedException("Appended value would exceed the complete observation bound; no text sent");
        var desired = before + text;
        validate();
        if (!string.Equals(read(), before, StringComparison.Ordinal)) throw new ActionRefusedException("The current value changed while preparing append; no text sent");
        beforeAct(); write(desired); // Exactly one SetValue. A failed/lost acknowledgement is terminal, never replayed.
        return new(Uia, "Value.SetValue (fresh current value + appended text)", EffectAfterCall(readAfter() == desired));
    }
    internal static object AppendValueSelfTest()
    {
        var checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException("UIA append selftest: " + label); }
        void Refused(Action action) { try { action(); throw new Exception("Expected a hard refusal"); } catch (ActionRefusedException) { checks++; } }
        var value = "fresh current"; int writes = 0, reads = 0, validations = 0;
        var result = AppendValueOnce(" appended 😀\nline", () => { reads++; return value; }, () => validations++, () => Acted = true,
            desired => { writes++; value = desired; }, () => value);
        Check(value == "fresh current appended 😀\nline" && writes == 1 && reads == 2 && validations == 2 && result.Effect == "applied", "fresh Unicode/multiline append uses one SetValue and two live reads");
        var secretReads = 0;
        string? Read() { secretReads++; return "private value"; }
        Refused(() => AppendableValue(true, true, false, Read)); Refused(() => AppendableValue(false, false, false, Read)); Refused(() => AppendableValue(false, true, true, Read));
        Check(secretReads == 0, "secret, disabled and readonly refusals precede value reads");
        Refused(() => AppendableValue(false, true, false, () => null));
        Refused(() => AppendableValue(false, true, false, () => new string('x', ComUia.MaxText + 1)));
        Acted = false; writes = 0; reads = 0;
        Refused(() => AppendValueOnce("x", () => ++reads == 1 ? "initial" : "changed", () => { }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "a concurrent value change sends nothing");
        Refused(() => AppendValueOnce("x", () => new string('x', ComUia.MaxText), () => { }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "a value that would be truncated is refused before input");
        validations = 0;
        Refused(() => AppendValueOnce("x", () => "base", () => { if (++validations == 2) throw new ActionRefusedException("identity changed"); }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "target/ancestor identity change sends nothing");
        try { AppendValueOnce("x", () => "base", () => { }, () => Acted = true, _ => { writes++; throw new IOException("reply lost"); }, () => throw new Exception("must not read after failed write")); }
        catch (IOException error) { var failure = Failure(error); Check(!failure.beforeInput && writes == 1, "a lost SetValue reply remains after-input with one write and no replay"); }
        Acted = false; writes = 0;
        result = AppendValueOnce("x", () => "base", () => { }, () => Acted = true, _ => writes++, () => "base");
        Check(result.Effect == "sent" && writes == 1, "late UIA publication is not absence of input and never retries");
        Acted = false;
        return new { ok = true, checks, gui = false };
    }
}
