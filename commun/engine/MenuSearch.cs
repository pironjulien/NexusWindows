using System.Text.Json;

// Search for a menu item DIRECTLY under another (Codex's review of 22/10), independent of the tree's source (UI
// Automation in ComUia.MenuChild, simulated tree in tests/CdpTests.cs): breadth-first walk that crosses containers
// (menu, bar, pane) without ever going past another menu item; exact name, automation id when given; a namesake, more
// than Max nodes or unreadable children (children throws): refused.
static class MenuSearch
{
    public const int Max = 2000;

    public static (bool Found, T Item) Find<T>(T scope, Func<T, IReadOnlyList<T>> children, Func<T, bool> isMenuItem,
        Func<T, string> name, Func<T, string> automationId, string wantedName, string? wantedId)
    {
        var queue = new Queue<T>(children(scope));
        var visited = queue.Count;
        (bool, T) found = (false, default!);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (isMenuItem(node))
            {
                if (name(node) != wantedName || wantedId is { Length: > 0 } && automationId(node) != wantedId) continue;
                if (found.Item1) throw new BackgroundNotApplicableException($"Menu item « {wantedName} » is ambiguous at this level: nothing was sent");
                found = (true, node);
                continue; // never crossed
            }
            foreach (var child in children(node))
            {
                if (++visited > Max) throw new BackgroundNotApplicableException("Menu too large to be read completely: nothing was sent");
                queue.Enqueue(child);
            }
        }
        if (visited > Max) throw new BackgroundNotApplicableException("Menu too large to be read completely: nothing was sent");
        return found;
    }

    // Path of a WinForms menu item targeted by a click: menu items of its observed ancestry (in order), then the item
    // itself (name, automation id of the selector); an ancestor the selector requires must appear in the observed
    // ancestry.
    public static (string Name, string? AutomationId)[] Segments(IReadOnlyList<JsonElement> ancestry, JsonElement selector)
    {
        static string Field(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static bool Same(JsonElement e, JsonProperty p) => p.Name == "window"
            ? e.TryGetProperty("nativeWindow", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt64() == p.Value.GetInt64()
            : Field(e, p.Name) == p.Value.GetString();
        if (selector.TryGetProperty("ancestor", out var wanted) &&
            !ancestry.Any(e => wanted.EnumerateObject().Where(p => p.Name != "ancestor").All(p => Same(e, p))))
            throw new ActionRefusedException("Menu context changed (required ancestor absent); inspect again");
        return ancestry.Where(e => Field(e, "role") == "menuitem").Select(e => (Field(e, "name"), (string?)Field(e, "automationId")))
            .Append((Field(selector, "name"), selector.TryGetProperty("automationId", out var aid) ? aid.GetString() : null)).ToArray();
    }

    // Scope of a menu command: the popup (open context or drop-down menu) of its observed ancestry, last own window
    // different from the followed window; otherwise the followed window. The popup must still be open and bound
    // (checked by the caller, Win32.IsTransientPopup): never another window guessed.
    public static long Scope(long window, IReadOnlyList<JsonElement> ancestry) =>
        ancestry.Select(e => e.TryGetProperty("nativeWindow", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt64() : 0)
            .LastOrDefault(w => w != 0 && w != window) is var popup && popup != 0 ? popup : window;
}
