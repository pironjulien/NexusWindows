// Earlier engine versions and leftovers removed at install and update (codex/native/InstalledVersions.cs), in a
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
// engine.lock even when the scan is blinded (an elevated, SYSTEM or other-session engine the scan could miss). The
// framework-dependent test engine also keeps its folder through the runtime's handle on its .dll; the Native AOT engine
// releases ship has no such handle, so test.ps1 -Engine <AOT engine> checks the lock alone (an engine without it fails
// the blinded pass: its folder is moved away).
[Collection("Engine")]
public sealed class RunningEngineVersionTests : IDisposable
{
    readonly string root = Path.Combine(TestEnvironment.SharedHome, "prune-live-" + Guid.NewGuid().ToString("N"));
    string Home => Path.Combine(root, ".nexus", "nexus-windows");
    Registration.Profile Profile => new(root, Path.Combine(root, "AppData", "Roaming"), Path.Combine(root, "AppData", "Local"), Path.Combine(root, ".codex"), null);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // The test engine copied as an installed version (native\<sha-256>\NexusWindows.exe), as get.ps1 lays it out.
    string Engine(char c)
    {
        var folder = Path.Combine(Home, "native", new string(c, 64));
        Directory.CreateDirectory(folder);
        File.Copy(SelfTest.Engine, Path.Combine(folder, "NexusWindows.exe"));
        foreach (var file in new[] { "NexusWindows.Engine.dll", "NexusWindows.Engine.runtimeconfig.json", "NexusWindows.Engine.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(folder, file));
        File.Copy(Path.Combine(TestEnvironment.RepositoryRoot, "commun", "garde-fous.json"), Path.Combine(folder, "garde-fous.json"));
        return Path.Combine(folder, "NexusWindows.exe");
    }

    [Fact]
    public void A_running_engine_keeps_its_version_folder_whole_even_when_the_scan_misses_it()
    {
        Engine('a');
        File.WriteAllText(Path.Combine(Home, "installation.json"), $$"""{"Schema":2,"NativeHash":"{{new string('a', 64)}}"}""");
        var running = Engine('b');
        var folder = Path.GetDirectoryName(running)!;
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        using (var session = new McpStdioTests.Server(["--mcp", "--agent", "codex"], executable: running))
        {
            var reply = session.Rpc("tools/call", new { name = "windows", arguments = new { } });
            Assert.True(reply["result"] is not null, reply.ToJsonString());
            Assert.True(File.Exists(Path.Combine(folder, InstalledVersions.LockName)), "the engine holds its version's lock");

            // The scan reads its path (QueryFullProcessImageNameW), whatever the process name.
            var scan = InstalledVersions.RunningExecutables();
            Assert.Contains(Path.GetFullPath(running), scan.Paths, StringComparer.OrdinalIgnoreCase);
            var seen = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, scan.Paths, DateTimeOffset.UtcNow));
            Assert.Contains(new string('b', 12) + " (running)", seen.Value.Kept);

            // Blinded scan: Windows refuses the move because of the lock, and nothing is touched.
            var blind = new InstalledVersionsTests.Report(InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow));
            Assert.Contains(new string('b', 12) + " (in use)", blind.Value.Kept);
            foreach (var name in files) Assert.True(File.Exists(Path.Combine(folder, name!)), name + ": " + blind.Text);
            Assert.Empty(Directory.GetDirectories(Path.Combine(Home, "native"), ".prune-*"));
            Assert.True(session.Rpc("tools/call", new { name = "windows", arguments = new { } })["result"] is not null, "still answering after the pass");
        }
        // Ended: the next pass removes it.
        for (var attempt = 0; Directory.Exists(folder) && attempt < 50; attempt++)
        {
            InstalledVersions.Prune(Profile, [], DateTimeOffset.UtcNow);
            if (Directory.Exists(folder)) Thread.Sleep(100);
        }
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Only_an_engine_started_from_a_version_folder_takes_the_lock()
    {
        Assert.Null(InstalledVersions.Hold(Path.Combine(root, "bin", "NexusWindows.exe")));
        Assert.Null(InstalledVersions.Hold(Path.Combine(root, "native", "stage-1", "NexusWindows.exe")));
        var running = Engine('c');
        using var held = InstalledVersions.Hold(running);
        Assert.NotNull(held);
        // A second engine of the same version shares it.
        using (var second = InstalledVersions.Hold(running)) Assert.NotNull(second);
        var refused = Record.Exception(() => Directory.Move(Path.GetDirectoryName(running)!, Path.Combine(Home, "native", ".prune-x")));
        Assert.True(refused is IOException or UnauthorizedAccessException, refused?.ToString() ?? "moved while held");
    }
}
