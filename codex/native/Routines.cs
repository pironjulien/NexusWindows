using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Procedures verifiees et persistantes (meme schema 1 que codex/lib/routines.mjs) :
// - jamais d'autorisation (confirm, accord) ni de texte utilisateur non parametre dans la definition
// - expansion {{parametre}}, validation des selecteurs et postconditions, synchronisation miroir.
static class Routines
{
    static readonly Regex ParamRegex = new(@"\{\{\s*([\p{L}_][\p{L}\p{N}_]*)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex NameRegex = new(@"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,59}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex HashRegex = new(@"^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Dictionary<string, string[]> AllowedStepFields = new(StringComparer.Ordinal)
    {
        ["click"] = ["selector", "button", "count"],
        ["setValue"] = ["selector", "value"],
        ["typeText"] = ["focus", "text"],
        ["key"] = ["key"],
        ["select"] = ["selector", "option"],
        ["menu"] = ["path"],
        ["secondary"] = ["selector", "action"],
        ["scroll"] = ["selector", "scrollX", "scrollY"],
        ["wait"] = ["seconds"],
        ["focus"] = ["selector"],
        ["hover"] = ["selector"],
    };

    static readonly HashSet<string> NamedRoles = new(StringComparer.Ordinal)
    {
        "button", "tab", "menuitem", "checkbox", "radio", "combobox", "link", "treeitem", "listitem",
    };

    static readonly HashSet<string> StructureNamedRoles = new(StringComparer.Ordinal)
    {
        "button", "tab", "menuitem", "checkbox", "radio", "combobox",
    };

    static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string ApplicationKey(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) throw new InvalidOperationException("A bound application is required");
        return app.StartsWith("process:", StringComparison.OrdinalIgnoreCase)
            ? "process:" + Path.GetFileName(app["process:".Length..]).ToLowerInvariant()
            : app.ToLowerInvariant();
    }

