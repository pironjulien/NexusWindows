using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public sealed class PublicMaintenanceTests
{
    // Both engine layouts: framework-dependent (NexusWindows.dll with its .deps.json and .runtimeconfig.json) and a
    // single-file Native AOT NexusWindows.exe. The host here is the test engine (NexusWindows.Engine.*, other names).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateOnly_installs_an_engine_and_preserves_all_client_files_byte_for_byte(bool nativeAot)
    {
        var root = Path.Combine(TestEnvironment.SharedHome, "maintenance-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var home = Path.Combine(profile, ".nexus", "nexus-windows");
        Directory.CreateDirectory(home);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(home, "NexusWindows.exe"));
        File.WriteAllText(Path.Combine(home, "installation.json"), "{\"previous\":true}");
        // An earlier engine version and a launcher set aside, used by no session: the update removes them.
        var earlier = Path.Combine(home, "native", new string('c', 64));
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "NexusWindows.exe"), "earlier engine");
        File.WriteAllText(Path.Combine(home, "NexusWindows.exe.old-" + Guid.NewGuid().ToString("N")), "earlier launcher");
        var clientFiles = new Dictionary<string, string>
        {
            [".claude.json"] = "{\"mcpServers\":{\"nexus-windows\":{\"command\":\"mine.exe\"}}}",
            [@".claude\settings.json"] = "{\"enabledPlugins\":{\"nexus-windows@nexus-plugins\":false}}",
            [@".codex\config.toml"] = "[plugins.\"nexus-windows@nexus-plugins\"]\nenabled = false\n",
            [@".gemini\config\config.json"] = "{\"plugins\":{\"nexus-windows\":{\"enabled\":false}}}",
            [@".gemini\config\mcp_config.json"] = "{\"mcpServers\":{\"custom\":{\"command\":\"mine.exe\"}}}",
        };
        foreach (var (relative, content) in clientFiles)
        {
            var path = Path.Combine(profile, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        var before = clientFiles.Keys.ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(profile, p)));
        var packageRoot = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "engine"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(packageRoot, "NexusWindows.exe"));
        File.Copy(SelfTest.Engine, Path.Combine(packageRoot, "engine", "NexusWindows.exe"));
        // The engine host runs its assembly (the update's --prune-installed); the published NexusWindows.dll and its host
        // files stay fixtures.
        foreach (var file in new[] { "NexusWindows.Engine.dll", "NexusWindows.Engine.runtimeconfig.json", "NexusWindows.Engine.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(packageRoot, "engine", file));
        if (!nativeAot)
            foreach (var file in new[] { "NexusWindows.dll", "NexusWindows.deps.json", "NexusWindows.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(packageRoot, "engine", file), "fixture never executed");
        File.WriteAllText(Path.Combine(packageRoot, "engine", "garde-fous.json"), "{}");
        var zip = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(packageRoot, zip);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestEnvironment.RepositoryRoot, "get.ps1"), "-UpdateOnly", "-RefreshExistingProfiles" }) start.ArgumentList.Add(arg);
        start.Environment["NEXUS_WINDOWS_PROFILE"] = profile;
        start.Environment["NEXUS_WINDOWS_SOURCE"] = zip;
        start.Environment["NEXUS_PACK"] = "1";
        // This deliberately non-executable fixture would fail if the out-of-scope Antigravity helper were run.
        start.Environment["NEXUS_PACK_TARGETS"] = "codex";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error + await output);
        Assert.Contains("NexusWindows=updated", await output);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "installation.json")))!;
        Assert.Equal(2, manifest["Schema"].I());
        Assert.True(File.Exists(manifest["NativeExecutable"].S()));
        Assert.Equal(!nativeAot, manifest["NativeArtifacts"]!.AsObject().ContainsKey("NexusWindows.dll"));
        Assert.False(Directory.Exists(earlier), await output);
        Assert.Empty(Directory.GetFiles(home, "NexusWindows.exe.old-*"));
        Assert.Single(Directory.GetDirectories(Path.Combine(home, "native")));
        foreach (var (relative, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(profile, relative)));
        Assert.False(Directory.Exists(Path.Combine(profile, ".gemini", "config", "plugins")));
        Assert.False(Directory.Exists(Path.Combine(home, "backups")));
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task A_scanner_briefly_holding_the_fresh_engine_does_not_fail_the_installation(string shell)
    {
        // What an antivirus does to a freshly written executable: opens it for a moment (every sharing mode allowed),
        // which blocks renaming its folder. get.ps1 waits that hold out instead of failing ("being used by another
        // process", seen once in NexusSystem's pack job on a fresh Windows runner, with NexusAGYmail's get.ps1).
        var root = Path.Combine(TestEnvironment.SharedHome, "scanner-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var home = Path.Combine(profile, ".nexus", "nexus-windows");
        Directory.CreateDirectory(home);
        // An existing installation (what -UpdateOnly, NexusSystem's maintenance, updates).
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(home, "NexusWindows.exe"));
        File.WriteAllText(Path.Combine(home, "installation.json"), "{\"previous\":true}");
        var packageRoot = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "engine"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(packageRoot, "NexusWindows.exe"));
        File.Copy(SelfTest.Engine, Path.Combine(packageRoot, "engine", "NexusWindows.exe"));
        foreach (var file in new[] { "NexusWindows.Engine.dll", "NexusWindows.Engine.runtimeconfig.json", "NexusWindows.Engine.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(packageRoot, "engine", file));
        File.WriteAllText(Path.Combine(packageRoot, "engine", "garde-fous.json"), "{}");
        var zip = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(packageRoot, zip);

        string? held = null;
        bool stagedWhileHeld = false;
        using var released = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(home, "NexusWindows.exe") { IncludeSubdirectories = true };
        var scanner = new Thread(() =>
        {
            string path = watcher.WaitForChanged(WatcherChangeTypes.Created, 60_000) is { TimedOut: false } change ? Path.Join(home, change.Name) : "";
            for (int i = 0; i < 400 && held is null && path.Length > 0; i++)
            {
                try
                {
                    using var scan = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    held = path;
                    Thread.Sleep(2_000);
                    stagedWhileHeld = File.Exists(path);
                }
                catch (IOException) { Thread.Sleep(5); }  // still being copied
            }
            released.Set();
        }) { IsBackground = true };
        watcher.EnableRaisingEvents = true;
        scanner.Start();

        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestEnvironment.RepositoryRoot, "get.ps1"), "-UpdateOnly" }) start.ArgumentList.Add(arg);
        start.Environment["NEXUS_WINDOWS_PROFILE"] = profile;
        start.Environment["NEXUS_WINDOWS_SOURCE"] = zip;
        start.Environment["NEXUS_PACK"] = "1";
        start.Environment["NEXUS_PACK_TARGETS"] = "codex";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(released.Wait(60_000, TestContext.Current.CancellationToken));
        Assert.True(process.ExitCode == 0, await error + await output);
        Assert.NotNull(held);
        Assert.Contains("stage-", held, StringComparison.OrdinalIgnoreCase);
        Assert.True(stagedWhileHeld, "the hold must have overlapped the staged folder");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "installation.json")))!;
        var engine = manifest["NativeExecutable"].S();
        Assert.True(File.Exists(engine), await output);
        Assert.Equal(Path.Combine(home, "native"), Path.GetDirectoryName(Path.GetDirectoryName(engine)), ignoreCase: true);
        Assert.Empty(Directory.GetDirectories(home, "stage-*"));
    }

    // NexusFree 1.3.3's failure (07/11): its update renamed the running single-file engine aside and wrote the new
    // version at its path; sessions already open then read the new file and every call failed. Here a real engine runs an
    // MCP session from the installed version, get.ps1 -UpdateOnly installs another version, and that session keeps
    // answering from its own unchanged folder (versioned, never rewritten, kept while it runs) until it ends.
    [Fact]
    public async Task A_running_session_keeps_answering_from_its_own_version_after_an_update()
    {
        var root = Path.Combine(TestEnvironment.SharedHome, "live-update-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var home = Path.Combine(profile, ".nexus", "nexus-windows");
        Directory.CreateDirectory(home);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(home, "NexusWindows.exe"));
        File.WriteAllText(Path.Combine(home, "installation.json"), "{\"previous\":true}");

        string Package(string name, string marker)
        {
            var packageRoot = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(packageRoot, "engine"));
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(packageRoot, "NexusWindows.exe"));
            File.Copy(SelfTest.Engine, Path.Combine(packageRoot, "engine", "NexusWindows.exe"));
            foreach (var file in new[] { "NexusWindows.Engine.dll", "NexusWindows.Engine.runtimeconfig.json", "NexusWindows.Engine.deps.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(packageRoot, "engine", file));
            File.Copy(Path.Combine(TestEnvironment.RepositoryRoot, "commun", "garde-fous.json"), Path.Combine(packageRoot, "engine", "garde-fous.json"));
            File.WriteAllText(Path.Combine(packageRoot, "engine", "release.txt"), marker);  // another package, another version folder
            var zip = Path.Combine(root, name + ".zip");
            ZipFile.CreateFromDirectory(packageRoot, zip);
            return zip;
        }

        async Task<string> Update(string zip)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(TestEnvironment.RepositoryRoot, "get.ps1"), "-UpdateOnly" }) start.ArgumentList.Add(arg);
            start.Environment["NEXUS_WINDOWS_PROFILE"] = profile;
            start.Environment["NEXUS_WINDOWS_SOURCE"] = zip;
            start.Environment["NEXUS_PACK"] = "1";
            start.Environment["NEXUS_PACK_TARGETS"] = "codex";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error + await output);
            return JsonNode.Parse(File.ReadAllText(Path.Combine(home, "installation.json")))!["NativeExecutable"].S();
        }

        static void Answers(McpStdioTests.Server session, string when)
        {
            var reply = session.Rpc("tools/call", new { name = "windows", arguments = new { } });
            Assert.True(reply["result"] is not null && !(reply["result"]?["isError"]?.GetValue<bool>() ?? false), when + ": " + reply.ToJsonString());
        }

        var first = await Update(Package("first", "first"));
        var firstFolder = Path.GetDirectoryName(first)!;
        var firstFiles = Directory.GetFiles(firstFolder).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        using (var session = new McpStdioTests.Server(["--mcp", "--agent", "codex"], executable: first))
        {
            Answers(session, "before the update");
            var second = await Update(Package("second", "second"));
            Assert.NotEqual(first, second, StringComparer.OrdinalIgnoreCase);
            // The running version is neither rewritten nor removed (the update's prune keeps what a process runs).
            foreach (var (name, bytes) in firstFiles) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(firstFolder, name)));
            Answers(session, "after the update, same session");
            using (var next = new McpStdioTests.Server(["--mcp", "--agent", "codex"], executable: second)) Answers(next, "new session on the new version");
            Answers(session, "after a new session started");
        }
        // Once that session has ended, the next update removes its version.
        await Update(Package("third", "third"));
        Assert.False(Directory.Exists(firstFolder));
    }
}
