// Offline self-checks of commun/engine/Physical.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using System.Text.Json;
using static Physical;

static partial class PhysicalSelfTests
{
    public static int TargetSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        Console.WriteLine(EngineTestJson.Serialize(input.RootElement.EnumerateArray().Select(e => SupportsRelocation(e.GetString() ?? "")).ToArray()));
        return 0;
    }
}
