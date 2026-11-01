using System.Diagnostics;
using System.IO;

// One full UI Automation tree read at a time across the agents of this session (01/11). Measured on the WPF lab window
// (663 elements), even with clients outside NexusWindows: one read takes 0.45 s alone, but three simultaneous reads of
// three different windows take 1.9 s each, longer than the same three reads in turn; the extra time is spent inside
// Windows, not in a NexusWindows lock. Reads in turn run at full speed. A file lock in the shared .nexus folder, like
// the window lock: the OS frees it if its holder dies. After WaitMs the read goes ahead anyway (a stuck provider never
// blocks the other agents for longer than that). A read sends no input: the emergency stop does not apply to it.
sealed class UiaReadGate : IDisposable
{
    const long Offset = 1L << 32;
    public const int WaitMs = 4854; // 3 x 1.618 s
    [ThreadStatic] static UiaReadGate? held;
    readonly FileStream? stream;
    int references = 1;
    UiaReadGate(FileStream? stream) => this.stream = stream;

    public static string PathOf() => Path.Combine(EmergencyStop.Directory, "uia-read.lock");

    public static UiaReadGate Enter(int waitMs = WaitMs)
    {
        if (held is { } own) { own.references++; return own; }
        FileStream? stream = null;
        try
        {
            var path = PathOf();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { stream.Lock(Offset, 1); break; }
                catch (IOException)
                {
                    if (timer.ElapsedMilliseconds >= waitMs) { stream.Dispose(); stream = null; break; }
                    Thread.Sleep(10);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { stream?.Dispose(); stream = null; }
        catch { stream?.Dispose(); throw; }
        return held = new UiaReadGate(stream);
    }

    public bool Gated => stream != null;

    public void Dispose()
    {
        if (--references > 0) return;
        held = null;
        if (stream == null) return;
        try { stream.Unlock(Offset, 1); } catch (IOException) { }
        finally { stream.Dispose(); }
    }
}
