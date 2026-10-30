// Stable entry point of NexusWindows: %USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe. MCP clients (Claude, Codex,
// Antigravity, the NexusSystem catalogue) always declare this path; it never changes when the engine is updated.
// It reads installation.json next to itself, checks that the engine it names is the immutable version under
// native\<sha-256>\ with the recorded hashes, then runs it with the same arguments, the same stdin/stdout/stderr
// handles (inherited, nothing is copied or buffered) and returns its exit code. An update writes a new engine
// directory and a new installation.json: sessions already running keep their engine, the next start gets the new one.
// The engine is tied to this process by a job object: killing the launcher (what an MCP host does) ends the engine;
// processes the engine starts itself (applications, its guard) are not tied to it.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusWindows.Launcher;

static partial class Launcher
{
    const int Unavailable = 3;

    static int Main(string[] args)
    {
        // Ctrl+C / Ctrl+Break reach the engine, which shares the console: the launcher only waits for its exit.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        string engine;
        try { engine = Resolve(AppContext.BaseDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine("NexusWindows: " + error.Message + " Run the NexusWindows installer again.");
            return Unavailable;
        }
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var job = KillOnCloseJob();
        Process child;
        try { child = Process.Start(start) ?? throw new Win32Exception("no process"); }
        catch (Win32Exception error)
        {
            Console.Error.WriteLine($"NexusWindows: the engine could not start ({error.Message}).");
            return Unavailable;
        }
        using (child)
        {
            // A failure here (launcher already in a job that forbids nesting) leaves the engine running unbound, as
            // it was started before the launcher existed: never a reason to refuse the session.
            if (job is not null) AssignProcessToJobObject(job, child.Handle);
            child.WaitForExit();
            return child.ExitCode;
        }
    }

    // installation.json is replaced atomically by the installers; a read racing that replacement is retried.
    internal static string Resolve(string home)
    {
        var manifest = Path.Combine(home, "installation.json");
        for (var attempt = 0; ; attempt++)
        {
            try { return Validate(home, File.ReadAllBytes(manifest)); }
            catch (Exception error) when (attempt < 20 && error is IOException or JsonException && error is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(25);
            }
        }
    }

    internal static string Validate(string home, byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (!root.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 2)
            throw new InvalidDataException("installation.json has an unknown schema.");
        var hash = root.TryGetProperty("NativeHash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "";
        if (!Sha256Pattern().IsMatch(hash)) throw new InvalidDataException("installation.json names no engine version.");
        var directory = Path.GetFullPath(Path.Combine(home, "native", hash));
        var engine = Path.Combine(directory, "NexusWindows.exe");
        var declared = root.TryGetProperty("NativeExecutable", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
        if (!string.Equals(Path.GetFullPath(declared), engine, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("installation.json names an engine outside its versioned folder.");
        if (!root.TryGetProperty("NativeArtifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("installation.json lists no engine files.");
        // The files that run code are checked against their recorded SHA-256 at every start (a few milliseconds).
        foreach (var name in new[] { "NexusWindows.exe", "NexusWindows.dll" })
        {
            if (!artifacts.TryGetProperty(name, out var expected) || expected.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"installation.json has no hash for {name}.");
            var path = Path.Combine(directory, name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"The engine file {name} is missing.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The engine file {name} does not match its recorded hash.");
        }
        return engine;
    }

    static SafeJobHandle? KillOnCloseJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { job.Dispose(); return null; }
        var limits = new JobObjectExtendedLimitInformation
        {
            // KILL_ON_JOB_CLOSE: the engine ends with this launcher. SILENT_BREAKAWAY_OK: what the engine starts is
            // not in the job (an application launched for the user must outlive the session).
            BasicLimitInformation = new() { LimitFlags = 0x2000 | 0x1000 },
        };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>())) { job.Dispose(); return null; }
        return job;
    }

    [GeneratedRegex("^[a-f0-9]{64}$")] private static partial Regex Sha256Pattern();

    sealed class SafeJobHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObjectW(nint attributes, string? name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, nint process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
