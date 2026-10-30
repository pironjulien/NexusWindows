// The built engine as each AI starts it: MCP over stdio (NexusWindows.exe --mcp --agent <ai>), black box
// (formerly native-mcp.test.mjs and native-contracts.test.mjs). Read-only calls; no window is shown or touched.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public class McpStdioTests
{
    static readonly string Version = typeof(McpServer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    sealed class Server : IDisposable
    {
        readonly Process process;
        int id;
        public int Pid => process.Id;

        public Server(string[] arguments, IDictionary<string, string>? environment = null)
        {
            var start = new ProcessStartInfo(SelfTest.Engine)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, UseShellExecute = false, StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
        }

        public JsonNode Rpc(string method, object? parameters = null)
        {
            var current = ++id;
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = current, method, @params = parameters ?? new { } }));
            process.StandardInput.Flush();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(15) - deadline.Elapsed) || read.Result is null) break;
                var reply = JsonNode.Parse(read.Result);
                if (reply?["id"]?.GetValue<int>() == current) return reply;
            }
            throw new TimeoutException($"MCP {method} timed out");
        }

        public void Dispose()
        {
            try { process.StandardInput.Close(); if (!process.WaitForExit(5000)) process.Kill(true); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("antigravity")]
    public void Native_mcp_stdio_server_serves_each_facade_2026_07_28_and_legacy(string agent)
    {
        using var server = new Server(["--mcp", "--agent", agent]);
        var discover = server.Rpc("server/discover");
        Assert.Equal($"nexus-windows-{agent}", discover["result"]!["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"].S());
        var names = server.Rpc("tools/list")["result"]!["tools"]!.AsArray().Select(t => t!["name"].S());
        Assert.Equal(agent == "claude" ? ["windows", "look", "act", "launch", "window", "logs", "notifications"] : ["windows", "look", "act", "launch", "window"], names);
        var call = server.Rpc("tools/call", new
        {
            name = agent == "antigravity" ? "computer_list_windows" : "windows",
            arguments = new { },
            _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28", ["io.modelcontextprotocol/clientCapabilities"] = new { } },
        });
        Assert.Equal("complete", call["result"]!["resultType"].S());
        Assert.IsType<JsonArray>(call["result"]!["structuredContent"]!["windows"]);
    }

    [Fact]
    public void Claude_logs_historical_typed_contract_count_limit_and_errors_only()
    {
        var root = Directory.CreateTempSubdirectory("nexus-contract-").FullName;
        try
        {
            var dir = Path.Combine(root, "journal", "windows", "claude");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "2026-10.jsonl"), string.Join("\n",
                """{"ts":"2026-10-29T12:00:00Z","action":"click","issue":"✓","statut":"verified","ms":3}""",
                """{"ts":"2026-10-29T12:01:00Z","action":"setValue","issue":"~","statut":"unknown","ms":5}""",
                """{"ts":"2026-10-29T12:02:00Z","action":"wait","issue":"=","statut":"already-satisfied","ms":1}""") + "\n");
            using var server = new Server(["--mcp", "--agent", "claude"], new Dictionary<string, string>
            {
                ["NEXUS_JOURNAL_HOME"] = Path.Combine(root, "journal"), ["NEXUS_WINDOWS_HOME"] = root, ["NEXUS_SHARED_HOME"] = root,
            });
            var tools = server.Rpc("tools/list")["result"]!["tools"]!.AsArray();
            foreach (var name in new[] { "windows", "notifications", "logs" })
                Assert.Equal("object", tools.First(t => t!["name"].S() == name)!["outputSchema"]!["type"].S());
            foreach (var (arguments, wanted) in new (object, int)[] { (new { count = 2 }, 2), (new { limit = 1 }, 1), (new { count = 100, errors_only = true }, 1) })
            {
                var result = server.Rpc("tools/call", new { name = "logs", arguments })["result"]!;
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                var data = result["structuredContent"]!;
                Assert.Equal(Version, data["version"].S()); Assert.Equal(server.Pid, data["pid"].I());
                Assert.IsType<JsonArray>(data["status"]);
                var records = data["records"]!.AsArray();
                Assert.Equal(wanted, records.Count);
                foreach (var row in records) foreach (var key in new[] { "ts", "tool", "ms", "ok", "error", "args", "steps" }) Assert.True(row!.AsObject().ContainsKey(key), key);
                if (arguments.ToString()!.Contains("errors_only")) Assert.Equal("setValue", records[0]!["tool"].S());
            }
            var spoof = server.Rpc("tools/call", new { name = "windows", arguments = new { agent = "antigravity" } })["result"]!;
            Assert.True(spoof["isError"].B()); Assert.Matches("fixed", spoof["content"]![0]!["text"].S());
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Startup_refuses_duplicate_or_extra_agent_arguments()
    {
        foreach (var arguments in new[] { new[] { "--mcp", "--agent", "codex", "--agent", "antigravity" }, ["--mcp", "--agent", "codex", "--profile", "claude"] })
        {
            var start = new ProcessStartInfo(SelfTest.Engine) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(2, process.ExitCode);
        }
    }

    [Fact]
    public void Raw_codex_profile_rejects_request_level_agent_switching()
    {
        var start = new ProcessStartInfo(SelfTest.Engine) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true };
        start.Environment["NEXUS_AGENT"] = "antigravity";
        using var process = Process.Start(start)!;
        process.StandardInput.Write(JsonSerializer.Serialize(new { operation = "windows", processId = 0, agent = "antigravity", requestId = Guid.NewGuid().ToString() }));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(15000));
        var result = JsonNode.Parse(output.Trim().Split('\n')[0])!;
        Assert.False(result["ok"].B()); Assert.False(result["inputStarted"].B()); Assert.Matches("profile is fixed", result["message"].S());
    }
}
