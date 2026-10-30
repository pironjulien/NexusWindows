using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// An observation of this process, not of installation.json. One file per process birth and session prevents an
// older or terminating connection from overwriting another AI's live version. NexusSystem validates the PID, birth,
// executable and host ancestry against the OS; a saved receipt on its own never proves a live AI connection.
internal sealed class RuntimeReceipt : IDisposable
{
    internal const string MetadataKey = "io.github.pironjulien/runtime";
    static readonly Lazy<JsonObject> ProcessIdentity = new(() =>
    {
        using var process = Process.GetCurrentProcess();
        var assembly = typeof(McpServer).Assembly.Location;
        using var stream = new FileStream(assembly, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return new JsonObject
        {
            ["schema"] = 1,
            ["product"] = "windows",
            ["pid"] = Environment.ProcessId,
            ["processStartUtc"] = process.StartTime.ToUniversalTime().ToString("O"),
            // Ticks exceed JavaScript's integer precision: keep the exact value as a decimal string.
            ["processStartTicks"] = process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["executable"] = Environment.ProcessPath,
            ["assembly"] = assembly,
            ["assemblySha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            ["version"] = McpServer.Version,
        };
    });

    readonly JsonObject identity;
    readonly string path;
    bool warned;
    bool disposed;

    internal static JsonObject Identity(string agent, string sessionId)
    {
        var result = (JsonObject)ProcessIdentity.Value.DeepClone();
        result["agent"] = agent;
        result["sessionId"] = sessionId;
        return result;
    }

    internal RuntimeReceipt(string agent, string sessionId, string? sharedHome = null)
    {
        identity = Identity(agent, sessionId);
        var home = sharedHome ?? Environment.GetEnvironmentVariable("NEXUS_SHARED_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus");
        path = Path.Combine(home, "status", "runtime", $"windows-{agent}-{Environment.ProcessId}-{identity["processStartTicks"]}-{sessionId}.json");
        Write("idle");
    }

    internal string PathName => path;

    internal void Write(string state)
    {
        if (disposed) return;
        if (state is not ("idle" or "busy" or "stopped")) throw new ArgumentOutOfRangeException(nameof(state));
        var payload = (JsonObject)identity.DeepClone();
        payload["state"] = state;
        payload["observedAt"] = DateTimeOffset.UtcNow.ToString("O");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, payload.ToJsonString() + "\n", new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Optional telemetry cannot break MCP or falsely announce success. Report it once outside MCP stdout.
            if (!warned) { Console.Error.WriteLine("NexusWindows: runtime receipt unavailable: " + error.Message); warned = true; }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Write("stopped");
        disposed = true;
    }
}
