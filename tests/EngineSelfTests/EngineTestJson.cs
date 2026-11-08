// JSON of the engine self-checks, without reflection like the engine's own (EngineJson): the test-only shapes have
// their source-generated metadata here, and every engine type still resolves through EngineJsonContext. Same API and
// same per-options resolution as EngineJson, so the documents the self-checks print are unchanged.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

static class EngineTestJson
{
    static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(EngineSelfTestJsonContext.Default, EngineJsonContext.Default);
    static readonly JsonSerializerOptions Plain = new() { TypeInfoResolver = Resolver };
    static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Resolved = [];

    static JsonSerializerOptions Options(JsonSerializerOptions? options) =>
        options is null ? Plain : Resolved.GetValue(options, static source => new JsonSerializerOptions(source) { TypeInfoResolver = Resolver });

    static JsonTypeInfo Info<T>(T value, JsonSerializerOptions? options) =>
        Options(options).GetTypeInfo(typeof(T) == typeof(object) && value is not null ? value.GetType() : typeof(T));

    internal static string Serialize<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.Serialize(value, Info(value, options));
    internal static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToUtf8Bytes(value, Info(value, options));
    internal static JsonElement SerializeToElement<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToElement(value, Info(value, options));
    internal static JsonNode? SerializeToNode<T>(T value, JsonSerializerOptions? options = null) => JsonSerializer.SerializeToNode(value, Info(value, options));
    internal static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) => JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Options(options).GetTypeInfo(typeof(T)));
    internal static T? Deserialize<T>(JsonElement element, JsonSerializerOptions? options = null) => element.Deserialize((JsonTypeInfo<T>)Options(options).GetTypeInfo(typeof(T)));
    internal static JsonNode? Node<T>(T value) => value is null ? null : value as JsonNode ?? SerializeToNode(value);
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CdpClaudeInputSelfTestJson))]
[JsonSerializable(typeof(CdpClaudeInputSelfTestJson2))]
[JsonSerializable(typeof(CdpClaudeInputSelfTestJson3))]
[JsonSerializable(typeof(CdpDomKeyTraceSelfTestJson))]
[JsonSerializable(typeof(CdpDomKeyTraceSelfTestJson2))]
[JsonSerializable(typeof(CdpDomKeyTraceSelfTestJson3))]
[JsonSerializable(typeof(CdpInstrumentedServerJson))]
[JsonSerializable(typeof(CdpInstrumentedServerJson2))]
[JsonSerializable(typeof(CdpInstrumentedServerJson3))]
[JsonSerializable(typeof(CdpProveLiveSelfTestJson))]
[JsonSerializable(typeof(CdpProveSelfTestJson))]
[JsonSerializable(typeof(CdpProveSelfTestJson2))]
[JsonSerializable(typeof(CdpProveSelfTestJson3))]
[JsonSerializable(typeof(CdpProveSelfTestJson4))]
[JsonSerializable(typeof(CdpSelfTestJson))]
[JsonSerializable(typeof(CdpSelfTestJson2))]
[JsonSerializable(typeof(CdpSessionSelfTestJson))]
[JsonSerializable(typeof(CdpSessionSelfTestJson2))]
[JsonSerializable(typeof(CdpSessionSelfTestJson3))]
[JsonSerializable(typeof(CdpSessionSelfTestJson4))]
[JsonSerializable(typeof(CdpSurfaceSelfTestJson))]
[JsonSerializable(typeof(CdpSurfaceSelfTestJson2))]
[JsonSerializable(typeof(CdpWheelTraceSelfTestJson2))]
[JsonSerializable(typeof(ChromiumContentWarmupSelfTestJson))]
[JsonSerializable(typeof(ChromiumInputSelfTestJson))]
[JsonSerializable(typeof(ChromiumInputSimulatedPacketGuardSelfTestJson))]
[JsonSerializable(typeof(ChromiumInputSimulatedTextSelfTestJson))]
[JsonSerializable(typeof(ChromiumInputSimulatedTextSelfTestJson2))]
[JsonSerializable(typeof(ChromiumInputSimulatedTextSelfTestJson3))]
[JsonSerializable(typeof(ComWorkerAppendValueSelfTestJson))]
[JsonSerializable(typeof(ComWorkerChromiumFocusSelfTestJson))]
[JsonSerializable(typeof(ComWorkerClickSelfTestJson))]
[JsonSerializable(typeof(ComWorkerClickSelfTestJson2))]
[JsonSerializable(typeof(ComWorkerExpandSelfTestJson))]
[JsonSerializable(typeof(ComWorkerExpandSelfTestJson2))]
[JsonSerializable(typeof(ComWorkerExpandSelfTestJson3))]
[JsonSerializable(typeof(ComWorkerVirtualizationSelfTestJson))]
[JsonSerializable(typeof(GardeFousSelfTestJson))]
[JsonSerializable(typeof(JournalSelfTestJson))]
[JsonSerializable(typeof(McpServerSelfTestJson))]
[JsonSerializable(typeof(MenuSearchSelfTestJson))]
[JsonSerializable(typeof(MenuSearchSelfTestJson2))]
[JsonSerializable(typeof(MenuSearchSelfTestJson3))]
[JsonSerializable(typeof(ProgramLaunchSelfTestJson))]
[JsonSerializable(typeof(ProgramLaunchSelfTestJson2))]
[JsonSerializable(typeof(ScrollPlanSelfTestJson))]
[JsonSerializable(typeof(SteamSelfTestJson))]
[JsonSerializable(typeof(SteamSelfTestJson2))]
[JsonSerializable(typeof(SteamSelfTestJson3))]
[JsonSerializable(typeof(SteamSelfTestJson4))]
[JsonSerializable(typeof(SteamSelfTestJson5))]
[JsonSerializable(typeof(WindowManagementSelfTestJson))]
[JsonSerializable(typeof(WindowManagementSelfTestJson2))]
sealed partial class EngineSelfTestJsonContext : JsonSerializerContext;
