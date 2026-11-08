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
            var ts = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
            WriteEntry("codex", "1.0.0", "sess-1", @"process:C:\Windows\notepad.exe", "click", "verified", "background:uia", 42, false, null, ts);
            var file = Path.Combine(tmp, "windows", "codex", "2026-10.jsonl");
            Assert(File.Exists(file), "journal file created");
            var content = File.ReadAllText(file, Encoding.UTF8).Trim();
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            Assert(root.GetProperty("agent").GetString() == "codex", "agent codex");
            Assert(root.GetProperty("app").GetString() == "notepad.exe", "app notepad.exe");
            Assert(root.GetProperty("issue").GetString() == "✓", "issue verified");
            Assert(root.GetProperty("ms").GetInt64() == 42, "ms 42");
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
