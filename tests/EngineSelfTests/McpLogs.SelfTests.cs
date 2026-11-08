// Offline self-checks of commun/engine/McpLogs.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpServer;

static partial class McpServerSelfTests
{
    public static int LogsSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-logs-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root);
            var historical = Path.Combine(root, "historical"); Directory.CreateDirectory(historical);
            var legacy = """{"ts":"2026-10-29T12:00:00Z","tool":"act","ms":9,"ok":false,"error":"Target missing: \"private-target\"","args":{"text":"typed-secret","accord":"authorization-secret","foreground":false},"steps":[{"do":"click","method":"uia:Invoke","attempts":[{"method":"win32","text":"attempt-secret"}]}]}""";
            var historicalFile = Path.Combine(historical, "2026-10-29.jsonl"); File.WriteAllText(historicalFile, legacy + "\n{partial\n");
            var before = File.ReadAllBytes(historicalFile);
            var state = new SessionState("claude");
            WriteDebugRecord(state, "act", new JsonObject { ["text"] = "new-typed-secret" }, new JsonObject { ["structuredContent"] = new JsonObject { ["success"] = true, ["steps"] = new JsonArray(new JsonObject { ["technique"] = "background:win32", ["status"] = "verified", ["text"] = "step-secret" }) } }, 3);
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            File.WriteAllText(Path.Combine(root, "journal", "2026-10.jsonl"), """{"ts":"2026-10-29T12:00:00Z","action":"click","issue":"~","statut":"unknown"}""" + "\n");
            var roots = new[] { historical, Path.Combine(root, "logs") };
            var all = ReadDebugRecords(roots, Path.Combine(root, "journal"), 10, false);
            var errors = ReadDebugRecords(roots, Path.Combine(root, "journal"), 1, true);
            var serialized = all.ToJsonString();
            void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException("Logs selftest: " + reason); }
            Assert(all.Count == 2 && errors.Count == 1 && !Bool(errors[0], "ok"), "legacy/native merge and errors_only");
            Assert(Number(all[0], "ms", -1) == 9 && Number(all[1], "ms", -1) == 3 && Number(DebugFields(new JsonObject { ["index"] = 7 }, DebugStepFields), "index", -1) == 7, "durations and native numeric step identifiers retained");
            Assert(ReadDebugRecords(roots, Path.Combine(root, "journal"), 1, false).Count == 1, "count");
            Assert(serialized.Contains("uia:Invoke") && serialized.Contains("background:win32") && serialized.Contains("win32"), "diagnostic techniques retained");
            Assert(!serialized.Contains("secret") && !serialized.Contains("private-target"), "typed data and permissions excluded");
            Assert(before.SequenceEqual(File.ReadAllBytes(historicalFile)), "legacy history is read-only");
            Assert(DebugRoots(state).SequenceEqual([Path.Combine(root, "logs")]), "custom home isolated");
            Assert(ReadDebugRecords([], Path.Combine(root, "journal"), 10, false).Count == 1, "journal fallback without full history");
            WriteDebugRecord(state, "act", new JsonObject(), null, 1, new InvalidOperationException("Unknown modifier typed-secret"));
            foreach (var tool in new[] { "launch", "window" }) WriteDebugRecord(state, tool, new JsonObject(), new JsonObject { ["structuredContent"] = new JsonObject { ["status"] = "unknown", ["opened"] = null, ["verified"] = false } }, 2);
            WriteDebugRecord(state, "act", new JsonObject(), new JsonObject { ["structuredContent"] = new JsonObject { ["status"] = "paused", ["paused"] = true, ["success"] = false, ["continuation"] = "fixture-token" } }, 3);
            var final = ReadDebugRecords(roots, Path.Combine(root, "journal"), 20, false);
            Assert(final.Count == 6 && ReadDebugRecords(roots, Path.Combine(root, "journal"), 20, true).Count == 4, "unknown is a diagnostic failure; normal pause is not");
            Assert(!final.ToJsonString().Contains("typed-secret"), "unquoted exception content excluded by the real writer");
            Assert(DebugFields(new JsonObject { ["technique"] = "UIA Invoke", ["method"] = "messages without activation" }, DebugStepFields).ToJsonString() == """{"method":"messages without activation","technique":"UIA Invoke"}""", "historical technique names with spaces are kept");
            Assert(DebugDiagnostic("Unknown modifier typed-secret") == "Unknown modifier" && !DebugFields(new JsonObject { ["message"] = "unquoted-step-secret", ["attempts"] = new JsonArray("unquoted-attempt-secret") }, DebugStepFields).ToJsonString().Contains("secret"), "step and attempt diagnostic content excluded");
            foreach (var category in new[] { "Observation expired", "Observation already consumed or replaced", "Interface structure changed", "Needs the foreground", "Unknown postcondition field", "Postcondition not observed", "Control identity changed", "Window application identity changed" })
            {
                Assert(DebugDiagnostic(category + ": typed-secret") == category, "safe actionable category retained");
                Assert(DebugException(new ActionRefusedException(category + ": typed-secret")) == category, "exception writer preserves only the safe category");
            }
            Assert(DebugDiagnostic("unmet") == "unmet" && DebugException(new InvalidOperationException("private-unrecognized-secret")) == "InvalidOperationException", "fixed statuses retained and unknown exception content excluded");
            var historicalVerdicts = DebugFields(new JsonObject { ["ok"] = true, ["already"] = true, ["preuve"] = "unquoted-proof-secret", ["error_type"] = "TimeoutException" }, DebugStepFields);
            Assert(Bool(historicalVerdicts, "ok") && Bool(historicalVerdicts, "already") && Str(historicalVerdicts, "error_type") == "TimeoutException" && !historicalVerdicts.ToJsonString().Contains("secret"), "legacy verdicts retained and proof text redacted");
            Assert(DebugAttempt("UIA Invoke ✗ unquoted-attempt-secret") == "UIA Invoke ✗" && DebugAttempt("DevTools insertText ✓") == "DevTools insertText ✓" && !DebugAttempt("unknown-attempt-secret ✗ private")!.Contains("secret"), "real legacy attempt strings retain known technique/verdict only");
            foreach (var proof in new[] { "auto", "invoque", "absente", "impossible" }) Assert(Str(DebugFields(new JsonObject { ["preuve"] = proof }, DebugStepFields), "preuve") == proof, "fixed legacy proof category retained");
            WriteDebugRecord(state, "window", new JsonObject { ["action"] = "unquoted-private-value", ["show"] = "private-show", ["direction"] = "private-direction" }, null, 1, new ActionRefusedException("invalid private input"));
            Assert(!ReadDebugRecords(roots, Path.Combine(root, "journal"), 30, false).ToJsonString().Contains("private-value"), "unknown action values never persisted");
            Assert(!DebugFields(new JsonObject { ["foreground"] = "private-foreground", ["count"] = "private-count", ["ms"] = "private-duration" }, ["foreground", "count", "ms"]).ToJsonString().Contains("private-"), "invalid scalar strings never persisted");
            JsonObject Gesture(string status, bool success, string message) => new() { ["status"] = status, ["success"] = success, ["message"] = message };
            JsonObject Batch(int total, params JsonObject[] steps) => new() { ["status"] = "unverified", ["success"] = false, ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()), ["progress"] = new JsonObject { ["completed"] = steps.Count(s => Bool(s, "success")), ["total"] = total, ["nextStep"] = steps.Length - 1 } };
            var sent = "Input sent; no exact postcondition was supplied. Look before deciding the next gesture.";
            Assert(SentWithoutExpectOnly(Batch(2, Gesture("verified", true, ""), Gesture("sent-unconfirmed", false, sent))), "complete batch ending in a send without expect is not a failure");
            Assert(!SentWithoutExpectOnly(Batch(2, Gesture("sent-unconfirmed", false, sent))), "batch stopped before its later gestures stays a failure");
            Assert(!SentWithoutExpectOnly(Batch(2, Gesture("unmet", false, "Postcondition not observed; no automatic replay"), Gesture("sent-unconfirmed", false, sent))), "earlier failed gesture stays a failure");
            Assert(!SentWithoutExpectOnly(Batch(1, Gesture("sent-unconfirmed", false, "Window command may have been sent; inspect its current state before deciding anything else."))), "uncertain send stays a failure");
            var withError = Gesture("sent-unconfirmed", false, sent); withError["error_type"] = "TimeoutException";
            Assert(!SentWithoutExpectOnly(Batch(1, withError)) && !SentWithoutExpectOnly(Batch(0)) && !SentWithoutExpectOnly(new JsonObject { ["status"] = "failed", ["steps"] = new JsonArray(Gesture("verified", true, "")) }), "captured errors, empty and paused/failed batches stay failures");
            WriteDebugRecord(state, "act", new JsonObject(), new JsonObject { ["structuredContent"] = Batch(1, Gesture("sent-unconfirmed", false, sent)) }, 4);
            WriteDebugRecord(state, "act", new JsonObject(), new JsonObject { ["structuredContent"] = Batch(3, Gesture("verified", true, ""), Gesture("sent-unconfirmed", false, sent)) }, 4);
            var lastTwo = ReadDebugRecords(roots, Path.Combine(root, "journal"), 2, false);
            Assert(lastTwo.Count == 2 && Bool(lastTwo[0], "ok") && !Bool(lastTwo[1], "ok"), "real writer: send without expect ok, interrupted batch kept in errors_only");
            foreach (var value in new[] { "refused-before-input", "returned", "input-uncertain" }) Assert(DebugCategorical("status", value) == value, "fixed fallback status retained");
            foreach (var value in new[] { "not-taken", "refused", "held-for-interaction", "kept-requested" }) Assert(DebugCategorical("foregroundReturned", value) == value, "fixed foreground result retained");
            var journalHome = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME");
            try
            {
                Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", Path.Combine(root, "empty-isolated-home")); Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", null);
                Assert(DebugJournalRoot(state) == Path.Combine(root, "empty-isolated-home", "journal", "windows", "claude"), "custom home does not fall back to the user's shared journal");
                Assert(LogsNative(state, new JsonObject())["structuredContent"]!["records"]!.AsArray().Count == 0, "public logs route is isolated with an empty custom home");
            }
            finally { Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root); Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", journalHome); }
            Console.WriteLine("{\"ok\":true,\"history\":true,\"readonly\":true,\"typedTextOmitted\":true,\"techniques\":true}"); return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", previous);
            // Only a fixture folder of the temporary directory is ever deleted (never thrown from finally).
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
