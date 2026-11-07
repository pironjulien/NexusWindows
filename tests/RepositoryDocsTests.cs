// Public documentation kept true by the tests: no README states a product version by hand (the release badge does, so
// it can never lag behind), and CHANGELOG.md follows Keep a Changelog 1.1.0 (an Unreleased section, only its six change
// types, a comparison link for every version).
using System.Text.RegularExpressions;

namespace NexusWindows.Tests;

public sealed partial class RepositoryDocsTests
{
    static readonly string Root = TestEnvironment.RepositoryRoot;
    static readonly string[] ChangeTypes = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"];
    const string Repository = "https://github.com/pironjulien/NexusWindows/";

    [Fact]
    public void No_readme_states_the_product_version_by_hand()
    {
        var readmes = new[] { "README.md", "claude/README.md", "codex/README.md", "agy/README.md", "plugin/antigravity/README.md" }
            .Select(path => Path.Combine(Root, path)).Where(File.Exists).ToList();
        Assert.True(readmes.Count >= 4);
        foreach (var readme in readmes)
            Assert.DoesNotMatch(StatedVersion(), File.ReadAllText(readme));
    }

    [Fact]
    public void The_changelog_follows_keep_a_changelog()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, "CHANGELOG.md"));
        var versions = lines.Select(line => VersionHeading().Match(line)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal("Unreleased", versions[0]);
        Assert.Contains(McpServer.Version, versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        var types = lines.Where(line => line.StartsWith("### ", StringComparison.Ordinal)).Select(line => line[4..].Trim()).Distinct();
        Assert.All(types, type => Assert.Contains(type, ChangeTypes));
        var links = lines.Select(line => LinkDefinition().Match(line)).Where(m => m.Success).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        foreach (var version in versions)
        {
            Assert.True(links.TryGetValue(version, out var link), $"No link for [{version}].");
            Assert.StartsWith(Repository, link, StringComparison.Ordinal);
        }
        Assert.EndsWith($"compare/v{versions[1]}...HEAD", links["Unreleased"], StringComparison.Ordinal);
        Assert.EndsWith($"...v{McpServer.Version}", links[McpServer.Version], StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^Version \d+\.\d+\.\d+", RegexOptions.Multiline)]
    private static partial Regex StatedVersion();

    [GeneratedRegex(@"^## \[([^\]]+)\]")]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^\[([^\]]+)\]: (\S+)$")]
    private static partial Regex LinkDefinition();
}
