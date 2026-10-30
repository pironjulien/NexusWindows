using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public sealed class PublicMaintenanceTests
{
    [Fact]
    public async Task UpdateOnly_installs_an_engine_and_preserves_all_client_files_byte_for_byte()
    {
        var root = Path.Combine(TestEnvironment.SharedHome, "maintenance-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var home = Path.Combine(profile, ".nexus", "nexus-windows");
        Directory.CreateDirectory(home);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(home, "NexusWindows.exe"));
        File.WriteAllText(Path.Combine(home, "installation.json"), "{\"previous\":true}");
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
        File.WriteAllText(Path.Combine(packageRoot, "engine", "NexusWindows.dll"), "fixture dll never executed");
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
        foreach (var (relative, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(profile, relative)));
        Assert.False(Directory.Exists(Path.Combine(profile, ".gemini", "config", "plugins")));
        Assert.False(Directory.Exists(Path.Combine(home, "backups")));
    }
}
