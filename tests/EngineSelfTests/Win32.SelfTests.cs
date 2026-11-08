// Offline self-checks of commun/engine/Win32.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using System.Text;
using static Win32;

static partial class Win32SelfTests
{
    public static int PopupSelfTest()
    {
        using var input = System.Text.Json.JsonDocument.Parse(Console.In.ReadToEnd());
        var results = input.RootElement.EnumerateArray()
            .Select(c => IsTransientPopupStyle(c.GetProperty("style").GetUInt32(), c.GetProperty("ex").GetUInt32(), c.GetProperty("cls").GetString() ?? "",
                c.TryGetProperty("menu", out var m) && m.GetBoolean(), c.TryGetProperty("linked", out var l) && l.GetBoolean())).ToList();
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }
}
