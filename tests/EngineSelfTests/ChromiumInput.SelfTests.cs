// Offline self-checks of commun/engine/ChromiumInput.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using static ChromiumInput;

static partial class ChromiumInputSelfTests
{
    // Offline check (tests): stdin = {"units": text} or {"same": [actual, expected]}.
    public static int SelfTest()
    {
        using var input = System.Text.Json.JsonDocument.Parse(Console.In.ReadToEnd());
        var root = input.RootElement;
        object result;
        if (root.TryGetProperty("simulatedText", out var simulatedText)) result = SimulatedTextSelfTest(simulatedText);
        else if (root.TryGetProperty("cef", out var cef))
        {
            var topClass = cef.GetProperty("topClass").GetString() ?? "";
            var hosted = OwnedCef(cef.GetProperty("pid").GetUInt32(), cef.GetProperty("root").GetInt64(),
                cef.GetProperty("children").EnumerateArray().Select(c => new HostChild(c.GetProperty("class").GetString() ?? "", c.GetProperty("pid").GetUInt32(), c.GetProperty("root").GetInt64())));
            var simulated = SupportsSimulatedActivation(topClass);
            result = new ChromiumInputSelfTestJson(content: hosted || simulated, cef: hosted, focusSimulation: simulated, typeSimulation: simulated,
                selectSimulation: simulated, postedHover: !simulated, wheelDevtools: hosted || simulated,
                doubleClickInvoke: !(hosted || simulated));
        }
        else if (root.TryGetProperty("click", out var click)) result = ComWorkerSelfTests.ClickSelfTest(click);
        else result = root.TryGetProperty("units", out var u) ? Units(u.GetString()!)
            : Same(root.GetProperty("same")[0].GetString(), root.GetProperty("same")[1].GetString()!);
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }
}
