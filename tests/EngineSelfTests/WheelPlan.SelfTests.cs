// Offline self-checks of commun/engine/WheelPlan.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Text.Json;
using static ScrollPlan;
using static ScrollChoice;

static partial class ScrollPlanSelfTests
{
    // Offline check (tests): stdin = [{"deltaY", "deltaX", "percent"}]; output one plan per case, or the error.
    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            var (dy, dx) = (c.GetProperty("deltaY").GetInt32(), c.GetProperty("deltaX").GetInt32());
            var percent = c.TryGetProperty("percent", out var p) ? p.GetDouble() : 50;
            try
            {
                var plan = Of(dy, dx);
                var (cx, cy) = DevTools(dy, dx);
                results.Add(new ScrollPlanSelfTestJson(vertical: plan.Vertical, forward: plan.Forward, amount: plan.Amount, notches: plan.Notches, atEnd: plan.AtEnd(percent), cdpX: cx, cdpY: cy));
            }
            catch (BackgroundNotApplicableException error) { results.Add(new CdpSurfaceSelfTestJson2(error: error.Message)); }
        }
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }
}

static partial class ScrollChoiceSelfTests
{
    // Offline check (tests): stdin = {"count", "areas", "parents": {"i": parent}}; output {"index"} or {"error"}.
    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var root = input.RootElement;
        var parents = root.TryGetProperty("parents", out var map) ? map.EnumerateObject().ToDictionary(e => int.Parse(e.Name, System.Globalization.CultureInfo.InvariantCulture), e => e.Value.GetInt32()) : new();
        bool Descends(int a, int b)
        {
            for (var hops = 0; hops < 64 && parents.TryGetValue(a, out var p); hops++) { if (p == b) return true; a = p; }
            return false;
        }
        object result;
        try
        {
            CheckCount(root.GetProperty("count").GetInt32());
            result = new CdpClaudeInputSelfTestJson(index: Pick(root.GetProperty("areas").EnumerateArray().Select(a => a.GetInt64()).ToList(), Descends));
        }
        catch (BackgroundNotApplicableException error) { result = new CdpSurfaceSelfTestJson2(error: error.Message); }
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }
}
