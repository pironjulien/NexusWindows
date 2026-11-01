// The same act on the same lab window gives the same technique and the same result for the three profiles (01/11, after
// Codex typed in the foreground into a WPF field while Claude appended in the background). The WPF lab window is shown
// off-screen at the bottom of the Z order, never activated; physical fallback is refused, so no input reaches the desktop.
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
                Path.Combine(TestEnvironment.RepositoryRoot, "commun", "benchmarks", "windows", "labo", "wpf_app.ps1"), "-X", "-32000", "-Y", "-32000" })
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

    static JsonObject Act(McpStdioTests.Server server, long window, object step)
    {
        var reply = server.Rpc("tools/call", new { name = "act", arguments = new { window, steps = new[] { step }, then = "none", fallback = false } });
        var data = Structured(reply);
        var first = data["steps"]!.AsArray()[0]!.AsObject();
        return new JsonObject { ["technique"] = first["technique"]?.DeepClone(), ["status"] = first["status"]?.DeepClone(), ["foregroundTaken"] = first["foregroundTaken"]?.DeepClone() };
    }

    [Fact]
    public void Type_and_click_on_a_wpf_window_take_the_same_background_route_for_the_three_profiles()
    {
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
            clicked[agent] = Act(server, lab.Window, new { @do = "click", target = $"[{Index(look, "Valider", "button")}]", expect = "clic " + (clicked.Count + 1) }).ToJsonString();
            var deadline = Stopwatch.StartNew();
            while (lab.Count("clic " + (clicked.Count)) == before && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            Assert.Equal(before + 1, lab.Count("clic " + clicked.Count));
        }
        Assert.Equal("""{"technique":"background:uia","status":"verified","foregroundTaken":false}""", typed["claude"]);
        Assert.All(typed.Values, t => Assert.Equal(typed["claude"], t));
        Assert.All(clicked.Values, c => Assert.Equal(clicked["claude"], c));
        Assert.Equal("""{"technique":"background:uia","status":"verified","foregroundTaken":false}""", clicked["claude"]);
    }
}
