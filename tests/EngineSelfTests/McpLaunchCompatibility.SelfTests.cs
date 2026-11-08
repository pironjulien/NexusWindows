// Offline self-checks of commun/engine/McpLaunchCompatibility.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpServer;

static partial class McpServerSelfTests
{
    public static int LaunchCompatibilitySelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd()); var results = new JsonArray();
        foreach (var item in input.RootElement.EnumerateArray())
        {
            try
            {
                var value = ObjectOf(item); var apps = (value["catalogue"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new ClaudeStartApp(Str(a, "name"), Str(a, "id"), Str(a, "target") is { Length: > 0 } t ? t : null, Str(a, "args"))).ToList();
                if (value["steamPortFixture"] is JsonObject steam)
                {
                    var helpers = steam["helpers"]!.AsArray().Select(p => checked((uint)p!.GetValue<long>())).ToHashSet();
                    var listeners = steam["listeners"]!.AsArray().OfType<JsonObject>().Select(l => (Str(l, "address"), checked((uint)Number(l, "pid"))));
                    results.Add(EngineTestJson.Node(new JsonObject { ["steamOwner"] = ClaudeSteamPortOwner(listeners, helpers), ["sessionKind"] = "genuine-steam-session" })); continue;
                }
                var plan = PlanClaudeLaunch(Str(value, "app"), Str(value, "args"), Bool(value, "devtools"), Bool(value, "restart"), apps);
                var data = new JsonObject { ["kind"] = plan.Kind, ["target"] = plan.Target, ["args"] = plan.Arguments, ["executable"] = plan.Executable, ["appId"] = plan.AppId, ["name"] = plan.Name,
                    ["commandArguments"] = plan.Executable == null || plan.Kind == "shortcut" ? plan.Arguments : ClaudeChromiumArguments(plan.Executable, plan.Arguments, plan.Devtools ? 16183 : null, plan.Devtools && ClaudeBrowser(plan.Executable) ? @"C:\NexusFixture\fresh profile" : null) };
                data["parsedArguments"] = new JsonArray(Program.SplitArgs(Str(data, "commandArguments")).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                if (value["profileFixture"] is JsonValue) data["accountMarker"] = ClaudeProfileAccount(Str(value, "profileFixture"));
                if (value["activationHResult"] != null) data["fileContractFallbackAllowed"] = ClaudeFileContractRejected(checked((int)Number(value, "activationHResult")));
                results.Add(EngineTestJson.Node(data));
            }
            catch (Exception error) when (error is ActionRefusedException or InvalidOperationException or InvalidDataException or ArgumentException) { results.Add(EngineTestJson.Node(new JsonObject { ["refused"] = error.Message })); }
        }
        Console.WriteLine(results.ToJsonString()); return 0;
    }
}