    public static string ValidateRoutineName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !NameRegex.IsMatch(name))
            throw new InvalidOperationException("Routine name: 1–60 letters, digits, spaces, dots, underscores or hyphens");
        return name;
    }

    public static List<string> ParametersIn(JsonNode? node)
    {
        if (node == null) return [];
        var raw = node.ToJsonString();
        return ParamRegex.Matches(raw).Select(m => m.Groups[1].Value).ToList();
    }

    static bool IsTemplateOnly(string value) =>
        ParamRegex.IsMatch(value) && string.IsNullOrWhiteSpace(ParamRegex.Replace(value, ""));

    public static void ValidateSelector(JsonObject? selector, bool nested = false)
    {
        if (selector == null) throw new InvalidOperationException("A semantic selector is required");
        var allowed = nested
            ? new HashSet<string>(StringComparer.Ordinal) { "role", "name", "automationId", "ancestor", "window" }
            : new HashSet<string>(StringComparer.Ordinal) { "role", "name", "automationId", "ancestor" };
        foreach (var kv in selector)
        {
            if (!allowed.Contains(kv.Key)) throw new InvalidOperationException("Unknown selector field");
        }
        if (selector.TryGetPropertyValue("window", out var winNode) && winNode != null)
        {
            if (winNode is not JsonValue wv || !wv.TryGetValue<long>(out var w) || w <= 0)
                throw new InvalidOperationException("Invalid selector field");
        }
        var hasString = false;
        foreach (var k in new[] { "role", "name", "automationId" })
        {
            if (selector.TryGetPropertyValue(k, out var v) && v != null)
            {
                if (v is not JsonValue sv || !sv.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s))
                    throw new InvalidOperationException("Invalid selector field");
                hasString = true;
            }
        }
        if (!hasString && !selector.ContainsKey("window"))
            throw new InvalidOperationException("Empty selector");
        if (selector.TryGetPropertyValue("ancestor", out var anc) && anc != null)
        {
            if (nested) throw new InvalidOperationException("Nested ancestor selectors are unsupported");
            ValidateSelector(anc as JsonObject, true);
        }
    }

    public static void ValidateExpectation(JsonObject? expect)
    {
        if (expect == null) throw new InvalidOperationException("An explicit postcondition is required");
        if (expect["all"] is JsonArray all && expect.Count == 1 && all.Count > 0) { foreach (var e in all) ValidateExpectation(e as JsonObject); return; }
        if (expect["text"] is JsonValue text && text.TryGetValue<string>(out var t) && t.Length > 0 && expect.All(k => k.Key is "text" or "gone")) return;
        ValidateSelector(expect["selector"] as JsonObject);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "selector", "exists", "field", "equals", "includes" };
        foreach (var kv in expect)
        {
            if (!allowed.Contains(kv.Key)) throw new InvalidOperationException("Unknown postcondition field");
        }
        if (expect.TryGetPropertyValue("exists", out var ex) && ex != null && (ex is not JsonValue exv || !exv.TryGetValue<bool>(out _)))
            throw new InvalidOperationException("exists must be boolean");
        if (expect.TryGetPropertyValue("field", out var fld) && fld != null)
        {
            if (fld is not JsonValue fv || !fv.TryGetValue<string>(out var fs) ||
                fs is not ("name" or "value" or "selected" or "checked" or "expanded" or "disabled" or "description" or "focused" or "visible"))
                throw new InvalidOperationException("Unsupported postcondition field");
        }
        var hasEquals = expect.TryGetPropertyValue("equals", out var eq) && eq != null;
        if (hasEquals && eq is JsonValue eqv && !eqv.TryGetValue<string>(out _) && !eqv.TryGetValue<bool>(out _))
            throw new InvalidOperationException("Invalid equality");
        var hasIncludes = expect.TryGetPropertyValue("includes", out var inc) && inc != null;
        if (hasIncludes && (inc is not JsonValue incv || !incv.TryGetValue<string>(out var incs) || string.IsNullOrEmpty(incs)))
            throw new InvalidOperationException("Invalid text condition");
        if (hasEquals && hasIncludes) throw new InvalidOperationException("Choose equals or includes");
        var isExistsFalse = expect.TryGetPropertyValue("exists", out var ex2) && ex2 is JsonValue ex2v && ex2v.TryGetValue<bool>(out var b) && !b;
        if (isExistsFalse && (hasEquals || hasIncludes)) throw new InvalidOperationException("Absence cannot have a value condition");
        if (!expect.ContainsKey("exists") && !hasEquals && !hasIncludes) throw new InvalidOperationException("Empty postcondition");
    }

    public static JsonArray PrepareRoutineSteps(JsonArray? steps)
    {
        if (steps == null || steps.Count == 0) throw new InvalidOperationException("A routine needs at least one step");
        var result = new JsonArray();
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject orig) throw new InvalidOperationException($"Step {i}: invalid routine step");
            var step = orig.DeepClone().AsObject();
            step.Remove("confirm");
            step.Remove("accord");
            var kind = step["kind"]?.GetValue<string>() ?? "";
            if (!AllowedStepFields.TryGetValue(kind, out var kindFields))
                throw new InvalidOperationException($"Step {i}: only semantic routine steps can be saved");
            var allowed = new HashSet<string>(kindFields, StringComparer.Ordinal) { "kind", "expect", "timeoutMs", "refresh", "fallback" };
            foreach (var kv in step)
            {
                if (!allowed.Contains(kv.Key)) throw new InvalidOperationException($"Step {i}: only semantic routine steps can be saved");
            }
            if (step["selector"] != null) ValidateSelector(step["selector"] as JsonObject);
            if (step["focus"] != null) ValidateSelector(step["focus"] as JsonObject);
            if (kind is "click" or "secondary" or "setValue" or "select" or "scroll" or "hover" && step["selector"] == null)
                throw new InvalidOperationException($"Step {i}: a semantic selector is required");
            if (kind == "typeText" && step["focus"] == null)
                throw new InvalidOperationException($"Step {i}: a semantic focus selector is required");

            foreach (var sel in new[] { step["selector"] as JsonObject, step["focus"] as JsonObject, (step["expect"] as JsonObject)?["selector"] as JsonObject })
            {
                if (sel == null) continue;
                if ((sel["ancestor"] as JsonObject)?.ContainsKey("window") == true)
                    throw new InvalidOperationException($"Step {i}: a saved selector cannot contain a window handle");
                foreach (var part in new[] { sel, sel["ancestor"] as JsonObject })
                {
                    if (part != null && part["name"] != null && part["role"]?.GetValue<string>() is "window" or "document")
                        throw new InvalidOperationException($"Step {i}: a transferable selector cannot contain a window or document title");
                }
            }

            if (step["expect"] == null && kind == "setValue")
                step["expect"] = new JsonObject { ["selector"] = step["selector"]!.DeepClone(), ["field"] = "value", ["equals"] = step["value"]?.DeepClone() };
            if (step["expect"] == null && kind == "select")
                step["expect"] = new JsonObject { ["selector"] = step["selector"]!.DeepClone(), ["field"] = "value", ["equals"] = step["option"]?.DeepClone() };
            if (step["expect"] == null && kind == "secondary" && step["action"]?.GetValue<string>() is { } actName && actName is "Expand" or "Collapse")
                step["expect"] = new JsonObject { ["selector"] = step["selector"]!.DeepClone(), ["field"] = "expanded", ["equals"] = actName == "Expand" };

            ValidateExpectation(step["expect"] as JsonObject);

            foreach (var kv in step)
            {
                if (kv.Key is "text" or "value" && kind is "typeText" or "setValue") continue;
                if (kv.Key == "expect" && kv.Value is JsonObject expObj)
                {
                    ValidateExpectationParameters(expObj);
                }
                else if (ParametersIn(kv.Value).Count > 0)
                {
                    throw new InvalidOperationException($"Step {i}: parameters are allowed only in typed text and text postconditions");
                }
            }

            foreach (var field in kind == "typeText" ? new[] { "text" } : kind == "setValue" ? new[] { "value" } : Array.Empty<string>())
            {
                if (step[field] is not JsonValue fv || !fv.TryGetValue<string>(out var s) || (!string.IsNullOrWhiteSpace(s) && !IsTemplateOnly(s)))
                    throw new InvalidOperationException($"Step {i}: typed text must contain only {{{{parameter}}}} templates");
            }

            var exp = step["expect"]!.AsObject();
            if (exp["field"]?.GetValue<string>() == "value")
            {
                foreach (var field in new[] { "equals", "includes" })
                {
                    if (exp[field] is JsonValue fv && fv.TryGetValue<string>(out var val) && !string.IsNullOrWhiteSpace(val) &&
                        !IsTemplateOnly(val) && !(kind == "select" && val == step["option"]?.GetValue<string>()))
                    {
                        throw new InvalidOperationException($"Step {i}: a field-value postcondition must use a text parameter");
                    }
                }
            }
            result.Add(step);
        }
        return result;
    }

    static void ValidateExpectationParameters(JsonObject expect)
    {
        foreach (var kv in expect)
        {
            if (kv.Key == "all" && kv.Value is JsonArray all) { foreach (var child in all.OfType<JsonObject>()) ValidateExpectationParameters(child); }
            else if (kv.Key is not ("equals" or "includes" or "text") && ParametersIn(kv.Value).Count > 0)
                throw new InvalidOperationException("Parameters cannot change a postcondition target or field");
        }
    }

    public static JsonArray ExpandRoutine(JsonArray steps, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var templates = PrepareRoutineSteps(steps);
        var bindings = parameters ?? new Dictionary<string, string>();
        var wanted = ParametersIn(templates).ToHashSet(StringComparer.Ordinal);
        var unknown = bindings.Keys.Where(k => !wanted.Contains(k)).ToList();
        var missing = wanted.Where(k => !bindings.ContainsKey(k)).ToList();
        if (unknown.Count > 0 || missing.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("missing " + string.Join(", ", missing));
            if (unknown.Count > 0) parts.Add("unknown " + string.Join(", ", unknown));
            throw new InvalidOperationException("Routine parameters: " + string.Join("; ", parts));
        }
        JsonNode? Fill(JsonNode? node)
        {
            if (node == null) return null;
            if (node is JsonValue v && v.TryGetValue<string>(out var s))
                return JsonValue.Create(ParamRegex.Replace(s, m => bindings[m.Groups[1].Value]));
            if (node is JsonArray arr)
            {
                var outArr = new JsonArray();
                foreach (var item in arr) outArr.Add(Fill(item));
                return outArr;
            }
            if (node is JsonObject obj)
            {
                var outObj = new JsonObject();
                foreach (var kv in obj) outObj[kv.Key] = Fill(kv.Value);
                return outObj;
            }
            return node.DeepClone();
        }
        return Fill(templates)!.AsArray();
    }

    public static string StructureHash(IReadOnlyList<Node> nodes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartArray();
            foreach (var n in nodes)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(n.Depth * 2);
                writer.WriteStringValue(n.Role);
                writer.WriteStringValue(n.AutomationId);
                writer.WriteStringValue(StructureNamedRoles.Contains(n.Role) ? n.Name : "");
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static int SelfTest()
    {
        void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("Routines SelfTest failed: " + msg);
        }

        Assert(ApplicationKey(@"process:C:\Windows\System32\notepad.exe") == "process:notepad.exe", "ApplicationKey process");
        Assert(ValidateRoutineName("remplir_fiche-1.0") == "remplir_fiche-1.0", "ValidateRoutineName");

        var rawSteps = JsonNode.Parse("""
        [
          { "kind": "setValue", "selector": { "role": "edit", "automationId": "nom" }, "value": "{{nom}}", "confirm": true },
          { "kind": "select", "selector": { "role": "combobox", "automationId": "choix" }, "option": "Alpha" }
        ]
        """)!.AsArray();

        var prepared = PrepareRoutineSteps(rawSteps);
        Assert(prepared.Count == 2, "prepared count");
        Assert(prepared[0]!.AsObject()["confirm"] == null, "confirm stripped from saved routine");
        Assert(prepared[0]!.AsObject()["expect"] != null, "inferred expect on setValue");

        var expanded = ExpandRoutine(prepared, new Dictionary<string, string> { ["nom"] = "Julien" });
        Assert(expanded[0]!.AsObject()["value"]!.GetValue<string>() == "Julien", "parameter expanded in value");
        Assert(expanded[0]!.AsObject()["expect"]!.AsObject()["equals"]!.GetValue<string>() == "Julien", "parameter expanded in expect");

        var caught = false;
        try { ExpandRoutine(prepared, new Dictionary<string, string>()); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("missing nom", StringComparison.Ordinal)) { caught = true; }
        Assert(caught, "missing parameter rejected");

        Console.WriteLine(JsonSerializer.Serialize(new { ok = true }));
        return 0;
    }
}
