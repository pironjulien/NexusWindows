// Anti-repeat guard of act (commun/engine/McpRepeatGuard.cs), offline: the rule of 16/10 (Gemini resent the same batch
// six times) restored for the three profiles on 02/11, with a changed window and repeat:true as the legitimate repeats.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public sealed class RepeatGuardTests
{
    static JsonObject Snapshot(double left = 100, double top = 100, string value = "", double listTop = 60, bool focused = false, int elements = 8)
    {
        var all = new JsonArray(JsonNode.Parse($$"""{"index":0,"role":"window","name":"Lab","bounds":[{{left}},{{top}},400,300]}"""),
            JsonNode.Parse($$"""{"index":1,"role":"edit","name":"Nom","value":"{{value}}","focused":{{(focused ? "true" : "false")}},"bounds":[{{left + 10}},{{top + 10}},200,20]}"""),
            JsonNode.Parse($$"""{"index":2,"role":"listitem","name":"Ligne 1","bounds":[{{left + 10}},{{top + listTop}},200,20]}"""));
        for (var i = 3; i < elements; i++) all.Add(JsonNode.Parse($$"""{"index":{{i}},"role":"text","name":"T{{i}}","bounds":[{{left + 10}},{{top + 100 + i * 20}},200,20]}"""));
        return new JsonObject { ["frame"] = JsonNode.Parse($$"""{"left":{{left}},"top":{{top}},"width":400,"height":300}"""), ["elements"] = elements < 3 ? new JsonArray(all.Take(elements).Select(e => e!.DeepClone()).ToArray()) : all };
    }

    [Fact]
    public void The_same_batch_on_an_unchanged_window_is_refused_until_a_look()
    {
        var guard = new McpServer.RepeatGuard();
        var reads = 0;
        string State() { reads++; return "S0"; }
        Assert.Null(guard.Check(7, "batch", repeat: false, State, out _));
        Assert.Equal(0, reads); // a batch never sent costs no inspection
        guard.Record(7, "batch", "S0", verified: false);
        Assert.Equal(McpServer.RepeatRefusal, guard.Check(7, "batch", repeat: false, State, out var now));
        Assert.Equal("S0", now);
        Assert.Null(guard.Check(7, "other batch", repeat: false, State, out _));
        Assert.Null(guard.Check(8, "batch", repeat: false, State, out _)); // another window
        guard.Observed(8);
        Assert.NotNull(guard.Check(7, "batch", repeat: false, State, out _)); // a look of another window releases nothing
        guard.Observed(7);
        Assert.Null(guard.Check(7, "batch", repeat: false, State, out _));
        guard.Record(7, "batch", "S0", verified: false); guard.Record(9, "batch", "S0", verified: false);
        guard.Observed(0); // the whole screen
        Assert.Null(guard.Check(7, "batch", repeat: false, State, out _));
        Assert.Null(guard.Check(9, "batch", repeat: false, State, out _));
    }

    [Fact]
    public void A_changed_window_lets_the_same_batch_through_and_becomes_its_new_reference()
    {
        var guard = new McpServer.RepeatGuard();
        guard.Record(7, "next page", "page 1", verified: true);
        Assert.Null(guard.Check(7, "next page", repeat: false, () => "page 2", out var now));
        guard.Record(7, "next page", now!, verified: true);
        Assert.Null(guard.Check(7, "next page", repeat: false, () => "page 3", out now));
        guard.Record(7, "next page", now!, verified: true);
        // The last page: the click changed nothing, the same click again is blind.
        Assert.Equal(McpServer.RepeatRefusal, guard.Check(7, "next page", repeat: false, () => "page 3", out _));
    }

    [Fact]
    public void Repeat_true_repeats_a_verified_batch_never_an_uncertain_or_failed_one()
    {
        var guard = new McpServer.RepeatGuard();
        guard.Record(7, "volume up", "S", verified: true);
        Assert.Null(guard.Check(7, "volume up", repeat: true, () => "S", out _));
        Assert.Equal(McpServer.RepeatRefusal, guard.Check(7, "volume up", repeat: false, () => "S", out _));
        guard.Record(7, "volume up", "S", verified: false);
        Assert.Equal(McpServer.RepeatUncertainRefusal, guard.Check(7, "volume up", repeat: true, () => "S", out _));
        Assert.Contains("look", McpServer.RepeatUncertainRefusal);
        Assert.Contains("nothing was sent", McpServer.RepeatRefusal);
    }

    [Fact]
    public void The_signature_is_the_batch_not_its_window_designation_or_presentation()
    {
        var batch = JsonNode.Parse("""{"window":"Bloc-notes","steps":[{"do":"type","target":"Texte","text":"lait"}],"then":"auto"}""")!.AsObject();
        var same = JsonNode.Parse("""{"settle_ms":500,"steps":[{"text":"lait","target":"Texte","do":"type"}],"hwnd":42,"repeat":true,"observation":"obs-3-ab","then":"none"}""")!.AsObject();
        Assert.Equal(McpServer.RepeatSignature(batch), McpServer.RepeatSignature(same));
        foreach (var different in new[]
        {
            """{"steps":[{"do":"type","target":"Texte","text":"oeufs"}]}""",
            """{"steps":[{"do":"type","target":"Texte","text":"lait"}],"confirm":true}""",
            """{"steps":[{"do":"type","target":"Texte","text":"lait"}],"foreground":true}""",
            """{"steps":[{"do":"type","target":"Texte","text":"lait"},{"do":"key","key":"enter"}]}""",
        })
            Assert.NotEqual(McpServer.RepeatSignature(batch), McpServer.RepeatSignature(JsonNode.Parse(different)!.AsObject()));
    }

    [Fact]
    public void The_window_state_ignores_a_move_and_focus_but_sees_values_scrolling_and_pixels_of_a_poor_tree()
    {
        var reference = McpServer.RepeatStateDigest(Snapshot());
        Assert.Equal(reference, McpServer.RepeatStateDigest(Snapshot(left: 900, top: 40)));
        Assert.Equal(reference, McpServer.RepeatStateDigest(Snapshot(focused: true)));
        Assert.NotEqual(reference, McpServer.RepeatStateDigest(Snapshot(value: "lait")));
        Assert.NotEqual(reference, McpServer.RepeatStateDigest(Snapshot(listTop: 40)));
        // Enough elements: the tree tells, pixels are never read.
        Assert.Equal(reference, McpServer.RepeatStateDigest(Snapshot(), () => throw new InvalidOperationException("pixels read")));
        // A canvas or a game: the pixels tell.
        var poor = Snapshot(elements: 2);
        Assert.NotEqual(McpServer.RepeatStateDigest(poor, () => "image 1"), McpServer.RepeatStateDigest(poor, () => "image 2"));
        Assert.Equal(McpServer.RepeatStateDigest(poor, () => "image 1"), McpServer.RepeatStateDigest(poor, () => "image 1"));
    }
}
