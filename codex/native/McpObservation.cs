using System.Text.Json.Nodes;

static partial class McpServer
{
    static void CheckNativeApplication(JsonObject observed, JsonObject current)
    {
        if (Number(observed, "processId") <= 0 || Number(observed, "processId") != Number(current, "processId"))
            throw new ActionRefusedException("Application identity changed since look; nothing sent");
        foreach (var key in new[] { "processStartedUtc", "revision" })
            if (Str(observed, key).Length == 0 || Str(observed, key) != Str(current, key))
                throw new ActionRefusedException("Application identity changed since look; nothing sent");
        foreach (var key in new[] { "processId", "contentProcessId", "contentProcessStartedUtc" })
            if (!JsonNode.DeepEquals(observed[key], current[key]))
                throw new ActionRefusedException("Application identity changed since look; nothing sent");
    }

    // A semantic gesture follows the observed control, not whichever control happens to acquire its name later.
    // Keep the broad guard for batches, routines, keyboard, OCR and pixels: they have no single observed UIA scope.
    static JsonObject? BindObservedNativeTarget(JsonObject observed, JsonObject current, JsonObject args, JsonArray steps)
    {
        if (Str(args, "operation", "run") != "run" || args["routine"] != null || args["remember"] != null || steps.Count != 1)
            return null;
        var step = steps[0]!.AsObject();
        if (Str(step, "kind") is not ("click" or "setValue" or "typeText" or "select" or "secondary" or "scroll" or "hover" or "focus") ||
            new[] { "point", "x", "y", "from", "to", "screenshotId", "ocrTarget" }.Any(step.ContainsKey) || Bool(step, "placeOnly")) return null;
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        if (selector == null) return null;
        if (step["observedIdentity"] == null && FindNative(observed, selector).Count == 0)
        {
            if (FindNative(current, selector).Count != 0)
                throw new ActionRefusedException("The target was not present in the look; observe again before input");
            return null; // unchanged virtualized lists retain their guarded materialization path
        }
        var target = StepTarget(observed, step, selector);
        if (Bool(target, "ocr") || target["runtimeId"] is not JsonArray { Count: > 0 }) return null;
        var ancestors = target["ancestors"] as JsonArray;
        if (ancestors == null || ancestors.Count == 0 || ancestors.Any(i => Elements(observed).OfType<JsonObject>()
            .Single(e => Number(e, "index", -1) == i!.GetValue<int>())["runtimeId"] is not JsonArray { Count: > 0 })) return null;
        if (step["observedIdentity"] == null) UniqueNative(current, selector); // named targets must remain unambiguous
        step["observedIdentity"] ??= ElementIdentity(observed, target);
        var fresh = StepTarget(current, step, selector);
        if (Bool(fresh, "disabled") || Bool(fresh, "password"))
            throw new ActionRefusedException("The observed target is disabled or protected; no input sent");
        return target;
    }

    static JsonObject? NativeTargetContext(JsonObject snapshot, JsonObject target)
    {
        var elements = Elements(snapshot).OfType<JsonObject>().ToDictionary(e => Number(e, "index", -1));
        foreach (var index in (target["ancestors"] as JsonArray ?? new()).Reverse())
        {
            var ancestor = elements[index!.GetValue<int>()];
            if (Str(ancestor, "role") is "group" or "pane" or "region" or "toolbar" or "list" or "tree" or "table" or "menu" or "tabpanel" or "document" or "window" &&
                HasStableSelector(ancestor)) return ancestor;
        }
        return null;
    }

    static string NativeContextDigest(JsonObject snapshot, JsonObject context)
    {
        var index = Number(context, "index", -1);
        var nodes = new JsonArray();
        foreach (var element in Elements(snapshot).OfType<JsonObject>().Where(e => Number(e, "index", -2) == index ||
            (e["ancestors"] as JsonArray ?? new()).Any(i => i!.GetValue<int>() == index)))
        {
            var node = new JsonObject();
            // Geometry, published indexes and focus can change without changing the semantic gesture. Values,
            // selection and local command structure belong to its context and must still agree with the look.
            foreach (var key in new[] { "depth", "role", "name", "automationId", "value", "valueTruncated", "checked", "selected", "expanded", "disabled", "password" })
                node[key] = element[key]?.DeepClone();
            nodes.Add(node);
        }
        return Routines.Digest(nodes.ToJsonString(JsonOut));
    }

