// The engine's JSON without reflection (trimming, Native AOT): no anonymous type may come back, every JSON shape has
// source-generated metadata, and EngineJson writes what JsonSerializer wrote with the caller's options.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public class EngineJsonTests
{
    static readonly Type[] EngineTypes = typeof(McpServer).Assembly.GetTypes();

    [Fact]
    public void The_engine_holds_no_anonymous_type_whose_JSON_would_need_reflection()
    {
        Assert.Empty(EngineTypes.Where(type => type.Name.Contains("AnonymousType", StringComparison.Ordinal)).Select(type => type.Name));
    }

    [Fact]
    public void Every_JSON_shape_has_source_generated_metadata()
    {
        var shapes = EngineTypes.Where(type => type.Namespace is null && System.Text.RegularExpressions.Regex.IsMatch(type.Name, "Json[0-9]*$") && type.GetMethod("<Clone>$") is not null).ToList();
        Assert.True(shapes.Count > 100, $"{shapes.Count} shapes");
        Assert.All(shapes, type => Assert.NotNull(EngineJsonContext.Default.GetTypeInfo(type)));
    }

    [Fact]
    public void The_COM_worker_writes_its_failure_instead_of_dying()
    {
        // 08/11: a click refused inside the worker crashed it (no metadata for its failure record), so the engine only
        // saw "worker exited; outcome unknown" and the cause was lost.
        ComWorker.Acted = false;
        using var reply = JsonDocument.Parse(EngineJson.Serialize((object)ComWorker.Failure(new InvalidOperationException("No InvokePattern"))));
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("InvalidOperationException", reply.RootElement.GetProperty("error").GetString());
        Assert.Equal("No InvokePattern", reply.RootElement.GetProperty("message").GetString());
        Assert.True(reply.RootElement.GetProperty("beforeInput").GetBoolean());
    }

    [Fact]
    public void A_value_declared_object_is_written_as_its_runtime_type_with_the_callers_options()
    {
        object value = new ExplorerNavigateJson(ok: true, technique: "background:shell", mechanism: "Navigate2", effect: "verified", path: @"C:\Dossier é", note: null);
        var relaxed = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        Assert.Equal("""{"ok":true,"technique":"background:shell","mechanism":"Navigate2","effect":"verified","path":"C:\\Dossier é","note":null}""", EngineJson.Serialize(value, relaxed));
        var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Equal("""{"name":"x"}""", EngineJson.Serialize((object)new ProgramMenuByNamesJson2(name: "x"), camel));
        // A node keeps its identity when added, as JsonArray.Add<T> did.
        var node = new JsonObject { ["a"] = 1 };
        Assert.Same(node, EngineJson.Node(node));
        Assert.Equal("\"text\"", EngineJson.Node("text")!.ToJsonString());
    }
}
