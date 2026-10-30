using System.Reflection;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public class NativeStructureTests
{
    static readonly MethodInfo Structure = typeof(McpServer).GetMethod("StructureNative", BindingFlags.NonPublic | BindingFlags.Static)!;
    static JsonObject Snapshot(string name, string role = "tab", string id = "tab-id", int depth = 1) => new()
    {
        ["elements"] = new JsonArray(new JsonObject { ["index"] = 0, ["role"] = role, ["name"] = name, ["automationId"] = id, ["depth"] = depth })
    };
    static string Digest(JsonObject snapshot) => (string)Structure.Invoke(null, [snapshot])!;

    // Chromium's IDS_TAB_AX_(HIGH_)MEMORY_USAGE in generated_resources.grd / generated_resources_fr.xtb.
    // ui/base/text/bytes_formatting.cc uses one fractional digit below 100 units, localized by FormatDouble.
    [Theory]
    [InlineData(" - Utilisation de la mémoire - 361\u00A0Mo")]
    [InlineData(" - Utilisation de la mémoire - 99,5\u00A0Mo")]
    [InlineData(" - Utilisation élevée de la mémoire - 1,2\u00A0Go")]
    [InlineData(" - Memory usage - 361 MB")]
    [InlineData(" - Memory usage - 99.5 MB")]
    [InlineData(" - High memory usage - 1.2 GB")]
    public void Only_the_terminal_chromium_memory_metric_is_ignored(string metric)
    {
        const string title = "Informations sur le package – Fait partie du groupe Nexus";
        Assert.Equal(Digest(Snapshot(title)), Digest(Snapshot(title + metric)));
    }

    [Fact]
    public void Real_tab_title_group_identity_depth_and_control_changes_remain_visible()
    {
        const string metric = " - Utilisation de la mémoire - 140\u00A0Mo";
        var original = Digest(Snapshot("Package – Nexus" + metric));
        Assert.NotEqual(original, Digest(Snapshot("Different page – Nexus" + metric)));
        Assert.NotEqual(original, Digest(Snapshot("Package – Other group" + metric)));
        Assert.NotEqual(original, Digest(Snapshot("Package – Nexus" + metric, id: "replacement")));
        Assert.NotEqual(original, Digest(Snapshot("Package – Nexus" + metric, depth: 2)));
        Assert.NotEqual(Digest(Snapshot("Package", "button")), Digest(Snapshot("Package" + metric, "button")));
        Assert.NotEqual(Digest(Snapshot("Package", "combobox")), Digest(Snapshot("Package" + metric, "combobox")));
    }

    [Theory]
    [InlineData(" - Utilisation de la mémoire - 140 Mo - Documentation")]
    [InlineData(" - Memory usage - 140 MB - Documentation")]
    [InlineData(" - Utilisation de la mémoire - inconnue")]
    public void Page_title_text_that_is_not_a_complete_terminal_metric_is_preserved(string suffix)
    {
        Assert.NotEqual(Digest(Snapshot("Package")), Digest(Snapshot("Package" + suffix)));
        Assert.NotEqual(Digest(Snapshot("Package - Documentation")), Digest(Snapshot("Package" + suffix)));
    }
}