    static string NativeWindowContext(JsonObject snapshot)
    {
        var windows = new JsonArray(Elements(snapshot).OfType<JsonObject>().Where(e => Str(e, "role") is "window" or "dialog").Select(e =>
        {
            var node = StableIdentityPart(snapshot, e); node["disabled"] = e["disabled"]?.DeepClone(); return (JsonNode)node;
        }).ToArray());
        return Routines.Digest(windows.ToJsonString(JsonOut));
    }

    static bool ValidateNativeTargetContext(JsonObject observed, JsonObject current, JsonObject args, JsonArray steps)
    {
        CheckNativeApplication(observed, current);
        var target = BindObservedNativeTarget(observed, current, args, steps);
        if (target != null && NativeTargetContext(observed, target) is { } context &&
            (Str(context, "role") != "window" || context["ancestors"] is JsonArray { Count: > 0 }))
        {
            var step = steps[0]!.AsObject();
            var selector = step["selector"] as JsonObject ?? step["focus"]!.AsObject();
            var freshTarget = StepTarget(current, step, selector);
            var freshContext = NativeTargetContext(current, freshTarget);
            var ancestors = (target["ancestors"] as JsonArray ?? new()).Select(i => Elements(observed).OfType<JsonObject>().Single(e => Number(e, "index", -1) == i!.GetValue<int>())).ToList();
            var freshAncestors = (freshTarget["ancestors"] as JsonArray ?? new()).Select(i => Elements(current).OfType<JsonObject>().Single(e => Number(e, "index", -1) == i!.GetValue<int>())).ToList();
            if (freshContext != null && JsonNode.DeepEquals(StableIdentityPart(observed, context), StableIdentityPart(current, freshContext)) &&
                NativeContextDigest(observed, context) == NativeContextDigest(current, freshContext) &&
                NativeWindowContext(observed) == NativeWindowContext(current) &&
                ancestors.Zip(freshAncestors).All(pair => Bool(pair.First, "disabled") == Bool(pair.Second, "disabled") && Bool(pair.First, "password") == Bool(pair.Second, "password"))) return true;
            throw new ActionRefusedException("The observed target/context changed since look; no input sent");
        }
        // A classic control right under the window (a Windows Open dialog: « Nom du fichier », « Ouvrir ») has no local
        // context, but its own window handle: no other control can take its place. It is checked on its own subtree and the
        // window's dialogs, not on every element of the window (05/11: the file list refreshed, « Interface structure
        // changed », and the typing was refused). A control without its own handle keeps the whole-window guard.
        if (target != null && NativeTargetContext(observed, target) is { } root && Str(root, "role") == "window" && root["ancestors"] is not JsonArray { Count: > 0 } &&
            OwnWindowHandle(observed, target, root))
        {
            var step = steps[0]!.AsObject();
            var freshTarget = StepTarget(current, step, step["selector"] as JsonObject ?? step["focus"]!.AsObject());
            if (NativeContextDigest(observed, target) == NativeContextDigest(current, freshTarget) && NativeWindowContext(observed) == NativeWindowContext(current) &&
                Bool(freshTarget, "disabled") == Bool(target, "disabled")) return true;
            throw new ActionRefusedException("The observed target changed since look; no input sent");
        }
        return false;
    }

    // The control (or, for a field synthesized under a classic combo box, that combo box) has a window handle of its own,
    // which no other element of the observation shares.
    static bool OwnWindowHandle(JsonObject snapshot, JsonObject target, JsonObject root)
    {
        var holder = target["runtimeId"] is JsonArray { Count: 4 } id && id[0]!.GetValue<int>() == -1 && (target["ancestors"] as JsonArray) is { Count: > 0 } chain
            ? Elements(snapshot).OfType<JsonObject>().FirstOrDefault(e => Number(e, "index", -1) == chain[^1]!.GetValue<int>()) ?? target : target;
        var handle = Number(holder, "nativeWindow");
        return handle != 0 && handle != Number(root, "nativeWindow") && Elements(snapshot).OfType<JsonObject>().Count(e => Number(e, "nativeWindow") == handle) == 1;
    }

    internal static bool ValidateNativeObservation(JsonObject observed, JsonObject current, JsonObject args, JsonArray steps)
    {
        if (ValidateNativeTargetContext(observed, current, args, steps)) return true;
        if (StructureNative(observed) == StructureNative(current)) return false;
        throw new ActionRefusedException("Interface structure changed in the observed target/context; observe again before input");
    }
}
