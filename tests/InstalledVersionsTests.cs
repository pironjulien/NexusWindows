// Earlier engine versions and leftovers removed at install and update (commun/engine/InstalledVersions.cs), in a
// throwaway profile: the installed, running and declared versions stay, the others go; a folder in use is never half
// removed; only this product's own disposable browser profiles go, once their browser has closed.
using System.Globalization;
using System.Text;

namespace NexusWindows.Tests;

public sealed class InstalledVersionsTests : IDisposable
{
    readonly string root = Path.Combine(TestEnvironment.SharedHome, "prune-" + Guid.NewGuid().ToString("N"));
    string Home => Path.Combine(root, ".nexus", "nexus-windows");
    Registration.Profile Profile => new(root, Path.Combine(root, "AppData", "Roaming"), Path.Combine(root, "AppData", "Local"), Path.Combine(root, ".codex"), null);
    static string H(char c) => new(c, 64);

    string Version(char c, int size = 1000)
    {
        var folder = Path.Combine(Home, "native", H(c));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "NexusWindows.exe"), new byte[size]);
        File.WriteAllText(Path.Combine(folder, "garde-fous.json"), "{}");
        return folder;
    }

    void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    string BrowserProfile(string agent, string owner, DateTimeOffset created, bool marker = true)
    {
        var id = created.ToString("yyyyMMdd-HHmmss-", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Home, "state", agent, "devtools-profiles", id);
        Directory.CreateDirectory(Path.Combine(folder, "Default"));
        File.WriteAllText(Path.Combine(folder, "Default", "Preferences"), "{}");
        if (marker) File.WriteAllText(Path.Combine(folder, ".nexus-profile"), $$"""{"created":"{{created:O}}","owner":"{{owner}}","id":"{{id}}"}""");
        return folder;
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Keeps_the_installed_running_and_declared_versions_and_removes_the_others()
    {
        var installed = Version('a'); var declared = Version('b'); var running = Version('c'); var earlier = Version('d', 4096);
        var daemon = Version('e'); var locked = Version('f');
        var custom = Path.Combine(Home, "native", "custom"); Directory.CreateDirectory(custom);
        var interrupted = Path.Combine(Home, "native", ".prune-0123"); Directory.CreateDirectory(interrupted);
        Write(@".nexus\nexus-windows\installation.json", $$"""{"Schema":2,"NativeHash":"{{H('a')}}"}""");
        // An entry written before the stable launcher, JSON-escaped, and a daemon spec.
        Write(".claude.json", "{\"mcpServers\":{\"nexus-windows\":{\"command\":\"" + Path.Combine(declared, "NexusWindows.exe").Replace("\\", "\\\\") + "\"}}}");
        Write(@".nexus\daemons\specs\other.json", "{\"exe\":\"" + Path.Combine(daemon, "NexusWindows.exe").Replace("\\", "\\\\") + "\"}");
        var freeAside = Path.Combine(Home, "NexusWindows.exe.old-1"); File.WriteAllText(freeAside, "old launcher");
        var busyAside = Path.Combine(Home, "NexusWindows.exe.old-2"); File.WriteAllText(busyAside, "running launcher");
        var oldStage = Path.Combine(Home, "stage-old"); Directory.CreateDirectory(oldStage); Directory.SetLastWriteTimeUtc(oldStage, DateTime.UtcNow.AddDays(-1));
        var newStage = Path.Combine(Home, "stage-new"); Directory.CreateDirectory(newStage);

        Report outcome;
        using (new FileStream(Path.Combine(locked, "NexusWindows.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(busyAside, FileMode.Open, FileAccess.Read, FileShare.Read))
            outcome = new(InstalledVersions.Prune(Profile, [Path.Combine(running, "NexusWindows.exe")], DateTimeOffset.UtcNow));

        foreach (var kept in new[] { installed, declared, running, daemon, custom, newStage }) Assert.True(Directory.Exists(kept), kept);
        // In use: refused by Windows at the rename, so left whole.
        Assert.True(File.Exists(Path.Combine(locked, "NexusWindows.exe")) && File.Exists(Path.Combine(locked, "garde-fous.json")), outcome.Text);
        Assert.True(File.Exists(busyAside));
        foreach (var gone in new[] { earlier, interrupted, oldStage }) Assert.False(Directory.Exists(gone), gone);
        Assert.False(File.Exists(freeAside));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Home, "native"), ".prune-*"));
        Assert.Contains(H('b')[..12] + " (declared)", outcome.Value.Kept);
        Assert.Contains(H('c')[..12] + " (running)", outcome.Value.Kept);
        Assert.Contains(H('f')[..12] + " (in use)", outcome.Value.Kept);
        Assert.True(outcome.Value.Bytes >= 4096, outcome.Text);
        // Released: the next installation removes it.
        InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow);
        Assert.False(Directory.Exists(locked));
        Assert.False(File.Exists(busyAside));
        Assert.False(Directory.Exists(running));
        Assert.True(Directory.Exists(installed) && Directory.Exists(declared));
    }

    [Fact]
    public void Without_a_record_of_the_installed_version_nothing_is_removed()
    {
        var a = Version('a'); var b = Version('b');
        Assert.Empty(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow).Removed);
        Write(@".nexus\nexus-windows\installation.json", "{\"previous\":true}");
        Assert.Empty(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow).Removed);
        Assert.True(Directory.Exists(a) && Directory.Exists(b));
    }

    [Fact]
    public void Only_this_products_own_browser_profiles_go_once_their_browser_has_closed()
    {
        Version('a');
        Write(@".nexus\nexus-windows\installation.json", $$"""{"Schema":2,"NativeHash":"{{H('a')}}"}""");
        var now = DateTimeOffset.UtcNow;
        var stale = BrowserProfile("claude", "nexus-windows-claude", now.AddDays(-2));
        var young = BrowserProfile("claude", "nexus-windows-claude", now.AddMinutes(-5));
        var otherAgent = BrowserProfile("codex", "nexus-windows-claude", now.AddDays(-2));
        var unmarked = BrowserProfile("claude", "", now.AddDays(-2), marker: false);
        var open = BrowserProfile("antigravity", "nexus-windows-antigravity", now.AddDays(-2));
        using (new FileStream(Path.Combine(open, "Default", "Preferences"), FileMode.Open, FileAccess.Read, FileShare.Read))
            InstalledVersions.Prune(Profile, [], now);
        Assert.False(Directory.Exists(stale));
        foreach (var kept in new[] { young, otherAgent, unmarked }) Assert.True(Directory.Exists(kept), kept);
        Assert.True(File.Exists(Path.Combine(open, "Default", "Preferences")), "a browser still running keeps its profile whole");
        InstalledVersions.Prune(Profile, [], now);
        Assert.False(Directory.Exists(open));
    }

    [Fact]
    public void An_engine_process_the_scan_cannot_identify_keeps_every_earlier_version_and_launcher()
    {
        var installed = Version('a'); var earlier = Version('b');
        var interrupted = Path.Combine(Home, "native", ".prune-0123"); Directory.CreateDirectory(interrupted);
        var aside = Path.Combine(Home, "NexusWindows.exe.old-1"); File.WriteAllText(aside, "old launcher");
        Write(@".nexus\nexus-windows\installation.json", $$"""{"Schema":2,"NativeHash":"{{H('a')}}"}""");
        var outcome = new Report(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow, unidentified: 1));
        Assert.Empty(outcome.Value.Removed);
        foreach (var kept in new[] { installed, earlier, interrupted }) Assert.True(Directory.Exists(kept), outcome.Text);
        Assert.True(File.Exists(aside), outcome.Text);
        // Once every engine process is identified, the next pass removes them.
        InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow);
        Assert.False(Directory.Exists(earlier) || Directory.Exists(interrupted) || File.Exists(aside));
    }

    internal readonly record struct Report(InstalledVersions.Outcome Value)
    {
        public string Text => "removed: " + string.Join(", ", Value.Removed) + "; kept: " + string.Join(", ", Value.Kept);
    }
}

