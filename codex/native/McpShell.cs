using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

static partial class McpServer
{
    internal sealed record ShellActPlan(string Name, string Shortcut, string Process, string Class, string RelativePath, string? Package);
    static readonly ShellActPlan QuickSettingsPlan = new("quick settings", "win+a", "shellhost", "ControlCenterWindow", @"System32\ShellHost.exe", null);
    static readonly ShellActPlan NotificationsPlan = new("notifications", "win+n", "shellexperiencehost", "Windows.UI.Core.CoreWindow", @"SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe", "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy");
    static readonly ShellActPlan StartPlan = new("start", "win", "startmenuexperiencehost", "Windows.UI.Core.CoreWindow", @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe", "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy");
    static readonly ShellActPlan SearchPlan = new("search", "", "searchhost", "Windows.UI.Core.CoreWindow", @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy");

    internal static ShellActPlan? PlanShellAct(JsonObject args)
    {
        var name = Str(args, "window", Str(args, "hwnd")).Trim().ToLowerInvariant();
        var plan = name switch
        {
            "quick settings" or "réglages rapides" => QuickSettingsPlan,
            "notifications" or "notification center" or "centre de notifications" => NotificationsPlan,
            "start" or "démarrer" => StartPlan,
            _ => null
        };
        if (plan != null && !RoutineBoolean(args, "foreground", false))
            throw new ActionRefusedException($"\"{name}\" is a Windows panel that only exists in the foreground: use act(window=\"{plan.Name}\", foreground=true, steps=[…]); no input sent");
        return plan;
    }

    // This is the single act boundary of the three profiles: the final observation is made before releasing the foreground lease.
    static JsonObject RunShellAct(SessionState state, JsonObject args, bool modern)
    {
        var plan = PlanShellAct(args); // A background panel request fails before a lease or SendInput.
        if (plan != null) return RunPanelAct(state, args, modern, plan);
        if (!RoutineBoolean(args, "foreground", false))
            return FinishLegacyAct(state, args, ActNative(state, args, modern));
        var operation = Str(RoutineCompatibilityArgs(state.Agent, args), "operation", "run");
        if (operation is not ("run" or "continue") || state.CurrentObservation == null || DateTimeOffset.UtcNow - state.ObservedAt > TimeSpan.FromSeconds(60))
            return FinishLegacyAct(state, args, ActNative(state, args, modern));
        var prior = state.BoundWindow;
        var named = ResolveWindow(state, args, modern);
        if (prior is { } old && old.Id != named.Id)
        {
            state.CurrentObservation = null; state.CurrentObservationId = null; state.Pending = null;
            throw new ActionRefusedException("The named window differs from the last look; observe it before acting");
        }
        using var scope = NativeInteraction.BeginLegacy(state);
        var result = FinishLegacyAct(state, args, ActNative(state, args, modern));
        result["structuredContent"]!["foregroundReturned"] = scope.ReturnForeground();
        return result;
    }

    static JsonObject RunPanelAct(SessionState state, JsonObject args, bool modern, ShellActPlan plan)
    {
        using var scope = NativeInteraction.BeginLegacyShell(state);
        var opened = false;
        JsonObject? result = null;
        try
        {
            scope.Check();
            var panel = FindShellPanel(plan); // Re-read after the user's pause: never toggle a panel they opened meanwhile.
            if (panel != null && !ShellPanelOwnsForeground(plan, panel.Value.Id))
                throw new ActionRefusedException("The visible Windows panel does not own the foreground; no shortcut or activation sent");
            if (panel == null)
            {
                SendShellShortcut(scope, plan.Shortcut); opened = true;
                panel = AwaitShellPanel(scope, plan, 3000);
                if (panel == null)
                {
                    // A transmitted shortcut is never replayed. Watch for the same late panel and close only its identity.
                    panel = AwaitShellPanel(scope, plan, 1000);
                    if (panel != null) scope.RememberShellWindows(ObservedPanelWindows(plan, panel.Value.Id));
                    throw new AfterInputException($"Windows panel \"{plan.Name}\" did not open within 3 s; shortcut was sent once, do not replay automatically", new TimeoutException("Panel opening timed out"));
                }
            }
            var owned = ObservedPanelWindows(plan, panel.Value.Id);
            scope.BindLegacyShell(panel.Value, owned);
            var call = args.DeepClone().AsObject(); call["window"] = panel.Value.Id; call.Remove("hwnd");
            PrepareShellObservation(state, panel.Value.Id);
            result = FinishLegacyAct(state, call, ActNative(state, call, modern));
            return result;
        }
        finally
        {
            var returned = opened ? CloseShellPanel(scope) : scope.ReturnForeground();
            if (result?["structuredContent"] is JsonObject data) data["foregroundReturned"] = returned;
        }
    }

    static (long Id, string App, string Title, long Pid)? ResolveBoundShellWindow(SessionState state, JsonNode? window)
    {
        if (state.Interaction is not { } scope || state.BoundWindow is not { } bound
            || !long.TryParse(window?.ToString(), out var requested) || requested != bound.Id || !scope.OwnsShellWindow((nint)bound.Id)) return null;
        scope.Check();
        return bound;
    }

    static void PrepareShellObservation(SessionState state, long window)
    {
        // Fresh semantic names may be resolved by the first act. No index or image is silently marked as shown.
        var snapshot = InspectNative(state); var memory = LookMemory(state, window, snapshot);
        PublishNativeIndices(state, snapshot, memory);
        state.CurrentObservation = JsonSerializer.SerializeToElement(snapshot, JsonOut);
        state.CurrentObservationId = state.NextObservationId(); state.ObservedAt = DateTimeOffset.UtcNow;
        if (state.Image?.Window != window || memory.Image == null) state.Image = null;
    }

    internal static bool OfficialShellIdentity(ShellActPlan plan, string path, string app, string windowsRoot)
    {
        try
        {
            var expected = Path.GetFullPath(Path.Combine(windowsRoot, plan.RelativePath));
            if (!Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            // Packaged shells carry their system package family; unpackaged ShellHost uses its exact protected binary.
            return app.Equals("process:" + expected, StringComparison.OrdinalIgnoreCase)
                || plan.Package != null && app.StartsWith(plan.Package + "!", StringComparison.OrdinalIgnoreCase) && app.Length > plan.Package.Length + 1;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    static (long Id, string App, string Title, long Pid)? ReadShellWindow(ShellActPlan plan, nint hwnd, bool requirePanel)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd)) return null;
        if (requirePanel && (Win32.ClassName(hwnd) != plan.Class || !Win32.GetWindowRect(hwnd, out var rect) || rect.Height < 400)) return null;
        var identity = NativeInteraction.ReadIdentity(hwnd);
        try
        {
            using var process = Process.GetProcessById((int)identity.Pid);
            if (!process.ProcessName.Equals(plan.Process, StringComparison.OrdinalIgnoreCase)) return null;
            var path = process.MainModule?.FileName; if (path == null) return null;
            var app = Program.AppId(process);
            if (!OfficialShellIdentity(plan, path, app, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return null;
            if (!NativeInteraction.SameIdentity(identity, NativeInteraction.ReadIdentity(hwnd))) throw new ActionRefusedException("Shell identity changed while reading; nothing else sent");
            return ((long)hwnd, app, Win32.Title(hwnd), identity.Pid);
        }
        catch (Exception error) when (error is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException && error is not ActionRefusedException) { return null; }
    }
    static (long Id, string App, string Title, long Pid)? FindShellPanel(ShellActPlan plan)
    {
        var candidates = new List<(long Id, string App, string Title, long Pid)>();
        var seen = new HashSet<nint>(); var hwnd = (nint)0;
        // EnumWindows omits immersive shell bands. FindWindowEx enumerates their actual top-level HWNDs too.
        while ((hwnd = Win32.FindWindowExW(0, hwnd, null, null)) != 0 && seen.Add(hwnd))
            if (ReadShellWindow(plan, hwnd, true) is { } candidate) candidates.Add(candidate);
        if (candidates.Count > 1) throw new ActionRefusedException("Several official shell windows match this panel; nothing else sent");
        return candidates.Count == 1 ? candidates[0] : null;
    }
    static IEnumerable<nint> ObservedPanelWindows(ShellActPlan plan, long main)
    {
        yield return (nint)main;
        var front = Win32.GetForegroundWindow(); var root = front == 0 ? 0 : Win32.GetAncestor(front, Win32.GA_ROOT); if (root != 0) front = root;
        // Start's search window is a different process. Only its official, currently observed HWND joins this lease.
        if (plan == StartPlan && front != (nint)main && ReadShellWindow(SearchPlan, front, false) != null) yield return front;
    }
    static bool ShellPanelOwnsForeground(ShellActPlan plan, long main)
    {
        var front = Win32.GetForegroundWindow(); var root = front == 0 ? 0 : Win32.GetAncestor(front, Win32.GA_ROOT); if (root != 0) front = root;
        return front == (nint)main || plan == StartPlan && ReadShellWindow(SearchPlan, front, false) != null;
    }
    static (long Id, string App, string Title, long Pid)? AwaitShellPanel(NativeInteraction scope, ShellActPlan plan, int ms)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            scope.Check();
            if (FindShellPanel(plan) is { } panel && ShellPanelOwnsForeground(plan, panel.Id)) return panel;
            Thread.Sleep(50);
        } while (clock.ElapsedMilliseconds < ms);
        return null;
    }
    static string CloseShellPanel(NativeInteraction scope)
    {
        nint Front()
        {
            var front = Win32.GetForegroundWindow(); var root = front == 0 ? 0 : Win32.GetAncestor(front, Win32.GA_ROOT);
            return root == 0 ? front : root;
        }
        bool VisibleOwnedFront()
        {
            var front = Front();
            return scope.OwnsShellWindow(front) && Win32.IsWindowVisible(front) && !Win32.IsCloaked(front);
        }
        try
        {
            // A foreground transfer can cloak and suspend the shell before it consumes Escape. Close the exact
            // visible panel we opened first; only then return the foreground. Never WM_CLOSE, toggle or replay.
            var returned = CloseShellPanelInOrder(() => !scope.UserResumed && VisibleOwnedFront(), scope.Check,
                () => SendShellKeys(scope, [0x1B]), () =>
                {
                    var timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < 600 && VisibleOwnedFront())
                    { EmergencyStop.ThrowIfStopped(); NativeMcpGuard.CheckInterruption(); Thread.Sleep(30); }
                }, scope.ReturnForeground);
            // Cloaked is not released: a hidden CoreWindow may still own the active input queue.
            return !scope.UserResumed && scope.OwnsShellWindow(Front()) ? "refused" : returned;
        }
        catch (Exception error) when (error is EmergencyStopException or JulienActiveException or ActionRefusedException) { return "interrupted"; }
    }
    internal static string CloseShellPanelInOrder(Func<bool> visibleOwnedPanel, Action guard, Action escape,
        Action waitForClosure, Func<string> returnForeground)
    {
        if (visibleOwnedPanel()) { guard(); escape(); waitForClosure(); }
        return returnForeground();
    }

    [StructLayout(LayoutKind.Sequential)] struct ShellKeyboard { public ushort Vk, Scan; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct ShellMouse { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)] struct ShellInputUnion { [FieldOffset(0)] public ShellKeyboard Keyboard; [FieldOffset(0)] public ShellMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct ShellInput { public uint Type; public ShellInputUnion Data; }
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] static extern uint SendShellInput(uint count, ShellInput[] inputs, int size);
    internal static ushort[] ShellShortcutKeys(string shortcut) => shortcut switch
    {
        "win" => [0x5B], "win+a" => [0x5B, 0x41], "win+n" => [0x5B, 0x4E],
        _ => throw new ActionRefusedException("Only win, win+a and win+n may open a Windows panel; nothing sent")
    };
    static void SendShellShortcut(NativeInteraction scope, string shortcut) => SendShellKeys(scope, ShellShortcutKeys(shortcut));
    static void SendShellKeys(NativeInteraction scope, ushort[] keys)
    {
        scope.Check();
        ShellInput Key(ushort key, bool up) => new() { Type = 1, Data = new ShellInputUnion { Keyboard = new ShellKeyboard { Vk = key, Flags = (up ? 2u : 0u) | (Keys.IsExtended(key) ? 1u : 0u) } } };
        var inputs = keys.Select(k => Key(k, false)).Concat(keys.Reverse().Select(k => Key(k, true))).ToArray();
        Program.InputStarted = true;
        var inserted = SendShellInput((uint)inputs.Length, inputs, Marshal.SizeOf<ShellInput>());
        if (inserted == inputs.Length) return;
        // Release only modifiers/keys whose down event was actually inserted; never resend the shortcut.
        var held = new HashSet<ushort>();
        foreach (var input in inputs.Take((int)inserted)) if ((input.Data.Keyboard.Flags & 2) == 0) held.Add(input.Data.Keyboard.Vk); else held.Remove(input.Data.Keyboard.Vk);
        var release = held.Select(k => Key(k, true)).ToArray();
        if (release.Length > 0) SendShellInput((uint)release.Length, release, Marshal.SizeOf<ShellInput>());
        throw new AfterInputException($"Windows inserted {inserted} of {inputs.Length} shell key events; outcome unknown, do not replay", new InvalidOperationException("Partial SendInput"));
    }
}
