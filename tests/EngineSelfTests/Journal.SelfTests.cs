// Offline self-checks of commun/engine/Journal.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Journal;

static partial class JournalSelfTests
{
    public static int SelfTest()
    {
        void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("Journal SelfTest failed: " + msg);
        }

        Assert(AppName(@"process:C:\Windows\System32\mspaint.exe") == "mspaint.exe", "AppName process");
        Assert(AppName("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") == "microsoft.windowscalculator_8wekyb3d8bbwe!app", "AppName AUMID");
        Assert(CleanError("Target not found: \"Secret button\" (window 12)") == "Target not found", "CleanError strips quotes and parens");
        Assert(CleanError(null) == null, "CleanError null");
        Assert(AppName(@"C:\Dossiers\Dossier-secret.pdf") == null && AppName("Dossier-secret.pdf") == null && AppName(Path.GetTempPath()) == null, "AppName never journals a document or folder");
        Assert(AppName(@"C:\Program Files\App\Editeur.exe") == "editeur.exe" && AppName("Bloc-notes") == "bloc-notes", "AppName keeps executables and app names");
        Assert(CleanError("Unknown key secretABC") == "Unknown key" && CleanError("Unknown modifier « secretABC »") == "Unknown modifier", "CleanError never journals an unknown key value");

        var tmp = Path.Combine(Path.GetTempPath(), "nexus-journal-test-" + Guid.NewGuid().ToString("N"));
        var prev = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", tmp);
            var ts = new DateTimeOffset(2026, 10, 29, 12, 0, 0, 123, TimeSpan.Zero);
            // Shared journal FORMAT.md v1: every line complete, so a writer that goes silent or drops a field fails here.
            // The version is the engine's own (McpServer.Version), as every MCP action passes it.
            WriteEntry("codex", McpServer.Version, "sess-1", @"process:C:\Windows\notepad.exe", "click", "verified", "background:uia", 42, false, "Clicked the target", ts);
            WriteEntry("codex", McpServer.Version, "sess-1", @"process:C:\Windows\notepad.exe", "wait", "waited", null, 7, false, "Waited 7 ms; no desktop input sent", ts);
            // Failure and guard rail classified by the act step itself (RoutineFailure), then journaled: a target not found
            // before any input is a failure (✗), only a guard rail that sent nothing on purpose is `refus`.
            var notFound = McpServer.RoutineFailure(new InvalidOperationException("Target not found: \"Secret field\" (window 12)"), false);
            var guarded = McpServer.RoutineFailure(new GuardRailException("Blocked by the shared garde-fous policy: \"Secret app\" is protected"), false);
            Assert(notFound["status"]!.GetValue<string>() == "failed" && guarded["status"]!.GetValue<string>() == "refused", "act step: failure vs guard rail");
            WriteEntry("codex", McpServer.Version, null, @"process:C:\Windows\notepad.exe", "setValue", notFound["status"]!.GetValue<string>(), null, 5, false, notFound["message"]!.GetValue<string>(), ts);
            WriteEntry("codex", McpServer.Version, "sess-2", "notepad.exe", "typeText", "julien-active", null, -3, false, "Julien is typing: \"private text\"", ts);
            WriteEntry("codex", McpServer.Version, "sess-2", "notepad.exe", "click", guarded["status"]!.GetValue<string>(), null, 2, false, guarded["message"]!.GetValue<string>(), ts);
            var file = Path.Combine(tmp, "windows", "codex", "2026-10.jsonl");
            Assert(File.Exists(file), "journal file created");
            var lines = File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Length > 0).ToArray();
            Assert(lines.Length == 5, "one line per action");
            Assert(!string.Join("\n", lines).Contains("Secret field") && !string.Join("\n", lines).Contains("private text") && !string.Join("\n", lines).Contains("Secret app"), "no screen or typed text");
            var keys = new[] { "v", "ts", "agent", "outil", "appel", "app", "cadre", "action", "technique", "issue", "ms", "premier_plan", "erreur" };
            var valid = new[] { "✓", "=", "~", "✗", "refus", "différé", "arrêt" };
            var docs = lines.Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                foreach (var d in docs)
                {
                    var r = d.RootElement;
                    foreach (var key in keys) Assert(r.TryGetProperty(key, out _), "field " + key);
                    Assert(r.GetProperty("v").ValueKind == JsonValueKind.Number && r.GetProperty("v").GetInt32() == 1, "v 1");
                    Assert(r.GetProperty("ts").GetString() == "2026-10-29T12:00:00.123Z", "ts UTC ISO 8601 with milliseconds");
                    Assert(DateTimeOffset.TryParseExact(r.GetProperty("ts").GetString(), "yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out _), "ts parses");
                    Assert(r.GetProperty("agent").GetString() == "codex", "agent codex");
                    Assert(r.GetProperty("outil").GetString() == McpServer.Version, "outil is the engine version");
                    Assert(r.GetProperty("app").GetString() == "notepad.exe", "app notepad.exe");
                    Assert(valid.Contains(r.GetProperty("issue").GetString()), "issue valid");
                    Assert(r.GetProperty("ms").ValueKind == JsonValueKind.Number && r.GetProperty("ms").TryGetInt64(out var ms) && ms >= 0, "ms non-negative integer");
                    Assert(r.GetProperty("premier_plan").ValueKind is JsonValueKind.True or JsonValueKind.False, "premier_plan boolean");
                }
                var (click, wait, failed, deferred, refused) = (docs[0].RootElement, docs[1].RootElement, docs[2].RootElement, docs[3].RootElement, docs[4].RootElement);
                Assert(click.GetProperty("appel").GetString() == "sess-1" && click.GetProperty("action").GetString() == "click", "appel and action");
                Assert(click.GetProperty("issue").GetString() == "✓" && click.GetProperty("ms").GetInt64() == 42, "issue verified, ms 42");
                Assert(click.GetProperty("erreur").ValueKind == JsonValueKind.Null, "erreur null on a verified step, even with a report message");
                Assert(wait.GetProperty("issue").GetString() == "✓" && wait.GetProperty("erreur").ValueKind == JsonValueKind.Null, "a completed wait is verified, without erreur");
                Assert(failed.GetProperty("appel").ValueKind == JsonValueKind.Null, "appel null without a session");
                Assert(failed.GetProperty("issue").GetString() == "✗" && failed.GetProperty("erreur").GetString() == "Target not found", "failure before input: ✗ with its short stable cause");
                Assert(refused.GetProperty("issue").GetString() == "refus" && refused.GetProperty("erreur").GetString() == "Blocked by the shared garde-fous policy", "guard rail: refus with its cause, never the app name");
                Assert(deferred.GetProperty("issue").GetString() == "différé" && deferred.GetProperty("erreur").GetString() == "Julien is typing", "deferred: cause cut before screen text");
                Assert(deferred.GetProperty("ms").GetInt64() == 0, "negative duration clamped to 0");
            }
            finally { foreach (var d in docs) d.Dispose(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", prev);
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine(EngineTestJson.Serialize(new JournalSelfTestJson(ok: true)));
        return 0;
    }
}