// A real engine running from an earlier version folder: found by the process scan, and kept whole by its own
// engine.lock even when the scan is blinded (an elevated, SYSTEM or other-session engine the scan could miss). Windows
// does not refuse renaming the folder of a running program: a running program WITHOUT the lock (a single-file native
// executable, as the Native AOT engine before 3.17.2) has its folder moved away by a blinded pass, which the lock-less
// control below shows on every run; test.ps1 -Engine <3.17.1 AOT engine> fails the engine test at that same blinded
// pass. The framework-dependent test engine would also keep its folder through the runtime's handle on its .dll, so the
// lock itself is checked directly too.
[Collection("Engine")]
public sealed class RunningEngineVersionTests : IDisposable
{
    readonly string root = Path.Combine(TestEnvironment.SharedHome, "prune-live-" + Guid.NewGuid().ToString("N"));
    string Home => Path.Combine(root, ".nexus", "nexus-windows");
    string Native => Path.Combine(Home, "native");
    Registration.Profile Profile => new(root, Path.Combine(root, "AppData", "Roaming"), Path.Combine(root, "AppData", "Local"), Path.Combine(root, ".codex"), null);
    static string Label(char c) => new(c, 12);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // The test engine copied as an installed version (native\<sha-256>\NexusWindows.exe), as get.ps1 lays it out.
    string Engine(char c)
    {
        var folder = Path.Combine(Native, new string(c, 64));
        Directory.CreateDirectory(folder);
        File.Copy(SelfTest.Engine, Path.Combine(folder, "NexusWindows.exe"));
        foreach (var file in new[] { "NexusWindows.Engine.dll", "NexusWindows.Engine.runtimeconfig.json", "NexusWindows.Engine.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        File.Copy(Path.Combine(TestEnvironment.RepositoryRoot, "commun", "garde-fous.json"), Path.Combine(folder, "garde-fous.json"));
        return Path.Combine(folder, "NexusWindows.exe");
    }

    void Installed(char c) => File.WriteAllText(Path.Combine(Home, "installation.json"), $$"""{"Schema":2,"NativeHash":"{{new string(c, 64)}}"}""");

    // Ended: the next passes remove the folder and anything set aside (the mapped image is released a moment later).
    void PruneUntilGone(string folder)
    {
        for (var attempt = 0; (Directory.Exists(folder) || Directory.GetDirectories(Native, ".prune-*").Length > 0) && attempt < 50; attempt++)
        {
            InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow);
            if (Directory.Exists(folder) || Directory.GetDirectories(Native, ".prune-*").Length > 0) Thread.Sleep(100);
        }
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetDirectories(Native, ".prune-*"));
    }

