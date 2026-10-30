// Wheel at a point (shared bench recipes A and B, 21/10): DevTools page choice, port proven to the window's process,
// scroll direction and ends, rendered surface, title proof, menu paths, Expand/Collapse readback, modal boxes and
// Chromium typing units. Offline, in-process (formerly cdp.test.mjs over --cdp-selftest, --wheel-selftest,
// --scroll-choice-selftest, --surface-selftest, --prove-selftest, --session-selftest, --wheel-trace-selftest,
// --popup-selftest, --menu-selftest, --collapse-selftest, --expand-selftest, --center-selftest, --modal-selftest,
// --chromium-selftest). No page or real window.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class CdpTests
{
    static object Page(string title, string url) => new { type = "page", title, webSocketDebuggerUrl = url };
    static string Ws(string id) => $"ws://127.0.0.1:9222/devtools/page/{id}";
    static JsonNode Pick(object input) => SelfTest.Json(Cdp.SelfTest, input);

    [Fact]
    public void DevTools_page_decoded_title_exact_match_up_to_the_browser_suffix_otherwise_nothing_is_sent()
    {
        const string edge = "Labo Web — zone gauche - Microsoft Edge";
        Assert.Equal(Ws("a"), Pick(new { title = edge, targets = new[] { Page("Labo Web — zone gauche", Ws("a")), Page("Autre", Ws("b")) } })["url"].S());
        Assert.Equal(Ws("a"), Pick(new { title = "Labo - Personnel - Microsoft​ Edge", targets = new[] { Page("Labo", Ws("a")), Page("Autre", Ws("b")) } })["url"].S());
        // Tabbed Edge with a profile, title measured on 22/10 (proof C): en dash before "Microsoft Edge".
        Assert.Equal(Ws("a"), Pick(new { title = "Labo Web — prêt - Profil 1 – Microsoft​ Edge", targets = new[] { Page("Labo Web — prêt", Ws("a")) } })["url"].S());
        // /json/list HTML-escapes titles: "A &amp; B" is the tab "A & B", never "A".
        Assert.Equal(Ws("ab"), Pick(new { title = "A & B - Google Chrome", targets = new[] { Page("A", Ws("a")), Page("A &amp; B", Ws("ab")) } })["url"].S());
        Assert.Equal(Ws("q"), Pick(new { title = "x <y> \"z\" - Chromium", targets = new[] { Page("x &lt;y&gt; &quot;z&quot;", Ws("q")), Page("x", Ws("x")) } })["url"].S());
        // A prefix is not an identity.
        Assert.Matches("No single DevTools page", Pick(new { title = "Labo Web - Microsoft Edge", targets = new[] { Page("Labo", Ws("a")), Page("Autre", Ws("b")) } })["error"].S());
        // Namesakes: ambiguous, refused.
        Assert.Matches("2 matching", Pick(new { title = edge, targets = new[] { Page("Labo Web — zone gauche", Ws("a")), Page("Labo Web — zone gauche", Ws("b")) } })["error"].S());
        // A single page with another title (Electron setTitle) is no longer accepted.
        Assert.Matches("0 matching", Pick(new { title = "Mon appli", targets = new[] { Page("index", Ws("e")) } })["error"].S());
        Assert.Matches("0 pages", Pick(new { title = "x", targets = Array.Empty<object>() })["error"].S());
        // The page WebSocket stays on the proven host and port.
        Assert.Matches("proven host and port", Pick(new { title = "Labo", targets = new[] { Page("Labo", "ws://127.0.0.1:9333/devtools/page/z") } })["error"].S());
        Assert.Matches("proven host and port", Pick(new { title = "Labo", targets = new[] { Page("Labo", "ws://198.51.100.5:9222/devtools/page/z") } })["error"].S());
        Assert.Equal(Ws("z"), Pick(new { title = "Labo", targets = new[] { Page("Labo", "ws://localhost:9222/devtools/page/z") } })["url"].S());
    }

    [Fact]
    public void DevTools_port_from_the_window_process_command_line_and_listened_to_by_that_process_only()
    {
        Assert.Equal(9333, Pick(new { title = "x", commandLine = "msedge.exe --remote-debugging-port=9333 --x", targets = Array.Empty<object>() })["port"].I());
        Assert.Equal(9227, Pick(new { title = "x", commandLine = "electron.exe --remote-debugging-port 9227", targets = Array.Empty<object>() })["port"].I());
        Assert.Null(Pick(new { title = "x", commandLine = "chrome.exe --remote-debugging-port=0", targets = Array.Empty<object>() })["port"]);
        Assert.Null(Pick(new { title = "x", commandLine = "chrome.exe --remote-debugging-pipe", targets = Array.Empty<object>() })["port"]);
        bool Owned(params object[] listeners) => Pick(new { title = "x", targets = Array.Empty<object>(), pid = 200, listeners })["owned"].B();
        Assert.True(Owned(new { address = "127.0.0.1", pid = 200 }));
        Assert.True(Owned(new { address = "0.0.0.0", pid = 200 }));
        Assert.False(Owned(new { address = "127.0.0.1", pid = 100 }));
        Assert.False(Owned(new { address = "127.0.0.1", pid = 300 }));
        Assert.False(Owned(new { address = "127.0.0.1", pid = 200 }, new { address = "0.0.0.0", pid = 100 }));
        Assert.False(Owned(new { address = "192.0.2.5", pid = 200 }));
        Assert.False(Owned());
    }

    [Fact]
    public void Wheel_at_a_point_only_the_vertical_windows_sign_is_inverted_real_ends_only()
    {
        var r = SelfTest.Json(ScrollPlan.SelfTest, new object[] {
            new { deltaY = -120, deltaX = 0 }, new { deltaY = 120, deltaX = 0 }, new { deltaY = 0, deltaX = 120 }, new { deltaY = 0, deltaX = -120 },
            new { deltaY = -120, deltaX = 0, percent = 99.95 }, new { deltaY = 120, deltaX = 0, percent = 0.05 }, new { deltaY = -240, deltaX = 0, percent = 100 },
            new { deltaY = 0, deltaX = 0 } }).AsArray();
        var (down, up, right, left, nearEnd, nearStart, atEnd, none) = (r[0]!, r[1]!, r[2]!, r[3]!, r[4]!, r[5]!, r[6]!, r[7]!);
        Assert.Equal((true, true, 4L, 120L), (down["vertical"].B(), down["forward"].B(), down["amount"].L(), down["cdpY"].L()));
        Assert.Equal((false, 1L, -120L), (up["forward"].B(), up["amount"].L(), up["cdpY"].L()));
        Assert.Equal((false, true, 4L, 120L), (right["vertical"].B(), right["forward"].B(), right["amount"].L(), right["cdpX"].L()));
        Assert.Equal((false, 1L, -120L), (left["forward"].B(), left["amount"].L(), left["cdpX"].L()));
        Assert.False(nearEnd["atEnd"].B());
        Assert.False(nearStart["atEnd"].B());
        Assert.Equal((true, 2L), (atEnd["atEnd"].B(), atEnd["notches"].L()));
        Assert.Matches("exactly one axis", none["error"].S());
    }

    [Fact]
    public void Scroll_container_under_the_point_smallest_area_deepest_when_equal_otherwise_ambiguous()
    {
        JsonNode Choose(string json) => SelfTest.Json(ScrollChoice.SelfTest, json);
        Assert.Equal(1, Choose("""{"count":3,"areas":[900,400,1600]}""")["index"].I());
        Assert.Equal(1, Choose("""{"count":2,"areas":[400,400],"parents":{"1":0}}""")["index"].I());
        Assert.Equal(0, Choose("""{"count":2,"areas":[400,400],"parents":{"0":1}}""")["index"].I());
        Assert.Equal(2, Choose("""{"count":3,"areas":[400,400,400],"parents":{"1":0,"2":1}}""")["index"].I());
        Assert.Matches("ambiguous", Choose("""{"count":2,"areas":[400,400]}""")["error"].S());
        Assert.Equal(-1, Choose("""{"count":0,"areas":[]}""")["index"].I());
        Assert.Matches("not exhaustive", Choose("""{"count":5001,"areas":[1]}""")["error"].S());
    }

    [Fact]
    public void Rendered_surface_exactly_one_visible_surface_the_point_on_it_its_size_the_proven_page()
    {
        JsonNode Surface(string surfaces, string point = "[500,500]", string inner = "[1280,800]") =>
            SelfTest.Json(Cdp.SurfaceSelfTest, $$"""{"surfaces":{{surfaces}},"point":{{point}},"inner":{{inner}},"ratio":1.25}""");
        const string one = "[[100,100,1700,1100]]";
        SelfTest.Same("""{"index":0,"gap":[0,0]}""", Surface(one));
        Assert.Matches("2 visible rendered surfaces", Surface("[[100,100,1700,1100],[300,300,700,600]]")["error"].S());
        Assert.Matches("not on the rendered page", Surface(one, point: "[50,50]")["error"].S());
        SelfTest.Same("[2,-2]", Surface("[[100,100,1702,1098]]")["gap"]!);
        Assert.Matches("gap 3/0 px", Surface("[[100,100,1703,1100]]")["error"].S());
        Assert.Matches("unreadable", Surface(one, inner: "[0,800]")["error"].S());
    }

    [Fact]
    public void Title_proof_the_pose_is_under_the_cleanup_conditional_and_a_failed_cleanup_is_said()
    {
        JsonNode Prove(string scenario) => SelfTest.Json(Cdp.ProveSelfTest, new { scenario });
        SelfTest.Same("""{"ok":true,"pageTitle":"Labo"}""", Prove("nominal"));
        var lost = Prove("lost-pose");
        Assert.Equal("Labo", lost["pageTitle"].S());
        Assert.NotNull(lost["error"]);
        Assert.Equal("Autre", Prove("title-changed")["pageTitle"].S());
        Assert.Matches("cleanup failed on two connections", Prove("dead")["error"].S());
        Assert.Equal("ActionRefusedException", Prove("dead")["kind"].S());
        foreach (var scenario in new[] { "hwnd-stuck", "lost-pose-hwnd-stuck", "restore-error-object" })
        {
            var r = Prove(scenario);
            Assert.Equal("ActionRefusedException", r["kind"].S());
            Assert.Equal("ActionRefusedException", r["wheelKind"].S());
        }
        Assert.Matches("still shown in the window title", Prove("hwnd-stuck")["error"].S());
        Assert.Equal("BackgroundNotApplicableException", lost["wheelKind"].S());
        Assert.Equal("Labo", Prove("never-shown")["pageTitle"].S());
        Assert.Matches("binding changed", Prove("tab-switched")["error"].S());
        SelfTest.Same("""{"ok":true,"pageTitle":"Nouveau"}""", Prove("page-retitled"));
        Assert.Matches("binding changed", Prove("profile-confusion")["error"].S());
    }

    [Fact]
    public void Fallback_cleanup_goes_through_the_real_connection_factory()
    {
        var result = SelfTest.Json(Cdp.SessionSelfTest);
        Assert.True(result["cleaned"].B());
        var received = result["received"]!.AsArray();
        Assert.Single(received);
        Assert.Contains("document.title === \"nexus-test\"", received[0].S());
    }

    [Fact]
    public void DevTools_wheel_traces_lost_acknowledgement_is_unknown_and_stop_during_preparation_sends_nothing()
    {
        // Test stop folder (never the real ~/.nexus/stop).
        var home = Directory.CreateTempSubdirectory("nexus-codex-wheel-").FullName;
        try
        {
            var environment = new Dictionary<string, string?> { ["NEXUS_SHARED_HOME"] = home };
            JsonNode Trace(string scenario) => SelfTest.Json(Cdp.WheelTraceSelfTest, new { scenario }, environment);
            SelfTest.Same("""{"outcome":"unknown","started":1,"dispatched":1,"evaluated":1}""", Trace("ack-lost"));
            SelfTest.Same("""{"outcome":"stopped","started":0,"dispatched":0,"evaluated":1}""", Trace("stop-during-prepare"));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void A_winforms_drop_down_or_context_menu_is_a_transient_popup()
    {
        const long popup = 0x80000000, caption = 0x00C00000, noActivate = 0x08000000, tool = 0x80;
        var result = SelfTest.Json(Win32.PopupSelfTest, new object[] {
            new { style = popup, ex = tool, cls = "WindowsForms10.Window.808.app.0.2bf8098_r6_ad1", menu = true, linked = true },
            new { style = popup, ex = noActivate, cls = "Popup" },
            new { style = 0, ex = 0, cls = "ComboLBox" },
            new { style = popup, ex = tool, cls = "SomeOtherTool", menu = true, linked = true },
            new { style = popup | caption, ex = tool, cls = "WindowsForms10.Window.8.app", menu = true, linked = true },
            new { style = 0, ex = tool, cls = "WindowsForms10.Window.8.app", menu = true, linked = true },
            new { style = popup, ex = tool, cls = "WindowsForms10.Window.8.app", menu = false, linked = true },
            new { style = popup, ex = tool, cls = "WindowsForms10.Window.8.app", menu = true, linked = false },
            new { style = 0x96000000, ex = 0x10008, cls = "WindowsForms10.Window.20808.app.0.38a021f_r13_ad1", menu = true, linked = true },
            new { style = 0x96000000, ex = 0x10008, cls = "WindowsForms10.Window.20808.app.0.38a021f_r13_ad1", menu = false, linked = true },
        });
        SelfTest.Same("[true,true,true,false,false,false,false,false,true,false]", result);
    }

    static JsonNode Menu(string json) => SelfTest.Json(MenuSearch.SelfTest, json);

    [Fact]
    public void Menu_path_each_segment_directly_under_the_previous_one_ambiguous_or_unreadable_refused()
    {
        const string nested = """[{"id":0,"role":"menu","name":"","children":[1]},{"id":1,"role":"menuitem","name":"Fichier","children":[2]},{"id":2,"role":"menu","name":"","children":[3]},{"id":3,"role":"menuitem","name":"Récents","children":[4]},{"id":4,"role":"menu","name":"","children":[5]},{"id":5,"role":"menuitem","name":"Ouvrir","children":[]}]""";
        Assert.Matches("not found", Menu($$"""{"nodes":{{nested}},"root":0,"path":[{"name":"Fichier"},{"name":"Ouvrir"}]}""")["error"].S());
        SelfTest.Same("""{"id":5}""", Menu($$"""{"nodes":{{nested}},"root":0,"path":[{"name":"Fichier"},{"name":"Récents"},{"name":"Ouvrir"}]}"""));
        const string twins = """[{"id":0,"role":"menu","name":"","children":[1]},{"id":1,"role":"menuitem","name":"Fichier","children":[2]},{"id":2,"role":"menu","name":"","children":[3,4]},{"id":3,"role":"menuitem","name":"Ouvrir","children":[],"automationId":"a"},{"id":4,"role":"menuitem","name":"Ouvrir","children":[],"automationId":"b"}]""";
        Assert.Matches("ambiguous", Menu($$"""{"nodes":{{twins}},"root":0,"path":[{"name":"Fichier"},{"name":"Ouvrir"}]}""")["error"].S());
        SelfTest.Same("""{"id":4}""", Menu($$"""{"nodes":{{twins}},"root":0,"path":[{"name":"Fichier"},{"name":"Ouvrir","automationId":"b"}]}"""));
        const string unreadable = """[{"id":0,"role":"menu","name":"","children":[1,2]},{"id":1,"role":"pane","name":"","unreadable":true},{"id":2,"role":"menuitem","name":"Fichier","children":[]}]""";
        Assert.Matches("could not be read completely", Menu($$"""{"nodes":{{unreadable}},"root":0,"path":[{"name":"Fichier"}]}""")["error"].S());
        var wide = new JsonArray(new JsonObject { ["id"] = 0, ["role"] = "menu", ["name"] = "", ["children"] = new JsonArray(Enumerable.Range(1, 2001).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) });
        for (var i = 1; i <= 2001; i++) wide.Add(new JsonObject { ["id"] = i, ["role"] = "pane", ["name"] = "", ["children"] = i == 1 ? new JsonArray(2002) : new JsonArray() });
        wide.Add(new JsonObject { ["id"] = 2002, ["role"] = "menuitem", ["name"] = "Fichier", ["children"] = new JsonArray() });
        Assert.Matches("too large", Menu($$"""{"nodes":{{wide.ToJsonString()}},"root":0,"path":[{"name":"Fichier"}]}""")["error"].S());
    }

    [Fact]
    public void WinForms_menu_item_click_path_from_the_observed_ancestry_with_automationId()
    {
        static string Sel(string ancestry, string selector) => "{\"ancestry\":" + ancestry + ",\"selector\":" + selector + "}";
        const string ancestry = """[{"role":"window","name":"Banc"},{"role":"menuitem","name":"Fichier","automationId":"fileMenu"},{"role":"menu","name":""}]""";
        SelfTest.Same("""[{"name":"Fichier","automationId":"fileMenu"},{"name":"Ouvrir","automationId":"openItem"}]""",
            Menu(Sel(ancestry, """{"role":"menuitem","name":"Ouvrir","automationId":"openItem"}"""))["segments"]!);
        SelfTest.Same("""[{"name":"Fichier","automationId":"fileMenu"},{"name":"Ouvrir","automationId":null}]""",
            Menu(Sel(ancestry, """{"role":"menuitem","name":"Ouvrir","ancestor":{"name":"Fichier"}}"""))["segments"]!);
        Assert.Matches("required ancestor absent", Menu(Sel(ancestry, """{"role":"menuitem","name":"Ouvrir","ancestor":{"name":"Édition"}}"""))["error"].S());
        const string opened = """[{"role":"window","name":"Banc"},{"role":"menu","name":"","nativeWindow":300}]""";
        Assert.Single(Menu(Sel(opened, """{"role":"menuitem","name":"Copier","ancestor":{"window":300}}"""))["segments"]!.AsArray());
        Assert.Matches("required ancestor absent", Menu(Sel(opened, """{"role":"menuitem","name":"Copier","ancestor":{"window":200}}"""))["error"].S());
    }

    [Fact]
    public void Context_menu_command_resolved_in_the_open_popup_of_its_observed_ancestry()
    {
        SelfTest.Same("""{"scope":200}""", Menu("""{"scopeOf":100,"ancestry":[{"role":"window","name":"Banc","nativeWindow":100},{"role":"menu","name":"Menu contextuel","nativeWindow":200}]}"""));
        SelfTest.Same("""{"scope":100}""", Menu("""{"scopeOf":100,"ancestry":[{"role":"window","name":"Banc","nativeWindow":100},{"role":"menuitem","name":"Fichier"}]}"""));
        SelfTest.Same("""{"scope":100}""", Menu("""{"scopeOf":100,"ancestry":[]}"""));
    }

    [Fact]
    public void Collapse_of_a_menu_opened_for_the_search_is_read_back()
    {
        var r = SelfTest.Json(ComWorker.CollapseSelfTest, new[] { "closed", "open", "throws" }).AsArray();
        Assert.Equal("closed", r[0].S());
        Assert.Matches("still open; outcome unknown", r[1].S());
        Assert.Matches("could not be closed; outcome unknown: element gone", r[2].S());
    }

    static JsonArray Expand(string json) => SelfTest.Json(ComWorker.ExpandSelfTest, json).AsArray();

    [Fact]
    public void An_expand_already_called_is_read_back()
    {
        SelfTest.Same("""["applied","sent","sent"]""", Expand("""[{"before":0,"after":1,"reached":true},{"before":0,"after":0,"reached":false},{"before":0,"after":3,"reached":false}]"""));
        SelfTest.Same("""["refusal","refusal","unknown","unknown"]""", Expand($$"""[{"hresult":{{0x80131509L}}},{"hresult":{{0x80040200L}}},{"hresult":{{0x80004005L}}},{"hresult":{{0x80040201L}}}]"""));
    }

    [Fact]
    public void Expand_never_invokes_a_second_pattern_after_a_provider_call_refusal_or_unreadable_readback()
    {
        const string i = "\"route\":true,\"pattern\":true,\"invoke\":true";
        var results = Expand($$"""[{{{i}},"reached":false},{{{i}},"reached":true},{{{i}},"failure":"refusal","reached":false},{{{i}},"failure":"disabled","reached":false},{{{i}},"failure":"refusal","reached":true},{{{i}},"failure":"unknown"},{{{i}},"failure":"read"}]""");
        var effects = new[] { "sent", "applied", "sent", "sent", "applied" };
        for (var n = 0; n < 5; n++) SelfTest.Same($$"""{"effect":"{{effects[n]}}","expandCalls":1,"invokeCalls":0}""", results[n]!);
        foreach (var unknown in results.Skip(5))
        {
            Assert.Equal("COMException", unknown!["error"].S());
            Assert.False(unknown["beforeInput"].B());
            Assert.Equal(1, unknown["expandCalls"].I());
            Assert.Equal(0, unknown["invokeCalls"].I());
        }
    }

    [Fact]
    public void Expand_keeps_invoke_available_only_when_the_first_pattern_is_absent_before_any_call()
    {
        const string m = "\"route\":true,\"pattern\":false,\"invoke\":true";
        var r = Expand($$"""[{{{m}}},{{{m}},"action":"Collapse"},{{{m}},"allowInvoke":false},{"route":true,"pattern":false,"invoke":false},{"route":true,"invoke":true,"pattern":true,"failure":"before"}]""");
        SelfTest.Same("""{"effect":"sent","expandCalls":0,"invokeCalls":1}""", r[0]!);
        foreach (var refusal in r.Skip(1))
        {
            Assert.Equal("BackgroundNotApplicableException", refusal!["error"].S());
            Assert.True(refusal["beforeInput"].B());
            Assert.Equal(0, refusal["expandCalls"].I());
            Assert.Equal(0, refusal["invokeCalls"].I());
        }
    }

    [Fact]
    public void Expand_never_mistakes_an_emergency_stop_for_the_provider_invalid_operation_hresult()
    {
        const string route = "\"route\":true,\"pattern\":true,\"invoke\":true,\"reached\":true";
        var r = Expand($$"""[{{{route}},"failure":"stopped"},{{{route}},"failure":"read-stopped"}]""");
        SelfTest.Same("""{"error":"EmergencyStopException","beforeInput":true,"expandCalls":0,"invokeCalls":0}""", r[0]!);
        SelfTest.Same("""{"error":"EmergencyStopException","beforeInput":false,"expandCalls":1,"invokeCalls":0}""", r[1]!);
    }

    [Fact]
    public void The_com_worker_and_bridge_preserve_the_actual_input_boundary_even_when_error_hints_contradict_it()
    {
        string Cases(string phase, bool hint, params string[] errors) => "[" + string.Join(",", errors.Select(e => $$"""{"phase":"{{phase}}","error":"{{e}}","hint":{{(hint ? "true" : "false")}}}""")) + "]";
        var before = Expand(Cases("before", false, "com", "missing", "refused", "stop"));
        Assert.Equal(["BackgroundNotApplicableException", "BackgroundNotApplicableException", "ActionRefusedException", "EmergencyStopException"], before.Select(r => r!["error"].S()));
        Assert.All(before, r => Assert.True(r!["workerBeforeInput"].B() && r["beforeInput"].B()));
        var after = Expand(Cases("after", true, "com", "missing", "refused", "stop"));
        Assert.Equal(["InvalidOperationException", "InvalidOperationException", "InvalidOperationException", "EmergencyStopException"], after.Select(r => r!["error"].S()));
        Assert.All(after, r => Assert.True(!r!["workerBeforeInput"].B() && !r["beforeInput"].B()));
        var startup = Expand(Cases("startup", false, "com", "missing"));
        Assert.All(startup, r => Assert.True(r!["error"].S() == "BackgroundNotApplicableException" && r["beforeInput"].B()));
    }

    [Fact]
    public void The_visible_centre_of_a_target_in_a_modal_box_is_taken_in_the_box_never_in_its_owner()
    {
        var r = SelfTest.Json(Program.CenterSelfTest, """[{"target":[650,100,100,30],"root":[600,0,600,600]},{"target":[650,100,100,30],"root":[0,0,300,300]}]""").AsArray();
        SelfTest.Same("[700,115]", r[0]!);
        Assert.Matches("off-screen", r[1].S());
    }

    [Fact]
    public void A_target_inside_the_modal_box_of_a_disabled_window_acts_on_that_box()
    {
        static string Node(int window, bool disabled = false) => $$"""{"window":{{window}},"disabled":{{(disabled ? "true" : "false")}}}""";
        var cases = $$"""
            [{"box":200,"chain":[{{Node(100, true)}},{{Node(0)}},{{Node(200)}},{{Node(0)}},{{Node(0)}}]},
             {"box":200,"chain":[{{Node(100, true)}},{{Node(0)}},{{Node(0)}}]},
             {"box":0,"chain":[{{Node(100, true)}},{{Node(200)}},{{Node(0)}}]},
             {"box":200,"chain":[{{Node(100)}},{{Node(200)}},{{Node(0)}}]},
             {"box":200,"chain":[{{Node(100, true)}},{{Node(200, true)}},{{Node(0)}}]}]
            """;
        SelfTest.Same("[2,-1,-1,-1,-1]", SelfTest.Json(Program.ModalSelfTest, cases));
    }

    [Fact]
    public void Chromium_typing_exact_comparison_and_bounded_units_that_never_split_a_surrogate_pair()
    {
        JsonNode Chromium(object input) => SelfTest.Json(ChromiumInput.SelfTest, input);
        Assert.True(Chromium(new { same = new[] { "Bonjour", "Bonjour" } }).B());
        Assert.True(Chromium(new { same = new[] { "a\r\nb", "a\nb" } }).B());
        Assert.False(Chromium(new { same = new[] { "bonjour", "Bonjour" } }).B());
        Assert.False(Chromium(new { same = new[] { "Bon  jour", "Bon jour" } }).B());
        Assert.False(Chromium(new { same = new[] { "Bonjour ", "Bonjour" } }).B());
        var units = Chromium(new { units = "abcdefg😀hij" }).Strings();
        Assert.Equal("abcdefg😀hij", string.Concat(units));
        Assert.All(units, u =>
        {
            Assert.True(u.EnumerateRunes().Count() <= 8);
            Assert.False(char.IsLowSurrogate(u[0]) || char.IsHighSurrogate(u[^1]));
        });
        Assert.True(units.Length >= 2);
    }

    [Fact]
    public void Title_proof_on_a_real_browser_window_with_provoked_breaks()
    {
        // Bench tool: a real browser window opened by the bench (NEXUS_LIVE_HWND, scenario in NEXUS_LIVE_SCENARIO).
        var hwnd = Environment.GetEnvironmentVariable("NEXUS_LIVE_HWND");
        Assert.SkipWhen(!long.TryParse(hwnd, out var handle), "NEXUS_LIVE_HWND (a bench browser window) not set.");
        var scenario = Environment.GetEnvironmentVariable("NEXUS_LIVE_SCENARIO") ?? "nominal";
        var (code, output) = SelfTest.Text(Cdp.ProveLiveSelfTest, System.Text.Json.JsonSerializer.Serialize(new { hwnd = handle, scenario }));
        Assert.True(code == 0, output);
    }
}