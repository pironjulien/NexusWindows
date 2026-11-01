using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

static partial class McpServer
{
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    static (long Id, string App, string Title, long Pid)? ShellWindow(string name)
    {
        var cls = name == "taskbar" ? "Shell_TrayWnd" : name == "desktop" ? "Progman" : "";
        if (cls.Length == 0) return null;
        var hwnd = Win32.FindWindowExW(0, 0, cls, null);
        if (hwnd == 0) throw new InvalidOperationException("Windows shell surface unavailable");
        using var process = Process.GetProcessById((int)Win32.ProcessOf(hwnd));
        return ((long)hwnd, "process:" + process.MainModule!.FileName, name, process.Id);
    }
    // Process, framework, size and state of each listed window, for the three profiles.
    static void AddWindowFields(JsonObject row, long id, string app)
    {
        var hwnd = (nint)id; var cls = Win32.ClassName(hwnd);
        Win32.GetWindowRect(hwnd, out var rect);
        row["process"] = app.StartsWith("process:") ? System.IO.Path.GetFileName(app[8..]) : app;
        row["framework"] = cls.StartsWith("HwndWrapper") ? "WPF" : cls.StartsWith("Chrome_") ? "Chromium" : cls.StartsWith("Windows.UI.") || cls == "ApplicationFrameWindow" ? "UWP" : "Win32";
        row["width"] = rect.Width; row["height"] = rect.Height;
        row["minimized"] = Win32.IsIconic(hwnd); row["maximized"] = IsZoomed(hwnd);
        row["foreground"] = Win32.GetForegroundWindow() == hwnd; row["hidden"] = !Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd);
        row["owner"] ??= 0; row["surface"] = cls == "Shell_TrayWnd" ? "taskbar" : cls is "Progman" or "WorkerW" ? "desktop" : "";
    }
    readonly record struct WindowToolIdentity(long Window, long Pid, string Birth, long Owner, string Class);
    static bool SameWindowToolIdentity(WindowToolIdentity expected, WindowToolIdentity actual) => expected == actual;
    static WindowToolIdentity ReadWindowToolIdentity((long Id, string App, string Title, long Pid) target)
    {
        var window = (nint)target.Id;
        if (!Win32.IsWindow(window) || Win32.ProcessOf(window) != target.Pid)
            throw new ActionRefusedException("Window identity changed before window management; nothing was sent");
        using var process = Process.GetProcessById((int)target.Pid);
        var birth = Program.ProcessStartedUtc((uint)target.Pid);
        if (string.IsNullOrEmpty(birth)) throw new ActionRefusedException("Window process birth unavailable; nothing was sent");
        var cls = Win32.ClassName(window);
        if (GardeFous.BlockReason(process.ProcessName, cls) is { } blocked) throw new ActionRefusedException(blocked);
        // ApplicationFrameHost is attested against the content application's AppId by Program.Execute.
        if (!process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            && !Program.AppId(process).Equals(target.App, StringComparison.OrdinalIgnoreCase))
            throw new ActionRefusedException("Window application identity changed; nothing was sent");
        var identity = new WindowToolIdentity(target.Id, target.Pid, birth, (long)Win32.GetWindow(window, Win32.GW_OWNER), cls);
        if (!Win32.IsWindow(window) || Win32.ProcessOf(window) != target.Pid || process.HasExited)
            throw new ActionRefusedException("Window identity changed while reading its owner; nothing was sent");
        return identity;
    }
    static JsonElement ExecuteWindowTool(SessionState state, (long Id, string App, string Title, long Pid) target, string action, JsonObject args)
    {
        Program.InputStarted = false;
        if (state.Interaction != null) throw new ActionRefusedException("End the foreground interaction before managing a window; nothing was sent");
        var identity = ReadWindowToolIdentity(target);
        var request = new JsonObject { ["operation"] = action == "close" ? "close" : "manageWindow", ["windowId"] = target.Id,
            ["app"] = target.App, ["expectedPid"] = identity.Pid, ["expectedProcessStartedUtc"] = identity.Birth, ["expectedOwner"] = identity.Owner };
        if (action != "close")
        {
            request["action"] = action;
            foreach (var key in new[] { "x", "y", "width", "height", "foreground" }) if (args[key] != null) request[key] = args[key]!.DeepClone();
            WindowManagement.Prepare(JsonSerializer.SerializeToElement(request)); // Reject malformed commands before waiting or taking a token.
        }
        void CheckIdentity()
        {
            EmergencyStop.ThrowIfStopped();
            if (!SameWindowToolIdentity(identity, ReadWindowToolIdentity(target)))
                throw new ActionRefusedException("Window identity changed while waiting to manage it; nothing was sent");
        }
        if (action == "close") { CheckIdentity(); return ExecuteInternal(request); }
        // ShowWindow and placement can activate an application, even for a background command.
        // This is the same presence/token boundary as the legacy Node manageWindow implementation.
        NativeMcpGuard.WaitForPause();
        using var token = ForegroundToken.Acquire("Native MCP window management");
        NativeMcpGuard.RequirePause(); CheckIdentity();
        using var presence = NativeMcpGuard.PhysicalScope();
        var noted = Win32.GetForegroundWindow();
        var returned = "none";
        JsonObject outcome;
        try
        {
            NativeMcpGuard.CheckInterruption(); CheckIdentity();
            outcome = ObjectOf(ExecuteInternal(request));
        }
        catch (Exception error) when (Program.InputStarted)
        {
            outcome = new JsonObject { ["ok"] = false, ["action"] = action, ["status"] = "sent-unconfirmed", ["verified"] = false,
                ["performed"] = true, ["state"] = "unknown", ["technique"] = "background:win32", ["error_type"] = NativeAttemptError(error),
                ["message"] = "Window command may have been sent; inspect its current state before deciding anything else. No automatic replay." };
        }
        finally
        {
            returned = NativeMcpGuard.UserResumed ? "kept-by-julien" : action == "front" ? "kept-requested"
                : ForegroundReturn.GiveBack(noted, (nint)target.Id);
        }
        outcome["foregroundReturned"] = returned;
        return JsonSerializer.SerializeToElement(outcome);
    }
    static int WindowToolCompatibilitySelfTest()
    {
        var count = 0;
        void Check(bool condition, string reason) { count++; if (!condition) throw new InvalidOperationException("Window tool selftest: " + reason); }
        var original = new WindowToolIdentity(42, 7, "first-process", 0, "fixture");
        Check(SameWindowToolIdentity(original, original), "same current window is allowed");
        Check(!SameWindowToolIdentity(original, original with { Window = 43 }), "another HWND is refused");
        Check(!SameWindowToolIdentity(original, original with { Pid = 8 }), "recycled HWND in another PID is refused");
        Check(!SameWindowToolIdentity(original, original with { Birth = "replacement-process" }), "recycled PID is refused");
        Check(!SameWindowToolIdentity(original, original with { Owner = 99 }), "changed owner is refused");
        Check(!SameWindowToolIdentity(original, original with { Class = "replacement-class" }), "replaced window class is refused");
        return count;
    }
}
