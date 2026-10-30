using System.Diagnostics;
using System.Text.Json;

// Independent lifecycle for the native MCP process: the existing stop/presence
// helper is a child, its event stream never appears on MCP stdout.
static class NativeMcpGuard
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long lastInput; static bool ready; static string source = "unknown";
    [ThreadStatic] static bool active;
    [ThreadStatic] static long inputAtStart;
    public static IDisposable Start(string version)
    {
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--stop-guard " + version)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Cannot start the native emergency-stop/presence guard");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { ready = false; return; }
            try
            {
                using var data = JsonDocument.Parse(e.Data); var value = data.RootElement;
                var kind = value.TryGetProperty("event", out var k) ? k.GetString() : "";
                if (kind == "presence") { source = value.GetProperty("source").GetString() ?? "unknown"; Interlocked.Exchange(ref lastInput, Clock.ElapsedMilliseconds); ready = true; }
                else if (kind == "input") Interlocked.Exchange(ref lastInput, Clock.ElapsedMilliseconds);
            }
            catch (JsonException) { ready = false; }
        };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return new OwnedGuard(process);
    }
    public static void WaitForPause()
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 8000)
        {
            EmergencyStop.ThrowIfStopped();
            if (ready && source == "raw-input" && Clock.ElapsedMilliseconds - Interlocked.Read(ref lastInput) >= 1618) return;
            Thread.Sleep(50);
        }
        RequirePause();
    }
    public static void RequirePause()
    {
        if (!ready || source != "raw-input") throw new JulienActiveException("Native physical input deferred: user presence is not known reliably; observe later");
        if (Clock.ElapsedMilliseconds - Interlocked.Read(ref lastInput) < 1618) throw new JulienActiveException("Native physical input deferred: the user is typing or moving the mouse; observe later");
    }
    public static IDisposable PhysicalScope()
    {
        RequirePause(); inputAtStart = Interlocked.Read(ref lastInput); active = true; return new Scope();
    }
    public static void CheckInterruption()
    {
        if (active && (!ready || Interlocked.Read(ref lastInput) != inputAtStart)) throw new JulienActiveException("The user took the mouse or keyboard back; input stopped, inspect before deciding anything else");
    }
    public static bool UserResumed => active && (!ready || Interlocked.Read(ref lastInput) != inputAtStart);
    sealed class Scope : IDisposable { public void Dispose() => active = false; }
    sealed class OwnedGuard(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { process.StandardInput.Close(); if (!process.WaitForExit(1000)) process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose(); ready = false;
        }
    }
}