    [Fact]
    public void A_running_engine_keeps_its_version_folder_whole_even_when_the_scan_misses_it()
    {
        Engine('a'); Installed('a');
        var running = Engine('b');
        var folder = Path.GetDirectoryName(running)!;
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        using (var session = new McpStdioTests.Server(["--mcp", "--agent", "codex"], executable: running))
        {
            var reply = session.Rpc("tools/call", new { name = "windows", arguments = new { } });
            Assert.True(reply["result"] is not null, reply.ToJsonString());

            // The scan reads its path (QueryFullProcessImageNameW), whatever the process name.
            var scan = InstalledVersions.RunningExecutables();
            Assert.Contains(Path.GetFullPath(running), scan.Paths, StringComparer.OrdinalIgnoreCase);
            var seen = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, scan.Paths, DateTimeOffset.UtcNow));
            Assert.Contains(Label('b') + " (running)", seen.Value.Kept);

            // Blinded scan: only the engine's own lock stands between its folder and the move. An engine without it
            // (3.17.1 and earlier) fails here: its folder is set aside under .prune-*.
            var blind = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow));
            var moved = Directory.GetDirectories(Native, ".prune-*");
            Assert.True(Directory.Exists(folder) && moved.Length == 0, "the blinded pass moved the running engine's folder away: " + blind.Text);
            Assert.Contains(Label('b') + " (in use)", blind.Value.Kept);
            Assert.DoesNotContain(Label('b'), blind.Value.Removed);
            foreach (var name in files) Assert.True(File.Exists(Path.Combine(folder, name!)), name + ": " + blind.Text);

            // What refused the move: engine.lock, held open by the engine without delete sharing.
            var lockPath = Path.Combine(folder, InstalledVersions.LockName);
            Assert.True(File.Exists(lockPath), "the engine holds its version's lock");
            var held = Record.Exception(() => File.Delete(lockPath));
            Assert.True(held is IOException or UnauthorizedAccessException, held?.ToString() ?? "engine.lock deleted while the engine runs");
            Assert.True(session.Rpc("tools/call", new { name = "windows", arguments = new { } })["result"] is not null, "still answering after the pass");
        }
        PruneUntilGone(folder);
    }

    // The control: a running program from a version folder, named like the engine but holding no engine.lock (a copy of
    // cmd.exe, a single-file native executable like the Native AOT engine). Seen by the scan, it is kept; blinded, the
    // pass moves its folder away under it, and the folder it could not delete is reported set aside, not removed.
    [Fact]
    public void Without_the_lock_a_blinded_pass_moves_a_running_programs_folder_and_does_not_count_it_removed()
    {
        Directory.CreateDirectory(Path.Combine(Native, new string('a', 64))); Installed('a');
        var folder = Path.Combine(Native, new string('b', 64));
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "NexusWindows.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), program);
        File.WriteAllText(Path.Combine(folder, "garde-fous.json"), "{}");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program, "/d /q /k")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            var scan = InstalledVersions.RunningExecutables();
            Assert.Contains(Path.GetFullPath(program), scan.Paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Label('b') + " (running)", InstalledVersions.Prune(Profile, scan.Paths, DateTimeOffset.UtcNow).Kept);
            Assert.True(Directory.Exists(folder));

            var blind = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow));
            Assert.False(Directory.Exists(folder), "Windows refused moving the folder of a program holding no lock: " + blind.Text);
            var aside = Assert.Single(Directory.GetDirectories(Native, ".prune-*"));
            Assert.True(File.Exists(Path.Combine(aside, "NexusWindows.exe")), "the running image cannot be deleted: " + blind.Text);
            Assert.False(process.HasExited);
            // Exact count: moved but not deleted is not removed, and its bytes are not counted.
            Assert.DoesNotContain(Label('b'), blind.Value.Removed);
            Assert.Contains(Label('b') + " (set aside, removed at the next pass)", blind.Value.Kept);
            Assert.Equal(0, blind.Value.Bytes);
        }
        finally
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.WaitForExit();
        }
        PruneUntilGone(folder);
    }

    // Fallback: when engine.lock cannot be created in the version folder (here a folder already has its name; in the
    // field, a folder another account wrote), the engine holds its own executable open without delete sharing instead,
    // and the move is refused the same way.
    [Fact]
    public void When_the_lock_cannot_be_created_the_engine_file_itself_keeps_the_folder()
    {
        Engine('a'); Installed('a');
        var running = Engine('d');
        var folder = Path.GetDirectoryName(running)!;
        Directory.CreateDirectory(Path.Combine(folder, InstalledVersions.LockName));
        using (var held = InstalledVersions.Hold(running))
        {
            Assert.NotNull(held);
            Assert.Equal(Path.GetFullPath(running), held!.Name, ignoreCase: true);
            var blind = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow));
            Assert.Contains(Label('d') + " (in use)", blind.Value.Kept);
            Assert.True(File.Exists(running), blind.Text);
            Assert.Empty(Directory.GetDirectories(Native, ".prune-*"));
        }
        PruneUntilGone(folder);
    }

    [Fact]
    public void Only_an_engine_started_from_a_version_folder_takes_the_lock()
    {
        Assert.Null(InstalledVersions.Hold(Path.Combine(root, "bin", "NexusWindows.exe")));
        Assert.Null(InstalledVersions.Hold(Path.Combine(root, "native", "stage-1", "NexusWindows.exe")));
        var running = Engine('c');
        using var held = InstalledVersions.Hold(running);
        Assert.NotNull(held);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(running))!, InstalledVersions.LockName), held!.Name, ignoreCase: true);
        // A second engine of the same version shares it.
        using (var second = InstalledVersions.Hold(running)) Assert.NotNull(second);
        var refused = Record.Exception(() => Directory.Move(Path.GetDirectoryName(running)!, Path.Combine(Native, ".prune-x")));
        Assert.True(refused is IOException or UnauthorizedAccessException, refused?.ToString() ?? "moved while held");
    }
}

