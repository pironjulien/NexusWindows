// Fuzzing of the real tools' arguments, decoded in isolation: the act steps a client writes (targets, keys, texts,
// expectations, routines) are mutated like the requests of McpFuzzTests and given to the decoders an act runs before any
// input (NormalizeStep against an observation fixture, the key sequence grammar, the routine and selector validation,
// the Windows panel plan). Nothing is shown, read from the desktop or sent: no window is resolved and no step runs.
// Whatever arrives, a decoder accepts it or refuses it with a readable error (InvalidOperationException, its
// ActionRefusedException, ArgumentException, FormatException); a NullReferenceException, InvalidCastException,
// IndexOutOfRangeException or the like is a decoder bug whose message tells the agent nothing. Deterministic by default;
// NEXUS_FUZZ_ITERATIONS and NEXUS_FUZZ_SEED run it longer (the weekly Fuzz workflow).
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public sealed class McpToolArgumentFuzzTests
{
    // An observation as the engine records it: a window with the controls the steps below name.
    static JsonObject Observation() => JsonNode.Parse("""
    {"ok":true,"processStartedUtc":"2026-11-07T00:00:00Z","revision":"fuzz","frame":{"left":100,"top":100,"width":1600,"height":900},
     "elements":[
      {"index":0,"role":"window","name":"Fixture","runtimeId":[7,1],"nativeWindow":100,"ancestors":[]},
      {"index":1,"role":"button","name":"OK","runtimeId":[7,2],"ancestors":[0],"bounds":[200,200,80,30]},
      {"index":2,"role":"edit","name":"Name","value":"Ada","runtimeId":[7,3],"ancestors":[0],"bounds":[200,250,300,30]},
      {"index":3,"role":"checkbox","name":"Remember","checked":false,"runtimeId":[7,4],"ancestors":[0]},
      {"index":4,"role":"listitem","name":"Alpha","selected":false,"runtimeId":[7,5],"ancestors":[0],"context":"Row 1"},
      {"index":5,"role":"combobox","name":"Colour","value":"Red","runtimeId":[7,6],"ancestors":[0]},
      {"index":6,"role":"document","name":"","runtimeId":[7,7],"ancestors":[0],"bounds":[100,300,1600,600]},
      {"index":7,"role":"edit","name":"Password","password":true,"runtimeId":[7,8],"ancestors":[0]},
      {"index":8,"role":"treeitem","name":"Folder","expanded":false,"runtimeId":[7,9],"ancestors":[0]}
     ]}
    """)!.AsObject();

    // Steps of the act tool's schema, the three profiles' wording (do, kind, action) and the historical fields.
    static readonly string[] StepSeeds =
    [
        """{"do":"click","target":"OK","expect":{"text":"Saved"}}""",
        """{"do":"click","target":"#1"}""",
        """{"do":"click","target":"[4]"}""",
        """{"do":"click","target":{"role":"button","name":"OK"}}""",
        """{"do":"click","target":"240,215"}""",
        """{"do":"double_click","target":[240,215]}""",
        """{"do":"right_click","target":"Alpha (Row 1)"}""",
        """{"do":"type","target":"Name","text":"fuzz","replace":true}""",
        """{"do":"set","target":"Name","value":"fuzz","expect":{"element":"Name","field":"value","equals":"fuzz"}}""",
        """{"do":"clear","target":"#2"}""",
        """{"do":"key","keys":"ctrl+s alt+f4 enter","target":"Name"}""",
        """{"do":"key","text":"shift+tab"}""",
        """{"do":"select","target":"Colour","option":"Blue"}""",
        """{"do":"menu","path":"File > Open"}""",
        """{"do":"scroll","target":"#6","delta_y":3,"delta_x":-1}""",
        """{"do":"drag","from":[1,2],"to":"300,400"}""",
        """{"do":"drag","target":"Alpha","to":"#3"}""",
        """{"do":"wait","target":"Ready","timeout":2}""",
        """{"do":"check","target":"Remember","expect":{"all":[{"element":"Remember","checked":true},{"title_contains":"Fixture"}]}}""",
        """{"do":"expand","target":"Folder"}""",
        """{"kind":"click","selector":{"role":"button","name":"OK","ancestor":{"role":"window","name":"Fixture"}},"expect":{"selector":{"name":"Done"},"field":"name","equals":"Done"}}""",
        """{"kind":"typeText","focus":{"automationId":"name"},"text":"fuzz","timeoutMs":500}""",
        """{"action":"hover","target":"OK","check":{"target":1,"selected":true}}""",
    ];

    static readonly string[] Agents = ["claude", "codex", "antigravity"];

    [Fact]
    public void Mutated_act_steps_are_decoded_or_refused_with_a_readable_error()
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 20_000;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("NEXUS_FUZZ_SEED"), out var s) ? s : 20261107;
        var random = new Random(seed);
        int decoded = 0, accepted = 0;
        for (var i = 0; i < iterations; i++)
        {
            var line = McpFuzzTests.Mutate(random, StepSeeds[random.Next(StepSeeds.Length)]);
            JsonNode? node;
            // What the request parser refuses (McpServer.ParseRequest: duplicate keys, a lone surrogate) never reaches a tool.
            try { node = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }); node?.ToJsonString(); }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { continue; }
            var agent = Agents[random.Next(Agents.Length)];
            var state = new McpServer.SessionState(agent) { BoundWindow = (100, "process:fixture.exe", "Fixture", 1) };
            Decode(seed, i, line, () =>
            {
                if (node is JsonObject step) { McpServer.NormalizeStep(state, Observation(), step.DeepClone().AsObject(), allowFutureTarget: random.Next(2) == 0); accepted++; }
                else throw new InvalidOperationException("not a step");
            });
            Decode(seed, i, line, () => Routines.PrepareRoutineSteps(new JsonArray(node?.DeepClone())));
            Decode(seed, i, line, () => Routines.ParametersIn(node));
            Decode(seed, i, line, () => McpServer.PlanShellAct(new JsonObject { ["window"] = (node as JsonObject)?["target"]?.DeepClone(), ["foreground"] = node?["replace"]?.DeepClone() }));
            if (node is JsonObject { } fields)
            {
                Decode(seed, i, line, () => Routines.ValidateSelector(fields["selector"] as JsonObject ?? fields["target"] as JsonObject));
                Decode(seed, i, line, () => Routines.ValidateExpectation(fields["expect"] as JsonObject));
                foreach (var key in new[] { "keys", "key", "text" })
                    if (fields[key] is JsonValue keyValue && keyValue.TryGetValue<string>(out var spec))
                        Decode(seed, i, line, () => McpServer.NormalizeKeySequence(spec));
            }
            decoded++;
        }
        // About a third of the mutations stay valid JSON; some of them still make a step the engine would run.
        Assert.True(decoded > iterations / 5 && accepted > decoded / 50, $"{decoded} of {iterations} steps decoded, {accepted} accepted");
    }

    static void Decode(int seed, int iteration, string line, Action decode)
    {
        try { decode(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException) { }
        catch (Exception error)
        {
            Assert.Fail($"seed {seed}, iteration {iteration}: {error}\n{line}");
        }
    }

    [Fact]
    public void An_exception_escaping_a_request_is_an_internal_error_and_the_session_goes_on()
    {
        static JsonObject Broken(string _) => throw new NullReferenceException("decoder bug");
        var answer = JsonNode.Parse(McpServer.ResponseLine("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", Broken)!)!;
        Assert.Equal(9, (int)answer["id"]!);
        Assert.Equal(-32603, (int)answer["error"]!["code"]!);
        Assert.Contains("NullReferenceException", (string)answer["error"]!["message"]!);
        Assert.Equal("\"a\"", JsonNode.Parse(McpServer.ResponseLine("""{"jsonrpc":"2.0","id":"a","method":"x"}""", Broken)!)!["id"]!.ToJsonString());
        // A notification gets no answer; an unreadable line an answer without id.
        Assert.Null(McpServer.ResponseLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", Broken));
        Assert.Null(JsonNode.Parse(McpServer.ResponseLine("{oops", Broken)!)!["id"]);
        // An answer that cannot be written is an internal error too, never a dead session.
        var unwritable = McpServer.ResponseLine("""{"jsonrpc":"2.0","id":3,"method":"x"}""", _ => new JsonObject { ["value"] = double.NaN });
        Assert.Equal(-32603, (int)JsonNode.Parse(unwritable!)!["error"]!["code"]!);
        // An ordinary answer goes through untouched.
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", McpServer.ResponseLine("x", _ => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JsonObject() }));
    }
}
