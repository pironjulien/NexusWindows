// Offline self-checks of commun/engine/ComChromiumFocus.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Text.Json;
using static ComWorker;

static partial class ComWorkerSelfTests
{
    internal static object ChromiumFocusSelfTest()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Chromium focus selftest: " + message); }
        var events = new List<string>(); var focused = false; var takes = 0; var publications = 0; var reads = 0; var waits = 0;
        bool Read() => ReadPublishedFocus(() => events.Add("identity"), () => { publications++; events.Add("publish"); focused = true; }, () => { reads++; events.Add("focus"); return focused; });
        var result = WaitPublishedFocus(() => { takes++; events.Add("take"); }, Read, () => waits < 3, () => waits++);
        Check(result && takes == 1 && publications == 1 && reads == 1 && waits == 0, "one accSelect, publication, then actual focus proof");
        Check(events.SequenceEqual(new[] { "take", "identity", "publish", "identity", "focus" }), "identity is fresh on both sides of publication");
        Check(focused, "publication can expose the pending real focus state");
        takes = publications = reads = waits = 0;
        result = WaitPublishedFocus(() => takes++, () => ReadPublishedFocus(() => { }, () => publications++, () => { reads++; return false; }), () => waits < 3, () => waits++);
        Check(!result && takes == 1 && publications == 3 && reads == 3 && waits == 3, "negative exact-target focus never becomes success and accSelect is never replayed");
        Check(!result, "a focused descendant does not stand in for the original field");
        var validations = 0; publications = reads = 0;
        try { ReadPublishedFocus(() => throw new ActionRefusedException("identity changed"), () => publications++, () => { reads++; return true; }); }
        catch (ActionRefusedException) { Check(publications == 0 && reads == 0, "identity refusal precedes publication and focus read"); }
        Acted = true;
        try { ReadPublishedFocus(() => { if (++validations == 2) throw new ActionRefusedException("ancestry changed"); }, () => publications++, () => { reads++; return true; }); }
        catch (ActionRefusedException error) { Check(publications == 1 && reads == 0, "identity or ancestry replacement after publication refuses even a positive getter"); Check(!Failure(error).beforeInput, "publication after accSelect remains after-input, never replayable"); }
        publications = reads = 0;
        try { ReadPublishedFocus(() => throw new EmergencyStopException("STOP"), () => publications++, () => { reads++; return true; }); }
        catch (EmergencyStopException) { Check(publications == 0 && reads == 0, "STOP prevents publication and focus acceptance"); }
        takes = reads = waits = 0;
        try { WaitPublishedFocus(() => { takes++; throw new IOException("lost accSelect reply"); }, () => { reads++; return true; }, () => waits < 3, () => waits++); }
        catch (IOException error) { Check(takes == 1 && reads == 0 && waits == 0, "a lost accSelect reply is terminal without retry"); Check(!Failure(error).beforeInput, "lost reply retains its after-input classification"); }
        Acted = false;
        return new ComWorkerChromiumFocusSelfTestJson(ok: true, checks: checks, gui: false, inputSent: false);
    }
}
