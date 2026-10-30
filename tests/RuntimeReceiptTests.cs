using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public sealed class RuntimeReceiptTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("antigravity")]
    public void Receipt_records_the_loaded_process_identity_and_owns_its_lifecycle(string agent)
    {
        var root = Path.Combine(TestEnvironment.SharedHome, "receipt-" + Guid.NewGuid().ToString("N"));
        using var process = Process.GetCurrentProcess();
        using var first = new RuntimeReceipt(agent, "first", root);
        using var second = new RuntimeReceipt(agent, "second", root);
        JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;
        var identity = Read(first.PathName);
        Assert.Equal(Environment.ProcessId, identity["pid"].I());
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks.ToString(), identity["processStartTicks"].S());
        Assert.Equal(Environment.ProcessPath, identity["executable"].S());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(McpServer).Assembly.Location))).ToLowerInvariant(), identity["assemblySha256"].S());
        Assert.Equal(McpServer.Version, identity["version"].S());
        Assert.Equal(agent, identity["agent"].S());
        Assert.Equal("idle", identity["state"].S());
        Assert.NotEqual(first.PathName, second.PathName);
        first.Write("busy");
        Assert.Equal("busy", Read(first.PathName)["state"].S());
        Assert.Equal("idle", Read(second.PathName)["state"].S());
        first.Dispose();
        Assert.Equal("stopped", Read(first.PathName)["state"].S());
        first.Write("idle");
        Assert.Equal("stopped", Read(first.PathName)["state"].S());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first.PathName)!, "*.tmp"));
    }
}
