// Offline self-checks of commun/engine/McpLegacyObservation.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static McpServer;

static partial class McpServerSelfTests
{
    internal static List<string> ChangedNativeLines(Dictionary<int, string> before, Dictionary<int, string> after)
    {
        var result = new List<string>();
        foreach (var pair in after) if (!before.TryGetValue(pair.Key, out var old)) result.Add("+ " + pair.Value); else if (old != pair.Value) result.Add("~ " + pair.Value);
        foreach (var pair in before) if (!after.ContainsKey(pair.Key)) result.Add("- " + pair.Value);
        return result.Count == 0 ? ["No visible element change since the previous look."] : result;
    }

    internal static int LegacyObservationSelfTest()
    {
        var assertions = 0;
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException("Legacy observation selftest: " + why); assertions++; }
        bool Refused(Action operation) { try { operation(); return false; } catch (ActionRefusedException) { return true; } }
        JsonObject Snapshot() => JsonNode.Parse("""{"ok":true,"processStartedUtc":"2026-10-30T00:00:00Z","revision":"fixture","frame":{"left":100,"top":100,"width":2560,"height":1440},"elements":[{"index":0,"role":"window","name":"Fixture","runtimeId":[42,1],"nativeWindow":100,"ancestors":[]},{"index":1,"role":"listitem","name":"Alpha","runtimeId":[42,2],"nativeWindow":101,"ancestors":[0],"bounds":[1350,780,60,40]},{"index":2,"role":"listitem","name":"Beta","runtimeId":[42,3],"nativeWindow":102,"ancestors":[0]}]}""")!.AsObject();
        var state = new SessionState("codex") { BoundWindow = (100, "process:fixture.exe", "Fixture", 1) };
        var observed = Snapshot(); var memory = LookMemory(state, 100, observed); memory.Shown.UnionWith(new[] { 0, 1, 2 });
        var step = NormalizeStep(state, observed, new JsonObject { ["do"] = "click", ["target"] = "#1" });
        var reordered = Snapshot(); reordered["elements"]![1]!["index"] = 2; reordered["elements"]![2]!["index"] = 1;
        Check(Str(StepTarget(reordered, step, step["selector"]!.AsObject()), "name") == "Alpha", "index follows observed identity, never the new occupant");
        var changed = Snapshot(); changed["elements"]![1]!["name"] = "Gamma";
        Check(Refused(() => StepTarget(changed, step, step["selector"]!.AsObject())), "runtime identity reused for another label refuses before pointer input");
        changed = Snapshot(); changed["elements"]![1]!["runtimeId"] = new JsonArray(99, 1);
        Check(Refused(() => StepTarget(changed, step, step["selector"]!.AsObject())), "replaced target refuses");
        changed = Snapshot(); changed["elements"]![0]!["name"] = "Another context";
        Check(Refused(() => StepTarget(changed, step, step["selector"]!.AsObject())), "ancestor identity is observed");
        memory.Shown.Remove(1);
        Check(Refused(() => NormalizeStep(state, observed, new JsonObject { ["do"] = "click", ["target"] = "#1" })), "unshown index refuses");
        var claude = new SessionState("claude") { BoundWindow = state.BoundWindow };
        var first = Snapshot(); var cldMemory = LookMemory(claude, 100, first); PublishNativeIndices(claude, first, cldMemory);
        var alpha = Elements(first).OfType<JsonObject>().Single(e => Str(e, "name") == "Alpha"); var alphaId = (int)Number(alpha, "index");
        var next = Snapshot(); var originalElements = next["elements"]!.AsArray();
        next["elements"] = new JsonArray(originalElements[0]!.DeepClone(), originalElements[2]!.DeepClone(), originalElements[1]!.DeepClone());
        next["elements"]![1]!["index"] = 1; next["elements"]![2]!["index"] = 2;
        PublishNativeIndices(claude, next, cldMemory);
        Check(Number(Elements(next).OfType<JsonObject>().Single(e => Str(e, "name") == "Alpha"), "index") == alphaId, "ids stable across reorder");
        var reference = JsonNode.Parse("""{"width":2000,"height":1125,"frame":{"left":100,"top":100,"width":2560,"height":1440},"windowFrame":{"left":100,"top":100,"width":2560,"height":1440}}""")!.AsObject();
        Check(NativeLine(alpha, reference).EndsWith("@1000,547", StringComparison.Ordinal), "printed points use rendered image pixels, not physical pixels");
        claude.Image = new NativeImage("img-fixture", 100, reference);
        var coordinate = NativePoint(claude, new JsonObject { ["point"] = new JsonObject { ["x"] = 1000, ["y"] = 546.875 } }, first, null);
        Check(coordinate.X == 1380 && coordinate.Y == 800, "printed point maps to actual center");
        var moved = first.DeepClone().AsObject(); moved["frame"]!["left"] = 200;
        coordinate = NativePoint(claude, new JsonObject { ["point"] = new JsonObject { ["x"] = 1000, ["y"] = 546.875 } }, moved, null);
        Check(coordinate.X == 1480 && coordinate.Y == 800, "a point follows window movement");
        moved["frame"]!["width"] = 2000;
        Check(Refused(() => NativePoint(claude, new JsonObject { ["point"] = new JsonObject { ["x"] = 1, ["y"] = 1 } }, moved, null)), "resize invalidates image point");
        var before = new Dictionary<int, string> { [1] = "old", [2] = "same", [3] = "removed" };
        var after = new Dictionary<int, string> { [1] = "new", [2] = "same", [4] = "added" };
        var delta = ChangedNativeLines(before, after);
        Check(delta.SequenceEqual(new[] { "~ new", "+ added", "- removed" }), "changes retain ids and distinguish add/change/remove");
        Check(Str(LegacyStep(new JsonObject { ["do"] = "select", ["target"] = "List", ["text"] = "Choice" }), "option") == "Choice", "select text fallback");
        Check(Str(LegacyStep(new JsonObject { ["do"] = "key", ["text"] = "ctrl+s" }), "key") == "ctrl+s", "key text fallback");
        Check(Str(LegacyStep(new JsonObject { ["do"] = "menu", ["target"] = "File > Open" }), "path") == "File > Open", "menu target fallback");
        Check(LegacyStep(new JsonObject { ["do"] = "wait", ["target"] = "Ready" })["target"] == null, "wait target is expected text, never an input selector");
        Check(Number(LegacyStep(new JsonObject { ["do"] = "scroll", ["target"] = "List" }), "scrollY") == 3, "legacy default scroll direction and amount");
        var conditions = NormalizeExpectation(observed, new JsonObject { ["element"] = "Alpha", ["value"] = "x", ["enabled"] = false });
        Check((conditions["all"] as JsonArray)?.Count == 2, "all expectation constraints survive translation");
        var numeric = LegacyStep(new JsonObject { ["do"] = "click", ["target"] = "#1", ["check"] = new JsonObject { ["target"] = 1, ["selected"] = true } });
        Check(NormalizeExpectation(observed, numeric["expect"]!.AsObject())["selector"] is JsonObject, "numeric check resolves against shown snapshot");
        Check(LegacyRoles(Str(LegacySelector(JsonValue.Create("champ:Nom")!), "role")[7..]).Contains("document"), "French field alias retains document support");
        Check(Str(LegacySelector(JsonValue.Create("nom:15")!), "name") == "15", "numeric visible name is explicit");
        foreach (var bounds in new JsonNode?[] { null, new JsonArray(), new JsonArray(1, 2, 3) })
        {
            var invisible = first["elements"]![1]!.DeepClone().AsObject(); invisible["bounds"] = bounds;
            Check(!NativeLine(invisible, claude.Image!.Capture).Contains(" @"), "an absent or incomplete provider rectangle renders without coordinates");
        }
        foreach (var frame in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 10 }, new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 0, ["height"] = 10 } })
            Check(Refused(() => RequireNativeFrame(new JsonObject { ["frame"] = frame })), "capture and OCR refuse absent or incomplete frame before any request");
        Check(ReferenceEquals(RequireNativeFrame(first), first["frame"]), "a complete native frame is preserved without substitution");
        // An unnamed web document is a wheel place, not an identity: its visible center inside the frame (31/10).
        var page = Snapshot(); page["elements"]!.AsArray().Add(JsonNode.Parse("""{"index":3,"role":"document","name":"","runtimeId":[42,4],"nativeWindow":0,"ancestors":[0],"bounds":[100,140,2560,9000]}"""));
        var pageState = new SessionState("codex") { BoundWindow = (100, "process:fixture.exe", "Fixture", 1) };
        LookMemory(pageState, 100, page).Shown.UnionWith(new[] { 0, 1, 2, 3 });
        var wheel = NormalizeStep(pageState, page, new JsonObject { ["do"] = "scroll", ["target"] = "#3", ["delta_y"] = -3 });
        Check(Bool(wheel, "placeOnly") && Number(wheel, "x") == 1280 && Number(wheel, "y") == 740 && wheel["selector"] == null && wheel["observedIdentity"] == null, "unnamed document scroll targets the center of its visible part");
        var wheelPoint = NativePoint(pageState, wheel, page, null);
        Check(wheelPoint.X == 1380 && wheelPoint.Y == 840, "a place-only point needs no image and stays in the observed frame");
        Check(!Bool(NormalizeStep(pageState, page, new JsonObject { ["do"] = "scroll", ["x"] = 5, ["y"] = 5, ["placeOnly"] = true }), "placeOnly"), "a caller can never claim a place-only point");
        Check(VisibleCenterOf(page, JsonNode.Parse("""{"bounds":[100,1600,50,50]}""")!.AsObject()) == null, "an element entirely outside the frame has no wheel place");
        Check(NormalizeLegacyText("  Hello\r\nworld ") == "Hello world", "legacy whitespace normalization");
        var stateCheck = LegacyStep(new JsonObject { ["do"] = "click", ["target"] = "Alpha", ["check"] = new JsonObject { ["target"] = "Alpha", ["contains"] = "READY", ["enabled"] = true } });
        var normalizedCheck = NormalizeExpectation(observed, stateCheck["expect"]!.AsObject());
        var checks = normalizedCheck["all"]!.AsArray();
        Check(checks.Count == 2 && LegacyStateCheck(normalizedCheck), "multi-field legacy check supports already-satisfied skip");
        var checkSnapshot = Snapshot(); checkSnapshot["elements"]![1]!["value"] = "  Ready\r\nnow "; checkSnapshot["elements"]![1]!["disabled"] = false;
        Check(ExpectationHolds(claude, checkSnapshot, normalizedCheck), "legacy contains is case-insensitive and whitespace normalized");
        memory.Shown.Clear();
        Check(Refused(() => CheckShownExpectationIndices(state, numeric["expect"]!.AsObject())), "numeric postcondition also requires a displayed index");
        // Skeleton: a layout wrapper is hidden, a named list with many items is a region, a leaf is never a wrapper (31/10).
        var dense = JsonNode.Parse("""{"elements":[{"index":0,"role":"window","name":"Chat","ancestors":[]},{"index":1,"role":"pane","name":"","automationId":"","ancestors":[0]},{"index":2,"role":"list","name":"Messages","ancestors":[0,1]},{"index":3,"role":"group","name":"","ancestors":[0]}]}""")!.AsObject();
        for (var i = 0; i < 10; i++) dense["elements"]!.AsArray().Add(EngineTestJson.Node(new JsonObject { ["index"] = 10 + i, ["role"] = "listitem", ["name"] = $"m{i}", ["ancestors"] = new JsonArray(0, 1, 2) }));
        var counts = ParentCounts(dense); var items = Elements(dense).OfType<JsonObject>().ToList();
        Check(EmptyContainer(items[1], counts) && !EmptyContainer(items[2], counts) && !EmptyContainer(items[3], counts), "only an unnamed container holding elements is a layout wrapper");
        Check(Regions(dense, counts).SequenceEqual(new[] { "[2] list \"Messages\" (10)" }), "a named container with most items is the region to look within");
        // A label repeating its parent (Gemini card: button then image of the same title; edit then its text) is hidden; a
        // label with its own words, an actionable or focused one, or one whose parent is unknown stays listed (08/11).
        var cards = JsonNode.Parse("""
            {"elements":[
            {"index":0,"role":"window","name":"Gemini","ancestors":[]},
            {"index":1,"role":"button","name":"Streaming","invoke":true,"ancestors":[0]},
            {"index":2,"role":"image","name":"Streaming","ancestors":[0,1]},
            {"index":3,"role":"button","name":"Lire l'aperçu","invoke":true,"ancestors":[0,1]},
            {"index":4,"role":"edit","name":"Prompt","value":"Hymne  médiéval\nsolennel","ancestors":[0]},
            {"index":5,"role":"text","name":"Hymne médiéval solennel","ancestors":[0,4]},
            {"index":6,"role":"button","name":"Genre, Cinématique","ancestors":[0]},
            {"index":7,"role":"text","name":"Cinématique","ancestors":[0,6]},
            {"index":8,"role":"image","name":"Streaming","invoke":true,"ancestors":[0,1]},
            {"index":9,"role":"text","name":"Streaming","focused":true,"ancestors":[0,1]},
            {"index":10,"role":"text","name":"Streaming","ancestors":[0,99]},
            {"index":11,"role":"button","name":"Streaming","ancestors":[0,1]},
            {"index":12,"role":"text","name":"","ancestors":[0,1]}]}
            """)!.AsObject();
        var byIndex = Elements(cards).OfType<JsonObject>().ToDictionary(e => (int)Number(e, "index"), e => e);
        var echoed = Elements(cards).OfType<JsonObject>().Where(e => EchoLabel(e, byIndex)).Select(e => (int)Number(e, "index")).ToArray();
        Check(echoed.SequenceEqual(new[] { 2, 5 }), "only a non-actionable image or text repeating its parent's name or value is an echo");
        var full = JsonNode.Parse("""{"index":7,"role":"button","name":"OK","automationId":"","value":null,"disabled":false,"focused":true,"checked":null,"selected":false,"password":false,"runtimeId":[42,7],"nativeWindow":5,"ancestors":[1,4],"bounds":[1,2,3,4],"actions":[],"flags":["offscreen"]}""")!.AsObject();
        var compact = CompactElement(full);
        Check(JsonNode.DeepEquals(compact, JsonNode.Parse("""{"index":7,"role":"button","name":"OK","selected":false,"focused":true,"flags":["offscreen"],"bounds":[1,2,3,4],"parent":4}""")), "compact element keeps states and drops defaults and internal identities");
        var secret = full.DeepClone().AsObject(); secret["password"] = true; secret["value"] = "hunter2";
        Check(CompactElement(secret)["value"] == null, "a password value is never listed");
        return assertions;
    }
}
