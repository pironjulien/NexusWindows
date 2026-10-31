using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

/// <summary>plugin/ is the single source of the plugin for the three AI apps: Claude Code and Codex read it through
/// the NexusSystem catalog, Antigravity's part ships with the engine. Every manifest names the engine's version and
/// every file it points to exists.</summary>
public sealed class PluginFolderTests
{
    static string Plugin(params string[] parts) => Path.Combine([TestEnvironment.RepositoryRoot, "plugin", .. parts]);
    static JsonNode Json(params string[] parts) => JsonNode.Parse(File.ReadAllText(Plugin(parts)))!;

    [Fact]
    public void Every_manifest_matches_the_engine_version_and_name()
    {
        foreach (var manifest in new[] { Json(".claude-plugin", "plugin.json"), Json(".codex-plugin", "plugin.json"), Json("antigravity", "plugin.json") })
        {
            Assert.Equal("nexus-windows", (string?)manifest["name"]);
            Assert.Equal(McpServer.Version, (string?)manifest["version"]);
        }
    }

    [Fact]
    public void Each_app_starts_the_stable_engine_with_its_own_profile()
    {
        static string Args(JsonNode server) => string.Join(' ', server["args"]!.AsArray().Select(a => (string?)a));
        Assert.EndsWith("--mcp --agent claude", Args(Json(".mcp.json")["mcpServers"]!["nexus-windows"]!));
        Assert.EndsWith("--mcp --agent codex", Args(Json(".codex-mcp.json")["mcpServers"]!["nexus-windows"]!));
        Assert.EndsWith("--mcp --agent antigravity", Args(Json("antigravity", "mcp_config.json")["mcpServers"]!["nexus-windows"]!));
    }

    [Fact]
    public void Referenced_files_exist()
    {
        var claude = Json(".claude-plugin", "plugin.json");
        Assert.True(File.Exists(Plugin((string)claude["icon"]!)));
        var codex = Json(".codex-plugin", "plugin.json");
        Assert.True(File.Exists(Plugin((string)codex["mcpServers"]!)));
        Assert.True(File.Exists(Plugin((string)codex["skills"]!, "nexus-windows", "SKILL.md")));
        foreach (var icon in new[] { "composerIcon", "logo" }) Assert.True(File.Exists(Plugin((string)codex["interface"]![icon]!)));
        Assert.True(File.Exists(Plugin("skills", "nexus-windows", "SKILL.md")));
        Assert.True(File.Exists(Plugin("antigravity", "skills", "nexus-windows", "SKILL.md")));
        Assert.True(File.Exists(Plugin("antigravity", (string)Json("antigravity", "plugin.json")["logo"]!)));
    }
}
