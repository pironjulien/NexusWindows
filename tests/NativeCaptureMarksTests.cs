using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public class NativeCaptureMarksTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_virtual_control_uses_the_numeric_root_scope_expected_by_native_capture(bool explicitNull)
    {
        var element = new JsonObject { ["index"] = 7, ["name"] = "", ["bounds"] = new JsonArray(10, 20, 80, 30) };
        if (explicitNull) element["nativeWindow"] = null;
        var snapshot = new JsonObject { ["elements"] = new JsonArray(element) };
        var marks = McpServer.NativeCaptureMarks(snapshot, "auto");
        using var wire = JsonDocument.Parse(marks.ToJsonString());
        // This is the same wire-level numeric read used by Program's capture
        // operation; null caused look(image/both) to fail before any capture.
        Assert.Equal(0L, wire.RootElement[0].GetProperty("scope").GetInt64());
        Assert.Equal(7, wire.RootElement[0].GetProperty("index").GetInt32());
    }

    [Fact]
    public void A_control_with_its_own_window_keeps_that_scope_for_the_foreign_window_guard()
    {
        var snapshot = new JsonObject { ["elements"] = new JsonArray(new JsonObject
        {
            ["index"] = 9, ["name"] = "", ["nativeWindow"] = 123456L, ["bounds"] = new JsonArray(10, 20, 80, 30),
        }) };
        var marks = McpServer.NativeCaptureMarks(snapshot, "auto");
        using var wire = JsonDocument.Parse(marks.ToJsonString());
        Assert.Equal(123456L, wire.RootElement[0].GetProperty("scope").GetInt64());
        Assert.Equal(9, wire.RootElement[0].GetProperty("index").GetInt32());
    }
}
