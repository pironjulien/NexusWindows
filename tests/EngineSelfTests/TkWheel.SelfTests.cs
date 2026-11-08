// Offline self-checks of commun/engine/TkWheel.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using static TkWheel;

static partial class TkWheelSelfTests
{
    // Offline tests of the coverage geometry (the same set as NexusWindows Claude).
    public static int SelfTest()
    {
        static Win32.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
        var target = R(100, 100, 300, 200);
        var checks = new (string Name, bool Ok)[]
        {
            ("one cover", Covered(target, [(R(0, 0, 1000, 1000), 10)])),
            ("one-pixel strip refused", !Covered(target, [(R(0, 0, 110, 1000), 10), (R(111, 0, 1000, 1000), 10)])),
            ("two adjacent covers", Covered(target, [(R(0, 0, 200, 1000), 10), (R(200, 0, 1000, 1000), 10)])),
            ("corner at 250 % refused", !Covered(target, [(R(88, 100, 1000, 1000), 22)])),
            ("no cover", !Covered(target, [])),
        };
        foreach (var (name, ok) in checks) Console.WriteLine($"{(ok ? "ok" : "FAILED")} {name}");
        return checks.All(c => c.Ok) ? 0 : 1;
    }
}
