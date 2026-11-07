// Fuzzing of the MCP request decoding: every line a client writes on stdin goes through RequestId and
// CancelledRequestId (the stdin reader thread) and HandleMessage. Valid seed messages are mutated byte by byte and
// structurally (types swapped, keys duplicated, values nested, truncated); whatever arrives, the engine answers with
// a JSON-RPC error or nothing, and never throws: one exception there would end the session (reader thread) or the
// server loop. Deterministic by default (fixed seed, test.ps1 and the CI); NEXUS_FUZZ_ITERATIONS and NEXUS_FUZZ_SEED
// run it longer with a new seed (the weekly Fuzz workflow). No message may reach a real tool: a call naming a tool the
// profile lists is skipped, so the fuzzer never touches the desktop.
using System.Text;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public sealed class McpFuzzTests
{
    static readonly string[] Seeds =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fuzz","version":"1"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
        """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""",
        """{"jsonrpc":"2.0","id":2,"method":"ping"}""",
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"fuzz_unknown_tool","arguments":{"window":"x","steps":[{"do":"click","target":"[1]"}]}}}""",
        """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":3,"reason":"fuzz"}}""",
        """{"jsonrpc":"2.0","id":4,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
        """{"jsonrpc":"2.0","id":5,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"1999-01-01"}}}""",
    ];

    // Values that change a field's JSON type: where the decoder expects a string, an object or an id.
    static readonly string[] Replacements =
        ["null", "true", "0", "-1", "1e400", "1.5", "\"\"", "\"2.0\"", "[]", "{}", "[1,\"a\",null]", "{\"a\":{}}", "\"\\u0000\"", "\"\\uD800\"", "9223372036854775808"];

    static readonly string[] Agents = ["claude", "codex", "antigravity"];

    [Fact]
    public void Mutated_requests_get_an_answer_or_nothing_and_never_an_exception()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 20_000;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_FUZZ_SEED"), out var s) ? s : 20261107;
        var random = new Random(seed);
        var states = Agents.ToDictionary(agent => agent, agent => new McpServer.SessionState(agent));
        var tools = Agents.ToDictionary(agent => agent, agent => McpServer.BuildToolsList(agent)
            .Select(tool => (string?)tool?["name"]).Where(name => name != null).Select(name => name!.ToLowerInvariant()).ToHashSet());
        var checkedMessages = 0;
        for (var i = 0; i < iterations; i++)
        {
            var line = Mutate(random, Seeds[random.Next(Seeds.Length)]);
            var agent = Agents[random.Next(Agents.Length)];
            if (CallsListedTool(line, tools[agent])) continue;
            try
            {
                McpServer.RequestId(line);
                McpServer.CancelledRequestId(line);
                var response = McpServer.HandleMessage(states[agent], line);
                if (response != null)
                {
                    Assert.Equal("2.0", (string?)response["jsonrpc"]);
                    Assert.True(response.ContainsKey("result") ^ response.ContainsKey("error"), line);
                    // The answer is written as one line of JSON on stdout.
                    Assert.NotNull(JsonNode.Parse(response.ToJsonString()));
                }
                checkedMessages++;
            }
            catch (Exception error)
            {
                Assert.Fail($"seed {seed}, iteration {i}, agent {agent}: {error}\n{line}");
            }
        }
        Assert.True(checkedMessages > iterations / 2, $"{checkedMessages} of {iterations} messages checked");
    }

    [Theory]
    [InlineData("""{"jsonrpc":2,"id":1,"method":"ping"}""", -32600)]
    [InlineData("""{"jsonrpc":"2.0","jsonrpc":"2.0","id":1,"method":"ping"}""", -32700)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":7}}""", 0)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":{"a":1}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":{}}}}""", -32602)]
    public void A_field_of_the_wrong_type_is_a_JSON_RPC_error_not_an_exception(string line, int code)
    {
        var response = McpServer.HandleMessage(new McpServer.SessionState("claude"), line)!;
        if (code == 0) Assert.NotNull(response["result"]);
        else Assert.True((int?)response["error"]?["code"] is -32700 or -32600 or -32602 or McpServer.UnsupportedProtocolVersion, response.ToJsonString());
        Assert.Null(McpServer.CancelledRequestId("""{"jsonrpc":"2.0","method":"notifications/cancelled","method":"x","params":{"requestId":1}}"""));
        Assert.Null(McpServer.RequestId("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}"""));
    }

    static bool CallsListedTool(string line, HashSet<string> tools)
    {
        if (!line.Contains("tools/call", StringComparison.Ordinal)) return false;
        foreach (var tool in tools)
            if (line.Contains(tool, StringComparison.OrdinalIgnoreCase)) return true;
        // A name built by the mutations (escapes, case) is compared once decoded.
        try { return JsonNode.Parse(line)?["params"]?["name"] is JsonValue v && v.TryGetValue<string>(out var name) && tools.Contains(name.Trim().ToLowerInvariant().Replace('-', '_')); }
        catch (Exception) { return false; }
    }

    static string Mutate(Random random, string seed)
    {
        var text = seed;
        var rounds = 1 + random.Next(4);
        for (var r = 0; r < rounds; r++)
        {
            switch (random.Next(8))
            {
                case 0: // a value of another JSON type in place of a scalar
                {
                    var values = System.Text.RegularExpressions.Regex.Matches(text, "(?<=:)(\"[^\"]*\"|-?[0-9.]+|true|false|null)");
                    if (values.Count == 0) break;
                    var m = values[random.Next(values.Count)];
                    text = string.Concat(text.AsSpan(0, m.Index), Replacements[random.Next(Replacements.Length)], text.AsSpan(m.Index + m.Length));
                    break;
                }
                case 1: // a key repeated
                {
                    var keys = System.Text.RegularExpressions.Regex.Matches(text, "\"[^\"]+\":(\"[^\"]*\"|-?[0-9.]+|true|false|null)");
                    if (keys.Count == 0) break;
                    var m = keys[random.Next(keys.Count)];
                    text = text.Insert(m.Index + m.Length, "," + m.Value);
                    break;
                }
                case 2: // truncated
                    text = text[..random.Next(text.Length + 1)];
                    break;
                case 3: // one byte changed to a JSON-significant character
                {
                    if (text.Length == 0) break;
                    const string significant = "{}[]\",:\\0aZ -.e\u00e9\uD83D";
                    var chars = text.ToCharArray();
                    chars[random.Next(chars.Length)] = significant[random.Next(significant.Length)];
                    text = new string(chars);
                    break;
                }
                case 4: // a value wrapped in deep arrays (depth limit)
                {
                    var depth = random.Next(1, 200);
                    var at = text.IndexOf(':', StringComparison.Ordinal);
                    if (at < 0) break;
                    var sb = new StringBuilder(text.Length + depth * 2);
                    sb.Append(text, 0, at + 1).Append('[', depth).Append('1').Append(']', depth);
                    var end = text.IndexOfAny([',', '}'], at + 1);
                    sb.Append(end < 0 ? "" : text[end..]);
                    text = sb.ToString();
                    break;
                }
                case 5: // the whole message replaced by another JSON value
                    text = Replacements[random.Next(Replacements.Length)];
                    break;
                case 6: // a long string value
                    text = text.Replace("\"fuzz\"", "\"" + new string('x', random.Next(1, 70_000)) + "\"", StringComparison.Ordinal);
                    break;
                default: // random bytes, decoded as UTF-8
                {
                    var bytes = new byte[random.Next(1, 64)];
                    random.NextBytes(bytes);
                    text = text.Insert(random.Next(text.Length + 1), Encoding.UTF8.GetString(bytes));
                    break;
                }
            }
        }
        return text;
    }
}
