// Offline self-checks of commun/engine/MenuSearch.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Text.Json;
using static MenuSearch;

static partial class MenuSearchSelfTests
{
    // Offline check (tests): stdin = {"nodes": [{"id", "role", "name", "automationId", "children": [ids],
    // "unreadable"}], "root", "path": [{"name", "automationId"}]} or {"ancestry", "selector"}; output {"id"} / {"segments"}
    // or {"error"}.
    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var root = input.RootElement;
        object result;
        try
        {
            if (root.TryGetProperty("scopeOf", out var scopeOf))
                result = new MenuSearchSelfTestJson(scope: Scope(scopeOf.GetInt64(), root.GetProperty("ancestry").EnumerateArray().ToList()));
            else if (root.TryGetProperty("selector", out var selector))
            {
                var segments = Segments(root.GetProperty("ancestry").EnumerateArray().ToList(), selector);
                result = new MenuSearchSelfTestJson2(segments: segments.Select(s => new ProgramMenuByNamesJson(name: s.Name, automationId: s.AutomationId)).ToArray());
            }
            else
            {
                var nodes = root.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetInt32());
                string F(int id, string key) => nodes[id].TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                IReadOnlyList<int> Children(int id)
                {
                    if (nodes[id].TryGetProperty("unreadable", out var u) && u.GetBoolean()) throw new BackgroundNotApplicableException("Menu could not be read completely: nothing was sent");
                    return nodes[id].TryGetProperty("children", out var c) ? c.EnumerateArray().Select(x => x.GetInt32()).ToList() : [];
                }
                var scope = root.GetProperty("root").GetInt32();
                foreach (var segment in root.GetProperty("path").EnumerateArray())
                {
                    var (found, item) = Find(scope, Children, id => F(id, "role") == "menuitem", id => F(id, "name"), id => F(id, "automationId"),
                        segment.GetProperty("name").GetString()!, segment.TryGetProperty("automationId", out var a) ? a.GetString() : null);
                    if (!found) throw new BackgroundNotApplicableException($"Menu item « {segment.GetProperty("name").GetString()} » not found at this level");
                    scope = item;
                }
                result = new MenuSearchSelfTestJson3(id: scope);
            }
        }
        catch (Exception error) when (error is BackgroundNotApplicableException or ActionRefusedException) { result = new CdpSurfaceSelfTestJson2(error: error.Message); }
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }
}