// An elevated process of this account (an engine started by an elevated AI app or task): Process.MainModule fails on
// it from a non-elevated scanner, the scan reads its path all the same. Runs only when such a process is running now
// and its image path is its own (no non-elevated process shares it, which would hide a miss).
public sealed partial class ElevatedProcessScanTests
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inherit, int pid);
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [System.Runtime.InteropServices.LibraryImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int kind, void* buffer, int length, out int returned);
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    sealed record Seen(int Pid, string Path, bool Elevated, string? User);

    static unsafe Seen? Read(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[32768]; var size = (uint)buffer.Length;
            fixed (char* name = buffer) if (!QueryFullProcessImageNameW(handle, 0, name, ref size)) return null;
            var path = System.IO.Path.GetFullPath(new string(buffer, 0, (int)size));
            if (!OpenProcessToken(handle, 0x0008 /* TOKEN_QUERY */, out var token)) return new(pid, path, false, null);
            try
            {
                int elevation; bool elevated = GetTokenInformation(token, 20 /* TokenElevation */, &elevation, 4, out _) && elevation != 0;
                var user = stackalloc byte[256];
                string? sid = GetTokenInformation(token, 1 /* TokenUser */, user, 256, out _)
                    ? new System.Security.Principal.SecurityIdentifier(*(nint*)user).Value : null;
                return new(pid, path, elevated, sid);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(handle); }
    }

    [Fact]
    public void The_scan_reads_the_path_of_an_elevated_process_of_this_account()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var self = identity.User!.Value;
        var elevatedSelf = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var all = System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return Read(p.Id); }).OfType<Seen>().ToList();
        var target = all.FirstOrDefault(s => s.Elevated && s.User == self && s.Pid != Environment.ProcessId &&
            all.Count(o => string.Equals(o.Path, s.Path, StringComparison.OrdinalIgnoreCase)) == 1);
        Assert.SkipWhen(target is null, "No elevated process of this account with an image path of its own is running.");
        if (!elevatedSelf)
        {
            // What the scan before 3.17.2 relied on fails here.
            using var process = System.Diagnostics.Process.GetProcessById(target!.Pid);
            Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => process.MainModule);
        }
        var scan = InstalledVersions.RunningExecutables();
        if (!scan.Paths.Contains(target!.Path, StringComparer.OrdinalIgnoreCase))
            Assert.SkipWhen(Read(target.Pid)?.Path is null, "The elevated process ended during the scan.");
        Assert.Contains(target.Path, scan.Paths, StringComparer.OrdinalIgnoreCase);
    }
}
