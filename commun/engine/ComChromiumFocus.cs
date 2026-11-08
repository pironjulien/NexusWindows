using System.Diagnostics;
using System.Text.Json;

static partial class ComWorker
{
    // Historical Claude _take_focus_once -> _refresh: Chromium publishes pending AX changes before the
    // current focus getter is trusted. Publication never substitutes for a positive exact-target getter.
    internal static bool ReadPublishedFocus(Action validate, Action publish, Func<bool> readFocus)
    {
        validate(); publish(); validate(); return readFocus();
    }
    internal static bool WaitPublishedFocus(Action takeOnce, Func<bool> readPublished, Func<bool> timeLeft, Action wait)
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
                || target.FrameworkId != "Chrome" || !ChromiumInput.TextTarget(target) || target.Password || target.Disabled || target.ValueTruncated
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
}
