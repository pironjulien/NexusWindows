// Offline self-checks of commun/engine/McpRoutines.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpServer;

static partial class McpServerSelfTests
{
    public static int RoutineCompatibilitySelfTest()
    {
        var count = 0;
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException("Routine compatibility selftest: " + why); count++; }
        void Refused(Action action) { try { action(); } catch (ActionRefusedException) { count++; return; } throw new InvalidOperationException("Expected refusal"); }
        JsonObject Args(string raw) => JsonNode.Parse(raw)!.AsObject();
        foreach (var agent in new[] { "codex", "claude", "antigravity" })
        {
            Check(!RoutinePhysicalAllowed(agent, new()), "background only by default for every profile");
            Check(!RoutinePhysicalAllowed(agent, Args("{\"foreground\":false}")), "explicit background policy");
            Check(!RoutinePhysicalAllowed(agent, Args("{\"interaction\":true}")), "interaction alone grants no legacy foreground permission");
            Check(RoutinePhysicalAllowed(agent, Args("{\"foreground\":true}")), "explicit physical permission");
            Check(!RoutinePhysicalAllowed(agent, Args("{\"foreground\":true,\"fallback\":false}")), "fallback false remains binding");
        }
        Check(RoutineLearningAllowed(new()), "learning by default");
        Check(!RoutineLearningAllowed(Args("{\"staticUI\":false}")), "explicit staticUI refusal");
        Refused(() => RoutineLearningAllowed(Args("{\"staticUI\":\"false\"}")));
        Refused(() => RoutinePhysicalAllowed("claude", Args("{\"foreground\":1}")));
        Refused(() => RoutinePhysicalAllowed("codex", Args("{\"foreground\":1}")));
        var old = Args("{\"remember\":\"register\",\"steps\":[],\"params\":{}}"); var original = old.ToJsonString();
        var forget = RoutineCompatibilityArgs(old); ValidateRoutineControls(forget, Str(forget, "operation"));
        Check(Str(forget, "operation") == "forget" && Str(forget, "routine") == "register" && old.ToJsonString() == original, "nonmutating legacy forget alias");
        Check(Str(RoutineCompatibilityArgs(Args("{\"remember\":\"register\"}")), "operation") == "forget", "omitted legacy steps forget the named routine");
        Check(Str(RoutineCompatibilityArgs(Args("{\"remember\":\"register\",\"steps\":null}")), "operation") == "forget", "null legacy steps forget the named routine");
        Refused(() => RoutineCompatibilityArgs(Args("{\"remember\":\"register\",\"steps\":[],\"routine\":\"other\"}")));
        Refused(() => RoutineCompatibilityArgs(Args("{\"remember\":\"register\",\"steps\":[],\"params\":[]}")));
        Refused(() => ValidateRoutineControls(Args("{\"remember\":\"register\"}"), "continue"));
        Refused(() => ValidateRoutineControls(Args("{\"continuation\":\"old\"}"), "run"));
        Refused(() => ValidateRoutineControls(Args("{\"steps\":[]}"), "forget"));
        ValidateRoutineAuthorizations(Args("{\"authorizations\":{\"1\":{\"confirm\":true,\"accord\":\"fixture\"}}}"), 2, 1); count++;
        foreach (var invalid in new[] { "{\"authorizations\":[]}", "{\"authorizations\":{\"2\":{}}}", "{\"authorizations\":{\"0\":{}}}", "{\"authorizations\":{\"01\":{}}}", "{\"authorizations\":{\"1\":{\"confirm\":\"true\"}}}", "{\"authorizations\":{\"1\":{\"accord\":true}}}" })
            Refused(() => ValidateRoutineAuthorizations(Args(invalid), 2, 1));
        foreach (var error in new Exception[] { new NativeConfirmationException("fixture"), new EmergencyStopException("fixture"), new JulienActiveException("fixture"), new InvalidOperationException("fixture") })
        {
            var before = RoutineFailure(error, false); var after = RoutineFailure(error, true);
            Check(Str(before, "mark") == "✗" && Str(after, "mark") == "~" && !Bool(after, "success"), "input boundary changes refusal to unknown");
        }
        Check(Str(RoutineFailure(new AfterInputException("fixture", new Exception()), false), "status") == "unknown", "typed after-input failure survives a later read reset");
        var satisfied = RoutinePreparedResult(StepResult("already-satisfied", "=", false, true, "no input sent"), true);
        Check(Bool(satisfied["preparation"], "inputAttempted") && !Str(satisfied, "message").Contains("no input sent"), "preparation is reported truthfully even if the primary action was unnecessary");
        Check(Str(RoutinePreparedResult(RoutineFailure(new InvalidOperationException("fixture"), false), true), "mark") == "~", "preparation forbids a false pre-input refusal");
        var native = Args("{\"elements\":[{\"index\":0,\"depth\":0,\"role\":\"edit\",\"automationId\":\"field\",\"name\":\"Field\",\"value\":\"fixture\"}]}");
        var withOcr = native.DeepClone().AsObject(); withOcr["elements"]!.AsArray().Add(EngineTestJson.Node(Args("{\"index\":1,\"depth\":0,\"role\":\"text\",\"name\":\"OCR fixture\",\"ocr\":true}")));
        Check(StructureNative(native) == StructureNative(withOcr), "OCR presentation does not invalidate native structure");
        Check(BoundaryNative(native) == BoundaryNative(withOcr), "OCR presentation does not invalidate a continuation boundary");
        Check(Bindings(Args("{\"params\":{\"field\":\"fixture\"}}"))["field"] == "fixture", "text bindings remain available");
        Refused(() => Bindings(Args("{\"params\":[]}")));
        var session = new SessionState("claude") { Pending = new PendingNativeBatch("fixture", DateTimeOffset.UtcNow, 1, 2, "start", "revision", "boundary", new(), 1, new(), null, null, null, null, new(), 0) };
        var completed = new JsonArray(StepResult("verified", "✓", true, true, null));
        var pauseFailure = RoutinePauseFailure(session, completed, 2, 1, new JsonException("fixture"))["structuredContent"]!.AsObject();
        Check(session.Pending == null && pauseFailure["continuation"] == null && !Bool(pauseFailure, "paused"), "a failed pause read invalidates its token without replay");
        Check(Number(pauseFailure["progress"], "completed") == 1 && Number(pauseFailure["progress"], "nextStep") == 1 && pauseFailure["steps"]!.AsArray().Count == 1 && Str(pauseFailure, "status") == "unverified", "a failed pause read retains verified progress without inventing a step");
        var initial = Args("{\"elements\":[{\"index\":0,\"role\":\"edit\",\"name\":\"Field\",\"visible\":true}]}");
        var direct = Args("{\"kind\":\"setValue\",\"selector\":{\"name\":\"Field\"}}");
        var inspections = 0; JsonObject Inspect() { inspections++; return initial.DeepClone().AsObject(); }
        Check(ReferenceEquals(RoutineStepSnapshot(initial, direct, "run", null, null, 0, 0, false, Inspect), initial) && inspections == 0, "a visible first direct step consumes the exact initial snapshot without a duplicate provider read");
        var wpf = Args("{\"elements\":[{\"index\":0,\"role\":\"text\",\"name\":\"Nom\",\"runtimeId\":[7,1],\"ancestors\":[],\"visible\":true},{\"index\":1,\"role\":\"edit\",\"name\":\"Nom\",\"automationId\":\"Nom\",\"runtimeId\":[7,2],\"ancestors\":[],\"visible\":true}]}");
        var named = Args("{\"kind\":\"setValue\",\"selector\":{\"name\":\"Nom\"}}");
        var wpfState = new SessionState("claude") { BoundWindow = (1, "fixture", "WPF fixture", 2) };
        var normalized = NormalizeStep(wpfState, wpf, Args("{\"action\":\"set\",\"target\":\"Nom\",\"text\":\"fixture\"}"));
        var beforeWpf = inspections;
        Check(Str(normalized["selector"], "role") == "edit" && ReferenceEquals(RoutineStepSnapshot(wpf, named, "run", null, null, 0, 0, false, Inspect), wpf) && ReferenceEquals(RoutineStepSnapshot(wpf, normalized, "run", null, null, 0, 0, false, Inspect), wpf) && inspections == beforeWpf, "WPF text label plus one edit resolves to the actual control, both before and after legacy normalization");
        var twoEdits = wpf.DeepClone().AsObject(); var secondEdit = twoEdits["elements"]![1]!.DeepClone().AsObject(); secondEdit["index"] = 2; secondEdit["runtimeId"] = new JsonArray(7, 3); secondEdit["automationId"] = "SecondNom"; twoEdits["elements"]!.AsArray().Add(EngineTestJson.Node(secondEdit));
        var beforeAmbiguity = inspections; RoutineStepSnapshot(twoEdits, named, "run", null, null, 0, 0, false, Inspect);
        Check(inspections == beforeAmbiguity + 1, "two genuinely distinct same-name edits retain a fresh provider read");
        var indexed = named.DeepClone().AsObject(); indexed["observedIdentity"] = ElementIdentity(wpf, wpf["elements"]![1]!.AsObject());
        var beforeIdentity = inspections;
        Check(ReferenceEquals(RoutineStepSnapshot(twoEdits, indexed, "run", null, null, 0, 0, false, Inspect), twoEdits) && inspections == beforeIdentity, "an observed runtime identity uses the same pure unambiguous resolution as execution");
        var replaced = wpf.DeepClone().AsObject(); replaced["elements"]![1]!["runtimeId"] = new JsonArray(7, 4);
        var beforeReplacement = inspections; RoutineStepSnapshot(replaced, indexed, "run", null, null, 0, 0, false, Inspect);
        Check(inspections == beforeReplacement + 1, "a replaced observed target cannot reuse the first snapshot");
        foreach (var excluded in new (string Operation, string? Routine, string? Remember, int Index, int Next, bool Interaction)[] {
            (Operation: "continue", Routine: (string?)null, Remember: (string?)null, Index: 0, Next: 0, Interaction: false),
            ("run", "saved", (string?)null, 0, 0, false), ("run", (string?)null, "learning", 0, 0, false),
            ("run", (string?)null, (string?)null, 1, 0, false), ("run", (string?)null, (string?)null, 0, 1, false), ("run", (string?)null, (string?)null, 0, 0, true) })
        {
            var reads = inspections;
            Check(!ReferenceEquals(RoutineStepSnapshot(initial, direct, excluded.Operation, excluded.Routine, excluded.Remember, excluded.Index, excluded.Next, excluded.Interaction, Inspect), initial) && inspections == reads + 1, "excluded execution paths keep their provider inspection");
        }
        var offscreen = initial.DeepClone().AsObject(); offscreen["elements"]![0]!["visible"] = false;
        var ambiguous = initial.DeepClone().AsObject(); ambiguous["elements"]!.AsArray().Add(ambiguous["elements"]![0]!.DeepClone());
        var unknown = direct.DeepClone().AsObject(); unknown["selector"]!["name"] = "Future target";
        foreach (var (snapshot, planned) in new[] { (offscreen, direct), (ambiguous, direct), (initial, unknown), (initial, Args("{\"kind\":\"wait\"}")), (initial, Args("{\"kind\":\"click\",\"ocrTarget\":{}}")) })
        {
            var reads = inspections; RoutineStepSnapshot(snapshot, planned, "run", null, null, 0, 0, false, Inspect);
            Check(inspections == reads + 1, "scroll/realization, waiting and OCR keep a distinct pre-action read");
        }
        var unproved = NativeResultWithoutProofRead(Args("{\"kind\":\"click\"}"), null, null);
        Check(Str(unproved, "mark") == "~" && !Bool(unproved, "success") && unproved?["continuation"] == null, "a result without a postcondition stays uncertain and exposes no continuation");
        Check(Str(NativeResultWithoutProofRead(Args("{\"kind\":\"click\",\"expect\":{}}"), null, "fixture failure"), "status") == "unknown", "an existing input error needs no unused proof read");
        Check(NativeResultWithoutProofRead(Args("{\"kind\":\"click\",\"expect\":{}}"), null, null) == null, "an explicit postcondition always retains its after-action read");
        Check(NativeResultWithoutProofRead(Args("{\"kind\":\"scroll\"}"), Args("{\"effect\":\"applied\",\"mechanism\":\"ScrollPattern vertical\"}"), null) == null, "a verified offset scroll keeps its after-action read too");
        // README promise (08/11, lost in the native port): a click proven by the clicked element alone is ✓ without expect.
        foreach (var proved in new[] { ComWorker.InvokedMechanism, "Toggle", "SelectionItem.Select" })
            Check(NativeResultWithoutProofRead(Args("{\"kind\":\"click\"}"), Args($"{{\"effect\":\"applied\",\"mechanism\":\"{proved}\"}}"), null) == null, $"a click proven by {proved} goes on to ✓");
        Check(Str(NativeResultWithoutProofRead(Args("{\"kind\":\"click\"}"), Args("{\"effect\":\"sent\",\"mechanism\":\"Invoke\"}"), null), "mark") == "~", "an Invoke whose event was not heard stays ~");
        Check(Str(NativeResultWithoutProofRead(Args("{\"kind\":\"click\"}"), Args("{\"effect\":\"applied\",\"mechanism\":\"LegacyIAccessible.DoDefaultAction\"}"), null), "mark") == "~", "only the element's own proofs count");
        count += NativeRoutineMirrorSelfTest();
        Console.WriteLine(new JsonObject { ["ok"] = true, ["assertions"] = count, ["gui"] = false }.ToJsonString()); return 0;
    }

    internal static int NativeRoutineMirrorSelfTest()
    {
        var assertions = 0;
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException("Routine mirror selftest: " + why); assertions++; }
        JsonObject Json(string raw) => JsonNode.Parse(raw)!.AsObject();
        var legacy = Json("""{"schema":1,"app":"fixture.exe","forgotten":{"forgotten":"2026-10-27T00:00:00.0000000+00:00"},"routines":{"private":{"saved":"2026-10-29T00:00:00Z","steps":[{"do":"click","target":"Register","expect":"Registered"}],"runs":7},"portable":{"saved":"2026-10-29T00:00:00Z","window":"private-window-metadata","fingerprint":{"path":"fixture.exe","size":42},"steps":[{"do":"set","target":"Name","text":"{{name}}"}],"runs":9},"other":{"saved":"2026-10-29T00:00:00Z","steps":[{"do":"set","target":"City","text":"{{city}}"}]},"local-only":{"saved":"2026-10-29T00:00:00Z","localOnly":true,"steps":[{"do":"set","target":"Name","text":"{{name}}"}]}}}""");
        var original = legacy.ToJsonString(); var data = ConvertLegacy(legacy, "claude");
        var portable = data["routines"]!["portable"]!.AsObject(); var privateEntry = data["routines"]!["private"]!.AsObject();
        Check(TransferableNativeRoutine("claude", portable), "pure parameter templates remain transferable");
        Check(!TransferableNativeRoutine("claude", privateEntry), "the historical fixed Registered postcondition remains local after conversion");
        Check(!TransferableNativeRoutine("codex", privateEntry), "a copied migrated private source cannot bypass its original policy through another profile");
        Check(!TransferableNativeRoutine("claude", data["routines"]!["local-only"]!.AsObject()), "legacy localOnly is retained as a privacy boundary");
        var localOnly = portable.DeepClone().AsObject(); localOnly["localOnly"] = true;
        Check(!TransferableNativeRoutine("claude", localOnly), "native localOnly is never exported");
        var mixed = portable.DeepClone().AsObject(); mixed["migration"]!["legacyDefinition"]!["steps"]![0]!["text"] = "private literal {{name}}";
        Check(!TransferableNativeRoutine("claude", mixed), "mixed fixed text and parameters stay local");
        var privateCheck = portable.DeepClone().AsObject(); privateCheck["migration"]!["legacyDefinition"]!["steps"]![0]!["check"] = Json("""{"target":"Name","contains":"private-value"}""");
        Check(!TransferableNativeRoutine("claude", privateCheck), "fixed legacy expected values cannot reappear through an archived definition");
        var title = portable.DeepClone().AsObject(); title["migration"]!["legacyDefinition"]!["steps"]![0]!["target"] = "window:private-document";
        Check(!TransferableNativeRoutine("claude", title), "legacy window/document selectors remain local");
        var agy = portable.DeepClone().AsObject(); agy["migration"]!["sourceAgent"] = "antigravity";
        agy["migration"]!["legacyDefinition"]!["fingerprint"] = Json("""{"app":"fixture.exe","path":"private-local-path","size":42,"mtime":43}""");
        agy["migration"]!["legacyDefinition"]!["anchors"] = new JsonArray(Json("""{"target":{"type":"Document","name_hash":"private-document-hash"}}"""));
        Check(!TransferableNativeRoutine("antigravity", agy), "AGY hashed document anchors are private even when typed text is a template");
        agy["migration"]!["legacyDefinition"]!["anchors"] = new JsonArray(Json("""{"target":{"type":"Edit","name_hash":"static-field-hash"}}"""));
        Check(TransferableNativeRoutine("antigravity", agy), "AGY semantic control anchors do not become document titles");
        var agyProjection = NativeMirrorDefinition(agy, "antigravity");
        Check(agyProjection["migration"]!["legacyDefinition"]!["fingerprint"]!["path"] == null && Str(agyProjection["migration"]!["legacyDefinition"]!["fingerprint"], "app") == "fixture.exe", "AGY fingerprint projection excludes the path and preserves app/size/mtime");
        var claimedClaude = agy.DeepClone().AsObject(); claimedClaude["migration"]!["sourceAgent"] = "claude";
        Check(NativeMirrorDefinition(claimedClaude, "antigravity")["migration"]!["legacyDefinition"]!["fingerprint"]!["path"] == null, "archive provenance cannot override the fixed AGY profile's path policy");
        Check(Str(NativeMirrorDefinition(portable, "claude")["migration"]!["legacyDefinition"]!["fingerprint"], "path") == "fixture.exe", "Claude retains its historical fingerprint path convention");
        var fixedWait = Json("""{"saved":"2026-10-30T00:00:00Z","steps":[{"kind":"wait","expect":{"text":"Dossier confidentiel 123"}}]}""");
        foreach (var profile in new[] { "claude", "codex", "antigravity" }) Check(!TransferableNativeRoutine(profile, fixedWait), "a fixed expected text stays local for every profile (" + profile + ")");
        var filtered = ConvertLegacy(legacy, "claude", fromMirror: true);
        Check(filtered["routines"]!.AsObject().Count == 2 && filtered["routines"]!["private"] == null && filtered["routines"]!["local-only"] == null, "legacy mirror filtering happens before conversion, while local import still retains all four definitions");
        Check(data["routines"]!.AsObject().Count == 4 && legacy.ToJsonString() == original, "local legacy source and its definitions remain intact");

        var root = Path.Combine(Path.GetTempPath(), "nexus-routine-mirror-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME"); var previousMirror = Environment.GetEnvironmentVariable("NEXUS_ROUTINES_MIRROR");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root); var mirror = Path.Combine(root, "mirror"); Environment.SetEnvironmentVariable("NEXUS_ROUTINES_MIRROR", mirror);
            Directory.CreateDirectory(root); var originalFile = Path.Combine(root, "original-legacy.json"); File.WriteAllText(originalFile, original);
            var originalBytes = File.ReadAllBytes(originalFile); var memory = new NativeRoutineMemory("claude", "process:fixture.exe");
            var importedChanges = memory.Import(data);
            var file = Path.Combine(root, "routines", Routines.Digest("process:fixture.exe") + ".json"); var localBytes = File.ReadAllBytes(file);
            // Import reports both definition changes and tombstones: four routines plus the existing forget.
            Check(importedChanges == 5 && memory.List().Count == 4 && JsonNode.DeepEquals(memory.Read("private"), privateEntry)
                && memory.Read("local-only") != null && Json(File.ReadAllText(file))["forgotten"]?["forgotten"] != null,
                "local migration retains all four definitions, including private routines, and imports its existing tombstone");
            var leakedMirror = data.DeepClone().AsObject(); var invalidPrivate = portable.DeepClone().AsObject(); invalidPrivate["steps"]![0]!["value"] = "private-literal"; leakedMirror["routines"]!["invalid-private"] = invalidPrivate;
            Directory.CreateDirectory(mirror); var mirrorFile = Path.Combine(mirror, Path.GetFileName(file)); File.WriteAllText(mirrorFile, leakedMirror.ToJsonString());
            memory.Sync(); var remote = JsonNode.Parse(File.ReadAllText(mirrorFile))!.AsObject();
            Check(remote["routines"]!.AsObject().Count == 2 && remote["routines"]!["private"] == null && remote["routines"]!["local-only"] == null, "the real synchronizer retracts only the non-transferable mirror copies");
            Check(File.ReadAllBytes(file).SequenceEqual(localBytes) && File.ReadAllBytes(originalFile).SequenceEqual(originalBytes), "local native storage and the original legacy file are byte-for-byte unchanged");
            Check(memory.List().Count == 4 && JsonNode.DeepEquals(memory.Read("private"), privateEntry) && memory.Read("other") != null, "private execution/review definitions and unrelated user routines are retained");
            Check(remote["routines"]!["portable"]!["runs"] == null && remote["routines"]!["portable"]!["migration"]!["legacyDefinition"]!["runs"] == null && !remote.ToJsonString().Contains("private-window-metadata"), "the mirror contains definitions only, not execution statistics or archived private window metadata");
            Check(remote["forgotten"]!["forgotten"] != null && memory.Read("private") != null, "existing user tombstones are preserved without inventing a forget for private local routines");
            var synchronized = File.ReadAllBytes(mirrorFile); memory.Sync();
            Check(File.ReadAllBytes(mirrorFile).SequenceEqual(synchronized) && File.ReadAllBytes(file).SequenceEqual(localBytes), "repeated synchronization is idempotent and cannot re-export the local private definition");
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", Path.Combine(root, "second-profile"));
            var incoming = new NativeRoutineMemory("claude", "process:fixture.exe");
            Check(incoming.Import(leakedMirror, fromMirror: true) == 3 && incoming.List().Count == 2 && incoming.Read("private") == null
                && incoming.Read("local-only") == null && incoming.Read("invalid-private") == null && incoming.Read("portable") != null,
                "mirror import changes two transferable definitions and one tombstone, excluding all private copies before validation");
            var received = incoming.Read("portable")!; received["migration"]!["requiresAnchors"] = false;
            Check(Bool(received, "mirrorReview") && NeedsReview(received), "a definition received from the mirror is reviewed before its first replay on this PC, even with valid anchors");
            incoming.RecordRun("portable", true); Check(incoming.Read("portable")!["mirrorReview"] == null, "a successful reviewed run clears the mirror review mark");
            Check(incoming.Import(data) == 2 && incoming.Read("private") != null, "the same definition is still importable from the explicitly local source context");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", previousHome); Environment.SetEnvironmentVariable("NEXUS_ROUTINES_MIRROR", previousMirror);
            var resolved = Path.GetFullPath(root); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Only a fixture folder of the temporary directory is ever deleted (never thrown from finally).
            if (string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return assertions;
    }
}
