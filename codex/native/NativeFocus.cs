using System.Text.Json;

static partial class Program
{
    // Focus is deliberately separate from click: a button must never be invoked merely to focus it.
    // UIA SetFocus may activate an application, hence the real foreground lease, fresh identity and give-back.
    static object NativeFocus(nint window, JsonElement request, Func<Node> resolveFresh)
    {
        InputStarted = false;
        if (Text(request, "mode", "background") != "foreground")
            throw new ActionRefusedException("Native focus requires foreground:true; no click or input was sent");
        var pid = Win32.ProcessOf(window);
        var birth = ProcessStartedUtc(pid) ?? throw new ActionRefusedException("Window process birth unavailable; no focus sent");
        using var token = ForegroundToken.Acquire("Native UIA focus");
        int? quiet = request.TryGetProperty("quietMs", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null;
        JulienPause.ThrowIfActive(quiet); EmergencyStop.ThrowIfStopped(); NativeMcpGuard.CheckInterruption();
        if (Win32.ProcessOf(window) != pid || ProcessStartedUtc(pid) != birth)
            throw new ActionRefusedException("Window identity changed while waiting for focus; nothing was sent");
        var target = resolveFresh();
        if (target.RuntimeId.Length == 0 || ComUia.IsSynthetic(target.RuntimeId))
            throw new ActionRefusedException("This control has no UIA identity for focus; no click was sent");
        var controlBirth = ProcessStartedUtc((uint)target.ProcessId)
            ?? throw new ActionRefusedException("Control process birth unavailable; no focus sent");
        var before = Win32.Probe(); var returned = "none"; var taken = false; Outcome outcome;
        try
        {
            InputStarted = true;
            try
            {
                outcome = ComBridge.Call(new { op = "setFocus", windowId = (long)window, runtimeId = target.RuntimeId,
                    nativeWindow = (long)target.NativeWindow, expectedWindowPid = pid, expectedWindowBirth = birth,
                    expectedControlPid = target.ProcessId, expectedControlBirth = controlBirth,
                    expectedName = target.Name, expectedRole = target.Role, expectedAutomationId = target.AutomationId });
            }
            catch (Exception error) when (error.Data["beforeInput"] is true) { InputStarted = false; throw; }
            Thread.Sleep(30); // A provider can post its activation after SetFocus returns.
            var during = Win32.GetForegroundWindow();
            taken = during != before.Foreground && during != 0 && Win32.ProcessOf(during) == pid;
        }
        finally
        {
            returned = Win32.ProcessOf(window) == pid && ProcessStartedUtc(pid) == birth
                ? ForegroundReturn.GiveBack(before.Foreground, window, before.LastInput) : "target-changed";
        }
        var after = Win32.Probe();
        return new { ok = true, technique = "foreground:uia", mechanism = outcome.Mechanism, effect = outcome.Effect,
            foregroundTaken = taken,
            foregroundReturned = returned, cursorMoved = before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            inputDuringAction = before.LastInput != after.LastInput,
            foreground = new { before = (long)before.Foreground, after = (long)after.Foreground } };
    }
}

static partial class ComWorker
{
    static Outcome SetBoundFocus(nint element, nint window, JsonElement request)
    {
        RequireWindowIdentity(window, request);
        var pid = request.GetProperty("expectedControlPid").GetInt32();
        if (ComUia.ProcessIdOf(element) != pid || Program.ProcessStartedUtc((uint)pid) != request.GetProperty("expectedControlBirth").GetString()
            || !ComUia.FocusIdentityMatches(element, request))
            throw Before("Focus target identity changed; no focus was sent");
        BeforeAct();
        ComUia.SetFocus(element);
        return new("foreground:uia", "SetFocus (never Invoke)", ComUia.HasKeyboardFocus(element) ? "applied" : "sent");
    }
    static void RequireWindowIdentity(nint window, JsonElement request)
    {
        var pid = request.GetProperty("expectedWindowPid").GetUInt32();
        if (!Win32.IsWindow(window) || Win32.ProcessOf(window) != pid
            || Program.ProcessStartedUtc(pid) != request.GetProperty("expectedWindowBirth").GetString())
            throw Before("Window identity changed; nothing was sent");
    }
}

unsafe sealed partial class ComUia
{
    internal static bool FocusIdentityMatches(nint element, JsonElement request) =>
        CurrentName(element) == request.GetProperty("expectedName").GetString()
        && Role(ControlType(element)) == request.GetProperty("expectedRole").GetString()
        && CurrentAutomationId(element) == request.GetProperty("expectedAutomationId").GetString();
}
