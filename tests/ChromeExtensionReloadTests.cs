// The three engine defects met on 04/11/2026 while reloading an unpacked extension in chrome://extensions (shared bench,
// RECETTES.md « Recharger une extension non empaquetée »), reproduced offline: no window is shown and no input is sent.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class ChromeExtensionReloadTests
{
    // Chrome's tree as the look reads it: the address bar and the page both report keyboard focus.
    static JsonObject ChromeSnapshot(string addressValue) => JsonNode.Parse($$"""
    {"elements":[
      {"index":0,"role":"window","name":"Extensions - Google Chrome","runtimeId":[1],"ancestors":[]},
      {"index":1,"role":"edit","name":"Barre d'adresse et de recherche","value":"{{addressValue}}","focused":true,"runtimeId":[2],"ancestors":[0]},
      {"index":2,"role":"document","name":"Extensions","focused":true,"runtimeId":[3],"ancestors":[0]},
      {"index":3,"role":"edit","name":"Rechercher dans les extensions","value":"","runtimeId":[4],"ancestors":[0,2]}
    ]}
    """)!.AsObject();

    static McpServer.SessionState Claude() => new("claude");

    [Fact]
    public void Enter_without_target_goes_to_the_field_the_batch_just_set()
    {
        var snapshot = ChromeSnapshot("chrome://extensions/");
        // Before 3.16.13: refused, "Keyboard focus is ambiguous; no key sent".
        var refused = Assert.Throws<ActionRefusedException>(() => McpServer.FocusedKeyLeaf(snapshot, null));
        Assert.Contains("ambiguous", refused.Message);

        var state = Claude();
        McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"set","target":"Barre d'adresse et de recherche","value":"chrome://extensions/"}""")!.AsObject());
        Assert.NotNull(state.RetainedFocus);
        var leaf = McpServer.FocusedKeyLeaf(snapshot, state.RetainedFocus);
        Assert.Equal("Barre d'adresse et de recherche", leaf!["name"]!.GetValue<string>());

        // The aimed field that is not among the focused leaves never settles the ambiguity.
        var other = new JsonObject { ["role"] = "edit", ["name"] = "Rechercher dans les extensions" };
        Assert.Throws<ActionRefusedException>(() => McpServer.FocusedKeyLeaf(snapshot, other));
    }

    [Fact]
    public void Type_says_it_appended_and_replace_true_replaces_the_field()
    {
        var snapshot = ChromeSnapshot("chrome://extensions/");
        var state = Claude();
        // type keeps its insertion at the end (rich editors, the shared bench), and now says so when the field held text.
        var appended = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"type","target":"Barre d'adresse et de recherche","text":"chrome://extensions/"}""")!.AsObject());
        Assert.Null(appended["replace"]);
        Assert.Equal("chrome://extensions/chrome://extensions/", appended["expect"]!["equals"]!.GetValue<string>());
        Assert.Contains("appended", McpServer.AppendedNote(appended, snapshot));

        var replaced = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"type","target":"Barre d'adresse et de recherche","text":"chrome://extensions/","replace":true}""")!.AsObject());
        Assert.True(replaced["replace"]!.GetValue<bool>());
        Assert.Equal("chrome://extensions/", replaced["expect"]!["equals"]!.GetValue<string>());
        Assert.Null(McpServer.AppendedNote(replaced, snapshot));

        // An empty field gets no note; replace is never carried by another kind of step.
        var empty = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"type","target":"Rechercher dans les extensions","text":"Nexus"}""")!.AsObject());
        Assert.Null(McpServer.AppendedNote(empty, snapshot));
        var set = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"set","target":"Rechercher dans les extensions","value":"Nexus","replace":true}""")!.AsObject());
        Assert.Null(set["replace"]);
    }

    static JsonNode Simulated(object simulatedText) => SelfTest.Json(ChromiumInput.SelfTest, new { simulatedText });

    [Fact]
    public void Replacing_text_in_the_address_bar_is_not_stopped_by_its_inline_suggestion()
    {
        const string url = "chrome://extensions/";
        // The address bar completes "chrome:/" to the known address: before 3.16.13 the read back never matched and the
        // text stopped at 8/20 when the 30 s foreground budget ran out.
        var typed = Simulated(new { before = "https://www.google.com/", text = url, replace = true, suggest = url });
        Assert.True(typed["ok"].B(), typed.ToJsonString());
        Assert.Equal(url, typed["value"].S());
        Assert.Equal(3, typed["events"].Strings().Count(e => e.StartsWith("chars:", StringComparison.Ordinal)));

        // A suggestion longer than the text is reported, never hidden.
        var longer = Simulated(new { before = "", text = "chrome://ext", replace = true, suggest = url });
        Assert.True(longer["ok"].B(), longer.ToJsonString());
        Assert.Contains("suggestion", longer["result"]!.ToJsonString());

        // Appending keeps the exact read back: an unexpected end is still a stop, not a success.
        var appended = Simulated(new { before = "", text = "chrome://ext", replace = false, suggest = url });
        Assert.False(appended["ok"].B(), appended.ToJsonString());

        Assert.Null(ChromiumInput.ConfirmedTextPrefix(url, "chrome:/"));
        Assert.Equal(8, ChromiumInput.ConfirmedTextPrefix(url, "chrome:/", completion: true));
    }

    [Fact]
    public void A_target_inside_chromes_modal_box_is_routed_to_the_box_not_refused()
    {
        const long chrome = 100, bubble = 200, box = 300, folder = 301;
        bool Valid(long w) => w == box; // the hidden bubble Chrome owns is not a usable box
        long Root(long w) => w is box or folder ? box : w == chrome ? chrome : 0;
        // Chain root..target: Chrome (disabled), its box, the « Dossier : » field.
        long[] chain = [chrome, box, folder];
        // Before 3.16.13 only the owner's first enabled popup was tried: Chrome's bubble, refused.
        Assert.Equal(box, Program.ModalBoxCandidate(bubble, chain, Root, Valid));
        Assert.Equal(1, Program.ModalBoxIndex([(chrome, true), (box, false), (folder, false)], box));
        // Paint: the first enabled popup is the box itself.
        Assert.Equal(box, Program.ModalBoxCandidate(box, chain, Root, Valid));
        // A target outside any box (in Chrome itself) stays refused.
        Assert.Equal(0, Program.ModalBoxCandidate(bubble, [chrome, chrome], Root, Valid));
    }
}
