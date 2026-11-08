// Offline self-checks of commun/engine/ComValueAppend.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Text.Json;
using static ComWorker;

static partial class ComWorkerSelfTests
{
    internal static object AppendValueSelfTest()
    {
        var checks = 0;
        void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException("UIA append selftest: " + label); }
        void Refused(Action action) { try { action(); throw new Exception("Expected a hard refusal"); } catch (ActionRefusedException) { checks++; } }
        var value = "fresh current"; int writes = 0, reads = 0, validations = 0;
        var result = AppendValueOnce(" appended 😀\nline", () => { reads++; return value; }, () => validations++, () => Acted = true,
            desired => { writes++; value = desired; }, () => value);
        Check(value == "fresh current appended 😀\nline" && writes == 1 && reads == 2 && validations == 2 && result.Effect == "applied", "fresh Unicode/multiline append uses one SetValue and two live reads");
        var secretReads = 0;
        string? Read() { secretReads++; return "private value"; }
        Refused(() => AppendableValue(true, true, false, Read)); Refused(() => AppendableValue(false, false, false, Read)); Refused(() => AppendableValue(false, true, true, Read));
        Check(secretReads == 0, "secret, disabled and readonly refusals precede value reads");
        Refused(() => AppendableValue(false, true, false, () => null));
        Refused(() => AppendableValue(false, true, false, () => new string('x', ComUia.MaxText + 1)));
        Acted = false; writes = 0; reads = 0;
        Refused(() => AppendValueOnce("x", () => ++reads == 1 ? "initial" : "changed", () => { }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "a concurrent value change sends nothing");
        Refused(() => AppendValueOnce("x", () => new string('x', ComUia.MaxText), () => { }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "a value that would be truncated is refused before input");
        validations = 0;
        Refused(() => AppendValueOnce("x", () => "base", () => { if (++validations == 2) throw new ActionRefusedException("identity changed"); }, () => Acted = true, _ => writes++, () => ""));
        Check(writes == 0 && !Acted, "target/ancestor identity change sends nothing");
        try { AppendValueOnce("x", () => "base", () => { }, () => Acted = true, _ => { writes++; throw new IOException("reply lost"); }, () => throw new Exception("must not read after failed write")); }
        catch (IOException error) { var failure = Failure(error); Check(!failure.beforeInput && writes == 1, "a lost SetValue reply remains after-input with one write and no replay"); }
        Acted = false; writes = 0;
        result = AppendValueOnce("x", () => "base", () => { }, () => Acted = true, _ => writes++, () => "base");
        Check(result.Effect == "sent" && writes == 1, "late UIA publication is not absence of input and never retries");
        Acted = false;
        return new ComWorkerAppendValueSelfTestJson(ok: true, checks: checks, gui: false);
    }
}
