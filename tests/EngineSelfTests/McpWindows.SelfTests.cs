// Offline self-checks of commun/engine/McpWindows.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using static McpServer;

static partial class McpServerSelfTests
{
    internal static int WindowToolCompatibilitySelfTest()
    {
        var count = 0;
        void Check(bool condition, string reason) { count++; if (!condition) throw new InvalidOperationException("Window tool selftest: " + reason); }
        var original = new WindowToolIdentity(42, 7, "first-process", 0, "fixture");
        Check(SameWindowToolIdentity(original, original), "same current window is allowed");
        Check(!SameWindowToolIdentity(original, original with { Window = 43 }), "another HWND is refused");
        Check(!SameWindowToolIdentity(original, original with { Pid = 8 }), "recycled HWND in another PID is refused");
        Check(!SameWindowToolIdentity(original, original with { Birth = "replacement-process" }), "recycled PID is refused");
        Check(!SameWindowToolIdentity(original, original with { Owner = 99 }), "changed owner is refused");
        Check(!SameWindowToolIdentity(original, original with { Class = "replacement-class" }), "replaced window class is refused");
        return count;
    }
}
