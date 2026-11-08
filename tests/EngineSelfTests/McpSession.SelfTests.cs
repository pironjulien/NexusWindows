// Offline self-checks of commun/engine/McpSession.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpServer;

static partial class McpServerSelfTests
{
    internal static int NativeFallbackTraceSelfTest()
    {
        var assertions = 0;
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException("Fallback trace selftest: " + why); assertions++; }
        var calls = 0;
        var outcome = NativeFallbackAttempt(() => { calls++; return new JsonObject { ["ok"] = true, ["effect"] = "sent" }; }, () => true);
        Check(calls == 1 && outcome["attempts"] is JsonArray { Count: 2 }, "one fallback sends once and retains its preceding refusal");
        var result = StepResult("unmet", "~", false, false, null, outcome);
        Check(Str(result, "status") == "unmet" && result["attempts"] is JsonArray { Count: 2 }, "transport return never changes UI proof status");
        Check(Str(outcome["attempts"]![0], "status") == "refused-before-input" && Str(outcome["attempts"]![1], "status") == "returned", "attempt statuses distinguish no-input refusal from a transport return");
        foreach (var entered in new[] { false, true })
        {
            var original = new IOException("private-field private-key private-text"); calls = 0;
            try { NativeFallbackAttempt(() => { calls++; throw original; }, () => entered); throw new InvalidOperationException("Expected fallback failure"); }
            catch (IOException error)
            {
                Check(ReferenceEquals(original, error) && calls == 1, "fallback errors are rethrown unchanged without replay");
                var failure = RoutineFailure(error, entered);
                Check(failure["attempts"] is JsonArray { Count: 2 } && Str(failure["attempts"]![1], "status") == (entered ? "input-uncertain" : "refused-before-input"), "failure metadata survives the same classification path");
                Check(Str(failure, "status") == (entered ? "unknown" : "failed"), "trace annotations leave failure/unknown classification intact");
                Check(!failure["attempts"]!.ToJsonString().Contains("private-"), "trace metadata excludes diagnostic, key, target and field text");
            }
        }
        return assertions;
    }
}
