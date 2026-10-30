// Chromium, CEF and Claude Desktop (Electron) input rules of the engine, offline (formerly cef.test.mjs,
// native-chromium-simulated.test.mjs and native-claude-cdp.test.mjs over --chromium-selftest and
// --claude-cdp-selftest). The DOM scripts the engine injects are still executed by Node (node:vm), as before.
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class ChromiumTests
{
    static JsonNode Chromium(object input) => SelfTest.Json(ChromiumInput.SelfTest, input);
    static JsonNode Cef(object[]? children = null, string topClass = "SDL_app") =>
        Chromium(new { cef = new { pid = 17, root = 42, topClass, children = children ?? [new Dictionary<string, object> { ["class"] = "CefBrowserWindow", ["pid"] = 17, ["root"] = 42 }] } });
    static JsonNode Click(object patterns, string role = "tab", bool hosted = true) => Chromium(new { click = new { cef = hosted, role, patterns } });
    static object Child(string cls, int pid, int root) => new Dictionary<string, object> { ["class"] = cls, ["pid"] = pid, ["root"] = root };
    static bool[] Simulation(JsonNode r) => [r["focusSimulation"].B(), r["typeSimulation"].B(), r["selectSimulation"].B()];

    [Fact]
    public void Cef_hosted_content_permits_publication_but_never_enables_chrome_simulation_for_sdl()
    {
        var result = Cef();
        Assert.True(result["content"].B()); Assert.True(result["cef"].B());
        Assert.Equal([false, false, false], Simulation(result));
        Assert.True(result["wheelDevtools"].B()); Assert.True(result["postedHover"].B()); Assert.False(result["doubleClickInvoke"].B());
        var chrome = Cef([], "Chrome_WidgetWin_1");
        Assert.Equal([true, true, true], Simulation(chrome));
        Assert.True(chrome["content"].B());
    }

    [Fact]
    public void An_sdl_name_a_similar_child_another_pid_or_another_root_never_prove_hosted_cef()
    {
        foreach (var children in new object[][] { [], [Child("CefBrowserWindowExtra", 17, 42)], [Child("Chrome_RenderWidgetHostHWND", 17, 42)],
            [Child("CefBrowserWindow", 18, 42)], [Child("CefBrowserWindow", 17, 43)] })
        {
            var result = Cef(children);
            Assert.False(result["content"].B()); Assert.False(result["cef"].B());
            Assert.Equal([false, false, false], Simulation(result));
            Assert.False(result["wheelDevtools"].B());
        }
    }

    [Fact]
    public void Cef_tabs_lists_radios_invoke_once_never_silently_select()
    {
        foreach (var role in new[] { "tab", "listitem", "radio" })
        {
            var invoked = Click(new { invoke = "sent", @default = "applied", select = "applied" }, role);
            Assert.Equal(["invoke", "default"], invoked["order"].Strings());
            Assert.Equal(["invoke"], invoked["calls"].Strings()); Assert.Equal(1, invoked["sent"].I());
            var legacy = Click(new { invoke = "missing", @default = "sent", select = "applied" }, role);
            Assert.Equal(["invoke", "default"], legacy["calls"].Strings()); Assert.Equal(1, legacy["sent"].I());
        }
        var native = Click(new { select = "applied", invoke = "sent" }, "tab", false);
        Assert.Equal(["select"], native["calls"].Strings()); Assert.Equal("applied", native["effect"].S());
    }

    [Fact]
    public void Cef_sent_unchanged_and_lost_outcomes_are_terminal()
    {
        foreach (var effect in new[] { "sent", "none" })
        {
            var result = Click(new { invoke = effect, @default = "applied", select = "applied" });
            Assert.Equal(["invoke"], result["calls"].Strings()); Assert.Equal(1, result["sent"].I());
            Assert.Equal(effect, result["effect"].S()); Assert.False(result["beforeInput"].B());
        }
        var lost = Click(new { invoke = "lost", @default = "applied", select = "applied" });
        Assert.Equal(["invoke"], lost["calls"].Strings()); Assert.Equal(1, lost["sent"].I());
        Assert.Equal("COMException", lost["error"].S()); Assert.False(lost["beforeInput"].B());
        var toggle = Click(new { toggle = "none", invoke = "applied", @default = "applied" }, "checkbox");
        Assert.Equal(["toggle"], toggle["calls"].Strings()); Assert.Equal(1, toggle["sent"].I()); Assert.False(toggle["beforeInput"].B());
    }

    [Fact]
    public void Cef_without_a_click_pattern_refuses_before_input()
    {
        var missing = Click(new { select = "applied" });
        Assert.Equal(["invoke", "default"], missing["calls"].Strings()); Assert.Equal(0, missing["sent"].I());
        Assert.Equal("BackgroundNotApplicableException", missing["error"].S()); Assert.True(missing["beforeInput"].B());
        var unreadable = Click(new { invoke = "read-error", @default = "applied" });
        Assert.Equal(["invoke"], unreadable["calls"].Strings()); Assert.Equal(0, unreadable["sent"].I()); Assert.True(unreadable["beforeInput"].B());
    }

    static JsonNode Simulated(object simulatedText) => Chromium(new { simulatedText });

    [Fact]
    public void Claude_simulated_typing_historical_exact_prefix_and_rich_paragraph_oracle()
    {
        (string Value, string Text, string Before, bool Append, int? Expected)[] fixtures = [
            ("", "abc", "", false, 0), ("ab", "abc", "", false, 2), ("abc", "abc", "", false, 3),
            ("abx", "abc", "", false, null), ("abcabc", "abc", "", false, null), ("ab\r\ncd", "ab\ncd", "", false, 5),
            ("\n", "Bonjour", "", false, 0), ("Bonjour Julien\n", "Bonjour Julien\nVoici", "", false, 15),
            ("ab\n\n", "ab\ncd", "", false, 3), ("Bonjour", "Bonjour", "Bonjour", true, 0),
            ("BonjourBon", "Bonjour", "Bonjour", true, 3), ("Bonjour Julien", "Bonjour Julien", "\n", true, 14),
            ("Note\nSuite", "Suite", "Note\n\n", true, 5), ("xNote", "x", "Note", true, null),
            ("A\nB\n", "A\n\nB", "", false, 4), ("abc ", "abc", "", false, null), ("ABC", "abc", "", false, null)];
        foreach (var (value, text, before, append, expected) in fixtures)
        {
            var confirmed = Simulated(new { prefix = new { value, text, before, append } })["confirmed"];
            Assert.True(expected is null ? confirmed is null : confirmed?.GetValue<int>() == expected, $"{value}|{text}|{before}|{append}: {confirmed?.ToJsonString()}");
        }
    }

    [Fact]
    public void Claude_simulated_typing_unicode_packets_preserve_utf16_pairs_and_isolate_each_real_enter()
    {
        const string text = "1234567😀\r\nété\n\n終わり\rfin";
        var units = Simulated(new { units = text }).Strings();
        Assert.Equal(text.Replace("\r\n", "\n").Replace('\r', '\n'), string.Concat(units));
        Assert.Equal(4, units.Count(u => u == "\n"));
        foreach (var unit in units)
        {
            Assert.True(unit == "\n" || !unit.Contains('\n'));
            Assert.True(unit.Length <= 8);
            Assert.False(char.IsHighSurrogate(unit[^1]), "packet ends inside surrogate pair");
            Assert.False(char.IsLowSurrogate(unit[0]), "packet starts inside surrogate pair");
        }
    }

    [Fact]
    public void Claude_simulated_typing_replacement_is_erased_before_characters_append_keeps_original()
    {
        const string text = "ligne1\nligne2 😀";
        var replaced = Simulated(new { before = "old text", text, replace = true });
        Assert.True(replaced["ok"].B(), replaced.ToJsonString()); Assert.Equal(text, replaced["value"].S());
        var events = replaced["events"].Strings();
        Assert.Equal(["select-all", "delete"], events.Take(2));
        Assert.Equal(1, events.Count(e => e == "delete"));
        Assert.Equal(1, events.Count(e => e == "enter"));
        var appended = Simulated(new { before = "préfixe ", text, replace = false });
        Assert.True(appended["ok"].B(), appended.ToJsonString()); Assert.Equal("préfixe " + text, appended["value"].S());
        Assert.Equal("caret-end", appended["events"].Strings()[0]); Assert.DoesNotContain("delete", appended["events"].Strings());
        var cleared = Simulated(new { before = "erase", text = "", replace = true });
        Assert.True(cleared["ok"].B()); Assert.Equal("", cleared["value"].S()); Assert.Equal(["select-all", "delete"], cleared["events"].Strings());
    }

    [Fact]
    public void Claude_simulated_typing_unread_deletion_or_character_acknowledgement_stops_without_replay()
    {
        var clear = Simulated(new { before = "old text", text = "replacement", replace = true, noAck = true });
        Assert.False(clear["ok"].B()); Assert.Equal("AfterInputException", clear["error"].S());
        Assert.Equal(["select-all", "delete"], clear["events"].Strings()); Assert.Equal("old text", clear["value"].S());
        var chars = Simulated(new { before = "", text = "12345678remaining", replace = false, noAck = true });
        Assert.False(chars["ok"].B()); Assert.Matches("unknown, do not replay", chars["message"].S());
        Assert.Equal(["caret-end", "chars:12345678"], chars["events"].Strings()); Assert.Equal(1, chars["inputPackets"].I());
    }

    [Fact]
    public void Claude_simulated_typing_missing_enter_and_changed_focus_never_emit_the_following_packet()
    {
        var enter = Simulated(new { before = "", text = "A\nDO NOT SEND", replace = false, suppressEnter = true });
        Assert.False(enter["ok"].B()); Assert.Equal(["caret-end", "chars:A", "enter"], enter["events"].Strings());
        var focus = Simulated(new { before = "", text = "12345678DO NOT SEND", replace = false, loseAfter = 1 });
        Assert.False(focus["ok"].B()); Assert.Equal(["caret-end", "chars:12345678"], focus["events"].Strings());
        Assert.Equal("12345678", focus["value"].S());
        var before = Simulated(new { before = "", text = "nothing", replace = false, loseAfter = 0 });
        Assert.False(before["ok"].B()); Assert.Empty(before["events"].Strings()); Assert.Equal(0, before["inputPackets"].I());
    }

    [Fact]
    public void Claude_simulated_typing_a_send_control_appearing_refuses_the_next_real_enter()
    {
        var result = Simulated(new { before = "", text = "A\nMUST NOT SEND", replace = false, revealSendAfter = "A" });
        Assert.False(result["ok"].B()); Assert.Equal("AfterInputException", result["error"].S());
        Assert.Matches("unknown, do not replay", result["message"].S());
        Assert.Equal("A", result["value"].S()); Assert.Equal(1, result["inputPackets"].I());
        Assert.Equal(["caret-end", "chars:A", "enter-refused:outward-context-changed"], result["events"].Strings());
    }

    static JsonNode Claude(object input) => SelfTest.Json(Cdp.ClaudeInputSelfTest, input);

    [Fact]
    public void Claude_dom_keys_function_keys_page_modifiers_printable_characters_and_malformed_combinations()
    {
        var r = Claude(new[] { "ctrl+k", "alt+f24", "win+shift+k", "enter", "+", "ctrl++", "ctrl", "é", "ctrl+", "ctrl+shift+", "f25", "bogus+x" }).AsArray();
        SelfTest.Same("""{"key":"k","code":"KeyK","vk":75,"modifiers":2,"text":null}""", r[0]);
        Assert.Equal("F24", r[1]!["key"].S()); Assert.Equal(1, r[1]!["modifiers"].I()); Assert.Equal(135, r[1]!["vk"].I());
        Assert.Equal(12, r[2]!["modifiers"].I()); Assert.Equal("K", r[2]!["key"].S());
        Assert.Equal("\r", r[3]!["text"].S()); Assert.Equal("+", r[4]!["text"].S()); Assert.Equal(2, r[5]!["modifiers"].I());
        Assert.Equal("Control", r[6]!["key"].S()); Assert.Equal("é", r[7]!["text"].S());
        foreach (var result in r.Skip(8)) Assert.NotNull(result!["error"]);
        Assert.Equal(Enumerable.Range(112, 24), Claude(Enumerable.Range(1, 24).Select(i => $"f{i}").ToArray()).AsArray().Select(k => k!["vk"].I()));
    }

    [Fact]
    public void Real_cdp_socket_one_grant_lost_acknowledgement_never_replays_changed_focus_releases_once()
    {
        var normal = Claude(new { scenario = "normal" });
        Assert.Equal(1, normal["started"].I()); Assert.Equal("acknowledged", normal["outcome"].S());
        Assert.Equal(["rawKeyDown", "char", "keyUp"], normal["received"].Strings());
        var lost = Claude(new { scenario = "ack-lost" });
        Assert.Equal("unknown", lost["outcome"].S()); Assert.Equal(1, lost["started"].I()); Assert.Equal(["rawKeyDown"], lost["received"].Strings());
        Assert.Equal(1, lost["cleanups"].I()); Assert.False(lost["automaticReplay"].B());
        var moved = Claude(new { scenario = "focus-after-down" });
        Assert.Equal("unknown", moved["outcome"].S()); Assert.Equal(1, moved["started"].I()); Assert.Equal(1, moved["cleanups"].I());
        Assert.Equal(["rawKeyDown", "keyUp"], moved["received"].Strings());
        var account = Claude(new { scenario = "account-after-down" });
        Assert.Equal("unknown", account["outcome"].S()); Assert.Equal(["rawKeyDown"], account["received"].Strings());
        foreach (var scenario in new[] { "account-before", "grant-refused" })
        {
            var r = Claude(new { scenario });
            Assert.Equal("refused-before-input", r["outcome"].S()); Assert.Empty(r["received"].Strings());
        }
    }

    [Fact]
    public void Production_dom_scripts_focus_and_select_mutation_behave_in_a_javascript_engine()
    {
        // The engine injects these scripts into Chromium pages: they are executed by Node's vm with DOM doubles.
        var node = Environment.GetEnvironmentVariable("PATH")!.Split(';').Select(d => Path.Combine(d.Trim(), "node.exe")).FirstOrDefault(File.Exists);
        Assert.SkipWhen(node is null, "Node.js absent: the injected DOM scripts cannot be executed.");
        var scripts = Path.Combine(Path.GetTempPath(), "nexus-claude-dom-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(scripts, Claude(new { scripts = true }).ToJsonString());
        try
        {
            var start = new ProcessStartInfo(node!) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            foreach (var argument in new[] { "--test", Path.Combine(TestEnvironment.RepositoryRoot, "tests", "js", "claude-dom-scripts.test.mjs") }) start.ArgumentList.Add(argument);
            start.Environment["NEXUS_CLAUDE_DOM_SCRIPTS"] = scripts;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(60000), "node --test timed out");
            Assert.True(process.ExitCode == 0, output.Result + errors);
        }
        finally { File.Delete(scripts); }
    }

    [Fact]
    public void Multiview_exact_document_rectangle_and_css_dpr_identify_one_surface()
    {
        JsonNode Run(string surfaces = "[[100,100,1700,1100],[300,300,700,600]]", string bounds = "[300,300,400,300]", string inner = "[320,240]") =>
            Claude($$"""{"surfaces":{{surfaces}},"bounds":{{bounds}},"inner":{{inner}},"ratio":1.25}""");
        Assert.Equal(1, Run()["index"].I());
        Assert.NotNull(Run(bounds: "[100,100,1600,1000]")["error"]);
        Assert.NotNull(Run(surfaces: "[[100,100,1700,1100],[300,300,700,600],[300,300,700,600]]")["error"]);
        Assert.NotNull(Run(bounds: "[301,301,403,300]")["error"]);
        Assert.Equal(1, Run(inner: "[321,240]")["index"]?.GetValue<int>());
        Assert.NotNull(Run(inner: "[400,240]")["error"]);
    }
}
