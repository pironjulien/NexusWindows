using System.Runtime.InteropServices;

namespace NexusWindows.Tests;

// 06/11: the « Signaler un problème » form of the Feedback Hub (WinUI 3 window hosting a WebView2) failed every look and
// act with « Malformed Cacheresponse pTreeStructure String (missing closing parens) » from the whole-subtree cache request.
public class TreeReadFallbackTests
{
    const string Malformed = "Malformed Cacheresponse pTreeStructure String (missing closing parens)";

    [Fact]
    public void MalformedSubtreeCacheIsReadAgainLevelByLevel()
    {
        using var levels = new UiTree();
        var levelReads = 0;
        var tree = TreeReadFallback.Read(() => throw new COMException(Malformed, unchecked((int)0x80004005)), () => { levelReads++; return levels; });
        Assert.Same(levels, tree);
        Assert.Equal(1, levelReads);
    }

    [Fact]
    public void ProviderArgumentAndIndexErrorsAlsoFallBack()
    {
        using var levels = new UiTree();
        Assert.Same(levels, TreeReadFallback.Read(() => throw new ArgumentException(Malformed), () => levels));
        Assert.Same(levels, TreeReadFallback.Read(() => throw new IndexOutOfRangeException(), () => levels));
    }

    [Fact]
    public void EngineRefusalsBoundsAndStopAreNotHidden()
    {
        var levelReads = 0;
        UiTree Levels() { levelReads++; return new UiTree(); }
        Assert.Throws<ActionRefusedException>(() => TreeReadFallback.Read(() => throw new ActionRefusedException("refused"), Levels));
        Assert.Throws<EmergencyStopException>(() => TreeReadFallback.Read(() => throw new EmergencyStopException("stop"), Levels));
        Assert.Throws<InvalidOperationException>(() => TreeReadFallback.Read(() => throw new InvalidOperationException("Accessibility tree exceeds inspection bound"), Levels));
        Assert.Equal(0, levelReads);
    }

    [Fact]
    public void UnreadableChildrenKeepTheElementFlagged()
    {
        var none = TreeReadFallback.ChildrenOrNone(() => throw new COMException(Malformed), out var unreadable);
        Assert.Empty(none);
        Assert.True(unreadable);
        var some = TreeReadFallback.ChildrenOrNone(() => [1, 2], out unreadable);
        Assert.Equal([1, 2], some);
        Assert.False(unreadable);

        var node = new Node { Role = "group", Name = "", AutomationId = "", Description = "", RuntimeId = [7], Ancestors = [], Bounds = [], Unreadable = true };
        Assert.Equal(["unreadable"], Program.Flags(node));
        node.Offscreen = true;
        Assert.Equal(["offscreen", "unreadable"], Program.Flags(node));
    }
}
