// A key the application ignores when posted (WPF Ctrl+K) goes by the real keyboard in a brief foreground lease. The
// 9 November 2026 bench measured 3.4 to 3.9 s in front: three full reads of the window and a background attempt already
// refused ran inside the lease. These tests pin which steps take that path and which shortened key path it uses;
// no window is shown and no input is sent.
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class BriefForegroundKeyTests
{
    static McpServer.SessionState Claude() => new("claude") { BoundWindow = (1, "process:wpf_app.exe", "Labo WPF", 2) };
    static JsonObject CtrlK(bool? fallback = null)
    {
        var step = new JsonObject { ["kind"] = "key", ["key"] = "ctrl+k" };
        if (fallback is { } f) step["fallback"] = f;
        return step;
    }
    static JsonObject RefusedForForeground() => new() { ["status"] = "refused", ["needsForeground"] = true };

    [Fact]
    public void Only_a_key_refused_for_the_foreground_and_not_forbidden_takes_the_brief_lease()
    {
        var state = Claude();
        Assert.True(McpServer.BriefForegroundKey(state, CtrlK(), RefusedForForeground(), physicalAllowed: false, routine: null));
        // fallback:false on the step forbids it; foreground=true already allows the real keyboard; routines never take it.
        Assert.False(McpServer.BriefForegroundKey(state, CtrlK(fallback: false), RefusedForForeground(), physicalAllowed: false, routine: null));
        Assert.False(McpServer.BriefForegroundKey(state, CtrlK(), RefusedForForeground(), physicalAllowed: true, routine: null));
        Assert.False(McpServer.BriefForegroundKey(state, CtrlK(), RefusedForForeground(), physicalAllowed: false, routine: "save"));
        // A key refused for another reason (guard, focus) or not refused at all: no lease.
        Assert.False(McpServer.BriefForegroundKey(state, CtrlK(), new JsonObject { ["status"] = "refused" }, physicalAllowed: false, routine: null));
        Assert.False(McpServer.BriefForegroundKey(state, CtrlK(), new JsonObject { ["status"] = "sent-unconfirmed", ["needsForeground"] = true }, physicalAllowed: false, routine: null));
        Assert.False(McpServer.BriefForegroundKey(state, new JsonObject { ["kind"] = "click" }, RefusedForForeground(), physicalAllowed: false, routine: null));
    }

    [Fact]
    public void The_shortened_key_path_needs_the_lease_and_the_real_keyboard_and_never_runs_through_devtools()
    {
        Assert.True(McpServer.BriefKeyPath(briefKey: true, devtools: false, physicalAllowed: true));
        Assert.False(McpServer.BriefKeyPath(briefKey: false, devtools: false, physicalAllowed: true));
        Assert.False(McpServer.BriefKeyPath(briefKey: true, devtools: true, physicalAllowed: true));
        Assert.False(McpServer.BriefKeyPath(briefKey: true, devtools: false, physicalAllowed: false));
    }

    [Fact]
    public void A_new_session_is_never_in_the_brief_key_path()
    {
        Assert.False(Claude().BriefKey);
    }
}
