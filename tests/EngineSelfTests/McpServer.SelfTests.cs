// Offline self-checks of commun/engine/McpServer.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using static McpServer;

static partial class McpServerSelfTests
{
    public static int SelfTest()
    {
        var legacyObservationAssertions = LegacyObservationSelfTest();
        var fallbackTraceAssertions = NativeFallbackTraceSelfTest();
        var windowCompatibilityAssertions = WindowToolCompatibilitySelfTest();
        KeysSelfTests.CheckPhysicalGrammar();
        GardeFous.EnsureDefaultPolicy();
        void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("McpServer SelfTest failed: " + msg);
        }

        // MCP cancellation: only the running request is targeted, seen as a stop, cleared when the request ends.
        Assert(RequestId("""{"jsonrpc":"2.0","id":7,"method":"tools/call"}""") == "7" && RequestId("""{"jsonrpc":"2.0","method":"notifications/initialized"}""") == null, "request id form");
        Assert(CancelledRequestId("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""") == "7", "cancelled id form");
        Assert(CancelledRequestId("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"7"}}""") == "\"7\"", "string and number ids stay distinct");
        RequestCancellation.Begin("7");
        Assert(!RequestCancellation.Cancel("8") && !RequestCancellation.Cancel("\"7\""), "another request is never cancelled");
        RequestCancellation.ThrowIfCancelled();
        Assert(RequestCancellation.Cancel("7"), "running request cancelled");
        var stopped = false;
        try { EmergencyStop.ThrowIfStopped(); } catch (EmergencyStopException) { stopped = true; }
        Assert(stopped, "cancellation stops at the next guard");
        RequestCancellation.End();
        RequestCancellation.ThrowIfCancelled();
        Assert(!RequestCancellation.Cancel("7"), "a finished request cannot be cancelled");
        Assert(ChromiumInput.McpTextBudget == 170_000 && ChromiumInput.Budget == 9000, "MCP text keeps the historical 170 s budget, other gestures 9 s");

        foreach (var agent in GardeFous.ValidAgents)
        {
            GardeFous.CurrentAgent = agent;
            var state = new SessionState(agent);

            // 1. server/discover (2026-07-28)
            var discover = HandleMessage(state, """{"jsonrpc":"2.0","id":1,"method":"server/discover"}""")!;
            Assert(discover["result"]?["resultType"]?.GetValue<string>() == "complete", $"{agent} discover resultType");
            Assert(discover["result"]?["_meta"]?[ServerInfoMeta]?["name"]?.GetValue<string>() == $"nexus-windows-{agent}", $"{agent} serverInfo.name");

            // 2. initialize (legacy)
            var init = HandleMessage(state, """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""")!;
            Assert(init["result"]?["protocolVersion"]?.GetValue<string>() == "2025-11-25", $"{agent} legacy initialize");

            // 3. tools/list
            var list = HandleMessage(state, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""")!;
            var tools = list["result"]?["tools"]?.AsArray()!;
            Assert(tools.Count == 8, $"{agent} tools count");

            // 4. tools/call windows (modern 2026-07-28 envelope)
            var winCall = HandleMessage(state, """
            {
              "jsonrpc": "2.0",
              "id": 4,
              "method": "tools/call",
              "params": {
                "name": "windows",
                "arguments": {},
                "_meta": {
                  "io.modelcontextprotocol/protocolVersion": "2026-07-28",
                  "io.modelcontextprotocol/clientCapabilities": {}
                }
              }
            }
            """)!;
            Assert(winCall["result"]?["resultType"]?.GetValue<string>() == "complete", $"{agent} modern windows call");
            Assert(winCall["result"]?["structuredContent"]?["windows"] is JsonArray, $"{agent} structuredContent.windows");
        }

        // 5. Antigravity hidden alias computer_list_windows
        GardeFous.CurrentAgent = "antigravity";
        var agyState = new SessionState("antigravity");
        var agyAlias = HandleMessage(agyState, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"computer_list_windows","arguments":{}}}""")!;
        Assert(agyAlias["result"]?["structuredContent"]?["windows"] is JsonArray, "antigravity hidden alias computer_list_windows");

        // 6. Claude logs tool
        GardeFous.CurrentAgent = "claude";
        var claudeState = new SessionState("claude");
        var claudeLogs = HandleMessage(claudeState, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"logs","arguments":{"limit":5}}}""")!;
        Assert(claudeLogs["result"]?["content"] is JsonArray, "claude logs tool");

        Console.WriteLine(EngineTestJson.Serialize(new McpServerSelfTestJson(ok: true, version: McpServer.Version, agents: GardeFous.ValidAgents, legacyObservationAssertions: legacyObservationAssertions, fallbackTraceAssertions: fallbackTraceAssertions, windowCompatibilityAssertions: windowCompatibilityAssertions)));
        return 0;
    }
}
