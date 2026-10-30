// In-process runner for the engine's offline self-checks (formerly the --*-selftest command-line options): the JSON
// input goes in as standard input, the printed result comes back as text or JSON. Console and environment are process
// state, so every class using this harness belongs to the non-parallel "Engine" collection.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[CollectionDefinition("Engine", DisableParallelization = true)]
public sealed class EngineCollection;

static class SelfTest
{
    static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Lock Gate = new();

    /// <summary>Built engine executable next to the tests (ProjectReference), used for its helper children.</summary>
    public static readonly string Engine = Path.Combine(AppContext.BaseDirectory, "NexusCodex.UIA.exe");

    static SelfTest() => Program.HelperExecutable = Engine;

    public static (int Code, string Output) Text(Func<int> run, string input = "", IDictionary<string, string?>? environment = null)
    {
        lock (Gate)
        {
            var saved = environment?.ToDictionary(e => e.Key, e => Environment.GetEnvironmentVariable(e.Key));
            var (inReader, outWriter) = (Console.In, Console.Out);
            var output = new StringWriter();
            try
            {
                if (environment is not null) foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
                Console.SetIn(new StringReader(input));
                Console.SetOut(output);
                var code = run();
                Console.Out.Flush();
                return (code, output.ToString());
            }
            finally
            {
                Console.SetIn(inReader);
                Console.SetOut(outWriter);
                if (saved is not null) foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>Runs a self-check fed with <paramref name="input"/> (serialized as JSON unless already a string) and
    /// parses its JSON output; a non-zero exit code fails the test with the output.</summary>
    public static JsonNode Json(Func<int> run, object? input = null, IDictionary<string, string?>? environment = null)
    {
        var text = input switch { null => "", string s => s, _ => JsonSerializer.Serialize(input, Options) };
        var (code, output) = Text(run, text, environment);
        Assert.True(code == 0, $"exit {code}: {output}");
        return JsonNode.Parse(output) ?? throw new InvalidDataException("empty self-check output");
    }

    /// <summary>Lines printed by a self-check that reports "ok ..." lines.</summary>
    public static string[] Lines(Func<int> run, string input = "")
    {
        var (code, output) = Text(run, input);
        Assert.True(code == 0, $"exit {code}: {output}");
        return output.Trim().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Serializes a result object returned directly by an internal test seam.</summary>
    public static JsonNode Node(object value) => JsonNode.Parse(JsonSerializer.Serialize(value, Options))!;

    /// <summary>Structural JSON equality (object key order ignored), like node:assert deepEqual.</summary>
    public static void Same(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}, got {actual?.ToJsonString()}");

    public static string S(this JsonNode? node) => node!.GetValue<string>();
    public static bool B(this JsonNode? node) => node!.GetValue<bool>();
    public static long L(this JsonNode? node) => node!.GetValue<long>();
    public static int I(this JsonNode? node) => node!.GetValue<int>();
    public static string[] Strings(this JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    public static bool IsNull(this JsonNode? node) => node is null;
}
