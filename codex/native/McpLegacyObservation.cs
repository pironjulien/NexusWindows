using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class McpServer
{
    sealed class NativeLookMemory
    {
        public string Identity = "";
        public int Next;
        public Dictionary<string, int> Ids = new(StringComparer.Ordinal);
        public Dictionary<int, string> Lines = new();
        public HashSet<int> Shown = new();
        public JsonObject? Image;
        public Dictionary<string, string> Sent = new(StringComparer.Ordinal);
    }
    static readonly ConditionalWeakTable<SessionState, Dictionary<long, NativeLookMemory>> NativeLooks = new();
    [ThreadStatic] static (long Window, string Birth)? BoundObservationIdentity;
    static NativeLookMemory LookMemory(SessionState state, long window, JsonObject? snapshot = null)
    {
        var windows = NativeLooks.GetOrCreateValue(state);
        if (!windows.TryGetValue(window, out var memory)) windows[window] = memory = new();
        if (snapshot != null)
        {
            var identity = Str(snapshot, "processStartedUtc") + "|" + Str(snapshot, "revision");
            if (memory.Identity != identity) windows[window] = memory = new() { Identity = identity };
        }
        return memory;
    }
    static string NodeKey(JsonObject element) => IdentityPart(element).ToJsonString(JsonOut);
    static void PublishNativeIndices(SessionState state, JsonObject snapshot, NativeLookMemory memory)
    {
        var translated = new Dictionary<int, int>();
        foreach (var element in Elements(snapshot).OfType<JsonObject>())
        {
            var key = NodeKey(element);
            if (!memory.Ids.TryGetValue(key, out var id)) memory.Ids[key] = id = ++memory.Next;
            translated[(int)Number(element, "index")] = id;
        }
        foreach (var element in Elements(snapshot).OfType<JsonObject>())
        {
            element["index"] = translated[(int)Number(element, "index")];
            element["ancestors"] = new JsonArray((element["ancestors"] as JsonArray ?? new()).Select(i => (JsonNode)JsonValue.Create(translated[i!.GetValue<int>()])!).ToArray());
        }
    }
    static JsonObject RequireNativeFrame(JsonObject snapshot)
    {
        if (snapshot["frame"] is not JsonObject frame || new[] { "left", "top", "width", "height" }.Any(field => !double.IsFinite(Number(frame, field, double.NaN)))
            || Number(frame, "width") <= 0 || Number(frame, "height") <= 0)
            throw new ActionRefusedException("The current window has no attested capture frame; look again before requesting an image or OCR");
        return frame;
    }
    static JsonObject ReadNativeCapture(SessionState state, JsonObject snapshot, JsonArray? zoom = null, string marks = "none")
    {
        var frame = RequireNativeFrame(snapshot);
        var request = NativeRequest(state, "capture"); request["directory"] = Path.Combine(NativeStateRoot(state.Agent), "captures");
        if (zoom != null) request["zoom"] = zoom;
        if (marks != "none")
        {
            request["treeFrame"] = frame.DeepClone();
            request["marks"] = new JsonArray(Elements(snapshot).OfType<JsonObject>().Where(e => e["bounds"] is JsonArray && (marks == "all" || Str(e, "name").Length == 0 || Bool(e, "ocr"))).Take(400).Select(e => (JsonNode)new JsonObject { ["index"] = e["index"]!.DeepClone(), ["bounds"] = e["bounds"]!.DeepClone(), ["scope"] = e["nativeWindow"]?.DeepClone() }).ToArray());
        }
        var shot = ObjectOf(ExecuteInternal(request));
        if (Bool(shot, "blank") || Str(shot, "path").Length == 0) throw new CaptureUnavailableException("The application returned a blank image; no usable screenshot was published");
        if (shot["windowFrame"] is not JsonObject capturedFrame || !JsonNode.DeepEquals(capturedFrame, snapshot["frame"]))
            throw new ActionRefusedException("Window geometry changed during capture; look again");
        return shot;
    }
    static void AppendNativeOcr(SessionState state, JsonObject snapshot, JsonObject shot, NativeLookMemory memory)
    {
        var frame = RequireNativeFrame(snapshot);
        var request = NativeRequest(state, "ocr"); request["path"] = Str(shot, "path");
        var ocr = ObjectOf(ExecuteInternal(request)); snapshot["ocr"] = ocr.DeepClone();
        foreach (var line in (ocr["lines"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var text = Str(line, "text"); if (text.Length == 0) continue;
            var key = "ocr|" + text + "|" + Number(line, "left") + "|" + Number(line, "top");
            if (!memory.Ids.TryGetValue(key, out var id)) memory.Ids[key] = id = ++memory.Next;
            Elements(snapshot).Add(new JsonObject { ["index"] = id, ["role"] = "text", ["name"] = text, ["ocr"] = true,
                ["bounds"] = new JsonArray(Number(line, "left"), Number(line, "top"), Number(line, "width"), Number(line, "height")), ["ocrFrame"] = frame.DeepClone(), ["ancestors"] = new JsonArray() });
        }
    }
    static string NativeLine(JsonObject element, JsonObject? reference)
    {
        var line = $"[{element["index"]}] {element["role"]} \"{Str(element, "name")}\"";
        if (Bool(element, "ocr")) line += " (OCR)";
        if (element["value"] != null && !Bool(element, "password")) line += $" = \"{Str(element, "value")}\"";
        foreach (var field in new[] { "checked", "selected", "expanded", "disabled", "focused" }) if (element[field] != null) line += $" {field}={element[field]}";
        if (reference?["frame"] is JsonObject frame && element["bounds"] is JsonArray { Count: 4 } bounds)
        {
            var x = (bounds[0]!.GetValue<double>() + bounds[2]!.GetValue<double>() / 2 - Number(frame, "left")) * Number(reference, "width") / Number(frame, "width");
            var y = (bounds[1]!.GetValue<double>() + bounds[3]!.GetValue<double>() / 2 - Number(frame, "top")) * Number(reference, "height") / Number(frame, "height");
            line += $" @{Math.Round(x)},{Math.Round(y)}";
        }
        return line;
    }
    static List<string> ChangedNativeLines(Dictionary<int, string> before, Dictionary<int, string> after)
    {
        var result = new List<string>();
        foreach (var pair in after) if (!before.TryGetValue(pair.Key, out var old)) result.Add("+ " + pair.Value); else if (old != pair.Value) result.Add("~ " + pair.Value);
        foreach (var pair in before) if (!after.ContainsKey(pair.Key)) result.Add("- " + pair.Value);
        return result.Count == 0 ? ["No visible element change since the previous look."] : result;
    }
    // Number of descendants of each element, from the ancestor chains every element carries.
    static Dictionary<int, int> ParentCounts(JsonObject snapshot)
    {
        var counts = new Dictionary<int, int>();
        foreach (var element in Elements(snapshot).OfType<JsonObject>())
            foreach (var ancestor in element["ancestors"] as JsonArray ?? new()) { var key = ancestor!.GetValue<int>(); counts[key] = counts.GetValueOrDefault(key) + 1; }
        return counts;
    }
    // A layout wrapper: an unnamed pane/group/custom with no id, value or focus, which only holds other elements.
    static bool EmptyContainer(JsonObject element, Dictionary<int, int> descendants)
        => Str(element, "role") is "pane" or "group" or "custom" && Str(element, "name").Length == 0 && Str(element, "automationId").Length == 0
            && element["value"] == null && !Bool(element, "focused") && !Bool(element, "ocr") && descendants.GetValueOrDefault((int)Number(element, "index", -1)) > 0;
    // show="table": headers then rows of the grid at or under element N (GridPattern/TablePattern, Explorer Details view,
    // DataGridView, WPF DataGrid), or, without a grid (Task Manager), the rows of the observed tree: list, data or tree
    // items under N, one cell per child.
    static JsonObject NativeTable(SessionState state, JsonObject snapshot, int within, int maximum)
    {
        var all = Elements(snapshot).OfType<JsonObject>().ToList();
        var root = all.First(e => Number(e, "index", -1) == within);
        if (root["runtimeId"] is JsonArray { Count: > 0 } runtimeId)
        {
            var request = NativeRequest(state, "table");
            request["runtimeId"] = runtimeId.DeepClone(); request["nativeWindow"] = root["nativeWindow"]?.DeepClone(); request["maxRows"] = maximum;
            try
            {
                var read = ObjectOf(ExecuteInternal(request));
                if (Bool(read, "grid"))
                    return new JsonObject { ["source"] = "grid", ["headers"] = read["headers"]!.DeepClone(), ["rows"] = read["rows"]!.DeepClone(),
                        ["rowCount"] = read["rowCount"]!.DeepClone(), ["columnCount"] = read["columnCount"]!.DeepClone() };
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException && error is not ActionRefusedException) { }
        }
        return TreeTable(all, within, maximum);
    }
    internal static JsonObject TreeTable(List<JsonObject> all, int within, int maximum)
    {
        int Parent(JsonObject e) => e["ancestors"] is JsonArray { Count: > 0 } chain ? chain[^1]!.GetValue<int>() : -1;
        bool Under(JsonObject e) => (e["ancestors"] as JsonArray)?.Any(i => i!.GetValue<int>() == within) == true;
        string Text(JsonObject e) => Str(e, "value").Length > 0 && !Bool(e, "password") ? Str(e, "value") : Str(e, "name");
        var items = all.Where(e => Under(e) && Str(e, "role") is "listitem" or "dataitem" or "treeitem").ToList();
        var itemIndexes = items.Select(e => (int)Number(e, "index")).ToHashSet();
        var rows = new List<List<string>>();
        // Cells grouped in row containers (DataGridView: custom "Row 0" holding one data item per column), else one row
        // per item with one cell per child (WinForms ListView, Task Manager).
        var grouped = items.Where(e => Parent(e) != within && !itemIndexes.Contains(Parent(e))).GroupBy(Parent).ToList();
        if (grouped.Count > 0 && grouped.All(g => g.Count() > 1) && grouped.Sum(g => g.Count()) == items.Count)
            rows = grouped.Select(g => g.Select(Text).ToList()).ToList();
        else foreach (var item in items)
        {
            var index = (int)Number(item, "index");
            var cells = all.Where(e => Parent(e) == index && Str(e, "role") is not ("listitem" or "dataitem" or "treeitem" or "image")).Select(Text).ToList();
            if (cells.Count == 0 || Str(item, "name").Length > 0 && !cells.Contains(Str(item, "name"))) cells.Insert(0, Text(item));
            rows.Add(cells);
        }
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        // Column headers: the first container of header items that holds no row (row headers sit inside their rows).
        var rowParents = items.Select(Parent).ToHashSet();
        var headers = all.Where(e => Under(e) && Str(e, "role") is "header" or "headeritem" && !rowParents.Contains(Parent(e)))
            .GroupBy(Parent).Where(g => g.Count() > 1).Select(g => g.Select(e => Str(e, "name")).ToList()).FirstOrDefault() ?? [];
        if (headers.Count == columns + 1) headers.RemoveAt(0); // top-left corner cell of a grid with row headers
        return new JsonObject { ["source"] = "tree", ["headers"] = new JsonArray(headers.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["rows"] = new JsonArray(rows.Take(maximum).Select(r => (JsonNode)new JsonArray(r.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())).ToArray()),
            ["rowCount"] = rows.Count, ["columnCount"] = Math.Max(headers.Count, columns) };
    }
    internal static List<string> TableLines(JsonObject table)
    {
        // Bidirectional marks (Explorer dates) are dropped: invisible, they only cost characters.
        static string Cell(JsonNode? c) => Truncate(Regex.Replace(c?.GetValue<string>() ?? "", "[\u200E\u200F\u202A-\u202E]", ""), 60).Replace(" | ", " / ");
        var lines = new List<string>();
        if (table["headers"] is JsonArray { Count: > 0 } headers) lines.Add("headers: " + string.Join(" | ", headers.Select(Cell)));
        var n = 0;
        foreach (var row in table["rows"]!.AsArray().OfType<JsonArray>()) lines.Add($"{++n}. " + string.Join(" | ", row.Select(Cell)));
        if (n == 0) lines.Add("No row.");
        return lines;
    }
    // Named containers holding a large part of the window: the places worth a look within=N.
    static List<string> Regions(JsonObject snapshot, Dictionary<int, int> descendants)
    {
        var all = Elements(snapshot).OfType<JsonObject>().ToList();
        var regions = all.Select((e, order) => (Element: e, Order: order, Count: descendants.GetValueOrDefault((int)Number(e, "index", -1))))
            .Where(r => r.Count >= 8 && r.Count < all.Count * 9 / 10 && (Str(r.Element, "name").Length > 0 || Str(r.Element, "automationId").Length > 0))
            .OrderByDescending(r => r.Count).Take(12).OrderBy(r => r.Order);
        return regions.Select(r => $"[{r.Element["index"]}] {r.Element["role"]} \"{Truncate(Str(r.Element, "name").Length > 0 ? Str(r.Element, "name") : Str(r.Element, "automationId"), 40)}\" ({r.Count})").ToList();
    }
    static string Truncate(string text, int length) => text.Length <= length ? text.ReplaceLineEndings(" ") : text[..length].ReplaceLineEndings(" ") + "…";
    // Listed elements without defaults (null, false, empty) nor internal identities: the full record stays in the
    // observation acts resolve against, and with detail="full" (31/10: ~400 characters per element, mostly empty fields).
    static JsonObject CompactElement(JsonObject element)
    {
        var compact = new JsonObject { ["index"] = element["index"]?.DeepClone(), ["role"] = element["role"]?.DeepClone() };
        foreach (var field in new[] { "name", "automationId", "description", "accessKey" }) if (Str(element, field).Length > 0) compact[field] = Str(element, field);
        if (element["value"] != null && !Bool(element, "password")) compact["value"] = element["value"]!.DeepClone();
        foreach (var field in new[] { "checked", "selected", "expanded" }) if (element[field] is JsonValue state) compact[field] = state.DeepClone();
        foreach (var field in new[] { "disabled", "focused", "password", "valueTruncated", "isDefault", "invoke", "ocr" }) if (Bool(element, field)) compact[field] = true;
        foreach (var field in new[] { "actions", "flags" }) if (element[field] is JsonArray { Count: > 0 } list) compact[field] = list.DeepClone();
        if (element["bounds"] is JsonArray { Count: 4 } bounds) compact["bounds"] = bounds.DeepClone();
        if (element["ancestors"] is JsonArray { Count: > 0 } ancestors) compact["parent"] = ancestors[^1]!.DeepClone();
        return compact;
    }
    static JsonObject ObserveBoundNative(SessionState state, JsonObject args, bool modern, (long Id, string App, string Title, long Pid) expected, string birth)
    {
        var previous = BoundObservationIdentity;
        try
        {
            if (birth.Length == 0) throw new ActionRefusedException("Observed launch process has no birth identity; no observation returned");
            BoundObservationIdentity = (expected.Id, birth);
            return ObserveCompatibleNative(state, args, modern, expected, birth);
        }
        finally { BoundObservationIdentity = previous; }
    }
    static JsonObject ObserveCompatibleNative(SessionState state, JsonObject args, bool modern, (long Id, string App, string Title, long Pid)? expected = null, string? expectedBirth = null)
    {
        args = args.DeepClone().AsObject();
        var screen = Str(args, "window").Trim().ToLowerInvariant();
        if (screen is "screen" or "écran" or "ecran") return FullScreenNative(state, args);
        // mode="text" is the tree (the published schema of the three profiles); show="text" is the document text.
        var nativeText = args["show"] == null && Str(args, "mode") == "text";
        var show = nativeText ? "elements" : Str(args, "show", Str(args, "mode", "auto"));
        if (show is not ("auto" or "both" or "image" or "elements" or "text" or "changes" or "none" or "table")) throw new InvalidOperationException("Unknown look presentation");
        var prior = state.BoundWindow; var target = ResolveWindow(state, args, modern);
        if (expected is { } known && (target.Id != known.Id || target.Pid != known.Pid || !target.App.Equals(known.App, StringComparison.OrdinalIgnoreCase) || Program.ProcessStartedUtc((uint)target.Pid) != expectedBirth))
            throw new ActionRefusedException("The launched window/process was replaced before observation; no screen returned");
        if (prior?.Id != target.Id) { state.RetainedFocus = null; state.Image = null; state.RegionImage = null; state.Pending = null; state.CurrentObservation = null; state.CurrentObservationId = null; }
        if (show == "none" && args["routine_info"] == null) return new JsonObject { ["content"] = new JsonArray { TextBlock("No observation requested; previous displayed indices retained.") } };
        state.CurrentObservation = null; state.CurrentObservationId = null;
        var snapshot = InspectNative(state); var memory = LookMemory(state, target.Id, snapshot);
        PublishNativeIndices(state, snapshot, memory);
        var nativeCount = Elements(snapshot).Count;
        var minimized = Win32.IsIconic((nint)target.Id);
        var wantsImage = !minimized && (show is "both" or "image" || args["region"] != null || args["zoom"] != null || Bool(args, "capture_screenshot") || Bool(args, "annotate") || args["output_path"] != null || (show == "auto" || nativeText) && nativeCount < 6 && !(args.ContainsKey("capture_screenshot") && !Bool(args, "capture_screenshot")));
        var needsOcr = !minimized && nativeCount < 6 && show is not ("text" or "none" or "table");
        JsonObject? full = null, shot = null;
        if (wantsImage || needsOcr)
        {
            var marks = Bool(args, "annotate") ? "all" : Str(args, "marks", "auto");
            full = ReadNativeCapture(state, snapshot, marks: needsOcr ? "none" : marks);
            if (needsOcr) AppendNativeOcr(state, snapshot, full, memory);
            if (needsOcr && wantsImage && marks != "none") full = ReadNativeCapture(state, snapshot, marks: marks);
            if (args["region"] is JsonArray region)
            {
                var basis = memory.Image ?? full;
                if (args["image"] != null && Str(args, "image") != state.Image?.Id) throw new ActionRefusedException("Image id does not match the current image");
                if (basis["windowFrame"] is not JsonObject old || !SameFrameSize(old, snapshot["frame"]!.AsObject())) { args.Remove("region"); shot = full; }
                else { var movedBasis = basis.DeepClone().AsObject(); movedBasis["frame"] = TranslateFrame(basis["frame"]!.AsObject(), old, snapshot["frame"]!.AsObject()); shot = ReadNativeCapture(state, snapshot, ImageRegion(movedBasis, region, false)); }
            }
            else if (args["zoom"] is JsonArray zoom) shot = ReadNativeCapture(state, snapshot, ImageRegion(snapshot, zoom, true));
            else shot = full;
        }
        var id = state.NextObservationId(); var shotId = "img-" + id;
        if (shot != null && wantsImage)
        {
            // A full image takes points without an id; a region takes the points that carry its own screenshotId.
            if (args["region"] == null) { state.Image = new NativeImage(shotId, target.Id, shot); memory.Image = shot.DeepClone().AsObject(); state.RegionImage = null; }
            else state.RegionImage = new NativeImage(shotId, target.Id, shot);
        }
        var reference = memory.Image;
        if (reference == null && full != null) reference = full;
        if (reference?["windowFrame"] is JsonObject referenceFrame)
        {
            if (snapshot["frame"] is not JsonObject currentFrame || !SameFrameSize(referenceFrame, currentFrame)) reference = null;
            else { reference = reference.DeepClone().AsObject(); reference["frame"] = TranslateFrame(reference["frame"]!.AsObject(), referenceFrame, snapshot["frame"]!.AsObject()); }
        }
        var query = Str(args, "query");
        // 150 lines by default for the three profiles (the rest is summarized with its regions), up to 2000 on request.
        var maximum = (int)Math.Clamp(Number(args, "max_elements", 150), 10, 2000);
        bool Wanted(JsonObject e) => query.Length == 0 || Str(e, "name").Contains(query, StringComparison.OrdinalIgnoreCase) || Str(e, "value").Contains(query, StringComparison.OrdinalIgnoreCase);
        // Dense windows (Claude, Slack, Teams: hundreds of nested unnamed panes): every profile reads a skeleton first, then one
        // region with within=N, like agent-desktop's --skeleton/--root (31/10). Unnamed containers stay targetable by name
        // of their children and come back with detail="full".
        var within = args["within"] != null ? (int)Number(args, "within") : -1;
        if (within >= 0 && !Elements(snapshot).OfType<JsonObject>().Any(e => Number(e, "index", -1) == within)) throw new ActionRefusedException("within: element absent from current observation; look again");
        var parents = ParentCounts(snapshot);
        var skeleton = Str(args, "detail") != "full";
        bool Inside(JsonObject e) => within < 0 || Number(e, "index", -1) == within || (e["ancestors"] as JsonArray)?.Any(i => i!.GetValue<int>() == within) == true;
        bool Noise(JsonObject e) => skeleton && EmptyContainer(e, parents);
        var hidden = Elements(snapshot).OfType<JsonObject>().Count(e => Wanted(e) && Inside(e) && Noise(e));
        // Every element's line is remembered, whatever this look lists: "changes" then compares the whole window state,
        // even after a narrow query (31/10: a filtered look followed by changes listed everything as new).
        var allLines = Elements(snapshot).OfType<JsonObject>().ToDictionary(e => (int)Number(e, "index"), e => NativeLine(e, reference));
        var previousLines = memory.Lines;
        var changed = show == "changes" && previousLines.Count > 0;
        var candidates = Elements(snapshot).OfType<JsonObject>().Where(e => Wanted(e) && Inside(e) && !Noise(e))
            .Where(e => !changed || !previousLines.TryGetValue((int)Number(e, "index"), out var old) || old != allLines[(int)Number(e, "index")]).ToList();
        var listed = candidates.Take(maximum).ToList();
        var lines = listed.ToDictionary(e => (int)Number(e, "index"), e => allLines[(int)Number(e, "index")]);
        var removed = changed ? previousLines.Where(p => !allLines.ContainsKey(p.Key) && (query.Length == 0 || p.Value.Contains(query, StringComparison.OrdinalIgnoreCase))).Select(p => "- " + p.Value).ToList() : [];
        var rendered = changed ? lines.Select(p => (previousLines.ContainsKey(p.Key) ? "~ " : "+ ") + p.Value).Concat(removed).ToList() : lines.Values.ToList();
        if (changed && rendered.Count == 0) rendered = ["No visible element change since the previous look."];
        if (show == "text") rendered = [Str(snapshot["accessibility"], "document_text")];
        if (show is "none" or "image") rendered.Clear();
        JsonObject? table = null;
        if (show == "table")
        {
            if (within < 0) throw new ActionRefusedException("show=\"table\" needs within=N: the list, grid or table to read");
            table = NativeTable(state, snapshot, within, maximum);
            rendered = TableLines(table);
            listed.Clear();
        }
        // Indices displayed by an earlier look stay usable after a "changes" look that lists only what moved.
        memory.Lines = allLines; memory.Shown = table != null ? [within] : changed ? memory.Shown.Where(allLines.ContainsKey).Concat(lines.Keys).ToHashSet() : listed.Select(e => (int)Number(e, "index")).ToHashSet();
        if (shot?["marks"]?["drawn"] is JsonArray drawn && wantsImage) foreach (var shown in drawn) memory.Shown.Add(shown!.GetValue<int>());
        var routines = new NativeRoutineMemory(state.Agent, target.App); routines.Sync();
        var data = snapshot.DeepClone().AsObject(); data["observation"] = id; data["window"] = target.Id; data["hwnd"] = target.Id; data["title"] = target.Title; data["routines"] = routines.List();
        // The structured result is what a client such as Claude Code shows the model, image included: the full tree and
        // document text (~25,000 tokens on a long web page, Chrome Web Store dashboard, 31/10) only with detail="full" or
        // show="text"; an image or a filtered look carries only the elements it lists and a short summary.
        data["elements"] = new JsonArray((show is "none" or "image" ? [] : listed).Select(e => Str(args, "detail") != "full" ? CompactElement(e) : e.DeepClone()).ToArray());
        if (Str(args, "detail") != "full" && show != "text" && data["accessibility"] is JsonObject accessibility)
        {
            accessibility.Remove("tree"); accessibility.Remove("document_text"); accessibility.Remove("document_truncated");
        }
        var total = Elements(snapshot).Count;
        if (table != null) data["table"] = table;
        data["summary"] = table != null ? $"table [{within}]: {table["rows"]!.AsArray().Count} of {table["rowCount"]} row(s), {table["columnCount"]} column(s), read by {table["source"]}" +
            (Number(table, "rowCount") > table["rows"]!.AsArray().Count ? "; more rows with max_elements" : "") + "." : (show is "none" or "image" ? $"{total} elements in the window, none listed with show=\"{show}\""
            : changed ? $"{listed.Count} added or changed element(s), {removed.Count} removed" : $"{listed.Count} of {total} element(s) listed")
            + (query.Length > 0 ? $" for query \"{query}\"" : "") + (within >= 0 ? $" within [{within}]" : "")
            + (hidden > 0 ? $"; {hidden} empty container(s) hidden" : "")
            + (candidates.Count > listed.Count ? $"; {candidates.Count - listed.Count} more: narrow with query, within or max_elements" : "")
            + (skeleton && within < 0 && query.Length == 0 && candidates.Count > listed.Count && Regions(snapshot, parents) is { Count: > 0 } regions ? "; regions (look within=N): " + string.Join(", ", regions) : "")
            + (show == "text" || Str(args, "detail") == "full" ? "" : "; full tree and document text with detail=\"full\"") + ".";
        var routineInfo = Str(args, "routine_info"); if (routineInfo.Length > 0) { var entry = routines.Read(routineInfo) ?? throw new InvalidOperationException("Routine not found"); data["routineDefinition"] = entry.DeepClone(); state.ReviewedRoutines[ReviewKey(target.App, routineInfo)] = RoutineStamp(entry); }
        var text = $"window {target.Id} \"{target.Title}\" · observation {id}\n" + string.Join('\n', rendered);
        if (show is not ("none" or "image" or "text")) text += "\n" + Str(data, "summary");
        if (minimized) text += "\nWindow minimized: list only; restore explicitly before requesting an image.";
        var note = state.Accords.Read($"window {target.Id} \"{target.Title}\"", text); if (note.Length > 0) text += "\n" + note;
        var content = new JsonArray { Str(args, "format") == "json" ? TextBlock(data) : TextBlock(text) };
        if (shot != null && wantsImage)
        {
            var bytes = File.ReadAllBytes(Str(shot, "path"));
            if (args["output_path"] != null) { var output = Path.GetFullPath(Str(args, "output_path")); Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(Str(shot, "path"), output, true); data["output_path"] = output; }
            var areaKey = (args["region"] ?? args["zoom"])?.ToJsonString() ?? "full";
            var print = Routines.Digest(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + shot["frame"]!.ToJsonString());
            data["image"] = shotId; data["capture"] = shot.DeepClone();
            if (show != "image" && memory.Sent.TryGetValue(areaKey, out var oldPrint) && oldPrint == print) content.Add(TextBlock("Image unchanged: same pixels and frame; show=image resends it."));
            else { content.Add(TextBlock($"image {shotId}; {shot.ToJsonString(JsonOut)}")); content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = "image/webp" }); memory.Sent[areaKey] = print; }
        }
        state.CurrentObservation = JsonSerializer.SerializeToElement(snapshot); state.CurrentObservationId = id; state.ObservedAt = DateTimeOffset.UtcNow;
        return new JsonObject { ["content"] = content, ["structuredContent"] = data };
    }
    static bool SameFrameSize(JsonObject a, JsonObject b) => Number(a, "width") == Number(b, "width") && Number(a, "height") == Number(b, "height");
    static string NormalizeLegacyText(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
    static bool LegacyStateCheck(JsonObject expect) => expect["all"] is JsonArray all ? all.OfType<JsonObject>().All(LegacyStateCheck) : expect["selector"] != null && expect["exists"] != null;
    static void CheckShownExpectationIndices(SessionState state, JsonObject expectation)
    {
        if (expectation["all"] is JsonArray all) foreach (var part in all.OfType<JsonObject>()) CheckShownExpectationIndices(state, part);
        if (IsLegacyIndex(expectation["element"]) && int.TryParse(expectation["element"]!.ToString().Trim().Trim('#', '[', ']'), out var index)
            && !LookMemory(state, state.BoundWindow!.Value.Id).Shown.Contains(index)) throw new ActionRefusedException("Postcondition index was not displayed in the previous look; use an exact visible name");
    }
    static void ValidateCurrentOcr(SessionState state, JsonObject snapshot, JsonObject observed)
    {
        var prior = observed["ocrFrame"]?.AsObject() ?? throw new ActionRefusedException("OCR target has no observed frame");
        var frame = snapshot["frame"]!.AsObject();
        if (!SameFrameSize(prior, frame)) throw new ActionRefusedException("Window resized since OCR; look again before input");
        var capture = ReadNativeCapture(state, snapshot); var request = NativeRequest(state, "ocr"); request["path"] = Str(capture, "path");
        var current = ObjectOf(ExecuteInternal(request)); var bounds = observed["bounds"]!.AsArray();
        var left = bounds[0]!.GetValue<double>() + Number(frame, "left") - Number(prior, "left");
        var top = bounds[1]!.GetValue<double>() + Number(frame, "top") - Number(prior, "top");
        var found = (current["lines"] as JsonArray ?? new()).OfType<JsonObject>().Where(line => Str(line, "text") == Str(observed, "name") && Number(line, "left") == left && Number(line, "top") == top && Number(line, "width") == bounds[2]!.GetValue<double>() && Number(line, "height") == bounds[3]!.GetValue<double>()).ToList();
        if (found.Count != 1) throw new ActionRefusedException("The observed OCR text moved, changed or became ambiguous; no input sent");
    }
    static JsonObject PrepareNativeTarget(SessionState state, JsonObject step, JsonObject before, Action beforeInput)
    {
        if (step["ocrTarget"] != null) return before;
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        if (selector == null) return before;
        // A shown index keeps its observed identity: an off-screen one (long web page) is scrolled into view like a named target.
        var already = step["observedIdentity"] != null ? [StepTarget(before, step, selector)] : FindNative(before, selector);
        if (already.Count > 1) throw new ActionRefusedException("Ambiguous target cannot be materialized");
        if (already.Count == 1 && Bool(already[0], "visible")) return before;
        if (already.Count == 1)
        {
            var scroll = NativeRequest(state, "secondary"); scroll["selector"] = SelectorOf(already[0]); scroll["action"] = "ScrollIntoView"; AddIdentity(scroll, already[0], before);
            var entered = false;
            try
            {
                ExecuteInternal(scroll); entered = Program.InputStarted; if (entered) beforeInput();
                // Chromium publishes the new geometry asynchronously: read back until the same target is visible (1.618 s at most).
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    var fresh = InspectNative(state);
                    var moved = step["observedIdentity"] != null ? StepTarget(fresh, step, selector) : UniqueNative(fresh, selector);
                    if (Bool(moved, "visible")) return fresh;
                    if (timer.ElapsedMilliseconds >= 1618) throw new ActionRefusedException("Target still out of view after scrolling; inspect again");
                    EmergencyStop.ThrowIfStopped(); Thread.Sleep(80);
                }
            }
            catch { if (entered || Program.InputStarted) { beforeInput(); Program.InputStarted = true; } throw; }
        }
        if (Str(selector, "name").Length == 0) throw new ActionRefusedException("Only an exact named target can be materialized in a virtualized list");
        var request = NativeRequest(state, "realizeNamed"); request["selector"] = selector.DeepClone();
        if (Str(selector, "role").StartsWith("legacy/", StringComparison.Ordinal)) request["selector"]!.AsObject().Remove("role");
        JsonObject result;
        try { result = ObjectOf(ExecuteInternal(request)); }
        catch { if (Program.InputStarted) beforeInput(); throw; }
        var sent = Bool(result, "inputSent"); if (sent) beforeInput();
        if (!Bool(result, "found")) return before;
        try
        {
            var fresh = InspectNative(state); var target = UniqueNative(fresh, selector);
            if (!Bool(target, "visible")) throw new ActionRefusedException("Materialized target is still not visible; observe again");
            return fresh;
        }
        catch { if (sent) Program.InputStarted = true; throw; }
    }
    static JsonObject TranslateFrame(JsonObject frame, JsonObject old, JsonObject current) => new() { ["left"] = Number(frame, "left") + Number(current, "left") - Number(old, "left"), ["top"] = Number(frame, "top") + Number(current, "top") - Number(old, "top"), ["width"] = Number(frame, "width"), ["height"] = Number(frame, "height") };
    static JsonObject FinishLegacyAct(SessionState state, JsonObject args, JsonObject result)
    {
        // The three profiles get the updated window in the same answer unless then="none" (one call instead of act + look).
        if (result["structuredContent"]?["forgotten"] != null || result["structuredContent"]?["operation"] != null || Str(args, "then", "auto") == "none" || state.BoundWindow is not { } target || !Win32.IsWindow((nint)target.Id)) return result;
        var status = Str(result["structuredContent"], "status");
        try
        {
            var delay = (int)Math.Clamp(Number(args, "settle_ms", 300), 0, 5000);
            var timer = Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < delay) { EmergencyStop.ThrowIfStopped(); Thread.Sleep((int)Math.Min(50, delay - timer.ElapsedMilliseconds)); }
            var presentation = Str(args, "then", "auto"); if (status is "unverified" or "failed" && presentation == "auto") presentation = "both";
            var observed = ObserveCompatibleNative(state, new JsonObject { ["window"] = target.Id, ["show"] = presentation }, false);
            foreach (var block in observed["content"]!.AsArray()) result["content"]!.AsArray().Add(block!.DeepClone());
            result["structuredContent"]!["observation"] = observed["structuredContent"]?.DeepClone();
        }
        catch (Exception error) { result["content"]!.AsArray().Add(TextBlock("Actions were not replayed. Final observation unavailable: " + error.Message)); result["structuredContent"]!["observationError"] = error.Message; }
        return result;
    }
    static JsonObject RunClaudeCompatibleAct(SessionState state, JsonObject args, bool modern)
    {
        var controls = RoutineCompatibilityArgs(state.Agent, args);
        if (PlanShellAct(args) == null && Str(controls, "operation", "run") == "run")
        {
            var prior = state.BoundWindow;
            var named = ResolveWindow(state, args, modern);
            if (state.CurrentObservation == null || prior?.Id != named.Id || DateTimeOffset.UtcNow - state.ObservedAt > TimeSpan.FromSeconds(60))
            {
                // A first semantic act is historically supported. It does not publish an image or any index.
                var snapshot = InspectNative(state); var memory = LookMemory(state, named.Id, snapshot);
                // An image of this same window (look or act then="image") stays usable for points: NativePoint still refuses
                // it when the window moved or was resized, and the element under the point is read again before input.
                memory.Shown.Clear(); state.Pending = null; state.RetainedFocus = null;
                if (state.Image?.Window != named.Id) { memory.Image = null; state.Image = null; state.RegionImage = null; }
                PublishNativeIndices(state, snapshot, memory);
                state.CurrentObservation = JsonSerializer.SerializeToElement(snapshot); state.CurrentObservationId = state.NextObservationId(); state.ObservedAt = DateTimeOffset.UtcNow;
            }
        }
        return RunShellAct(state, args, modern);
    }
    static int LegacyObservationSelfTest()
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
        for (var i = 0; i < 10; i++) dense["elements"]!.AsArray().Add(new JsonObject { ["index"] = 10 + i, ["role"] = "listitem", ["name"] = $"m{i}", ["ancestors"] = new JsonArray(0, 1, 2) });
        var counts = ParentCounts(dense); var items = Elements(dense).OfType<JsonObject>().ToList();
        Check(EmptyContainer(items[1], counts) && !EmptyContainer(items[2], counts) && !EmptyContainer(items[3], counts), "only an unnamed container holding elements is a layout wrapper");
        Check(Regions(dense, counts).SequenceEqual(new[] { "[2] list \"Messages\" (10)" }), "a named container with most items is the region to look within");
        var full = JsonNode.Parse("""{"index":7,"role":"button","name":"OK","automationId":"","value":null,"disabled":false,"focused":true,"checked":null,"selected":false,"password":false,"runtimeId":[42,7],"nativeWindow":5,"ancestors":[1,4],"bounds":[1,2,3,4],"actions":[],"flags":["offscreen"]}""")!.AsObject();
        var compact = CompactElement(full);
        Check(JsonNode.DeepEquals(compact, JsonNode.Parse("""{"index":7,"role":"button","name":"OK","selected":false,"focused":true,"flags":["offscreen"],"bounds":[1,2,3,4],"parent":4}""")), "compact element keeps states and drops defaults and internal identities");
        var secret = full.DeepClone().AsObject(); secret["password"] = true; secret["value"] = "hunter2";
        Check(CompactElement(secret)["value"] == null, "a password value is never listed");
        return assertions;
    }
}
