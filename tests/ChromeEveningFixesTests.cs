// The engine defects met on 05/11/2026 in the evening while driving Chrome (Chrome Web Store dashboard and
// chrome://extensions, pages NexusBrowser cannot drive), reproduced offline: no window is shown and no input is sent.
// The live reproductions (a Chrome of its own profile, the Windows Open dialog) are in the shared bench's RECETTES.md.
using System.Globalization;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class ChromeEveningFixesTests
{
    static McpServer.SessionState Claude() => new("claude") { BoundWindow = (1, "process:chrome.exe", "Chrome", 2) };

    // The dashboard as Chrome publishes it: its toolbar (address bar) beside the page, whose « Envoyer pour examen » button
    // is the outward action of the page.
    static JsonObject Dashboard() => JsonNode.Parse("""
    {"elements":[
      {"index":0,"role":"window","name":"Tableau de bord - Google Chrome","runtimeId":[42,1],"ancestors":[]},
      {"index":1,"role":"edit","name":"Barre d'adresse et de recherche","automationId":"view_1012","value":"chrome://extensions/","runtimeId":[42,2],"ancestors":[0]},
      {"index":2,"role":"document","name":"Tableau de bord","automationId":"RootWebArea","focused":true,"runtimeId":[42,3],"ancestors":[0]},
      {"index":3,"role":"button","name":"Envoyer pour examen","runtimeId":[42,4],"ancestors":[0,2]}
    ]}
    """)!.AsObject();

    [Fact]
    public void Enter_in_the_address_bar_is_not_the_page_s_send_button()
    {
        var state = Claude();
        var enterInAddressBar = new JsonObject { ["kind"] = "key", ["key"] = "enter", ["selector"] = new JsonObject { ["role"] = "edit", ["name"] = "Barre d'adresse et de recherche" } };
        // Before: refused, the page's « Envoyer pour examen » taken for the action of Enter.
        McpServer.CheckGestureForTest(state, enterInAddressBar, Dashboard(), "Tableau de bord - Google Chrome");
        Assert.Equal(-1, McpServer.KeySurface(Dashboard(), enterInAddressBar, null));

        // Enter that goes to the page (its focused element) still needs the user's accord.
        var enterInPage = new JsonObject { ["kind"] = "key", ["key"] = "enter" };
        Assert.ThrowsAny<InvalidOperationException>(() => McpServer.CheckGestureForTest(state, enterInPage, Dashboard(), "Tableau de bord - Google Chrome"));
    }

    [Fact]
    public void Keys_in_a_row_go_on_after_a_delivered_key_and_stop_after_an_uncertain_one()
    {
        JsonObject Delivered(string delivery) => new()
        {
            ["status"] = "sent-unconfirmed",
            ["keySequence"] = new JsonObject { ["delivered"] = delivery == "confirmed" ? 1 : 0, ["total"] = 1, ["deliveries"] = new JsonArray(new JsonObject { ["delivery"] = delivery }) },
        };
        var altN = new JsonObject { ["kind"] = "key", ["key"] = "alt+n" };
        var ctrlA = new JsonObject { ["kind"] = "key", ["key"] = "ctrl+a" };
        Assert.True(McpServer.KeyFollowsDeliveredKey(altN, Delivered("confirmed"), ctrlA));
        Assert.False(McpServer.KeyFollowsDeliveredKey(altN, Delivered("uncertain"), ctrlA));
        Assert.False(McpServer.KeyFollowsDeliveredKey(altN, Delivered("confirmed"), new JsonObject { ["kind"] = "click", ["point"] = new JsonObject() }));
    }

    // chrome://extensions: the cards are not published, their controls are siblings of one group.
    static JsonObject Extensions(string first = "NexusBrowser", string second = "Google Docs hors connexion") => JsonNode.Parse($$"""
    {"elements":[
      {"index":0,"role":"window","name":"Extensions - Google Chrome","runtimeId":[42,1],"ancestors":[]},
      {"index":1,"role":"document","name":"Extensions","automationId":"RootWebArea","runtimeId":[42,2],"ancestors":[0]},
      {"index":2,"role":"group","name":"","automationId":"viewManager","runtimeId":[42,3],"ancestors":[0,1]},
      {"index":3,"role":"text","name":"Toutes les extensions","runtimeId":[42,4],"ancestors":[0,1,2]},
      {"index":4,"role":"text","name":"{{first}}","automationId":"name","runtimeId":[42,5],"ancestors":[0,1,2]},
      {"index":5,"role":"text","name":"Description","runtimeId":[42,6],"ancestors":[0,1,2]},
      {"index":6,"role":"button","name":"Détails","automationId":"detailsButton","runtimeId":[42,7],"ancestors":[0,1,2]},
      {"index":7,"role":"button","name":"Supprimer","automationId":"removeButton","runtimeId":[42,8],"ancestors":[0,1,2]},
      {"index":8,"role":"text","name":"{{second}}","automationId":"name","runtimeId":[42,9],"ancestors":[0,1,2]},
      {"index":9,"role":"text","name":"Description","runtimeId":[42,10],"ancestors":[0,1,2]},
      {"index":10,"role":"button","name":"Détails","automationId":"detailsButton","runtimeId":[42,11],"ancestors":[0,1,2]},
      {"index":11,"role":"button","name":"Supprimer","automationId":"removeButton","runtimeId":[42,12],"ancestors":[0,1,2]}
    ]}
    """)!.AsObject();
    static JsonObject At(JsonObject snapshot, int index) => snapshot["elements"]!.AsArray().OfType<JsonObject>().Single(e => e["index"]!.GetValue<int>() == index);

    [Fact]
    public void A_repeated_control_carries_its_row_and_an_old_index_never_reaches_the_neighbouring_extension()
    {
        var observed = Extensions(); McpServer.AnnotateRowContext(observed);
        Assert.Equal("NexusBrowser", At(observed, 7)["context"]!.GetValue<string>());
        Assert.Equal("Google Docs hors connexion", At(observed, 11)["context"]!.GetValue<string>());
        Assert.Null(At(observed, 4)["context"]); // a unique label has no row to tell

        // Named with its row, as the look shows it: that very control, bound to its identity.
        var state = Claude();
        var step = McpServer.NormalizeStep(state, observed, JsonNode.Parse("""{"do":"click","target":"Supprimer (NexusBrowser)"}""")!.AsObject());
        Assert.NotNull(step["observedIdentity"]);

        // The page re-binds the cards (NexusBrowser removed elsewhere, the list shifted): the same runtime identity now
        // shows the neighbouring extension. Before: the click went to it; now: refused before input.
        var shifted = Extensions("Google Docs hors connexion", "McAfee"); McpServer.AnnotateRowContext(shifted);
        Assert.ThrowsAny<ActionRefusedException>(() => McpServer.NormalizeStep(state, shifted, JsonNode.Parse("""{"do":"click","target":"Supprimer (NexusBrowser)"}""")!.AsObject()));
    }

    [Fact]
    public void A_page_title_change_keeps_indices_and_identities()
    {
        var before = Extensions(); var after = Extensions();
        At(after, 0)["name"] = "Extensions (1) - Google Chrome"; At(after, 1)["name"] = "Extensions (1)";
        Assert.True(JsonNode.DeepEquals(McpServer.StableIdentityPart(before, At(before, 1)), McpServer.StableIdentityPart(after, At(after, 1))));
        Assert.True(JsonNode.DeepEquals(McpServer.StableIdentityPart(before, At(before, 0)), McpServer.StableIdentityPart(after, At(after, 0))));
        // A window that is not named after its page keeps its name in its identity.
        var renamed = Extensions(); At(renamed, 0)["name"] = "Another window";
        Assert.False(JsonNode.DeepEquals(McpServer.StableIdentityPart(before, At(before, 0)), McpServer.StableIdentityPart(renamed, At(renamed, 0))));
        Assert.Equal("Essai — prêt", McpServer.TabName("Essai — prêt - Utilisation de la mémoire - 17,7 Mo"));
        Assert.True(McpServer.InWebPage(before, At(before, 7)));
        Assert.False(McpServer.InWebPage(Dashboard(), At(Dashboard(), 1)));
    }

    // The Windows Open dialog Chrome shows: « Nom du fichier » (an editable combo box with its own Edit) right under the
    // window, beside a file list that refreshes itself.
    static JsonObject OpenDialog(int files) => new()
    {
        ["processId"] = 39876, ["processStartedUtc"] = "2026-11-05T18:49:49Z", ["revision"] = "chrome",
        ["frame"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 1264, ["height"] = 712 },
        ["elements"] = new JsonArray([
            new JsonObject { ["index"] = 0, ["depth"] = 0, ["role"] = "window", ["name"] = "Ouvrir", ["automationId"] = "", ["runtimeId"] = new JsonArray(42, 100), ["nativeWindow"] = 100, ["ancestors"] = new JsonArray(), ["visible"] = true },
            new JsonObject { ["index"] = 1, ["depth"] = 1, ["role"] = "list", ["name"] = "Vue d'éléments", ["automationId"] = "", ["runtimeId"] = new JsonArray(42, 101), ["nativeWindow"] = 101, ["ancestors"] = new JsonArray(0), ["visible"] = true },
            .. Enumerable.Range(0, files).Select(i => (JsonNode)new JsonObject { ["index"] = 10 + i, ["depth"] = 2, ["role"] = "listitem", ["name"] = "fichier " + i, ["automationId"] = i.ToString(CultureInfo.InvariantCulture), ["runtimeId"] = new JsonArray(42, 101, i), ["nativeWindow"] = 101, ["ancestors"] = new JsonArray(0, 1), ["visible"] = true }),
            new JsonObject { ["index"] = 2, ["depth"] = 1, ["role"] = "combobox", ["name"] = "Nom du fichier :", ["automationId"] = "1148", ["runtimeId"] = new JsonArray(42, 102), ["nativeWindow"] = 102, ["ancestors"] = new JsonArray(0), ["visible"] = true, ["bounds"] = new JsonArray(10, 10, 300, 20) },
            new JsonObject { ["index"] = 3, ["depth"] = 2, ["role"] = "edit", ["name"] = "Nom du fichier :", ["automationId"] = "", ["runtimeId"] = new JsonArray(-1, 102, 3, 0), ["nativeWindow"] = 103, ["ancestors"] = new JsonArray(0, 2), ["visible"] = true, ["bounds"] = new JsonArray(10, 10, 280, 20) },
        ]),
    };

    [Fact]
    public void Typing_the_file_name_survives_a_file_list_that_refreshes()
    {
        var steps = new JsonArray(new JsonObject { ["kind"] = "typeText", ["text"] = "essai.html", ["focus"] = new JsonObject { ["role"] = "edit", ["name"] = "Nom du fichier :" } });
        // Before: « Interface structure changed in the observed target/context » as soon as one file was added to the list.
        Assert.True(McpServer.ValidateNativeObservation(OpenDialog(3), OpenDialog(4), new JsonObject(), steps));
        // The field itself replaced: still refused.
        var replaced = OpenDialog(4); At(replaced, 2)["nativeWindow"] = 999;
        Assert.ThrowsAny<ActionRefusedException>(() => McpServer.ValidateNativeObservation(OpenDialog(3), replaced, new JsonObject(),
            new JsonArray(new JsonObject { ["kind"] = "typeText", ["text"] = "essai.html", ["focus"] = new JsonObject { ["role"] = "edit", ["name"] = "Nom du fichier :" } })));
    }

    [Fact]
    public void The_file_name_then_Ouvrir_in_one_act_survive_the_refreshing_file_list()
    {
        JsonArray Steps() => new(
            new JsonObject { ["kind"] = "typeText", ["text"] = "essai.html", ["focus"] = new JsonObject { ["role"] = "combobox", ["name"] = "Nom du fichier :" } },
            new JsonObject { ["kind"] = "click", ["selector"] = new JsonObject { ["role"] = "combobox", ["name"] = "Nom du fichier :" } });
        Assert.False(McpServer.ValidateNativeObservation(OpenDialog(3), OpenDialog(5), new JsonObject(), Steps()));
        // A step without a named target keeps the whole-window guard.
        var blind = Steps(); blind.Add(new JsonObject { ["kind"] = "key", ["key"] = "enter" });
        Assert.ThrowsAny<ActionRefusedException>(() => McpServer.ValidateNativeObservation(OpenDialog(3), OpenDialog(5), new JsonObject(), blind));
    }

    [Fact]
    public void Keys_after_a_click_in_a_field_go_on_in_the_same_act()
    {
        var click = new JsonObject { ["kind"] = "click", ["selector"] = new JsonObject { ["role"] = "edit", ["name"] = "Code" } };
        var sent = new JsonObject { ["status"] = "sent-unconfirmed", ["technique"] = "background:simulated-activation" };
        Assert.True(McpServer.KeyFollowsDeliveredKey(click, sent, new JsonObject { ["kind"] = "key", ["key"] = "alt+1" }));
        Assert.False(McpServer.KeyFollowsDeliveredKey(click, sent, new JsonObject { ["kind"] = "key", ["key"] = "alt+1", ["target"] = "Autre" }));
        var button = new JsonObject { ["kind"] = "click", ["selector"] = new JsonObject { ["role"] = "button", ["name"] = "Valider" } };
        Assert.False(McpServer.KeyFollowsDeliveredKey(button, sent, new JsonObject { ["kind"] = "key", ["key"] = "enter" }));
        // Alt in the background, Chromium only: never what Windows itself does with Alt.
        Assert.Equal([0x09, 0x1B, 0x20, 0x73], Keys.AltForWindows);
        // The Open dialog's « Ouvrir » is a split button: aimed as a button, it is found.
        var dialog = OpenDialog(1); dialog["elements"]!.AsArray().Add(new JsonObject { ["index"] = 4, ["depth"] = 1, ["role"] = "splitbutton", ["name"] = "Ouvrir", ["automationId"] = "1", ["runtimeId"] = new JsonArray(42, 104), ["nativeWindow"] = 104, ["ancestors"] = new JsonArray(0), ["visible"] = true });
        Assert.Equal("splitbutton", McpServer.NormalizeStep(Claude(), dialog, JsonNode.Parse("""{"do":"click","target":{"role":"button","name":"Ouvrir"}}""")!.AsObject())["selector"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void Chromium_reveal_is_kept_for_covered_browser_windows()
    {
        var result = SelfTest.Node(ComUiaSelfTests.ChromiumWarmupSelfTest());
        Assert.True(result["ok"].B());
        Assert.True(result["checks"].L() >= 40);
    }
}
