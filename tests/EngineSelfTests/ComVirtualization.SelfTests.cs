// Offline self-checks of commun/engine/ComVirtualization.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static ComWorker;

static partial class ComWorkerSelfTests
{
    public static int VirtualizationSelfTest()
    {
        var checks = 0; var calls = new List<long>(); var releases = new List<long>();
        void Assert(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        MaterializeSafely(() => { }, () => 1, () => 2, p => calls.Add(p), p => releases.Add(p));
        Assert(calls.SequenceEqual(new long[] { 1, 2 }), "Realize precedes ScrollIntoView");
        Assert(releases.SequenceEqual(new long[] { 1, 2 }), "All acquired patterns released");
        calls.Clear(); releases.Clear();
        var stopped = false;
        try { MaterializeSafely(() => { if (calls.Count > 0) throw new ActionRefusedException("identity changed"); }, () => 1, () => 2,
            p => calls.Add(p), p => releases.Add(p)); }
        catch (ActionRefusedException) { stopped = true; }
        Assert(stopped && calls.SequenceEqual(new long[] { 1 }), "Identity change after Realize forbids ScrollIntoView");
        Assert(releases.SequenceEqual(new long[] { 1 }), "Pattern released on interruption");
        calls.Clear(); releases.Clear(); var unknown = false;
        try { MaterializeSafely(() => { }, () => 1, () => 2, p => { calls.Add(p); throw new IOException("lost after Realize"); }, p => releases.Add(p)); }
        catch (IOException) { unknown = true; }
        Assert(unknown && calls.SequenceEqual(new long[] { 1 }), "An uncertain Realize is never retried and never scrolls");
        Assert(releases.SequenceEqual(new long[] { 1 }), "Failed pattern released");
        calls.Clear(); MaterializeSafely(() => { }, () => 0, () => 0, p => calls.Add(p), _ => { });
        Assert(calls.Count == 0, "An unsupported pattern sends nothing");
        Console.WriteLine(EngineTestJson.Serialize(new ComWorkerVirtualizationSelfTestJson(ok: true, checks: checks))); return 0;
    }
}
