// What made a two-line task slow on 05/11/2026 (an « OK » asked of Codex and Antigravity hidden in the notification
// area: 64 s and 14 calls), reproduced offline: no window is shown and no input is sent.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class AgentPaceTests
{
    static JsonObject CodexSnapshot(bool secondShown)
    {
        var flags = secondShown ? "[]" : "[\"offscreen\"]";
        return JsonNode.Parse($$"""
        {"elements":[
          {"index":0,"role":"window","name":"ChatGPT","runtimeId":[1],"ancestors":[]},
          {"index":294,"role":"button","name":"Nouveau chat","runtimeId":[2],"ancestors":[0]},
          {"index":320,"role":"button","name":"Nouveau chat","runtimeId":[3],"ancestors":[0],"flags":{{flags}}}
        ]}
        """)!.AsObject();
    }

    [Fact]
    public void Two_buttons_with_the_same_name_resolve_to_the_one_on_screen()
    {
        var state = new McpServer.SessionState("claude");
        // Before 3.16.20: refused, "Ambiguous target \"Nouveau chat\": #294 button, #320 button".
        var step = McpServer.NormalizeStep(state, CodexSnapshot(secondShown: false), JsonNode.Parse("""{"do":"click","target":"Nouveau chat"}""")!.AsObject());
        Assert.NotNull(step["selector"]);
        // Both on screen: still refused, never a guess.
        var refused = Assert.Throws<ActionRefusedException>(() => McpServer.NormalizeStep(state, CodexSnapshot(secondShown: true), JsonNode.Parse("""{"do":"click","target":"Nouveau chat"}""")!.AsObject()));
        Assert.Contains("Ambiguous", refused.Message);
    }

    [Fact]
    public void A_step_naming_its_own_target_may_follow_an_unconfirmed_one()
    {
        Assert.True(McpServer.NamesOwnTarget(JsonNode.Parse("""{"do":"key","target":"Message input","keys":"Enter"}""")));
        Assert.True(McpServer.NamesOwnTarget(JsonNode.Parse("""{"kind":"click","selector":{"name":"OK"}}""")));
        // A key without target goes to whatever has the focus: the batch still stops before it.
        Assert.False(McpServer.NamesOwnTarget(JsonNode.Parse("""{"do":"key","keys":"Enter"}""")));
        Assert.False(McpServer.NamesOwnTarget(null));
    }

    static Node Element(string role, bool text) => new() { Role = role, Name = "Message input", AutomationId = "", Description = "",
        HasText = text, RuntimeId = [1], Ancestors = [], Bounds = [0, 0, 10, 10] };

    [Fact]
    public void An_editable_combobox_takes_text_and_a_select_does_not()
    {
        // Antigravity's « Message input » is a contenteditable with role combobox: before 3.16.21, "needs a native Edit control".
        Assert.True(ChromiumInput.TextTarget(Element("combobox", text: true)));
        Assert.False(ChromiumInput.TextTarget(Element("combobox", text: false)));
        Assert.True(ChromiumInput.TextTarget(Element("edit", text: false)));
        // An empty field that shows its placeholder as its value is empty.
        Assert.Equal("", ChromiumInput.WithoutPlaceholder("Travaillez avec Codex\n", "Travaillez avec Codex"));
        Assert.Equal("Bonjour", ChromiumInput.WithoutPlaceholder("Bonjour", "Travaillez avec Codex"));
    }

    [Fact]
    public void Message_text_goes_in_packets_of_64_characters()
    {
        var units = ChromiumInput.SimulatedWriteUnits("Test du NexusPad : réponds seulement OK, sans aucun outil.");
        Assert.Single(units);
        Assert.Equal(2, ChromiumInput.SimulatedWriteUnits(new string('a', ChromiumInput.TextUnit + 1)).Count);
    }
}
