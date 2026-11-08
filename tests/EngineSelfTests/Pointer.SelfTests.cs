// Offline self-checks of commun/engine/Pointer.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using static Pointer;

static partial class PointerSelfTests
{
    internal static object RecipientContextSelfTest()
    {
        var checks = 0;
        void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("Pointer DPI context selftest failed"); }
        var sent = 0; var restored = new List<nint>();
        InRecipientContext(() => 42, () => sent++, previous => restored.Add(previous));
        Check(sent == 1 && restored.SequenceEqual(new nint[] { 42 }));
        try { InRecipientContext(() => 43, () => { sent++; throw new IOException("partial message"); }, previous => restored.Add(previous)); }
        catch (IOException) { Check(sent == 2 && restored.Last() == 43); }
        var before = sent;
        try { InRecipientContext(() => 0, () => sent++, previous => restored.Add(previous)); }
        catch (ActionRefusedException) { Check(sent == before && restored.Count == 2); }
        var contexts = new Stack<nint>();
        InRecipientContext(() => 1, () => InRecipientContext(() => 2, () => contexts.Push(3), previous => contexts.Push(previous)), previous => contexts.Push(previous));
        Check(contexts.SequenceEqual(new nint[] { 1, 2, 3 }));
        return new ComWorkerChromiumFocusSelfTestJson(ok: true, checks: checks, gui: false, inputSent: false);
    }
}
