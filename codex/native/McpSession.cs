using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The MCP adapter owns observation lifetime, action translation, proof and continuation.
// Program.Execute is the existing low-level desktop provider; a transmitted gesture is never retried.
static partial class McpServer
{
    public sealed record NativeImage(string Id, long Window, JsonObject Capture);
    public sealed record PendingNativeBatch(string Token, DateTimeOffset Created, long Window, long Pid,
        string Started, string Revision, string Boundary, JsonArray Steps, int Next, JsonArray Results,
        JsonArray? Definition, string? Remember, string? Routine, string? RoutineStamp, JsonArray Anchors, int Transitions);

    static string Str(JsonNode? o, string key, string fallback = "") => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    static bool Bool(JsonNode? o, string key, bool fallback = false) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    static double Number(JsonNode? o, string key, double fallback = 0)
    {
        if (o?[key] is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var n)) return double.IsFinite(n) ? n : fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return fallback;
    }
    static JsonObject ObjectOf(JsonElement e) => JsonNode.Parse(e.GetRawText())!.AsObject();
    static JsonObject NativeRequest(SessionState state, string operation)
    {
        state.Interaction?.Check();
        var w = state.BoundWindow ?? throw new InvalidOperationException("Look at a named window first");
        var request = new JsonObject { ["operation"] = operation, ["windowId"] = w.Id, ["app"] = w.App, ["expectedPid"] = w.Pid, ["quietMs"] = 1618 };
        if (BoundObservationIdentity is { } expected && expected.Window == w.Id) request["expectedProcessStartedUtc"] = expected.Birth;
        return request;
    }
    static JsonObject InspectNative(SessionState state)
    {
        GuardClaudeDevtools(state);
        var snapshot = ObjectOf(ExecuteInternal(NativeRequest(state, "inspect")));
        if (!Bool(snapshot, "ok")) throw new InvalidOperationException(Str(snapshot, "message", "Native inspection failed"));
        return snapshot;
    }
    static JsonArray Elements(JsonObject snapshot) => snapshot["elements"] as JsonArray ?? throw new InvalidOperationException("Incomplete native observation");
    static JsonObject TranslateNativeAlias(string name, JsonObject arguments)
    {
        var args = arguments.DeepClone().AsObject();
        if (name == "computer_open_path") args["app"] ??= args["path"]?.DeepClone();
        if (name == "computer_get_window_state") { args["format"] = "json"; args["annotate"] = Bool(args, "annotate_screenshot"); }
        if (name == "computer_take_screenshot")
        {
            args["mode"] = "image"; args["annotate"] = Bool(args, "annotate_screenshot");
        }
        if (name == "computer_close_window") args["action"] = "close";
        if (name == "computer_focus_window") { args["action"] = "front"; args["foreground"] = true; }
        var action = name switch { "computer_click" => "click", "computer_type_text" => Bool(args, "clear_first") ? "set" : "type", "computer_press_key" => "key",
            "computer_scroll" => "scroll", "computer_drag" => "drag", "computer_mouse_move" => "hover", _ => "" };
        if (action.Length == 0) return args;
        var step = new JsonObject { ["action"] = action };
        foreach (var field in new[] { "text", "x", "y", "from_x", "from_y", "to_x", "to_y", "box_2d", "button", "delta_x", "delta_y", "confirm", "accord", "expect" })
            if (args[field] != null) step[field] = args[field]!.DeepClone();
        if (args["element_index"] != null) step["target"] = "#" + args["element_index"];
        if (name == "computer_click") step["count"] = Number(args, "click_count", 1);
        if (name == "computer_press_key") step["key"] = Str(args, "keys", Str(args, "key"));
        if (Bool(args, "submit")) throw new ActionRefusedException("Use a separate observed Enter step so its exact submission can be guarded");
        args["steps"] = new JsonArray(step); return args;
    }
    static JsonObject SelectorOf(JsonObject element)
    {
        var selector = new JsonObject { ["role"] = Str(element, "role") };
        if (Str(element, "automationId").Length > 0) selector["automationId"] = Str(element, "automationId");
        if (Str(element, "name").Length > 0) selector["name"] = Str(element, "name");
        if (selector.Count == 1) throw new InvalidOperationException("This element has no stable semantic selector: use its current image point");
        return selector;
    }
    static bool HasStableSelector(JsonObject element) => Str(element, "automationId").Length > 0 || Str(element, "name").Length > 0;
    // Center of the element's part inside the observed window frame, relative to that frame (pointer x/y of a step).
    static (int X, int Y)? VisibleCenterOf(JsonObject snapshot, JsonObject element)
    {
        if (snapshot["frame"] is not JsonObject frame || element["bounds"] is not JsonArray { Count: 4 } b) return null;
        var (fl, ft) = (Number(frame, "left"), Number(frame, "top"));
        var left = Math.Max(b[0]!.GetValue<double>(), fl); var top = Math.Max(b[1]!.GetValue<double>(), ft);
        var right = Math.Min(b[0]!.GetValue<double>() + b[2]!.GetValue<double>(), fl + Number(frame, "width"));
        var bottom = Math.Min(b[1]!.GetValue<double>() + b[3]!.GetValue<double>(), ft + Number(frame, "height"));
        if (right <= left || bottom <= top) return null;
        return ((int)((left + right) / 2 - fl), (int)((top + bottom) / 2 - ft));
    }
    static bool MatchesNative(JsonObject element, JsonObject selector, JsonArray elements)
    {
        foreach (var kv in selector)
        {
            if (kv.Key == "ancestor")
            {
                if (kv.Value is not JsonObject ancestor || element["ancestors"] is not JsonArray chain ||
                    !chain.Any(i => elements.OfType<JsonObject>().Any(e => Number(e, "index", -1) == i!.GetValue<int>() && MatchesNative(e, ancestor, elements)))) return false;
            }
            else if (kv.Key == "window") { if (Number(element, "nativeWindow") != kv.Value!.GetValue<long>()) return false; }
            else if (kv.Key == "role" && kv.Value?.GetValue<string>() is { } role && role.StartsWith("legacy/", StringComparison.Ordinal)) { if (!LegacyRoles(role[7..]).Contains(Str(element, "role"))) return false; }
            else if (kv.Key is "role" or "name" or "automationId") { if (Str(element, kv.Key) != kv.Value?.GetValue<string>()) return false; }
            else throw new InvalidOperationException($"Unknown selector field {kv.Key}");
        }
        return selector.Count > 0;
    }
    static List<JsonObject> FindNative(JsonObject snapshot, JsonObject selector)
    {
        var elements = Elements(snapshot);
        var found = elements.OfType<JsonObject>().Where(e => MatchesNative(e, selector, elements)).ToList();
        if (found.Count > 1 && selector["role"] == null)
        {
            var controls = found.Where(e => Str(e, "role") != "text" && !Bool(e, "ocr")).ToList();
            if (controls.Count > 0) found = controls;
            if (found.Count > 1)
            {
                var containers = found.Where(e => Str(e, "role") is "combobox" or "list" && found.All(other => ReferenceEquals(e, other) ||
                    (other["ancestors"] as JsonArray)?.Any(i => i!.GetValue<int>() == Number(e, "index", -1)) == true)).ToList();
                if (containers.Count == 1) found = containers;
            }
        }
        return found;
    }
    static JsonObject UniqueNative(JsonObject snapshot, JsonObject selector)
    {
        var matches = FindNative(snapshot, selector);
        if (matches.Count != 1) throw new ActionRefusedException($"Semantic target count is {matches.Count}; expected one. Look again; no input sent.");
        return matches[0];
    }
    static JsonObject ElementIdentity(JsonObject snapshot, JsonObject element) => new()
    {
        ["runtimeId"] = element["runtimeId"]?.DeepClone(), ["nativeWindow"] = element["nativeWindow"]?.DeepClone(),
        ["role"] = element["role"]?.DeepClone(), ["name"] = element["name"]?.DeepClone(), ["automationId"] = element["automationId"]?.DeepClone(),
        ["ancestors"] = new JsonArray((element["ancestors"] as JsonArray ?? new JsonArray()).Select(i =>
            IdentityPart(Elements(snapshot).OfType<JsonObject>().Single(e => Number(e, "index", -1) == i!.GetValue<int>()))).ToArray())
    };
    static JsonObject IdentityPart(JsonObject element) => new() { ["runtimeId"] = element["runtimeId"]?.DeepClone(), ["nativeWindow"] = element["nativeWindow"]?.DeepClone(), ["role"] = element["role"]?.DeepClone(), ["name"] = element["name"]?.DeepClone(), ["automationId"] = element["automationId"]?.DeepClone() };
    static JsonObject StepTarget(JsonObject snapshot, JsonObject step, JsonObject selector)
    {
        if (step["observedIdentity"] is not JsonObject identity) return UniqueNative(snapshot, selector);
        var matches = Elements(snapshot).OfType<JsonObject>().Where(e => JsonNode.DeepEquals(e["runtimeId"], identity["runtimeId"])).ToList();
        if (matches.Count != 1 || !JsonNode.DeepEquals(ElementIdentity(snapshot, matches[0]), identity) || !MatchesNative(matches[0], selector, Elements(snapshot)))
            throw new ActionRefusedException("The element shown at that index was replaced or moved to another context; no input sent");
        return matches[0];
    }
    static JsonObject ResolveTarget(JsonObject snapshot, JsonNode target)
    {
        if (target is JsonObject selector) return UniqueNative(snapshot, selector);
        var name = target.ToString();
        if (Regex.IsMatch(name, @"^\s*\d+\s*$") && Elements(snapshot).OfType<JsonObject>().Any(e => Str(e, "name") == name.Trim())) throw new ActionRefusedException("Numeric target is ambiguous: use #N for an index or name:N for its visible name");
        if (int.TryParse(name.Trim('#', '[', ']'), out var index))
            return Elements(snapshot).OfType<JsonObject>().SingleOrDefault(e => Number(e, "index", -1) == index) ?? throw new ActionRefusedException("Element index absent from current observation");
        var exact = FindNative(snapshot, LegacySelector(JsonValue.Create(name)!));
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new ActionRefusedException($"Ambiguous target \"{name}\": {string.Join(", ", exact.Select(e => $"#{e["index"]} {e["role"]}"))}");
        // Partial field labels are usable, but never fuzzy button/link/menu matching.
        var partialSelector = LegacySelector(JsonValue.Create(name)!); var partialName = Str(partialSelector, "name");
        var partial = Elements(snapshot).OfType<JsonObject>().Where(e => Str(e, "role") is "edit" or "combobox" && Str(e, "name").Contains(partialName, StringComparison.OrdinalIgnoreCase) && (partialSelector["role"] == null || MatchesNative(e, new JsonObject { ["role"] = partialSelector["role"]!.DeepClone() }, Elements(snapshot)))).ToList();
        if (partial.Count == 1) return partial[0];
        throw new ActionRefusedException(partial.Count > 1 ? $"Ambiguous field target \"{name}\"" : $"Target \"{name}\" absent from current observation; look again");
    }
    static JsonObject ObserveNative(SessionState state, JsonObject args, bool modern)
        => ObserveCompatibleNative(state, args, modern);
    static JsonArray ImageRegion(JsonObject basis, JsonArray box, bool normalized)
    {
        if (box.Count != 4 || basis["frame"] is not JsonObject f) throw new InvalidOperationException("Region requires a native frame");
        var v = box.Select(n => n!.GetValue<double>()).ToArray();
        if (v.Any(n => !double.IsFinite(n))) throw new InvalidOperationException("Invalid region");
        var width = normalized ? 1000 : Number(basis, "width"); var height = normalized ? 1000 : Number(basis, "height");
        var (x1, y1, x2, y2) = normalized ? (v[1], v[0], v[3], v[2]) : (v[0], v[1], v[2], v[3]);
        if (width <= 0 || height <= 0 || x1 < 0 || y1 < 0 || x2 <= x1 || y2 <= y1 || x2 > width || y2 > height) throw new InvalidOperationException("Region outside current image");
        return new JsonArray((int)(Number(f, "left") + x1 * Number(f, "width") / width), (int)(Number(f, "top") + y1 * Number(f, "height") / height),
            (int)Math.Ceiling((x2 - x1) * Number(f, "width") / width), (int)Math.Ceiling((y2 - y1) * Number(f, "height") / height));
    }

    static JsonObject NormalizeStep(SessionState state, JsonObject snapshot, JsonObject original, bool allowFutureTarget = false)
    {
        var step = LegacyStep(original); step.Remove("observedIdentity"); step.Remove("ocrTarget"); step.Remove("observedDestination"); step.Remove("placeOnly");
        var action = Str(step, "kind", Str(step, "do", Str(step, "action", "")));
        if (action.Length == 0) throw new InvalidOperationException("Every step needs action or kind");
        var kind = action switch { "set" or "clear" => "setValue", "type" => "typeText", "check" or "uncheck" or "double_click" or "right_click" => "click", "expand" or "collapse" => "secondary", "focus" => "focus", _ => action };
        if (kind is not ("click" or "setValue" or "typeText" or "key" or "secondary" or "select" or "menu" or "scroll" or "hover" or "drag" or "wait" or "focus")) throw new InvalidOperationException($"Unsupported native action {action}");
        step["kind"] = kind; step.Remove("do");
        if (state.Agent == "claude" && kind == "key") step["key"] = NormalizeClaudeKeySequence(Str(step, "key"));
        if (action is not ("secondary")) step.Remove("action");
        if (step["target"] is JsonArray at && at.Count == 2)
        { step["point"] = new JsonObject { ["x"] = at[0]!.DeepClone(), ["y"] = at[1]!.DeepClone() }; step.Remove("target"); }
        else if (step["target"] is JsonValue tv && tv.TryGetValue<string>(out var coords) && System.Text.RegularExpressions.Regex.IsMatch(coords, @"^-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?$"))
        { var parts = coords.Split(','); step["point"] = new JsonObject { ["x"] = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), ["y"] = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) }; step.Remove("target"); }
        JsonObject? target = null; var placeOnly = false;
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        if (selector != null)
        {
            if (!allowFutureTarget || FindNative(snapshot, selector).Count > 0) target = UniqueNative(snapshot, selector);
        }
        else if (step["target"] != null)
        {
            try
            {
                target = ResolveTarget(snapshot, step["target"]!);
                if (Bool(target, "ocr")) { step["ocrTarget"] = target.DeepClone(); }
                else if (kind is "scroll" or "hover" && !HasStableSelector(target))
                {
                    // An unnamed container (a web page's document) is a place for the wheel, not an identity: its visible center.
                    var center = VisibleCenterOf(snapshot, target) ?? throw new ActionRefusedException("This element has no visible part in the window; scroll its container instead");
                    step["x"] = center.X; step["y"] = center.Y; step["placeOnly"] = true; placeOnly = true;
                }
                else selector = SelectorOf(target);
                if (Bool(target, "ocr") && kind is not ("click" or "hover" or "drag")) throw new ActionRefusedException("OCR text is a pointer target only; it exposes no editable or selection control");
                if (int.TryParse(step["target"]!.ToString().Trim('#', '[', ']'), out _))
                {
                    if (!LookMemory(state, state.BoundWindow!.Value.Id).Shown.Contains((int)Number(target, "index"))) throw new ActionRefusedException("This index was not displayed in the previous look");
                    if (!Bool(target, "ocr") && !placeOnly) { if (target["runtimeId"] is not JsonArray runtime || runtime.Count == 0) throw new ActionRefusedException("The shown index has no stable runtime identity; no input sent"); step["observedIdentity"] = ElementIdentity(snapshot, target); }
                }
            }
            catch (ActionRefusedException e) when (allowFutureTarget && e.Message.Contains(" absent from current observation", StringComparison.Ordinal) && !step["target"]!.ToString().StartsWith('#'))
            { selector = new JsonObject { ["name"] = step["target"]!.ToString() }; }
        }
        else if (kind == "typeText" && state.RetainedFocus != null) { selector = state.RetainedFocus.DeepClone().AsObject(); target = UniqueNative(snapshot, selector); }
        if (selector != null)
        {
            step[kind == "typeText" ? "focus" : "selector"] = selector.DeepClone();
            if (kind == "click" && target != null && Str(target, "role") is "edit" or "document") state.RetainedFocus = selector.DeepClone().AsObject();
        }
        step.Remove("target"); if (placeOnly) target = null;
        if (kind is "setValue" or "typeText")
        {
            if (target == null && selector == null) throw new InvalidOperationException("Text input needs a named field (or a field retained by click)");
            if (Bool(target, "password")) throw new ActionRefusedException("Password controls require the dedicated supported workflow");
            var text = action == "clear" ? "" : Str(step, kind == "setValue" ? "value" : "text", Str(step, "text"));
            step[kind == "setValue" ? "value" : "text"] = text;
            if (kind == "setValue") step.Remove("text");
        }
        if (action == "double_click") step["count"] = 2;
        if (action == "right_click") step["button"] = "right";
        if (action is "expand" or "collapse") step["action"] = action == "expand" ? "Expand" : "Collapse";
        if (target is not null && action is "check" or "uncheck" && Str(target, "role") == "radio" && target["checked"] == null) target["checked"] = target["selected"]?.DeepClone();
        if (kind == "drag")
        {
            step["point"] ??= step["from"]?.DeepClone(); step["x"] ??= step["from_x"]?.DeepClone(); step["y"] ??= step["from_y"]?.DeepClone();
            if (step["to"] is JsonValue toValue && toValue.TryGetValue<string>(out var destinationText) && Regex.IsMatch(destinationText, @"^-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?$"))
            { var parts = destinationText.Split(','); step["to"] = new JsonArray(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)); }
            else if (IsLegacyIndex(step["to"])) { var destination = ResolveTarget(snapshot, step["to"]!); if (!LookMemory(state, state.BoundWindow!.Value.Id).Shown.Contains((int)Number(destination, "index"))) throw new ActionRefusedException("Drag destination index was not displayed"); step["observedDestination"] = ElementIdentity(snapshot, destination); step["to"] = SelectorOf(destination); }
        }
        if (kind == "scroll") { step["scrollY"] ??= -Number(step, "delta_y"); step["scrollX"] ??= Number(step, "delta_x"); step.Remove("delta_y"); step.Remove("delta_x"); }
        if (step["expect"] is JsonObject expectation)
        {
            CheckShownExpectationIndices(state, expectation);
            step["expect"] = NormalizeExpectation(snapshot, expectation);
        }
        else if (selector != null)
        {
            var field = kind switch { "setValue" or "typeText" or "select" => "value", "secondary" when Str(step, "action") is "Expand" or "Collapse" => "expanded", "click" when action is "check" or "uncheck" || target?["checked"] != null => "checked", "click" when Str(target, "role") is "listitem" or "radio" or "tab" or "treeitem" => "selected", "focus" => "focused", _ => "" };
            if (field.Length > 0)
            {
                JsonNode? wanted = kind switch
                {
                    "setValue" => step["value"]?.DeepClone(), "typeText" => JsonValue.Create(Str(target, "value") + Str(step, "text")), "select" => step["option"]?.DeepClone(),
                    "secondary" => JsonValue.Create(Str(step, "action") == "Expand"),
                    "click" when action == "check" => JsonValue.Create(true), "click" when action == "uncheck" => JsonValue.Create(false),
                    "click" when field == "checked" => JsonValue.Create(!Bool(target, "checked")), _ => JsonValue.Create(true),
                };
                step["expect"] = new JsonObject { ["selector"] = selector!.DeepClone(), ["field"] = field, ["equals"] = wanted };
            }
        }
        if (action is "check" or "uncheck" && target != null && target["checked"] == null) throw new ActionRefusedException("Target exposes no checked state");
        return step;
    }
    static JsonObject NormalizeExpectation(JsonObject snapshot, JsonObject original)
    {
        if (original["all"] is JsonArray all) return new JsonObject { ["all"] = new JsonArray(all.OfType<JsonObject>().Select(e => (JsonNode)NormalizeExpectation(snapshot, e)).ToArray()) };
        if (original["selector"] != null || original["text"] != null) { Routines.ValidateExpectation(original); return original.DeepClone().AsObject(); }
        if (original["title"] != null || original["title_contains"] != null) return original.DeepClone().AsObject();
        var converted = original["element"] != null && (original["field"] != null || original["equals"] != null || original["includes"] != null)
            ? original.DeepClone().AsObject() : LegacyElementExpectation(original);
        if (converted["all"] != null) return NormalizeExpectation(snapshot, converted);
        if (converted["element"] != null)
        {
            var element = converted["element"]!;
            try { converted["selector"] = SelectorOf(ResolveTarget(snapshot, element)); }
            catch (ActionRefusedException) when (!IsLegacyIndex(element)) { converted["selector"] = LegacySelector(element); }
            converted.Remove("element");
        }
        Routines.ValidateExpectation(converted); return converted;
    }
    static bool TextShownByOcr(SessionState state, JsonObject snapshot, string wanted)
    {
        try
        {
            var shot = ReadNativeCapture(state, snapshot);
            var request = NativeRequest(state, "ocr"); request["path"] = Str(shot, "path");
            var lines = ObjectOf(ExecuteInternal(request))["lines"] as JsonArray ?? new();
            var shown = string.Join(" ", lines.OfType<JsonObject>().Select(line => Str(line, "text")));
            return NormalizeLegacyText(shown).Contains(NormalizeLegacyText(wanted), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is not EmergencyStopException) { return false; } // capture or OCR unavailable: the tree alone judges
    }
    static bool ExactExpectation(JsonObject expect) => expect["equals"] != null || expect["title"] != null || expect["all"] is JsonArray all && all.OfType<JsonObject>().All(ExactExpectation);
    // ocr: a text the window shows but its tree does not carry (WPF TextBlock named by AutomationProperties.Name, drawn
    // label) is also looked for by OCR in a background capture of the window, before the gesture (an already visible text
    // cannot verify it) and after. Only for a text that must appear; "gone" stays judged on the tree.
    static bool ExpectationHolds(SessionState state, JsonObject snapshot, JsonObject expect, bool ocr = false)
    {
        if (expect["all"] is JsonArray all) return all.OfType<JsonObject>().All(e => ExpectationHolds(state, snapshot, e, ocr));
        if (expect["text"] is JsonValue text)
        {
            var wanted = text.GetValue<string>();
            var found = Elements(snapshot).OfType<JsonObject>().Any(e => !Bool(e, "password") && (Str(e, "name").Contains(wanted, StringComparison.OrdinalIgnoreCase) || Str(e, "value").Contains(wanted, StringComparison.OrdinalIgnoreCase)));
            if (Bool(expect, "gone")) return !found;
            return found || ocr && wanted.Trim().Length > 0 && TextShownByOcr(state, snapshot, wanted);
        }
        var title = Win32.Title((nint)state.BoundWindow!.Value.Id);
        if (expect["title"] != null) return title == Str(expect, "title");
        if (expect["title_contains"] != null) return title.Contains(Str(expect, "title_contains"), StringComparison.Ordinal);
        var matches = FindNative(snapshot, expect["selector"]!.AsObject());
        if (expect["exists"] != null && !Bool(expect, "exists")) return matches.Count == 0;
        if (matches.Count > 1) throw new ActionRefusedException("Ambiguous postcondition target");
        if (matches.Count == 0) return false;
        if (Bool(matches[0], "password") || Bool(matches[0], "valueTruncated") && Str(expect, "field", "name") == "value") return false;
        var field = Str(expect, "field", "name");
        JsonNode? actual = matches[0][field];
        // A radio button carries its state as SelectionItem (selected), a text or a label its text as its name.
        if (field == "checked" && actual == null && Str(matches[0], "role") == "radio") actual = matches[0]["selected"];
        if (field == "value" && actual == null && Str(matches[0], "role") is "text" or "label") actual = matches[0]["name"];
        if (actual == null && (expect["equals"] != null || expect["includes"] != null)) return false;
        // Text is compared with its spaces normalized (line breaks, non-breaking spaces), and includes ignores case.
        if (expect["equals"] is JsonValue eq && field == "value" && eq.TryGetValue<string>(out var expectedText)) return NormalizeLegacyText(actual?.ToString()) == NormalizeLegacyText(expectedText);
        if (expect["equals"] != null) return JsonNode.DeepEquals(actual, expect["equals"]);
        if (expect["includes"] != null) return NormalizeLegacyText(actual?.ToString()).Contains(NormalizeLegacyText(Str(expect, "includes")), StringComparison.OrdinalIgnoreCase);
        return expect.ContainsKey("exists") && Bool(expect, "exists");
    }
    static JsonObject? NativeResultWithoutProofRead(JsonObject step, JsonObject? outcome, string? error)
    {
        if (error != null) return StepResult("unknown", "~", null, false, error, outcome);
        if (step["expect"] is JsonObject) return null;
        if (Str(step, "kind") == "scroll" && Str(outcome, "effect") == "applied" && Str(outcome, "mechanism").StartsWith("ScrollPattern ", StringComparison.Ordinal)) return null;
        return StepResult("sent-unconfirmed", "~", null, false, "Input sent; no exact postcondition was supplied. Look before deciding the next gesture.", outcome);
    }
    static JsonObject ProveNative(SessionState state, JsonObject step, JsonObject before, JsonObject? outcome, string? error = null, bool inputEverSent = true)
    {
        // This flag belongs to the gesture, not to Program.Execute's per-request bit: later reads reset that bit.
        try
        {
            if (NativeResultWithoutProofRead(step, outcome, error) is { } unproved) return unproved;
            var after = InspectNative(state);
            if (step["expect"] is not JsonObject expect)
            {
                return StepResult("verified", "✓", true, true, "Chosen container's scroll offset changed and was read back", outcome);
            }
            var timer = Stopwatch.StartNew(); var timeout = Math.Clamp(Number(step, "timeoutMs", 1618), 0, 30000);
            while (true)
            {
                if (ExpectationHolds(state, after, expect, ocr: true)) return StepResult("verified", "✓", true, true, null, outcome);
                if (timer.ElapsedMilliseconds >= timeout) return StepResult("unmet", "~", false, false, "Postcondition not observed; no automatic replay", outcome);
                EmergencyStop.ThrowIfStopped(); Thread.Sleep(50); after = InspectNative(state);
            }
        }
        catch (Exception e) when (inputEverSent)
        {
            return StepResult(e is EmergencyStopException or JulienActiveException ? "interrupted" : "unknown", "~", null, false, error ?? e.Message, outcome);
        }
    }
    static JsonObject StepResult(string status, string mark, bool? verified, bool success, string? message, JsonObject? outcome = null) => new()
    {
        ["status"] = status, ["mark"] = mark, ["verified"] = verified, ["success"] = success,
        ["message"] = message, ["technique"] = outcome?["technique"]?.DeepClone(), ["effect"] = outcome?["effect"]?.DeepClone(),
        ["foregroundTaken"] = outcome?["foregroundTaken"]?.DeepClone() ?? JsonValue.Create(false), ["foregroundReturned"] = outcome?["foregroundReturned"]?.DeepClone(),
        ["attempts"] = outcome?["attempts"]?.DeepClone(),
        ["trace"] = outcome?["trace"]?.DeepClone(),
    };
    const string NativeAttemptsKey = "NexusWindows.NativeFallbackAttempts";
    static string NativeAttemptError(Exception error) => error switch
    {
        AfterInputException => "AfterInputException", BackgroundNotApplicableException => "BackgroundNotApplicableException",
        ActionRefusedException => "ActionRefusedException", EmergencyStopException => "EmergencyStopException",
        JulienActiveException => "JulienActiveException", NativeConfirmationException => "NativeConfirmationException",
        TimeoutException => "TimeoutException", IOException => "IOException", ArgumentException => "ArgumentException",
        InvalidOperationException => "InvalidOperationException", _ => "Exception",
    };
    // Called only after the caller proved that its background attempt refused before input.
    // Metadata records the transport path, never a key, field value, selector or exception message.
    static JsonObject NativeFallbackAttempt(Func<JsonObject> fallback, Func<bool> inputAttempted)
    {
        var attempts = new JsonArray(new JsonObject { ["method"] = "background", ["status"] = "refused-before-input", ["error_type"] = "BackgroundNotApplicableException" });
        try
        {
            var outcome = fallback();
            attempts.Add(new JsonObject { ["method"] = "physical", ["status"] = "returned" });
            outcome["attempts"] = attempts; return outcome;
        }
        catch (Exception error)
        {
            attempts.Add(new JsonObject { ["method"] = "physical", ["status"] = inputAttempted() || error is AfterInputException ? "input-uncertain" : "refused-before-input", ["error_type"] = NativeAttemptError(error) });
            error.Data[NativeAttemptsKey] = attempts;
            throw;
        }
    }
    static JsonObject NativeFailureAttempts(JsonObject result, Exception error)
    {
        if (error.Data[NativeAttemptsKey] is JsonArray attempts) result["attempts"] = attempts.DeepClone();
        return result;
    }
    static int NativeFallbackTraceSelfTest()
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
                Check(Str(failure, "status") == (entered ? "unknown" : "refused"), "trace annotations leave refusal/unknown classification intact");
                Check(!failure["attempts"]!.ToJsonString().Contains("private-"), "trace metadata excludes diagnostic, key, target and field text");
            }
        }
        return assertions;
    }
    // The one enabled element under a point that has its own UIA gesture, or null (none, or several nested: ambiguous).
    internal static JsonObject? PointGesture(IEnumerable<JsonObject> hits)
    {
        var actionable = hits.Where(e => Bool(e, "visible") && !Bool(e, "disabled") && !Bool(e, "password") && Str(e, "role") != "window" &&
            (Bool(e, "invoke") || e["checked"] is JsonValue || e["selected"] is JsonValue)).Take(2).ToList();
        return actionable.Count == 1 ? actionable[0] : null;
    }
    static void AddIdentity(JsonObject request, JsonObject element, JsonObject observation)
    {
        foreach (var field in new[] { "runtimeId", "nativeWindow" }) request[field] = element[field]?.DeepClone();
        var chain = element["ancestors"] as JsonArray ?? new JsonArray();
        request["ancestry"] = new JsonArray(chain.Select(i => Elements(observation).OfType<JsonObject>().First(e => Number(e, "index", -1) == i!.GetValue<int>()).DeepClone()).ToArray());
    }
    static void CheckGesture(SessionState state, JsonObject step, JsonObject observation, JsonObject? target, NativeTextSubmitScope? textSubmit = null, string? currentTitle = null, bool textUsesEnter = false)
    {
        var kind = Str(step, "kind");
        var triggers = new List<JsonObject>();
        if (step["ocrTarget"] is JsonObject ocrTrigger && kind is "click" or "drag" or "secondary") triggers.Add(ocrTrigger);
        if (target != null && kind is "click" or "secondary" or "drag") triggers.Add(target);
        if (kind == "menu") triggers.Add(new JsonObject { ["name"] = Str(step, "path"), ["role"] = "menuitem" });
        // ValuePattern/WM_SETTEXT replace field contents, including newlines, without pressing Enter.
        // A transport that really types Enter rechecks the current submission context at that boundary.
        var keyKinds = kind == "key" ? GardeFous.KeyKinds(Str(step, "key")) :
            (kind == "typeText" || kind == "setValue" && textUsesEnter) && Str(step, "text", Str(step, "value")).IndexOfAny(['\r', '\n']) >= 0 ? new[] { "submit" } : [];
        if (keyKinds.Length > 0)
        {
            var elements = Elements(observation).OfType<JsonObject>().ToList();
            triggers.AddRange(elements.Where(e => Bool(e, "focused") || Bool(e, "isDefault") ||
                (keyKinds.Contains("submit") || keyKinds.Contains("send")) && Str(e, "role") is "button" or "menuitem" && GardeFous.OutwardWord(Str(e, "name")).Length > 0));
            foreach (var alt in keyKinds.Where(k => k.StartsWith("alt+"))) triggers.AddRange(elements.Where(e => Str(e, "accessKey").Replace(" ", "").Equals(alt, StringComparison.OrdinalIgnoreCase)));
        }
        var fields = Elements(observation).OfType<JsonObject>().Where(e => !Bool(e, "password") && Str(e, "value").Length > 0).Select(e => Str(e, "name") + "=" + Str(e, "value")).ToArray();
        var context = currentTitle ?? Win32.Title((nint)state.BoundWindow!.Value.Id);
        foreach (var trigger in triggers)
        {
            var label = Str(trigger, "name");
            if (GardeFous.OutwardWord(label, context).Length > 0)
            {
                var action = GardeFous.BuildAction($"{state.Agent}-windows", kind + (kind == "key" ? " " + Str(step, "key") : ""), label,
                    [], string.Join("; ", fields) + "\n" + Str(step, "text", Str(step, "value")), [], context);
                var refusal = textSubmit != null ? textSubmit.Authorize(action) : state.Accords.Check(action, Bool(step, "confirm"), Str(step, "accord"));
                if (refusal.Length > 0) throw new NativeConfirmationException(refusal);
            }
            else if (GardeFous.ConfirmWord(label, context).Length > 0 && !Bool(step, "confirm")) throw new NativeConfirmationException($"Irreversible action \"{label}\": nothing sent; confirm:true requires the user's explicit order");
        }
    }
    sealed class NativeConfirmationException(string message) : InvalidOperationException(message);

    static JsonObject PerformNative(SessionState state, JsonObject step, JsonObject snapshot, bool physicalAllowed)
    {
        state.Interaction?.Check();
        GuardClaudeDevtools(state);
        var kind = Str(step, "kind");
        if (TryPerformKeySequence(state, step, snapshot, physicalAllowed) is { } keySequence) return keySequence;
        using var cdpScope = ClaudeDevtoolsScope(state, step, snapshot);
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        var target = selector != null ? StepTarget(snapshot, step, selector) : null;
        var semanticTarget = target;
        if (step["ocrTarget"] is JsonObject ocrTarget) ValidateCurrentOcr(state, snapshot, ocrTarget);
        if (Bool(target, "password")) throw new ActionRefusedException("Password controls are refused; no input sent");
        if (kind is "setValue" or "typeText" && target != null && Str(target, "role") is not ("edit" or "document" or "combobox"))
            throw new ActionRefusedException("Text input requires an editable field; no input sent");
        using var textSubmit = kind is ("setValue" or "typeText") && target != null && Str(step, "text", Str(step, "value")).IndexOfAny(['\r', '\n']) >= 0
            ? new NativeTextSubmitScope(state, step, snapshot, target) : null;
        if (target == null && kind is "click" or "drag")
        {
            var at = NativePoint(state, step, snapshot, null);
            var hits = Elements(snapshot).OfType<JsonObject>().Where(e => e["bounds"] is JsonArray b && b.Count == 4 &&
                at.X >= b[0]!.GetValue<double>() && at.Y >= b[1]!.GetValue<double>() && at.X < b[0]!.GetValue<double>() + b[2]!.GetValue<double>() && at.Y < b[1]!.GetValue<double>() + b[3]!.GetValue<double>()).ToList();
            target = hits.OrderByDescending(e => Number(e, "depth")).FirstOrDefault();
            // The coordinate remains the requested point; the hit element is read only for safeguards. Except where a posted
            // click has no effect (WPF, UWP): the single element under the point that has its own UIA gesture (Invoke, Toggle,
            // SelectionItem) gets it in the background instead of the foreground fallback, like trycua/cua (31/10).
            if (kind == "click" && Str(step, "button", "left") == "left" && Number(step, "count", 1) <= 1 && PointGesture(hits) is { } gesture &&
                Pointer.IgnoresPostedClick((nint)state.BoundWindow!.Value.Id))
            {
                target = semanticTarget = gesture; selector = SelectorOf(gesture);
            }
        }
        if (kind == "key" && target != null && !Bool(target, "focused")) throw new ActionRefusedException("The named key target is not focused; first click it with a focused:true postcondition, then send the key");
        CheckGesture(state, step, snapshot, target, textSubmit);
        if (step["expect"] is JsonObject expect && ExpectationHolds(state, snapshot, expect, ocr: true))
        {
            if (kind == "wait" || ExactExpectation(expect) || LegacyStateCheck(expect)) return StepResult("already-satisfied", "=", false, true, "Exact state already satisfied; no input sent");
            throw new ActionRefusedException("Vague postcondition is already true before input; choose a condition that this gesture changes");
        }
        if (kind == "wait")
        {
            if (step["expect"] == null && step["text"] != null)
                step["expect"] = new JsonObject { ["selector"] = new JsonObject { ["name"] = Str(step, "text") }, ["exists"] = !Bool(step, "gone") };
            if (step["expect"] == null)
            {
                var timer = Stopwatch.StartNew(); var delay = Math.Clamp(Number(step, "seconds"), 0, 30) * 1000;
                while (timer.ElapsedMilliseconds < delay) { EmergencyStop.ThrowIfStopped(); Thread.Sleep((int)Math.Min(50, Math.Max(1, delay - timer.ElapsedMilliseconds))); }
                return StepResult("waited", "✓", true, true, $"Waited {timer.ElapsedMilliseconds} ms; no desktop input sent");
            }
            step["timeoutMs"] ??= Math.Clamp(Number(step, "seconds", 1.618), 0, 30) * 1000;
            return ProveNative(state, step, snapshot, null, inputEverSent: false);
        }
        var request = NativeRequest(state, kind);
        foreach (var field in new[] { "value", "text", "key", "action", "option", "path", "button", "count" }) if (step[field] != null) request[field] = step[field]!.DeepClone();
        if (selector != null) { request["selector"] = SelectorOf(target!); AddIdentity(request, target!, snapshot); }
        if (kind == "focus")
        {
            if (!physicalAllowed) throw new ActionRefusedException("Explicit focus requires foreground:true; no click or input sent");
            request["operation"] = "focus"; request["mode"] = "foreground";
        }
        if (kind == "scroll" || (kind == "click" && Str(step, "button", "left") != "left") || kind is "hover" or "drag" || semanticTarget == null && kind == "click")
        {
            var point = NativePoint(state, step, snapshot, semanticTarget);
            request["operation"] = "point"; request.Remove("selector"); request.Remove("runtimeId"); request.Remove("ancestry"); request.Remove("nativeWindow");
            request["action"] = kind == "scroll" ? "wheel" : kind == "hover" ? "hover" : "click";
            request["x"] = point.X; request["y"] = point.Y; request["frame"] = point.Frame;
            if (kind == "scroll") { request["deltaY"] = (int)(-120 * Number(step, "scrollY")); request["deltaX"] = (int)(120 * Number(step, "scrollX")); }
            if (kind == "drag")
            {
                request["operation"] = "physical"; request["action"] = "drag";
                var destinationArgs = new JsonObject { ["x"] = step["to_x"]?.DeepClone(), ["y"] = step["to_y"]?.DeepClone(), ["screenshotId"] = step["screenshotId"]?.DeepClone() };
                JsonObject? destinationTarget = null;
                if (step["to"] is JsonArray to && to.Count == 2) destinationArgs["point"] = new JsonObject { ["x"] = to[0]!.DeepClone(), ["y"] = to[1]!.DeepClone() };
                else if (step["to"] is JsonObject toPoint && toPoint["x"] != null && toPoint["y"] != null) destinationArgs["point"] = toPoint.DeepClone();
                else if (step["to"] != null) destinationTarget = step["observedDestination"] is JsonObject destinationIdentity ? StepTarget(snapshot, new JsonObject { ["observedIdentity"] = destinationIdentity.DeepClone() }, step["to"]!.AsObject()) : ResolveTarget(snapshot, step["to"]!);
                var destination = NativePoint(state, destinationArgs, snapshot, destinationTarget);
                request["toX"] = destination.X; request["toY"] = destination.Y;
            }
        }
        JsonObject outcome;
        try
        {
            if (Str(request, "operation") == "physical" && !(kind == "drag" && ClaudeDevtoolsInScope((nint)state.BoundWindow!.Value.Id)))
            {
                if (!physicalAllowed) throw new ActionRefusedException("This action needs foreground:true; nothing sent");
                outcome = PhysicalNative(state, request);
            }
            else outcome = ObjectOf(ExecuteInternal(request));
        }
        catch (BackgroundNotApplicableException e) when (!Program.InputStarted && physicalAllowed && !HasClaudeDevtools(state))
        {
            // Only a proven refusal before input permits this fallback. An unchanged result never does.
            request["operation"] = "physical"; request["action"] = kind switch { "click" => "click", "focus" => "focus", "setValue" => "replaceText", "typeText" => "type", "key" => "key", "scroll" => "wheel", "hover" => "move", _ => throw new ActionRefusedException("No physical fallback for " + kind + ": " + e.Message) };
            if (kind == "typeText") request["text"] = Str(step, "text");
            // Unlike a value replacement, physical multiline input sends real Enter keys. Bind its
            // original field and recheck new/changed outward controls before each such key, for every agent.
            using var fallbackSubmission = textSubmit == null && kind is ("setValue" or "typeText") && target != null && Str(step, "text", Str(step, "value")).IndexOfAny(['\r', '\n']) >= 0
                ? new NativeTextSubmitScope(state, step, snapshot, target) : null;
            outcome = NativeFallbackAttempt(() => PhysicalNative(state, request), () => Program.InputStarted);
        }
        catch (Exception e) when (Program.InputStarted)
        {
            return ProveNative(state, step, snapshot, null, e.Message);
        }
        if (kind == "click" && target != null && Str(target, "role") is "edit" or "document") state.RetainedFocus = SelectorOf(target);
        return ProveNative(state, step, snapshot, outcome);
    }
    static JsonObject PhysicalNative(SessionState state, JsonObject request)
    {
        state.Interaction?.Check();
        if (state.Interaction == null) NativeMcpGuard.WaitForPause();
        using var lease = state.Interaction == null ? ForegroundToken.Acquire("Native MCP physical fallback") : null;
        if (state.Interaction == null) NativeMcpGuard.RequirePause(); EmergencyStop.ThrowIfStopped();
        using var presence = state.Interaction == null ? NativeMcpGuard.PhysicalScope() : null;
        var noted = Win32.GetForegroundWindow();
        JsonObject result; string returned; var entered = false;
        try
        {
            var action = Str(request, "action");
            if (action is "replaceText" or "type" && request["selector"] is JsonObject focus)
            {
                var click = request.DeepClone().AsObject(); click["action"] = "click"; click.Remove("text"); click.Remove("value");
                ExecuteInternal(click); entered = Program.InputStarted;
                var read = InspectNative(state);
                if (!Bool(UniqueNative(read, focus), "focused")) throw new InvalidOperationException("The target field did not acquire focus; no text sent");
                foreach (var key in new[] { "selector", "runtimeId", "nativeWindow", "ancestry" }) request.Remove(key);
                if (action == "replaceText")
                {
                    var selectAll = request.DeepClone().AsObject(); selectAll["action"] = "key"; selectAll["key"] = "ctrl+a"; ExecuteInternal(selectAll); entered |= Program.InputStarted;
                    request["text"] = Str(request, "value");
                    request["action"] = Str(request, "value").Length > 0 ? "type" : "key";
                    if (Str(request, "value").Length == 0) request["key"] = "backspace";
                }
            }
            if (Str(request, "action") == "focus") { request["operation"] = "focus"; request["mode"] = "foreground"; }
            if (Str(request, "action") == "key") foreach (var key in new[] { "selector", "runtimeId", "nativeWindow", "ancestry" }) request.Remove(key);
            result = ObjectOf(ExecuteInternal(request));
        }
        catch (Exception e) when (entered && !Program.InputStarted)
        {
            Program.InputStarted = true;
            throw new AfterInputException("Physical input stopped after focusing the field; inspect before deciding anything else: " + e.Message, e);
        }
        finally { returned = state.Interaction != null ? "held-for-interaction" : NativeMcpGuard.UserResumed ? "kept-by-julien" : ForegroundReturn.GiveBack(noted, (nint)state.BoundWindow!.Value.Id); }
        result["foregroundReturned"] = returned;
        return result;
    }
    static (int X, int Y, JsonArray Frame) NativePoint(SessionState state, JsonObject step, JsonObject snapshot, JsonObject? target)
    {
        if (snapshot["frame"] is not JsonObject frame) throw new ActionRefusedException("No current window frame; look at a native image first");
        var left = Number(frame, "left"); var top = Number(frame, "top"); var width = Number(frame, "width"); var height = Number(frame, "height");
        double x, y;
        if (step["ocrTarget"] is JsonObject ocr && ocr["bounds"] is JsonArray ocrBounds)
        {
            var prior = ocr["ocrFrame"]?.AsObject() ?? throw new ActionRefusedException("OCR position is not bound to an observed frame");
            if (!SameFrameSize(prior, frame)) throw new ActionRefusedException("Window resized since the OCR text; look again");
            x = ocrBounds[0]!.GetValue<double>() + ocrBounds[2]!.GetValue<double>() / 2 + left - Number(prior, "left");
            y = ocrBounds[1]!.GetValue<double>() + ocrBounds[3]!.GetValue<double>() / 2 + top - Number(prior, "top");
        }
        else if (target?["bounds"] is JsonArray b)
        {
            x = b[0]!.GetValue<double>() + b[2]!.GetValue<double>() / 2; y = b[1]!.GetValue<double>() + b[3]!.GetValue<double>() / 2;
        }
        else if (Bool(step, "placeOnly"))
        {
            x = left + Number(step, "x"); y = top + Number(step, "y");
        }
        else
        {
            var image = (step["screenshotId"] != null && state.RegionImage?.Id == Str(step, "screenshotId") ? state.RegionImage : state.Image)
                ?? throw new ActionRefusedException("Point input requires the image returned by look");
            if (image.Window != state.BoundWindow!.Value.Id || step["screenshotId"] != null && Str(step, "screenshotId") != image.Id) throw new ActionRefusedException("Stale screenshot id");
            if (image.Capture["windowFrame"] is not JsonObject originalFrame || !SameFrameSize(originalFrame, frame)) throw new ActionRefusedException("Window moved or resized since image; look again");
            if (step["box_2d"] is JsonArray box && box.Count == 4)
            {
                var shotFrame = TranslateFrame(image.Capture["frame"]!.AsObject(), originalFrame, frame);
                x = Number(shotFrame, "left") + (box[1]!.GetValue<double>() + box[3]!.GetValue<double>()) * Number(shotFrame, "width") / 2000;
                y = Number(shotFrame, "top") + (box[0]!.GetValue<double>() + box[2]!.GetValue<double>()) * Number(shotFrame, "height") / 2000;
            }
            else if (step["point"] is JsonObject point)
            {
                var shotFrame = TranslateFrame(image.Capture["frame"]!.AsObject(), originalFrame, frame);
                x = Number(shotFrame, "left") + Number(point, "x") * Number(shotFrame, "width") / Number(image.Capture, "width");
                y = Number(shotFrame, "top") + Number(point, "y") * Number(shotFrame, "height") / Number(image.Capture, "height");
            }
            else
            {
                if (step["x"] == null || step["y"] == null) throw new ActionRefusedException("Pointer action needs a semantic target or observed coordinates");
                x = left + Number(step, "x"); y = top + Number(step, "y");
            }
        }
        if (x < left || y < top || x >= left + width || y >= top + height) throw new ActionRefusedException("Point is outside the observed window");
        return ((int)Math.Round(x), (int)Math.Round(y), new JsonArray((int)left, (int)top, (int)width, (int)height));
    }
}
