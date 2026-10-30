// Declarations to Claude Code, Claude Desktop, Codex and Antigravity (Registration.cs) in a throwaway profile: the
// real %USERPROFILE% is never read or written, and no Claude CLI is called.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public sealed class RegistrationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "nexuswindows-registration-" + Guid.NewGuid().ToString("N"));
    readonly Registration.Profile profile;

    public RegistrationTests()
    {
        var home = Path.Combine(root, "home");
        profile = new Registration.Profile(home, Path.Combine(home, "AppData", "Roaming"), Path.Combine(home, "AppData", "Local"), Path.Combine(home, ".codex"), null);
        Directory.CreateDirectory(Path.Combine(home, ".gemini"));
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string Stable => profile.Stable;
    string Versioned(char c) => Path.Combine(profile.NativeRoot, new string(c, 64), "NexusWindows.exe");
    static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    static void Put(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
    Dictionary<string, string> Run(string client, Registration.Mode mode = Registration.Mode.Install, bool desktop = true, string? source = null, string? guide = null) =>
        Registration.Run(profile, [client], mode, desktop, source, guide);

    [Fact]
    public void Every_earlier_form_is_ours_and_custom_or_disabled_entries_are_not()
    {
        JsonObject Server(string command, params string[] args) => new() { ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode)a!).ToArray()) };
        Assert.True(Registration.Owned(profile, Server(Stable, "--mcp", "--agent", "claude"), "claude"));
        Assert.True(Registration.Owned(profile, Server(Versioned('a'), "--mcp", "--agent", "codex"), "codex"));
        Assert.True(Registration.Owned(profile, Server(@"${USERPROFILE}\.nexus\nexus-windows\native\" + new string('b', 64) + @"\NexusWindows.exe", "--mcp", "--agent", "claude"), "claude"));
        Assert.True(Registration.Owned(profile, Server("uvx", "--python", "3.14", "nexus-windows-claude"), "claude"));
        Assert.True(Registration.Owned(profile, Server(@"C:\x\uvx.exe", "--python", "3.14", "nexus-windows-antigravity@latest"), "antigravity"));
        Assert.True(Registration.Owned(profile, Server("npx", "-y", "nexus-windows-codex@latest"), "codex"));
        Assert.True(Registration.Owned(profile, Server(Path.Combine(profile.Home, @".claude\nexus-claude\venv\Scripts\python.exe"), "-m", "nexus_windows"), "claude"));
        Assert.True(Registration.Owned(profile, Server("pwsh", "-NoProfile", "-File", @"D:\src\NexusWindows\agy\scripts\start-computer-mcp.ps1"), "antigravity"));
        // Another agent's profile, another engine folder, extra settings, a disabled entry: never ours.
        Assert.False(Registration.Owned(profile, Server(Stable, "--mcp", "--agent", "codex"), "claude"));
        Assert.False(Registration.Owned(profile, Server(Path.Combine(profile.NativeRoot, "custom", "NexusWindows.exe"), "--mcp", "--agent", "claude"), "claude"));
        Assert.False(Registration.Owned(profile, Server(@"C:\Tools\NexusWindows.exe", "--mcp", "--agent", "claude"), "claude"));
        var extended = Server(Stable, "--mcp", "--agent", "claude"); extended["env"] = new JsonObject { ["X"] = "1" };
        Assert.False(Registration.Owned(profile, extended, "claude"));
        var disabled = Server(Stable, "--mcp", "--agent", "claude"); disabled["disabled"] = true;
        Assert.False(Registration.Owned(profile, disabled, "claude"));
        Assert.False(Registration.Owned(profile, Server("custom.exe"), "claude"));
    }

    [Fact]
    public void Claude_plugin_copies_point_at_the_stable_launcher_and_a_duplicate_user_entry_goes()
    {
        var cache = Path.Combine(profile.Home, @".claude\plugins\cache\nexus\nexus-windows\abc");
        Put(Path.Combine(profile.Home, @".claude\settings.json"), """{"enabledPlugins":{"nexus-windows@nexus":true}}""");
        Put(Path.Combine(profile.Home, @".claude\plugins\installed_plugins.json"),
            new JsonObject { ["plugins"] = new JsonObject { ["nexus-windows@nexus"] = new JsonArray(new JsonObject { ["installPath"] = cache }) } }.ToJsonString());
        Put(Path.Combine(cache, ".mcp.json"), new JsonObject { ["mcpServers"] = new JsonObject { ["nexus-windows"] = new JsonObject { ["type"] = "stdio", ["command"] = Versioned('a'), ["args"] = new JsonArray("--mcp", "--agent", "claude") } } }.ToJsonString());
        Put(Path.Combine(profile.Home, ".claude.json"), """{"numStartups":3,"mcpServers":{"nexus-windows":{"type":"stdio","command":"uvx","args":["--python","3.14","nexus-windows-claude"],"env":{"PYTHONUTF8":"1"}},"other":{"command":"x"}}}""");

        Assert.StartsWith("not aligned", Run("claude", Registration.Mode.Check, desktop: false)["Claude Code"]);
        Assert.Equal("plugin (1 copy updated), earlier user entry removed", Run("claude", desktop: false)["Claude Code"]);
        var server = Read(Path.Combine(cache, ".mcp.json"))["mcpServers"]!["nexus-windows"]!;
        Assert.Equal(Stable, (string)server["command"]!);
        Assert.Equal("[\"--mcp\",\"--agent\",\"claude\"]", server["args"]!.ToJsonString());
        var user = Read(Path.Combine(profile.Home, ".claude.json"));
        Assert.Null(user["mcpServers"]!["nexus-windows"]);
        Assert.NotNull(user["mcpServers"]!["other"]);
        Assert.Equal(3, (int)user["numStartups"]!);
        Assert.Equal("plugin", Run("claude", Registration.Mode.Check, desktop: false)["Claude Code"]);
        Assert.Single(Directory.GetFiles(profile.Backups)); // .claude.json backed up before its change
    }

    [Fact]
    public void Claude_plugin_copy_outside_the_cache_is_refused()
    {
        Put(Path.Combine(profile.Home, @".claude\settings.json"), """{"enabledPlugins":{"nexus-windows@nexus":true}}""");
        Put(Path.Combine(profile.Home, @".claude\plugins\installed_plugins.json"),
            new JsonObject { ["plugins"] = new JsonObject { ["nexus-windows@nexus"] = new JsonArray(new JsonObject { ["installPath"] = Path.Combine(root, "elsewhere") }) } }.ToJsonString());
        Assert.StartsWith("failed", Run("claude", desktop: false)["Claude Code"]);
    }

    [Fact]
    public void Claude_user_scope_without_plugin_or_cli_is_written_once_and_custom_entries_are_kept()
    {
        Assert.Equal("absent", Run("claude", desktop: false)["Claude Code"]); // Claude Code not installed: nothing created
        Put(Path.Combine(profile.Home, ".claude.json"), """{"mcpServers":{}}""");
        Assert.Equal("declared", Run("claude", desktop: false)["Claude Code"]);
        Assert.Equal(Stable, (string)Read(Path.Combine(profile.Home, ".claude.json"))["mcpServers"]!["nexus-windows"]!["command"]!);
        Assert.Equal("already declared", Run("claude", Registration.Mode.Check, desktop: false)["Claude Code"]);
        Assert.Equal("removed", Run("claude", Registration.Mode.Remove, desktop: false)["Claude Code"]);
        Put(Path.Combine(profile.Home, ".claude.json"), """{"mcpServers":{"nexus-windows":{"command":"mine.exe"}}}""");
        Assert.Equal("kept (your own entry)", Run("claude", desktop: false)["Claude Code"]);
        Assert.Equal("mine.exe", (string)Read(Path.Combine(profile.Home, ".claude.json"))["mcpServers"]!["nexus-windows"]!["command"]!);
    }

    [Fact]
    public void Claude_desktop_folders_classic_store_and_left_ones()
    {
        var classic = Path.Combine(profile.Roaming, "Claude");
        var store = Path.Combine(profile.Local, @"Packages\AnthropicPBC.Claude_abc\LocalCache\Roaming\Claude");
        var data = Path.Combine(profile.Local, @"Claude-Data\pkg");
        var left = Path.Combine(profile.Local, @"Claude-Data\old");
        foreach (var folder in new[] { classic, store, data, left }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(left, ".user-data-left.pkg"), "");
        Put(Path.Combine(store, "claude_desktop_config.json"), """{"mcpServers":{"nexus-windows":{"command":"uvx","args":["--python","3.14","nexus-windows-claude"],"env":{"PYTHONUTF8":"1"}}},"theme":"dark"}""");
        Put(Path.Combine(data, "claude_desktop_config.json"), """{"mcpServers":{"nexus-windows":{"command":"C:\\mine\\server.exe"}}}""");

        Assert.Equal("declared, declared, kept (your own entry)", Run("claude")["Claude Desktop"]);
        Assert.False(File.Exists(Path.Combine(left, "claude_desktop_config.json")));
        Assert.Equal("dark", (string)Read(Path.Combine(store, "claude_desktop_config.json"))["theme"]!);
        Assert.Equal(Stable, (string)Read(Path.Combine(classic, "claude_desktop_config.json"))["mcpServers"]!["nexus-windows"]!["command"]!);
        Assert.Equal("already declared, already declared, kept (your own entry)", Run("claude", Registration.Mode.Check)["Claude Desktop"]);
        Assert.Equal("removed, removed, kept (your own entry)", Run("claude", Registration.Mode.Remove)["Claude Desktop"]);
    }

    [Fact]
    public void Codex_table_is_appended_replaced_and_removed_without_touching_the_rest()
    {
        var config = Path.Combine(profile.CodexHome, "config.toml");
        var head = "model = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"x\"\n\n";
        Put(config, head + "[mcp_servers.\"nexus-windows\"]\ncommand = \"npx\"\nargs = [\"-y\", \"nexus-windows-codex@latest\"]\nstartup_timeout_sec = 120\n\n[profiles.fast]\nmodel = \"y\"\n");
        Assert.StartsWith("not aligned", Run("codex", Registration.Mode.Check)["Codex"]);
        Assert.Equal("updated", Run("codex")["Codex"]);
        var text = File.ReadAllText(config);
        Assert.Equal(head + Registration.CodexBlock(profile) + "\n[profiles.fast]\nmodel = \"y\"\n", text);
        Assert.Equal("already declared", Run("codex", Registration.Mode.Check)["Codex"]);
        Assert.Equal("removed", Run("codex", Registration.Mode.Remove)["Codex"]);
        Assert.DoesNotContain("nexus-windows", File.ReadAllText(config));
        Assert.Contains("[profiles.fast]", File.ReadAllText(config));
        Assert.Equal("declared", Run("codex")["Codex"]);
        Assert.EndsWith(Registration.CodexBlock(profile), File.ReadAllText(config));
    }

    [Theory]
    [InlineData("[plugins.\"nexus-windows@personal\"]\nenabled = false\n", "plugin")]
    [InlineData("[ plugins . 'nexus-windows@another' ] # kept\nenabled=true\n", "plugin")]
    [InlineData("[mcp_servers.nexus-windows]\ncommand = \"custom.exe\"\nenabled = false\n", "kept (your own entry)")]
    [InlineData("[mcp_servers.nexus-windows]\ncommand = \"npx\"\nargs = [\"-y\", \"nexus-windows-codex\"]\n[mcp_servers.nexus-windows.env]\nA = \"1\"\n", "kept (your own entry)")]
    [InlineData("mcp_servers = { nexus-windows = { command = \"x\" } }\n", "kept (your own entry)")]
    public void Codex_plugin_custom_or_disabled_declarations_are_left_exactly(string content, string expected)
    {
        var config = Path.Combine(profile.CodexHome, "config.toml");
        Put(config, content);
        Assert.Equal(expected, Run("codex")["Codex"]);
        Assert.Equal(content, File.ReadAllText(config));
    }

    [Fact]
    public void Codex_duplicate_table_is_refused_before_any_write()
    {
        var config = Path.Combine(profile.CodexHome, "config.toml");
        var content = "[mcp_servers.nexus-windows]\ncommand = \"npx\"\n[mcp_servers.'nexus-windows']\ncommand = \"npx\"\n";
        Put(config, content);
        Assert.StartsWith("failed", Run("codex")["Codex"]);
        Assert.Equal(content, File.ReadAllText(config));
    }

    string AntigravitySource()
    {
        var source = Path.Combine(root, "plugin");
        Put(Path.Combine(source, "plugin.json"), """{"name":"nexus-windows"}""");
        Put(Path.Combine(source, @"skills\nexus-windows\SKILL.md"), "guide");
        Put(Path.Combine(source, "logo.png"), "png");
        Put(Path.Combine(source, "mcp_config.json"), """{"mcpServers":{"nexus-windows":{"command":"ignored"}}}""");
        return source;
    }

    [Fact]
    public void Antigravity_plugin_is_installed_checked_and_migrated_from_its_former_name()
    {
        var config = Path.Combine(profile.Home, @".gemini\config");
        Put(Path.Combine(config, "config.json"), """{"plugins":{"nexus-computer":{"enabled":false}}}""");
        Put(Path.Combine(config, @"plugins\nexus-computer\plugin.json"), "{}");
        Put(Path.Combine(config, "mcp_config.json"), """{"mcpServers":{"nexus-windows-antigravity":{"command":"uvx","args":["--python","3.14","nexus-windows-antigravity@latest"],"env":{"PYTHONUTF8":"1"}},"mine":{"command":"m"}}}""");
        Put(Path.Combine(config, @"skills\nexus-windows\SKILL.md"), "old");
        Put(Path.Combine(config, @"plugins\nexus-windows\stale.txt"), "x");
        var source = AntigravitySource();
        var guide = Path.Combine(root, "SKILL.fr.md"); File.WriteAllText(guide, "guide fr");

        Assert.StartsWith("installed", Run("antigravity", source: source, guide: guide)["Antigravity"]);
        var target = Path.Combine(config, @"plugins\nexus-windows");
        Assert.Equal("guide fr", File.ReadAllText(Path.Combine(target, @"skills\nexus-windows\SKILL.md")));
        Assert.False(File.Exists(Path.Combine(target, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(config, @"plugins\nexus-computer")));
        Assert.False(File.Exists(Path.Combine(config, @"skills\nexus-windows\SKILL.md")));
        var server = Read(Path.Combine(target, "mcp_config.json"))["mcpServers"]!["nexus-windows"]!;
        Assert.Equal(Stable, (string)server["command"]!);
        Assert.Equal("[\"--mcp\",\"--agent\",\"antigravity\"]", server["args"]!.ToJsonString());
        var global = Read(Path.Combine(config, "mcp_config.json"))["mcpServers"]!.AsObject();
        Assert.Equal(["mine"], global.Select(p => p.Key));
        Assert.False((bool)Read(Path.Combine(config, "config.json"))["plugins"]!["nexus-windows"]!["enabled"]!);
        Assert.Equal("up to date; turned off in Antigravity (Customizations > Installed)", Run("antigravity", Registration.Mode.Check, source: source, guide: guide)["Antigravity"]);
        // Without the private guide the shipped one is expected: drift until reinstalled.
        Assert.StartsWith("not aligned", Run("antigravity", Registration.Mode.Check, source: source)["Antigravity"]);
        Assert.StartsWith("removed", Run("antigravity", Registration.Mode.Remove)["Antigravity"]);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Antigravity_absent_from_this_PC_gets_nothing()
    {
        Directory.Delete(Path.Combine(profile.Home, ".gemini"));
        Assert.Equal("absent", Run("antigravity", source: AntigravitySource())["Antigravity"]);
        Assert.False(Directory.Exists(Path.Combine(profile.Home, ".gemini")));
    }

    [Fact]
    public void Antigravity_custom_declaration_is_kept_while_the_guide_is_updated()
    {
        var target = Path.Combine(profile.Home, @".gemini\config\plugins\nexus-windows");
        var custom = """{"mcpServers":{"nexus-windows":{"command":"C:\\mine.exe","args":["x"]}}}""";
        Put(Path.Combine(target, "mcp_config.json"), custom);
        Assert.StartsWith("installed", Run("antigravity", source: AntigravitySource())["Antigravity"]);
        Assert.Equal("C:\\mine.exe", (string)Read(Path.Combine(target, "mcp_config.json"))["mcpServers"]!["nexus-windows"]!["command"]!);
        Assert.Equal("guide", File.ReadAllText(Path.Combine(target, @"skills\nexus-windows\SKILL.md")));
    }

    [Fact]
    public void Maintenance_refreshes_only_an_existing_active_plugin_without_changing_any_configuration()
    {
        var source = AntigravitySource();
        var target = Path.Combine(profile.Home, @".gemini\config\plugins\nexus-windows");
        Assert.Equal("absent (not created)", Registration.RefreshInstalled(profile, source));
        Assert.False(Directory.Exists(target));
        Run("antigravity", source: source);
        var config = Path.Combine(profile.Home, @".gemini\config\config.json");
        var global = Path.Combine(profile.Home, @".gemini\config\mcp_config.json");
        Put(config, """{"plugins":{"nexus-windows":{"enabled":true}},"custom":"kept"}""");
        Put(global, """{"mcpServers":{"mine":{"command":"custom.exe"}}}""");
        var declaration = Path.Combine(target, "mcp_config.json");
        var unchanged = new[] { config, global, declaration }.ToDictionary(p => p, File.ReadAllBytes);
        Put(Path.Combine(source, @"skills\nexus-windows\SKILL.md"), "updated guide");
        Put(Path.Combine(target, "my-notes.txt"), "custom asset");
        Assert.StartsWith("updated", Registration.RefreshInstalled(profile, source));
        Assert.Equal("updated guide", File.ReadAllText(Path.Combine(target, @"skills\nexus-windows\SKILL.md")));
        Assert.Equal("custom asset", File.ReadAllText(Path.Combine(target, "my-notes.txt")));
        foreach (var (path, content) in unchanged) Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal("already up to date", Registration.RefreshInstalled(profile, source));
    }

    [Theory]
    [InlineData("{\"plugins\":{\"nexus-windows\":{\"enabled\":false}}}", false, false)]
    [InlineData("{\"plugins\":{\"nexus-windows\":false}}", false, false)]
    [InlineData("{}", true, false)]
    [InlineData("{}", false, true)]
    public void Maintenance_keeps_disabled_or_custom_plugins_exactly(string settings, bool disabled, bool custom)
    {
        var source = AntigravitySource();
        Run("antigravity", source: source);
        var target = Path.Combine(profile.Home, @".gemini\config\plugins\nexus-windows");
        Put(Path.Combine(profile.Home, @".gemini\config\config.json"), settings);
        var file = Path.Combine(target, "mcp_config.json");
        var declaration = Read(file);
        var server = declaration["mcpServers"]!["nexus-windows"]!;
        if (disabled) server["disabled"] = true;
        if (custom) server["command"] = "mine.exe";
        Put(file, declaration.ToJsonString());
        var before = Directory.GetFiles(target, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Put(Path.Combine(source, @"skills\nexus-windows\SKILL.md"), "new guide");
        Assert.StartsWith("kept", Registration.RefreshInstalled(profile, source));
        foreach (var (path, content) in before) Assert.Equal(content, File.ReadAllBytes(path));
    }
}
