using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class McpServer
{
    // Conversion de syntaxe uniquement : aucune cible ni preuve n'est inventée à l'import.
    static JsonObject LegacyStep(JsonObject original)
    {
        var step = original.DeepClone().AsObject();
        if (step["kind"] != null || step["do"] == null && step["action"] == null) return step;
        var action = Str(step, "do", Str(step, "action"));
        if (action == "toggle") step[step["do"] != null ? "do" : "action"] = "click";
        if (step["keys"] != null) { step["key"] = step["keys"]!.DeepClone(); step.Remove("keys"); }
        if (action == "key" && step["key"] == null && step["text"] != null) { step["key"] = step["text"]!.DeepClone(); step.Remove("text"); }
        if (action == "select" && step["option"] == null && step["text"] != null) { step["option"] = step["text"]!.DeepClone(); step.Remove("text"); }
        if (action == "menu") { step["path"] ??= step["text"]?.DeepClone() ?? step["target"]?.DeepClone(); step.Remove("text"); step.Remove("target"); }
        if (action == "wait") { step["text"] ??= step["target"]?.DeepClone(); step.Remove("target"); }
        if (step["timeout"] != null) { step["timeoutMs"] = (int)(DoubleValue(step["timeout"]) * 1000); step.Remove("timeout"); }
        var conditions = new JsonArray();
        if (step["check"] is JsonObject check)
        {
            foreach (var field in new[] { "value", "contains", "checked", "selected", "expanded", "focused", "enabled", "exists" })
            {
                if (check[field] == null) continue;
                var checkTarget = check["target"] ?? step["target"] ?? throw new InvalidOperationException("check needs target");
                var c = new JsonObject { [IsLegacyIndex(checkTarget) ? "element" : "selector"] = IsLegacyIndex(checkTarget) ? checkTarget.DeepClone() : LegacySelector(checkTarget) };
                c["exists"] = true;
                if (field == "exists") c["exists"] = check[field]!.DeepClone();
                else { c["field"] = field == "enabled" ? "disabled" : field == "contains" ? "value" : field; c[field == "contains" ? "includes" : "equals"] = field == "enabled" ? JsonValue.Create(!check[field]!.GetValue<bool>()) : check[field]!.DeepClone(); }
                conditions.Add(c);
            }
            step.Remove("check");
        }
        if (step["expect"] is JsonObject oldExpect && oldExpect["element"] != null)
        {
            conditions.Add(LegacyElementExpectation(oldExpect)); step.Remove("expect");
        }
        if (step["expect"] is JsonValue ev && ev.TryGetValue<string>(out var text)) conditions.Add(new JsonObject { ["text"] = text, ["gone"] = false });
        if (action == "wait" && step["text"] != null) { conditions.Add(new JsonObject { ["text"] = step["text"]!.DeepClone(), ["gone"] = Bool(step, "gone") }); step.Remove("text"); }
        step.Remove("gone");
        if (conditions.Count > 0) step["expect"] = conditions.Count == 1 ? conditions[0]!.DeepClone() : new JsonObject { ["all"] = conditions };
        if (action == "scroll")
        {
            var direction = Str(step, "direction", "down"); var amount = Number(step, "amount", 3);
            if (direction == "into_view")
            {
                step["do"] = "secondary"; step["action"] = "ScrollIntoView";
                step["expect"] ??= new JsonObject { [IsLegacyIndex(step["target"]) ? "element" : "selector"] = IsLegacyIndex(step["target"]) ? step["target"]!.DeepClone() : LegacySelector(step["target"]!), ["field"] = "visible", ["equals"] = true };
            }
            else if (direction is "up" or "down") step["scrollY"] = direction == "down" ? amount : -amount;
            else if (direction is "left" or "right") step["scrollX"] = direction == "right" ? amount : -amount;
            else throw new InvalidOperationException("Unknown scroll direction");
            step.Remove("direction"); step.Remove("amount");
        }
        return step;
    }
    static bool IsLegacyIndex(JsonNode? value) => value is JsonValue && Regex.IsMatch(value.ToString(), @"^\s*(?:#|\[)?\s*\d+\s*\]?\s*$");
    static JsonObject LegacyElementExpectation(JsonObject original)
    {
        var target = original["element"] ?? throw new InvalidOperationException("Postcondition needs element");
        var conditions = new JsonArray();
        JsonObject Condition() => new() { [IsLegacyIndex(target) ? "element" : "selector"] = IsLegacyIndex(target) ? target.DeepClone() : LegacySelector(target) };
        if (original["exists"] != null) { var c = Condition(); c["exists"] = original["exists"]!.DeepClone(); conditions.Add(c); }
        foreach (var field in new[] { "value", "checked", "selected", "expanded", "focused", "disabled", "name", "description", "enabled" })
            if (original[field] != null) { var c = Condition(); c["field"] = field == "enabled" ? "disabled" : field; c["equals"] = field == "enabled" ? JsonValue.Create(!original[field]!.GetValue<bool>()) : original[field]!.DeepClone(); conditions.Add(c); }
        if (original["contains"] != null) { var c = Condition(); c["field"] = original["field"]?.DeepClone() ?? JsonValue.Create("value"); c["includes"] = original["contains"]!.DeepClone(); conditions.Add(c); }
        if (conditions.Count == 0) { var c = Condition(); c["exists"] = true; return c; }
        return conditions.Count == 1 ? conditions[0]!.DeepClone().AsObject() : new JsonObject { ["all"] = conditions };
    }
    static double DoubleValue(JsonNode? value) => value?.GetValue<double>() ?? 0;
    static JsonObject LegacySelector(JsonNode value)
    {
        if (value is JsonObject selector) return selector.DeepClone().AsObject();
        var text = value.ToString().Trim();
        if (Regex.IsMatch(text, @"^(?:#|\[)?\d+\]?$|^-?\d+\s*[,x]\s*-?\d+$")) throw new InvalidDataException("A saved routine needs a semantic target, not an index or point");
        var match = Regex.Match(text, "^([A-Za-zéèÉ]+)\\s*(?::\\s*(.+)|[«\\\"'](.+?)[»\\\"'])$");
        if (!match.Success) return new JsonObject { ["name"] = text };
        var key = match.Groups[1].Value.ToLowerInvariant(); var name = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
        if (key is "name" or "nom") return new JsonObject { ["name"] = name };
        return LegacyRoles(key).Length > 0 ? new JsonObject { ["role"] = "legacy/" + key, ["name"] = name } : new JsonObject { ["name"] = text };
    }
    static string[] LegacyRoles(string key) => key switch
    {
        "bouton" or "button" => ["button", "splitbutton"], "case" or "checkbox" => ["checkbox"], "radio" => ["radio"], "champ" => ["edit", "document", "combobox"],
        "edit" => ["edit"], "texte" => ["text", "edit", "document"], "text" => ["text"], "lien" or "link" => ["link"], "menu" => ["menuitem", "menu"], "menuitem" => ["menuitem"],
        "onglet" or "tab" => ["tab"], "liste" => ["list", "listitem", "combobox"], "list" => ["list"], "listitem" or "cellule" => ["listitem", "dataitem"],
        "élément" => ["listitem", "treeitem", "dataitem"], "option" => ["listitem", "radio", "menuitem"], "combo" or "combobox" => ["combobox"], "arbre" => ["treeitem", "tree"],
        "treeitem" => ["treeitem"], "document" => ["document"], "image" => ["image"], "groupe" or "group" => ["group"], "curseur" or "slider" => ["slider"], _ => []
    };
}
