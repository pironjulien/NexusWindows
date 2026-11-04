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

        // Appending to a field that held text keeps the exact read back: an unexpected end is still a stop, not a success.
        var appended = Simulated(new { before = "abc", text = "chrome://ext", replace = false, suggest = "abc" + url });
        Assert.False(appended["ok"].B(), appended.ToJsonString());

        Assert.Null(ChromiumInput.ConfirmedTextPrefix(url, "chrome:/"));
        Assert.Equal(8, ChromiumInput.ConfirmedTextPrefix(url, "chrome:/", completion: true));
    }

    [Fact]
    public void Type_without_replace_in_the_empty_address_bar_is_not_stopped_by_its_suggestion()
    {
        const string url = "chrome://extensions/";
        // 3.16.17, real Chrome: type (no replace) into a new window's empty address bar stopped at 8/20, "Interaction
        // budget expired": "chrome:/" read back as the suggested address and the append path never accepted it.
        var typed = Simulated(new { before = "", text = url, replace = false, suggest = url });
        Assert.True(typed["ok"].B(), typed.ToJsonString());
        Assert.Contains("\"applied\"", typed["result"]!.ToJsonString());
        Assert.Equal(3, typed["events"].Strings().Count(e => e.StartsWith("chars:", StringComparison.Ordinal)));
        // A shorter text completed by the suggestion is accepted and the suggestion reported.
        var shorter = Simulated(new { before = "", text = "chrome://ext", replace = false, suggest = url });
        Assert.True(shorter["ok"].B(), shorter.ToJsonString());
        Assert.Contains("suggestion", shorter["result"]!.ToJsonString());
    }

    [Fact]
    public void Type_is_judged_on_the_field_value_read_back_not_on_the_acknowledged_count()
    {
        // The wait runs out just as the last characters land: 16 of 20 acknowledged, the field shows the whole text.
        // Before 3.16.18: "Simulated text stopped after input (20/20); outcome unknown" and the batch's Enter never went.
        var late = Simulated(new { before = "", text = "chrome://extensions/", replace = false, lateAck = true });
        Assert.True(late["ok"].B(), late.ToJsonString());
        Assert.Contains("\"applied\"", late["result"]!.ToJsonString());
        Assert.Contains("whole text read back", late["result"]!.ToJsonString());

        // A field that never shows the whole text stays unknown, never a success, and says what was read back.
        var lost = Simulated(new { before = "", text = "chrome://extensions/", replace = false, noAck = true });
        Assert.False(lost["ok"].B(), lost.ToJsonString());
        Assert.Contains("does not show the whole text", lost["message"].S());
    }

    [Fact]
    public void The_foreground_lease_covers_the_typing_time_of_the_batch_text()
    {
        static JsonArray Steps(string json) => JsonNode.Parse(json)!.AsArray();
        // Without text: the historical 30 s.
        Assert.Equal(McpServer.NativeInteraction.DefaultBudgetMs, McpServer.NativeInteraction.BudgetFor(Steps("""[{"do":"key","keys":"enter"}]""")));
        Assert.Equal(McpServer.NativeInteraction.DefaultBudgetMs, McpServer.NativeInteraction.BudgetFor(null));
        // type of 20 characters then Enter (the 04/11 batch): 30 s plus the typing time of the 20 characters.
        var batch = McpServer.NativeInteraction.BudgetFor(Steps("""[{"do":"type","target":"Barre d'adresse et de recherche","text":"chrome://extensions/"},{"do":"key","keys":"enter"}]"""));
        Assert.Equal(McpServer.NativeInteraction.DefaultBudgetMs + 20 * McpServer.NativeInteraction.TextMsPerCharacter, batch);
        // set counts its value too; a long text never exceeds the historical text budget.
        Assert.True(McpServer.NativeInteraction.BudgetFor(Steps("""[{"do":"set","target":"Dossier :","value":"C:\\Users"}]""")) > McpServer.NativeInteraction.DefaultBudgetMs);
        var huge = new JsonArray(new JsonObject { ["do"] = "type", ["text"] = new string('a', 100_000) });
        Assert.Equal(ChromiumInput.McpTextBudget, McpServer.NativeInteraction.BudgetFor(huge));
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

    [Fact]
    public void Chromes_box_from_its_file_dialog_process_is_routed_but_a_foreign_box_is_not()
    {
        // 04/11 (3.16.14): the box ran in Chrome's file-dialog utility process (42636), the window in 30592.
        const uint window = 30592, utility = 42636, foreign = 5000;
        string? Image(uint pid) => pid switch
        {
            window or utility => @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            foreign => @"C:\Windows\explorer.exe",
            _ => null,
        };
        Assert.True(Program.ModalBoxEligible(true, true, true, utility, window, Image));
        Assert.True(Program.ModalBoxEligible(true, true, true, window, window, Image)); // Paint: same process
        // Same executable spelled with another case: still the same application.
        Assert.True(Program.ModalBoxEligible(true, true, true, utility, window, p => p == utility ? @"c:\program files\google\chrome\application\CHROME.EXE" : Image(p)));
        // Never another application's window, nor an unowned, hidden or disabled one, nor an unreadable executable.
        Assert.False(Program.ModalBoxEligible(true, true, true, foreign, window, Image));
        Assert.False(Program.ModalBoxEligible(true, true, false, utility, window, Image));
        Assert.False(Program.ModalBoxEligible(false, true, true, utility, window, Image));
        Assert.False(Program.ModalBoxEligible(true, false, true, utility, window, Image));
        Assert.False(Program.ModalBoxEligible(true, true, true, 6000, window, Image));
    }

    [Fact]
    public void A_name_with_a_no_break_space_matches_the_name_typed_with_a_plain_space()
    {
        // Chrome's box names its field "Dossier\u00a0:" (French typography); the look shows "Dossier :".
        var snapshot = JsonNode.Parse("""
        {"elements":[
          {"index":0,"role":"window","name":"Extensions - Google Chrome","runtimeId":[1],"ancestors":[]},
          {"index":1,"role":"window","name":"Sélectionner le répertoire de l'extension","runtimeId":[2],"ancestors":[0]},
          {"index":2,"role":"edit","name":"Dossier\u00a0:","value":"","runtimeId":[3],"ancestors":[0,1]},
          {"index":3,"role":"button","name":"Sélectionner\u202fun dossier","runtimeId":[4],"ancestors":[0,1]}
        ]}
        """)!.AsObject();
        var state = Claude();
        var set = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"set","target":"Dossier :","value":"C:\\ext"}""")!.AsObject());
        Assert.Equal("Dossier\u00a0:", set["selector"]!["name"]!.GetValue<string>());
        var click = McpServer.NormalizeStep(state, snapshot, JsonNode.Parse("""{"do":"click","target":"Sélectionner un dossier"}""")!.AsObject());
        Assert.Equal("Sélectionner\u202fun dossier", click["selector"]!["name"]!.GetValue<string>());
        // Both sides: a typed no-break space finds a plain-space name.
        Assert.True(TargetName.Same("Dossier :", "Dossier\u202f:"));
        Assert.True(TargetName.Contains("Dossier\u00a0:", "dossier :"));
        Assert.False(TargetName.Same("Dossier:", "Dossier :"));
    }
}
