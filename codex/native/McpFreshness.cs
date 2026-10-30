using System.Text.Json.Nodes;

static partial class McpServer
{
    // A streaming text sibling need not invalidate a currently identical semantic control. This is deliberately
    // narrower than general tree reconciliation: one direct background-only step; every non-text node, the target's
    // parent subtree and its ancestry remain exact. New dialogs, menus and untyped overlay groups therefore refuse.
    internal static void ValidateNativeFreshness(JsonObject observed, JsonObject current, JsonObject args, string operation)
    {
        if (Str(observed, "processStartedUtc").Length == 0 || Str(observed, "processStartedUtc") != Str(current, "processStartedUtc") || Str(observed, "revision") != Str(current, "revision"))
            throw new ActionRefusedException("Application identity changed since look; nothing sent");
        var unchanged = StructureNative(observed) == StructureNative(current);
        // Preserve the established path, including legacy/partial presentations, when its full structural guard holds.
        // The stronger proof below replaces only a rejection caused by unrelated text-node structure changes.
        if (unchanged) return;
        var step = operation == "run" && args["routine"] == null && args["remember"] == null && !Bool(args, "interaction")
            && args["steps"] is JsonArray { Count: 1 } steps ? steps[0] as JsonObject : null;
        var semantic = step?["selector"] as JsonObject;
        var eligible = semantic != null && Str(step, "kind") is "click" or "setValue" or "secondary" or "select" or "scroll" or "hover"
            && !new[] { "target", "point", "x", "y", "screenshotId", "ocrTarget", "from", "to", "focus" }.Any(k => step!.ContainsKey(k));
        if (!eligible)
        {
            throw new ActionRefusedException("Interface structure changed since look; observe again before input");
        }
        if (step!["fallback"] is not JsonValue fallback || !fallback.TryGetValue<bool>(out var allowed) || allowed)
            throw new ActionRefusedException("A changed interface requires a proved background-only semantic target; no input sent");

        foreach (var key in new[] { "processId", "processStartedUtc", "contentProcessId", "contentProcessStartedUtc", "app", "revision", "frame" })
            if (observed[key] == null || !JsonNode.DeepEquals(observed[key], current[key]))
                throw new ActionRefusedException("Window or content-process identity changed since look; nothing sent");
        if (new[] { "processStartedUtc", "contentProcessStartedUtc", "app", "revision" }.Any(k => Str(observed, k).Length == 0)
            || Number(observed, "processId") <= 0 || Number(observed, "contentProcessId") <= 0)
            throw new ActionRefusedException("Window or content-process identity is incomplete; nothing sent");
        var beforeTarget = UniqueNative(observed, semantic!);
        var afterTarget = UniqueNative(current, semantic!);
        var before = FreshnessNodes(observed); var after = FreshnessNodes(current);
        var target = FreshnessKey(beforeTarget);
        if (target != FreshnessKey(afterTarget) || !JsonNode.DeepEquals(before[target], after[target]))
            throw new ActionRefusedException("Semantic target identity or properties changed since look; no input sent");
        if (!Bool(beforeTarget, "visible") || Bool(beforeTarget, "disabled") || Bool(beforeTarget, "password"))
            throw new ActionRefusedException("A changed interface requires a directly available semantic target; no input sent");
        _ = FreshnessBounds(before[target]);
        var ancestors = before[target]["ancestors"]!.AsArray();
        if (ancestors.Count == 0) throw new ActionRefusedException("Semantic target has no proved parent context; no input sent");
        var parent = ancestors[^1]!.GetValue<string>();
        if (!JsonNode.DeepEquals(before[parent], after.GetValueOrDefault(parent)))
            throw new ActionRefusedException("Semantic target parent changed since look; no input sent");
        var parentBounds = FreshnessBounds(before[parent]);

        foreach (var key in before.Keys.Union(after.Keys))
        {
            var old = before.GetValueOrDefault(key); var now = after.GetValueOrDefault(key);
            if (JsonNode.DeepEquals(old, now)) continue;
            if (old != null && now != null && !JsonNode.DeepEquals(PassiveTextMetadata(old), PassiveTextMetadata(now)))
                throw new ActionRefusedException("Text control metadata changed since look; no input sent");
            foreach (var node in new[] { old, now }.OfType<JsonObject>())
            {
                if (!ProvedPassiveText(node) || node["ancestors"]!.AsArray().Any(a => a!.GetValue<string>() == parent)
                    || before.Values.Concat(after.Values).Any(n => n["ancestors"]!.AsArray().Any(a => a!.GetValue<string>() == key)))
                    throw new ActionRefusedException("A control, dialog or target context changed since look; no input sent");
                var bounds = FreshnessBounds(node);
                if (bounds[0] < parentBounds[0] + parentBounds[2] && bounds[0] + bounds[2] > parentBounds[0]
                    && bounds[1] < parentBounds[1] + parentBounds[3] && bounds[1] + bounds[3] > parentBounds[1])
                    throw new ActionRefusedException("Changed text overlaps the target context; no input sent");
                foreach (var ancestor in node["ancestors"]!.AsArray())
                {
                    var id = ancestor!.GetValue<string>();
                    if (!before.TryGetValue(id, out var was) || !after.TryGetValue(id, out var isNow) || !JsonNode.DeepEquals(was, isNow))
                        throw new ActionRefusedException("Changed text has an unproved ancestor context; no input sent");
                }
            }
        }
    }

