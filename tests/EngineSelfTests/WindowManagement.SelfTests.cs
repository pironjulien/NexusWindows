// Offline self-checks of commun/engine/WindowManagement.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static WindowManagement;

static partial class WindowManagementSelfTests
{
    internal static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var answers = new List<object>();
        foreach (var item in input.RootElement.EnumerateArray())
        {
            try
            {
                if (item.TryGetProperty("chain", out var chain))
                {
                    var links = chain.EnumerateObject().ToDictionary(e => (nint)long.Parse(e.Name, System.Globalization.CultureInfo.InvariantCulture), e => (nint)e.Value.GetInt64());
                    answers.Add(new WindowManagementSelfTestJson(bottom: AtApplicationBottom((nint)item.GetProperty("target").GetInt64(),
                        h => links.TryGetValue(h, out var next) ? next : 0,
                        (nint)item.GetProperty("shell").GetInt64(), (nint)item.GetProperty("desktop").GetInt64())));
                    continue;
                }
                var plan = Prepare(item.GetProperty("request"));
                State StateOf(string key) => EngineTestJson.Deserialize<State>(item.GetProperty(key), CaseInsensitive);
                answers.Add(new WindowManagementSelfTestJson2(reached: Reached(plan, StateOf("before"), StateOf("after"))));
            }
            catch (Exception error) { answers.Add(new CdpSurfaceSelfTestJson2(error: error.Message)); }
        }
        Console.WriteLine(EngineTestJson.Serialize(answers));
        return 0;
    }
}
