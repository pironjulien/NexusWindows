using System.Diagnostics;
using System.IO;
using System.Text.Json;

// One agent at a time per window during an act (idea from DCU / agent-control-fabric, 31/10): a file lock per hwnd in
// the shared .nexus folder, held for the whole batch, like the foreground token. The OS releases it when its holder
// dies; the file content only names the holder for the refusal, it never grants or breaks the lock.
sealed class WindowBusyException(string message) : ActionRefusedException(message);
sealed class WindowLock : IDisposable
{
    const long Offset = 1L << 32;
    public const int WaitMs = 4854; // 3 x 1.618 s: a short act of the other agent ends meanwhile
    [ThreadStatic] static Dictionary<long, WindowLock>? held;
    readonly FileStream stream;
    readonly long window;
    int references = 1;
    WindowLock(FileStream stream, long window) { this.stream = stream; this.window = window; }

    public static string PathOf(long window) => Path.Combine(EmergencyStop.Directory, "windows", window + ".lock");

    public static WindowLock Acquire(long window, string agent, int waitMs = WaitMs)
    {
        EmergencyStop.ThrowIfStopped();
        held ??= new();
        if (held.TryGetValue(window, out var own)) { own.references++; return own; }
        var path = PathOf(window);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var timer = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try { stream.Lock(Offset, 1); break; }
                catch (IOException)
                {
                    EmergencyStop.ThrowIfStopped();
                    if (timer.ElapsedMilliseconds >= waitMs) throw new WindowBusyException("Window busy: " + Holder(stream) + " is acting on it; nothing sent, retry in a few seconds");
                    Thread.Sleep(50);
                }
            }
        }
        catch { stream.Dispose(); throw; }
        var token = new WindowLock(stream, window);
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new { agent, pid = Environment.ProcessId, since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            stream.SetLength(0); stream.Write(data); stream.Flush();
            held[window] = token;
            return token;
        }
        catch { token.Dispose(); throw; }
    }

    // "codex (pid 1234, for 3 s)", read from the holder's own record; "another agent" when it is unreadable.
    static string Holder(FileStream stream)
    {
        try
        {
            stream.Position = 0;
            var bytes = new byte[1024];
            var count = stream.Read(bytes, 0, bytes.Length);
            using var record = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = record.RootElement;
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - root.GetProperty("since").GetInt64();
            return $"{root.GetProperty("agent").GetString()} (pid {root.GetProperty("pid").GetInt32()}, for {Math.Max(0, since)} s)";
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return "another agent"; }
    }

    public void Dispose()
    {
        if (--references > 0) return;
        if (held != null && held.TryGetValue(window, out var own) && ReferenceEquals(own, this)) held.Remove(window);
        try { stream.SetLength(0); stream.Flush(); }
        finally { try { stream.Unlock(Offset, 1); } finally { stream.Dispose(); } }
    }

    // --window-lock <hwnd> <agent> [waitMs]: holds the lock until stdin closes (two-process test of the refusal and of the
    // release when the holder dies). Prints {"ok":true} once held, or the refusal.
    public static int Hold(string[] args)
    {
        if (!long.TryParse(args[1], out var window)) return 2;
        var wait = args.Length == 4 && int.TryParse(args[3], out var ms) ? ms : WaitMs;
        try
        {
            using var token = Acquire(window, args[2], wait);
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true }));
            Console.Out.Flush();
            Console.In.ReadToEnd();
            return 0;
        }
        catch (WindowBusyException error) { Console.WriteLine(JsonSerializer.Serialize(new { ok = false, message = error.Message })); return 1; }
    }
}