    static JsonObject PassiveTextMetadata(JsonObject node)
    {
        var metadata = node.DeepClone().AsObject();
        foreach (var key in new[] { "name", "value", "bounds" }) metadata.Remove(key);
        return metadata;
    }

    static bool ProvedPassiveText(JsonObject node)
    {
        if (Str(node, "role") != "text" || Bool(node, "invoke") || Bool(node, "focused") || Bool(node, "isDefault")
            || Bool(node, "password") || Str(node, "accessKey").Length != 0 || node["value"] != null
            || node["checked"] != null || node["selected"] != null || node["expanded"] != null
            || node["actions"] is not JsonArray { Count: 0 } || node["capabilities"] is not JsonObject capabilities) return false;
        foreach (var key in new[] { "focusable", "focused", "invoke", "toggle", "selectionItem", "selection", "expandCollapse", "value", "rangeValue", "scroll", "dialog", "modal", "ariaModal", "accessKeyPresent", "ariaRolePresent", "ariaPropertiesPresent" })
            if (capabilities[key] is not JsonValue known || !known.TryGetValue<bool>(out var available) || available) return false;
        if (capabilities["legacy"] is not JsonValue legacy || !legacy.TryGetValue<bool>(out var hasLegacy)
            || hasLegacy && (capabilities["legacyActionPresent"] is not JsonValue action || !action.TryGetValue<bool>(out var actionPresent) || actionPresent)) return false;
        // MSAA selected/focused/checked/default/expanded/collapsed/focusable/selectable states.
        const int interactiveLegacyStates = 0x2 | 0x4 | 0x10 | 0x100 | 0x200 | 0x400 | 0x100000 | 0x200000;
        if (capabilities["legacyState"] is not JsonValue state || !state.TryGetValue<int>(out var flags) || (flags & interactiveLegacyStates) != 0) return false;
        return true;
    }

    static string FreshnessKey(JsonObject node) => node["runtimeId"] is JsonArray { Count: > 0 } runtime
        ? runtime.ToJsonString(JsonOut) : throw new ActionRefusedException("A control has no runtime identity; no input sent");

    static Dictionary<string, JsonObject> FreshnessNodes(JsonObject snapshot)
    {
        var nodes = Elements(snapshot).OfType<JsonObject>().ToArray();
        if (Bool(snapshot, "truncated") || Bool(snapshot, "treeTruncated") || Bool(snapshot, "incomplete")
            || snapshot["complete"] is JsonValue complete && complete.TryGetValue<bool>(out var isComplete) && !isComplete
            || Bool(snapshot["accessibility"], "document_truncated") || Bool(snapshot["accessibility"], "truncated")
            || nodes.Length == 0 || nodes.Length != Elements(snapshot).Count || nodes.Length >= ComUia.MaxNodes
            || nodes.Any(n => Bool(n, "ocr") || Bool(n, "valueTruncated") || Number(n, "depth") >= ComUia.MaxDepth * 2))
            throw new ActionRefusedException("The native context is incomplete; look again before input");
        var indices = nodes.ToDictionary(n => (int)Number(n, "index", -1));
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node["ancestors"] is not JsonArray ancestors) throw new ActionRefusedException("A control has no ancestor proof; no input sent");
            var copy = node.DeepClone().AsObject(); copy.Remove("index");
            var chain = new JsonArray();
            foreach (var entry in ancestors)
            {
                if (!indices.TryGetValue(entry!.GetValue<int>(), out var ancestor) || ReferenceEquals(ancestor, node)
                    || ancestor["ancestors"] is not JsonArray prefix || prefix.Count != chain.Count)
                    throw new ActionRefusedException("Control ancestry is incomplete; no input sent");
                for (var i = 0; i < prefix.Count; i++)
                    if (!indices.TryGetValue(prefix[i]!.GetValue<int>(), out var preceding) || FreshnessKey(preceding) != chain[i]!.GetValue<string>())
                        throw new ActionRefusedException("Control ancestry is inconsistent; no input sent");
                chain.Add(FreshnessKey(ancestor));
            }
            copy["ancestors"] = chain;
            if (!result.TryAdd(FreshnessKey(node), copy)) throw new ActionRefusedException("Runtime identity is ambiguous; no input sent");
        }
        return result;
    }

    static double[] FreshnessBounds(JsonObject node)
    {
        if (node["bounds"] is not JsonArray { Count: 4 } bounds) throw new ActionRefusedException("Context geometry is unavailable; no input sent");
        var values = bounds.Select(v => v!.GetValue<double>()).ToArray();
        if (values.Any(v => !double.IsFinite(v)) || values[2] <= 0 || values[3] <= 0)
            throw new ActionRefusedException("Context geometry is unproved; no input sent");
        return values;
    }
}
