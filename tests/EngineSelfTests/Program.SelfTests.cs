// Offline self-checks of commun/engine/Program.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using static Program;

static partial class ProgramSelfTests
{
    // Offline tests invoke this private seam by reflection, without a new production CLI mode or a UIA provider.
    internal static object InspectionFrameSelfTest()
    {
        var checks = 0;
        void Assert(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var area = new Win32.Rect { Left = 10, Top = 20, Right = 610, Bottom = 420 };
        var stale = new List<Node>(); var fresh = new List<Node>(); var calls = new List<string>(); var reads = 0;
        var observation = ReadInspection(() => { calls.Add("area"); return area; }, () => { calls.Add("read"); reads++; return fresh; },
            () => calls.Add("identity"), nodes => { calls.Add("content"); Assert(ReferenceEquals(nodes, fresh), "Content must be validated against the fresh tree"); });
        Assert(ReferenceEquals(observation.Nodes, fresh) && !ReferenceEquals(observation.Nodes, stale) && reads == 1, "Never return the preparatory cached tree");
        Assert(observation.Frame is { } f && f.Left == area.Left && f.Bottom == area.Bottom, "A stable fresh read has a frame even when a preparatory cache existed");
        Assert(calls.SequenceEqual(new[] { "identity", "area", "identity", "read", "content", "area", "content" }), "Identity and actual read are bracketed by frame measurements");
        var position = 0;
        observation = ReadInspection(() => ++position == 1 ? area : new Win32.Rect { Left = 11, Top = 20, Right = 611, Bottom = 420 }, () => fresh, () => { }, _ => { });
        Assert(observation.Frame == null, "Moving during the fresh read invalidates coordinates");
        foreach (var missingFirst in new[] { true, false })
        {
            position = 0;
            observation = ReadInspection(() => (++position == 1) == missingFirst ? null : area, () => fresh, () => { }, _ => { });
            Assert(observation.Frame == null, "A missing before/after frame never becomes a fabricated frame");
        }
        reads = 0; var refused = false;
        try { ReadInspection(() => area, () => { reads++; return fresh; }, () => throw new ActionRefusedException("window replaced"), _ => { }); }
        catch (ActionRefusedException) { refused = true; }
        Assert(refused && reads == 0, "A replaced window is rejected before reading its contents");
        var captures = 0; refused = false;
        try { ReadInspection(() => { captures++; return area; }, () => fresh, () => { }, _ => { if (captures == 2) throw new ActionRefusedException("content replaced"); }); }
        catch (ActionRefusedException) { refused = true; }
        Assert(refused && captures == 2, "Content changed during the final measurement is rejected even with stable geometry");
        return new ComWorkerVirtualizationSelfTestJson(ok: true, checks: checks);
    }

    internal static int ModalSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = input.RootElement.EnumerateArray().Select(c => ModalBoxIndex(
            c.GetProperty("chain").EnumerateArray().Select(n => (n.GetProperty("window").GetInt64(), n.GetProperty("disabled").GetBoolean())).ToList(),
            c.GetProperty("box").GetInt64())).ToList();
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }

    // Visible center of a target in the action window's rectangle (tree root, or modal dialog), offline.
    internal static int CenterSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            Node Of(string key) => new() { Role = "pane", Name = "", AutomationId = "", Description = "", RuntimeId = [], Ancestors = [],
                Bounds = c.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray() };
            try { var (x, y) = VisibleCenter(Of("target"), Of("root")); results.Add(new[] { x, y }); }
            catch (ActionRefusedException error) { results.Add(error.Message); }
        }
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }

    internal static int LaunchSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            try
            {
                var plan = PlanLaunch(Text(c, "target"), Text(c, "args"));
                var info = LaunchInfo(plan.Start, plan.ExePath, plan.Args);
                results.Add(new ProgramLaunchSelfTestJson(target: plan.Target, args: plan.Args, isExe: plan.IsExe, exePath: plan.ExePath, start: info.FileName, arguments: info.Arguments));
            }
            catch (ActionRefusedException error) { results.Add(new ProgramLaunchSelfTestJson2(refused: error.Message)); }
        }
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }
}
