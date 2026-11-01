// The same act on the same lab window gives the same technique and the same result for the three profiles (01/11, after
// Codex typed in the foreground into a WPF field while Claude appended in the background). The WPF window
// (fixtures/wpf_lab.ps1) is shown off-screen at the bottom of the Z order, never activated; physical fallback is refused,
// so no input reaches the desktop. The guards answer the same way too (01/11): no window named, a step already done,
// the real mouse without foreground:true, a password field, a Windows panel in the background, the whole screen, an act
// without a prior look; so do the defaults (look, stable indices, act's updated window, text compared spaces normalized). It needs a desktop session and Windows OCR: not run by the offline CI.
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class ThreeProfilesBehaviorTests
{
    sealed class WpfLab : IDisposable
    {
        readonly Process process;
        public readonly long Window;
        public readonly List<string> Events = [];
        public WpfLab()
        {
            var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new System.Text.UTF8Encoding(false) };
            foreach (var argument in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(TestEnvironment.RepositoryRoot, "tests", "fixtures", "wpf_lab.ps1") })
                start.ArgumentList.Add(argument);
            process = Process.Start(start)!;
            var ready = process.StandardOutput.ReadLineAsync();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result is not null, "WPF lab did not start");
            Window = JsonNode.Parse(ready.Result!)!["top"]!.GetValue<long>();
            new Thread(() => { while (process.StandardOutput.ReadLine() is { } line) lock (Events) Events.Add(line); }) { IsBackground = true }.Start();
        }
        public int Count(string evt) { lock (Events) return Events.Count(e => e.Contains($"\"{evt}\"", StringComparison.Ordinal)); }
        public void Dispose() { try { process.Kill(true); } catch (InvalidOperationException) { } process.Dispose(); }
    }

    static JsonObject Structured(JsonNode reply) => reply["result"]!["structuredContent"]!.AsObject();
    static int Index(JsonObject look, string name, string role) => look["elements"]!.AsArray().OfType<JsonObject>()
        .Single(e => e["name"]?.GetValue<string>() == name && e["role"]?.GetValue<string>() == role)["index"]!.GetValue<int>();

    static JsonObject Act(McpStdioTests.Server server, long window, object step, string then = "none")
    {
        var reply = server.Rpc("tools/call", new { name = "act", arguments = new { window, steps = new[] { step }, then, fallback = false } });
        var data = Structured(reply);
        var first = data["steps"]!.AsArray()[0]!.AsObject();
        var observed = data["observation"]?["elements"] is JsonArray elements && elements.Count > 0;
        return new JsonObject { ["technique"] = first["technique"]?.DeepClone(), ["status"] = first["status"]?.DeepClone(), ["foregroundTaken"] = first["foregroundTaken"]?.DeepClone(), ["observed"] = observed };
    }

    [Fact]
    public void Type_and_click_on_a_wpf_window_take_the_same_background_route_for_the_three_profiles()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Live WPF window and OCR: run on a desktop (test.ps1).");
        using var lab = new WpfLab();
        var typed = new Dictionary<string, string>(); var clicked = new Dictionary<string, string>();
        var expected = "";
        foreach (var agent in new[] { "claude", "codex", "antigravity" })
        {
            using var server = new McpStdioTests.Server(["--mcp", "--agent", agent]);
            server.Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "behavior-test", version = "1" } });
            var look = Structured(server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window, detail = "full" } }));
            typed[agent] = Act(server, lab.Window, new { @do = "type", target = $"[{Index(look, "Nom", "edit")}]", text = agent[..2] }).ToJsonString();
            expected += agent[..2];
            look = Structured(server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window, detail = "full" } }));
            Assert.Equal(expected, look["elements"]!.AsArray().OfType<JsonObject>().Single(e => e["name"]?.GetValue<string>() == "Nom" && e["role"]?.GetValue<string>() == "edit")["value"]!.GetValue<string>());
            var before = lab.Count("clic " + (clicked.Count + 1));
            // The status text is named "Statut" for UI Automation: "clic N" is only seen in the window's pixels (OCR).
            clicked[agent] = Act(server, lab.Window, new { @do = "click", target = $"[{Index(look, "Valider", "button")}]", expect = "clic " + (clicked.Count + 1) }, then: "elements").ToJsonString();
            var deadline = Stopwatch.StartNew();
            while (lab.Count("clic " + (clicked.Count)) == before && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            Assert.Equal(before + 1, lab.Count("clic " + clicked.Count));
        }
        Assert.Equal("""{"technique":"background:uia","status":"verified","foregroundTaken":false,"observed":false}""", typed["claude"]);
        Assert.All(typed.Values, t => Assert.Equal(typed["claude"], t));
        Assert.All(clicked.Values, c => Assert.Equal(clicked["claude"], c));
        // then="elements": the updated window comes back in the same answer, for the three profiles.
        Assert.Equal("""{"technique":"background:uia","status":"verified","foregroundTaken":false,"observed":true}""", clicked["claude"]);
    }

    // One line per rule: the error of a refused call, or the first step's status, mark, foreground and message.
    static string Outcome(JsonNode reply)
    {
        var result = reply["result"]!.AsObject();
        if (result["isError"]?.GetValue<bool>() == true) return "error: " + result["content"]![0]!["text"]!.GetValue<string>();
        if (result["structuredContent"]?["steps"] is not JsonArray { Count: > 0 } steps) return "image: " + (result["content"]!.AsArray().Any(c => c?["type"]?.GetValue<string>() == "image"));
        var step = steps[0]!.AsObject();
        return $"{step["status"]} {step["mark"]} foregroundTaken={step["foregroundTaken"]} {step["message"]}";
    }

    [Fact]
    public void The_guards_answer_the_same_way_for_the_three_profiles()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "Live WPF window: run on a desktop (test.ps1).");
        using var lab = new WpfLab();
        var outcomes = new Dictionary<string, JsonObject>();
        string Act(McpStdioTests.Server server, object arguments) => Outcome(server.Rpc("tools/call", new { name = "act", arguments }));
        var already = new { @do = "click", target = "Valider", expect = new { selector = new { name = "Valider" }, exists = true } };
        foreach (var agent in new[] { "claude", "codex", "antigravity" })
        {
            using var server = new McpStdioTests.Server(["--mcp", "--agent", agent]);
            server.Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "behavior-test", version = "1" } });
            var row = new JsonObject
            {
                // No window named and none bound: never the window in the foreground.
                ["noWindow"] = Outcome(server.Rpc("tools/call", new { name = "act", arguments = new { steps = new[] { already } } })),
                // An act before any look observes the named window first.
                ["withoutLook"] = Act(server, new { window = lab.Window, steps = new[] { already } }),
            };
            // From here each act follows its own look (Codex's contract consumes the observation).
            string Looked(object step)
            {
                server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window, detail = "full" } });
                return Act(server, new { window = lab.Window, steps = new[] { step } });
            }
            // A step already done answers "=" and sends nothing.
            row["alreadyDone"] = Looked(already);
            // Hover needs the real mouse: refused without foreground:true, Codex included.
            row["foregroundDefault"] = Looked(new { @do = "hover", target = "Valider" });
            row["password"] = Looked(new { @do = "type", target = "Secret", text = "x" });
            // Text postconditions compare with spaces normalized: the step is already done, the click is not sent.
            row["expectSpaces"] = Looked(new { @do = "click", target = "Valider", expect = new { selector = new { name = "Statut" }, field = "value", equals = "  Statut " } });
            // The same look by default (lines, summary), with the same index for the same element from one look to the next.
            var first = Structured(server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window } }));
            var second = Structured(server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window } }));
            row["lookDefault"] = $"{first["elements"]!.AsArray().Count} {first["summary"]} stable={Index(first, "Nom", "edit") == Index(second, "Nom", "edit")}";
            // act returns the updated window in the same answer by default.
            server.Rpc("tools/call", new { name = "look", arguments = new { window = lab.Window } });
            row["actDefault"] = Structured(server.Rpc("tools/call", new { name = "act", arguments = new { window = lab.Window, steps = new[] { already } } }))["observation"]?["elements"] is JsonArray { Count: > 0 };
            row["panelInBackground"] = Act(server, new { window = "start", foreground = false, steps = new[] { new { @do = "click", target = "Paramètres" } } });
            row["fullScreen"] = Outcome(server.Rpc("tools/call", new { name = "look", arguments = new { window = "screen" } }));
            outcomes[agent] = row;
        }
        var report = string.Join("\n", outcomes.Select(o => o.Key + " " + o.Value.ToJsonString()));
        Assert.True(lab.Count("clic 1") == 0, report);
        Assert.True(outcomes.Values.All(r => JsonNode.DeepEquals(r, outcomes["claude"])), report);
        var claude = outcomes["claude"];
        Assert.StartsWith("error:", claude["noWindow"]!.GetValue<string>());
        Assert.StartsWith("already-satisfied =", claude["alreadyDone"]!.GetValue<string>());
        Assert.StartsWith("already-satisfied =", claude["withoutLook"]!.GetValue<string>());
        Assert.StartsWith("refused", claude["foregroundDefault"]!.GetValue<string>());
        Assert.Contains("foregroundTaken=false", claude["foregroundDefault"]!.GetValue<string>());
        Assert.Contains("Password", claude["password"]!.GetValue<string>());
        Assert.Contains("only exists in the foreground", claude["panelInBackground"]!.GetValue<string>());
        Assert.Equal("image: True", claude["fullScreen"]!.GetValue<string>());
        Assert.StartsWith("already-satisfied =", claude["expectSpaces"]!.GetValue<string>());
        Assert.EndsWith("stable=True", claude["lookDefault"]!.GetValue<string>());
        Assert.True(claude["actDefault"]!.GetValue<bool>(), report);
    }
}
