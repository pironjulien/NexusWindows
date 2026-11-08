// Offline self-checks of commun/engine/McpMigration.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using static McpServer;

static partial class McpServerSelfTests
{
    public static int MigrationSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-migration-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME"); var previousMirror = Environment.GetEnvironmentVariable("NEXUS_ROUTINES_MIRROR");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root); Environment.SetEnvironmentVariable("NEXUS_ROUTINES_MIRROR", Path.Combine(root, "mirror"));
            var old = JsonNode.Parse("""{"schema":1,"app":"fixture.exe","forgotten":{},"routines":{"register":{"saved":"2026-10-26T17:43:29","fingerprint":{"path":"fixture.exe","size":42},"last_ok":true,"runs":1,"steps":[{"do":"set","target":"Name","text":"{{name}}"},{"do":"check","target":"Reminder"},{"do":"click","target":"Register","expect":"Registered"}]}}}""")!.AsObject();
            var original = old.ToJsonString(); var converted = ConvertLegacy(old, "claude");
            var memory = new NativeRoutineMemory("claude", "process:fixture.exe");
            void Assert(bool c, string why) { if (!c) throw new InvalidOperationException("Migration selftest: " + why); }
            Assert(old.ToJsonString() == original, "source preserved");
            Assert(memory.Import(converted) == 1 && memory.Import(converted) == 0, "idempotent");
            var entry = memory.Read("register")!;
            Assert(entry["anchors"] == null && !Bool(entry, "lastOk", true) && entry["revision"] == null, "no fabricated anchors or revision");
            Assert(entry["migration"]!["legacyDefinition"]!["fingerprint"]!["size"]!.GetValue<int>() == 42, "fingerprint preserved");
            Assert(Routines.ExpandRoutine(entry["steps"]!.AsArray(), new Dictionary<string, string> { ["name"] = "fixture" })[0]!["value"]!.GetValue<string>() == "fixture", "definition usable");
            var agy = JsonNode.Parse("""{"schema":1,"engine":"antigravity","app":"fixture.exe","forgotten":{},"routines":{"agy":{"saved":"2026-10-26T17:43:29Z","fingerprint":{},"steps":[{"action":"set","target":"Name","text":"{{name}}","expect":{"element":"Name","value":"{{name}}"}}]}}}""")!.AsObject();
            Assert(ConvertLegacy(agy, "antigravity")["routines"]!["agy"]!["steps"]![0]!["expect"]!["selector"]!["name"]!.GetValue<string>() == "Name", "AGY syntax import");
            var beforeInvalid = memory.Read("register")!.ToJsonString();
            var invalid = converted.DeepClone().AsObject(); invalid["routines"]!["register"]!["saved"] = "invalid";
            try { memory.Import(invalid); throw new InvalidOperationException("invalid import accepted"); } catch (InvalidDataException) { }
            Assert(memory.Read("register")!.ToJsonString() == beforeInvalid, "invalid import leaves destination intact");
            memory.RecordRun("register", false);
            memory.Sync(); Assert(memory.List().Count == 1 && Bool(memory.List()[0], "needsReview"), "mirror preserves pending review");
            memory.Forget("register"); Assert(memory.Import(converted) == 0 && memory.List().Count == 0, "tombstone wins");
            Console.WriteLine("{\"ok\":true,\"sourcePreserved\":true,\"anchorsFabricated\":false,\"idempotent\":true,\"mirror\":true,\"tombstones\":true}"); return 0;
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", previous); Environment.SetEnvironmentVariable("NEXUS_ROUTINES_MIRROR", previousMirror); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
