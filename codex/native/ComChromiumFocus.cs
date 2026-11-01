using System.Diagnostics;
using System.Text.Json;

static partial class ComWorker
{
    // Historical Claude _take_focus_once -> _refresh: Chromium publishes pending AX changes before the
    // current focus getter is trusted. Publication never substitutes for a positive exact-target getter.
    static bool ReadPublishedFocus(Action validate, Action publish, Func<bool> readFocus)
    {
        validate(); publish(); validate(); return readFocus();
    }
    static bool WaitPublishedFocus(Action takeOnce, Func<bool> readPublished, Func<bool> timeLeft, Action wait)
    {
        takeOnce();
        while (timeLeft()) { if (readPublished()) return true; wait(); }
        return false;
    }
    static bool ChromiumFocusIdentityMatches(Node node, JsonElement expected) =>
        node.RuntimeId.SequenceEqual(expected.GetProperty("runtimeId").EnumerateArray().Select(value => value.GetInt32()))
        && node.ProcessId == expected.GetProperty("processId").GetInt32() && (long)node.NativeWindow == expected.GetProperty("nativeWindow").GetInt64()
        && node.Name == expected.GetProperty("name").GetString() && node.Role == expected.GetProperty("role").GetString()
        && node.AutomationId == expected.GetProperty("automationId").GetString() && node.FrameworkId == expected.GetProperty("framework").GetString();

    static void RequireBoundChromiumWindow(nint window, JsonElement request)
    {
        RequireWindowIdentity(window, request);
        if (Win32.ClassName(window) != request.GetProperty("expectedWindowClass").GetString()) throw Before("Chromium window class changed; no focus sent");
        using var process = Process.GetProcessById((int)Win32.ProcessOf(window));
        if (GardeFous.BlockReason(process.ProcessName, Win32.ClassName(window), GardeFous.CurrentAgent) is { } blocked) throw new ActionRefusedException(blocked);
    }

    static Outcome BoundChromiumFocus(nint element, nint window, JsonElement request, bool take)
    {
        RequireBoundChromiumWindow(window, request);
        var runtimeId = request.GetProperty("runtimeId").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var hint = (nint)request.GetProperty("nativeWindow").GetInt64();
        var expected = request.GetProperty("identity");
        var ancestors = request.GetProperty("ancestors").EnumerateArray().ToArray();
        void Validate()
        {
            EmergencyStop.ThrowIfStopped(); RequireBoundChromiumWindow(window, request);
            var pid = request.GetProperty("expectedControlPid").GetInt32();
            if (ComUia.ProcessIdOf(element) != pid || Program.ProcessStartedUtc((uint)pid) != request.GetProperty("expectedControlBirth").GetString())
                throw new ActionRefusedException("Chromium focus control process changed; no text sent");
            // Ancestor identities require their current names/roles/runtime IDs, not a copy of the whole page.
            // The exact target keeps its complete text and truncation checks.
            using var tree = ComUia.Shared.ReadIdentity(window, runtimeId, hint, readAncestorText: false);
            var target = tree.Nodes.LastOrDefault();
            if (target == null || !ComUia.Shared.SameElement(element, target.Element) || !ChromiumFocusIdentityMatches(target, expected)
                || target.FrameworkId != "Chrome" || target.Role is not ("edit" or "document") || target.Password || target.Disabled || target.ValueTruncated
                || target.Ancestors.Length != ancestors.Length)
                throw new ActionRefusedException("The exact Chromium focus target changed or became unreadable; no text sent");
            for (var i = 0; i < ancestors.Length; i++)
                if (!ChromiumFocusIdentityMatches(tree.Nodes[target.Ancestors[i]], ancestors[i]))
                    throw new ActionRefusedException("The Chromium focus target ancestry changed; no text sent");
            RequireWindowIdentity(window, request);
        }
        bool Read() => ReadPublishedFocus(Validate, () =>
        {
            BeforeAct();
            if (ComUia.Shared.Flush(window) == "stopped") throw new EmergencyStopException("Emergency stop before Chromium focus publication");
        }, () => ComUia.HasKeyboardFocus(element));
        if (!take) return new(Uia, "HasKeyboardFocus (bound RootWebArea publication)", Read() ? "applied" : "none");
        var clock = new Stopwatch();
        var focused = WaitPublishedFocus(() => { Validate(); BeforeAct(); ComUia.TakeFocus(element); clock.Start(); },
            Read, () => clock.ElapsedMilliseconds < 500, () => Thread.Sleep(20));
        return new(Uia, "accSelect(SELFLAG_TAKEFOCUS), published exact-target focus", focused ? "applied" : "sent");
    }

    internal static object ChromiumFocusSelfTest()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Chromium focus selftest: " + message); }
        var events = new List<string>(); var focused = false; var takes = 0; var publications = 0; var reads = 0; var waits = 0;
        bool Read() => ReadPublishedFocus(() => events.Add("identity"), () => { publications++; events.Add("publish"); focused = true; }, () => { reads++; events.Add("focus"); return focused; });
        var result = WaitPublishedFocus(() => { takes++; events.Add("take"); }, Read, () => waits < 3, () => waits++);
        Check(result && takes == 1 && publications == 1 && reads == 1 && waits == 0, "one accSelect, publication, then actual focus proof");
        Check(events.SequenceEqual(new[] { "take", "identity", "publish", "identity", "focus" }), "identity is fresh on both sides of publication");
        Check(focused, "publication can expose the pending real focus state");
        takes = publications = reads = waits = 0;
        result = WaitPublishedFocus(() => takes++, () => ReadPublishedFocus(() => { }, () => publications++, () => { reads++; return false; }), () => waits < 3, () => waits++);
        Check(!result && takes == 1 && publications == 3 && reads == 3 && waits == 3, "negative exact-target focus never becomes success and accSelect is never replayed");
        Check(!result, "a focused descendant does not stand in for the original field");
        var validations = 0; publications = reads = 0;
        try { ReadPublishedFocus(() => throw new ActionRefusedException("identity changed"), () => publications++, () => { reads++; return true; }); }
        catch (ActionRefusedException) { Check(publications == 0 && reads == 0, "identity refusal precedes publication and focus read"); }
        Acted = true;
        try { ReadPublishedFocus(() => { if (++validations == 2) throw new ActionRefusedException("ancestry changed"); }, () => publications++, () => { reads++; return true; }); }
        catch (ActionRefusedException error) { Check(publications == 1 && reads == 0, "identity or ancestry replacement after publication refuses even a positive getter"); Check(!Failure(error).beforeInput, "publication after accSelect remains after-input, never replayable"); }
        publications = reads = 0;
        try { ReadPublishedFocus(() => throw new EmergencyStopException("STOP"), () => publications++, () => { reads++; return true; }); }
        catch (EmergencyStopException) { Check(publications == 0 && reads == 0, "STOP prevents publication and focus acceptance"); }
        takes = reads = waits = 0;
        try { WaitPublishedFocus(() => { takes++; throw new IOException("lost accSelect reply"); }, () => { reads++; return true; }, () => waits < 3, () => waits++); }
        catch (IOException error) { Check(takes == 1 && reads == 0 && waits == 0, "a lost accSelect reply is terminal without retry"); Check(!Failure(error).beforeInput, "lost reply retains its after-input classification"); }
        Acted = false;
        return new { ok = true, checks, gui = false, inputSent = false };
    }
}
