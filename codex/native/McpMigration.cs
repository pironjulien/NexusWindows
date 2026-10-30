using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

static partial class McpServer
{
    static string CommonHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "nexus-windows");
    static string? SourceRoot()
    {
        var manifest = Path.Combine(CommonHome, "installation.json");
        return File.Exists(manifest) ? JsonNode.Parse(File.ReadAllText(manifest))?["SourceRoot"]?.GetValue<string>() : null;
    }
    static JsonObject ConvertLegacy(JsonObject data, string agent, bool fromMirror = false)
    {
        if (Number(data, "schema") != 1 || data["app"] is not JsonValue || data["routines"] is not JsonObject) throw new InvalidDataException("Unknown legacy routine format; source left intact");
        var key = Str(data, "app");
        if (!key.StartsWith("process:") && key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key = "process:" + key.ToLowerInvariant();
        var converted = new JsonObject { ["schema"] = 1, ["appKey"] = Routines.ApplicationKey(key), ["routines"] = new JsonObject(), ["forgotten"] = data["forgotten"]?.DeepClone() ?? new JsonObject() };
        foreach (var pair in data["routines"]!.AsObject())
        {
            var old = pair.Value!.AsObject();
            if (fromMirror && !TransferableLegacyRoutine(old)) continue;
            var steps = new JsonArray();
            foreach (var node in old["steps"]!.AsArray())
            {
                var raw = node as JsonObject ?? throw new InvalidDataException("Invalid legacy routine step; source left intact");
                var step = LegacyStep(raw); var action = Str(step, "do", Str(step, "action")); var secondaryAction = Str(step, "action");
                step.Remove("do"); step.Remove("action"); step.Remove("confirm"); step.Remove("accord");
                step["kind"] = action switch { "set" or "clear" or "set_value" => "setValue", "type" or "paste" or "type_text" => "typeText", "check" or "uncheck" or "double_click" or "right_click" => "click", "expand" or "collapse" => "secondary", "press_key" or "hotkey" => "key", "select_option" => "select", "sleep" => "wait", "move" or "mouse_move" => "hover", "left_click" => "click", _ => action };
                var kind = Str(step, "kind");
                if (kind == "secondary" && secondaryAction.Length > 0) step["action"] = secondaryAction;
                if (step["target"] != null) { step[kind == "typeText" ? "focus" : "selector"] = LegacySelector(step["target"]!); step.Remove("target"); }
                if (kind == "setValue") { step["value"] = action == "clear" ? JsonValue.Create("") : step["text"]?.DeepClone() ?? step["value"]?.DeepClone(); step.Remove("text"); }
                if (action is "check" or "uncheck") step["expect"] ??= new JsonObject { ["selector"] = step["selector"]!.DeepClone(), ["field"] = "checked", ["equals"] = action == "check" };
                if (action is "expand" or "collapse") { step["action"] = action == "expand" ? "Expand" : "Collapse"; }
                if (action == "double_click") step["count"] = 2;
                if (action == "right_click") step["button"] = "right";
                if (kind == "scroll") { step["scrollY"] ??= -Number(step, "delta_y"); step["scrollX"] ??= Number(step, "delta_x"); step.Remove("delta_y"); step.Remove("delta_x"); }
                steps.Add(step);
            }
            steps = Routines.PrepareRoutineSteps(steps);
            if (!DateTimeOffset.TryParse(Str(old, "saved"), out _)) throw new InvalidDataException("Invalid legacy saved date");
            var entry = new JsonObject { ["steps"] = steps, ["saved"] = Str(old, "saved"), ["anchors"] = null, ["revision"] = null, ["lastOk"] = false,
                ["coverage"] = old["coverage"]?.DeepClone() ?? JsonValue.Create("unqualified"), ["verifiedTransitions"] = old["verifiedTransitions"]?.DeepClone(),
                ["runs"] = Number(old, "runs"), ["lastRun"] = old["last_run"]?.DeepClone(),
                ["migration"] = new JsonObject { ["sourceAgent"] = agent, ["requiresAnchors"] = true, ["legacyDefinition"] = old.DeepClone(), ["note"] = "Fresh look and explicit inspect required; anchors established only by a complete verified run." } };
            // Authorizations never survive an import, including in the archived definition.
            foreach (var step in entry["migration"]!["legacyDefinition"]!["steps"]!.AsArray().OfType<JsonObject>()) { step.Remove("confirm"); step.Remove("accord"); }
            converted["routines"]![Routines.ValidateRoutineName(pair.Key)] = entry;
        }
        return converted;
    }
    public static int MigrateRoutines()
    {
        var source = SourceRoot() ?? throw new InvalidOperationException("Install the common runtime before migrating routines");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var report = new JsonArray();
        foreach (var agent in GardeFous.ValidAgents)
        {
            var old = agent switch { "claude" => Path.Combine(home, ".claude", "nexus-claude", "routines"), "antigravity" => Path.Combine(home, ".nexusagy", "routines"), _ => Path.Combine(local, "NexusCodex", "ComputerUse", "routines") };
            var project = agent == "antigravity" ? "agy" : agent;
            var candidates = new (string Directory, bool Mirror)[] { (old, false), (Path.Combine(source, project, "routines"), true),
                (Path.Combine(local, "NexusWindows", agent == "antigravity" ? "Antigravity" : agent == "claude" ? "Claude" : "Codex", "routines"), false),
                (Path.Combine(source, project, "routines", "native"), true) };
            var imported = 0;
            foreach (var candidate in candidates.DistinctBy(c => c.Directory, StringComparer.OrdinalIgnoreCase))
            {
                var dir = candidate.Directory;
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var data = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                    if (data["appKey"] == null) data = ConvertLegacy(data, agent, fromMirror: candidate.Mirror);
                    var memory = new NativeRoutineMemory(agent, Str(data, "appKey"));
                    imported += memory.Import(data, fromMirror: candidate.Mirror); memory.Sync();
                }
            }
            report.Add(new JsonObject { ["agent"] = agent, ["imported"] = imported, ["checkedLocations"] = new JsonArray(candidates.Select(s => (JsonNode)JsonValue.Create(s.Directory)!).ToArray()) });
        }
        Console.WriteLine(report.ToJsonString()); return 0;
    }
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
