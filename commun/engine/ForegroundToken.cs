using System.Diagnostics;
using System.IO;
using System.Text.Json;

// The handle is the authority; diagnostics are never used to break a lock.
sealed class ForegroundToken : IDisposable
{
    const long Offset = 1L << 32;
    public const int WaitMs = 8000; // longest wait for the token
    readonly FileStream stream;
    [ThreadStatic] static ForegroundToken? current;
    int references = 1;
    bool released;
    ForegroundToken(FileStream stream) => this.stream = stream;

    public static ForegroundToken Acquire(string purpose, Task? abandoned = null)
    {
        EmergencyStop.ThrowIfStopped();
        // Internal helpers share the same thread's live OS lease (interaction -> posted click/key).
        // No diagnostic file or request argument can grant this ownership.
        if (current is { released: false } owner) { owner.references++; return owner; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "foreground.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                if (abandoned?.IsCompleted == true) throw new OperationCanceledException("Foreground request abandoned");
                try { stream.Lock(Offset, 1); break; }
                catch (IOException)
                {
                    // Interruptible wait (commun/foreground-token.md, like `abort` in commun/foreground_lock.py): the
                    // emergency stop is read again every 50 ms, never only after the 8 s.
                    EmergencyStop.ThrowIfStopped();
                    if (timer.ElapsedMilliseconds >= WaitMs)
                    {
                        stream.Position = 0;
                        var bytes = new byte[4096];
                        var count = stream.Read(bytes, 0, bytes.Length);
                        throw new TimeoutException("Foreground token unavailable; action deferred. Holder: " + System.Text.Encoding.UTF8.GetString(bytes, 0, count));
                    }
                    Thread.Sleep(50);
                }
            }
        }
        catch { stream.Dispose(); throw; }
        // The wait can last up to eight seconds: a stop requested meanwhile refuses the action before any input.
        try { EmergencyStop.ThrowIfStopped(); } catch { stream.Unlock(Offset, 1); stream.Dispose(); throw; }
        var token = new ForegroundToken(stream);
        try
        {
            var data = EngineJson.SerializeToUtf8Bytes(new ForegroundTokenAcquireJson(owner: "NexusWindows", pid: Environment.ProcessId, since: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), purpose: purpose));
            stream.SetLength(0);
            stream.Write(data);
            stream.Flush();
            current = token;
            return token;
        }
        catch { token.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (released || --references > 0) return;
        released = true; if (ReferenceEquals(current, this)) current = null;
        try { stream.SetLength(0); stream.Flush(); }
        finally { try { stream.Unlock(Offset, 1); } finally { stream.Dispose(); } }
    }
}

// Did the user take the keyboard or mouse back while waiting for the token? GetLastInputInfo counts every input
// (injected included): cautious, another agent's input defers too. No input has been sent here yet.
sealed class JulienActiveException(string message) : InvalidOperationException(message);
static class JulienPause
{
    public static void ThrowIfActive(int? quietMs)
    {
        if (quietMs is not { } quiet || quiet <= 0) return;
        var info = new Win32.LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.LastInputInfo>() };
        if (!Win32.GetLastInputInfo(ref info)) throw new JulienActiveException("User presence unreadable after taking the token: input deferred, nothing was sent.");
        var idle = unchecked(Win32.GetTickCount() - info.Time);
        if (idle < quiet)
            throw new JulienActiveException($"The user is using the keyboard or mouse (last input {idle} ms ago, after waiting for the token): input deferred, nothing was sent. Observe again later; never replay automatically.");
    }
}

// Foreground given back to the user UNDER the token, before giving the token back (commun/foreground-token.md): the
// active window noted when the token was taken is brought back in front, only if the target window (its process) still
// holds the foreground. If the user moved to another window, nothing is taken from them. States: restored, not-taken
// (the target does not hold the foreground, or the user was already in the target), kept-by-user, none (no window to
// give it back to), refused.
static class ForegroundReturn
{
    static readonly HashSet<string> Switchers = new(StringComparer.OrdinalIgnoreCase) { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "TaskSwitcherWnd", "ForegroundStaging" };

    // `input`: LastInput noted with the window; read again before each restoring gesture and during each wait. The user
    // (or a peer) touched the keyboard or mouse meanwhile: their choice is kept, nothing is brought back in front (Codex's
    // review, 21/10: the second attempt could land in the middle of their typing).
    // `sameProcess`: the target may be ANOTHER window of the user's process (a launch that opens a document in their
    // instance); their window is then given back too (Codex's review of 23/10).
    public static string GiveBack(nint noted, nint target, uint? input = null, bool sameProcess = false)
    {
        // No reference given (UIA fallback, physical lease): input noted on entry, which covers the restore itself without
        // mistaking the agent's earlier inputs for the user's (Codex's review, 21/10).
        input ??= Win32.Probe().LastInput;
        bool Moved() => Win32.Probe().LastInput != input;
        if (target == 0 || noted == 0) return "none";
        if (Moved()) return "kept-by-user";
        var fg = Win32.GetForegroundWindow();
        if (fg == noted) return "not-taken";
        var targetPid = Win32.ProcessOf(target);
        if (!sameProcess && Win32.ProcessOf(noted) == targetPid) return "not-taken"; // the user was already in the target application
        if (fg == 0) return "not-taken";
        if (Win32.ProcessOf(fg) != targetPid) return "kept-by-user";
        if (!Restorable(noted)) return "none";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (Win32.GetForegroundWindow() != fg) break; // the foreground changed meanwhile: force nothing
            if (Moved()) return "kept-by-user";
            var own = Win32.GetCurrentThreadId();
            var thread = Win32.GetWindowThreadProcessId(fg, out _);
            var attached = thread != 0 && thread != own && Win32.AttachThreadInput(own, thread, true);
            try
            {
                if (Win32.GetForegroundWindow() == fg) { Win32.BringWindowToTop(noted); Win32.SetForegroundWindow(noted); }
            }
            finally { if (attached) Win32.AttachThreadInput(own, thread, false); }
            if (Wait(noted, 150, Moved)) return "restored";
            if (Moved()) return "kept-by-user";
        }
        // No more fallback through a POSTED WM_SYSCOMMAND SC_HOTKEY: an expired wait does not remove the message from the
        // queue, which could reactivate the old window after the token and after the user's choice (Codex's review, 21/10).
        return Win32.GetForegroundWindow() is var now && now != 0 && Win32.ProcessOf(now) != targetPid && now != noted ? "kept-by-user" : "refused";
    }
    static bool Wait(nint window, int ms, Func<bool>? stop = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            if (Win32.GetForegroundWindow() == window) return true;
            if (stop?.Invoke() == true) return false;
            Thread.Sleep(10);
        }
        return Win32.GetForegroundWindow() == window;
    }
    static bool Restorable(nint hwnd) =>
        Win32.IsWindow(hwnd) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) && !Win32.IsCloaked(hwnd) && !Switchers.Contains(Win32.ClassName(hwnd));
}
