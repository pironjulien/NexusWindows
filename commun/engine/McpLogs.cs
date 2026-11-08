using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class McpServer
{
    internal static readonly string[] DebugStepFields = ["n", "index", "do", "action", "method", "technique", "status", "success", "verified", "ok", "already", "preuve", "error_type", "ms", "foregroundTaken", "foregroundReturned", "error", "message", "attempts"];
    internal static string? DebugDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (DebugCategorical("status", message) is { } status) return status;
        // Errors can echo a caller's key, target or typed text without quotes or punctuation.
        // Keep only known fixed diagnostic categories, never an arbitrary free-text suffix.
        foreach (var category in new[] { "Target not found", "Target missing", "Application identity changed", "Window identity changed", "Emergency stop", "Unknown modifier", "Unknown key", "Incomplete key combination", "User resumed", "Interaction budget expired",
            "Observation expired", "Observation already consumed or replaced", "Look at a named window first", "Look at the named window before acting", "Interface structure changed", "Semantic target count", "Ambiguous postcondition target", "Vague postcondition is already true", "An explicit postcondition is required", "Unknown postcondition field", "Unsupported postcondition field", "Empty postcondition", "Needs the foreground", "Window is disabled", "Window busy",
            "Input sent; no exact postcondition was supplied", "Postcondition not observed", "Current key delivery is uncertain", "Window command may have been sent", "Window application identity changed",
            "Target or its window is disabled", "Control identity changed", "Control ancestry changed", "Target is no longer present", "Target is no longer inside the window", "Target is off-screen",
            "Target still out of view", "Target exposes no checked state", "The named window differs from the last look", "Window moved or resized since the native capture",
            "A previous simulated press in this application is not finished", "Password controls require the dedicated supported workflow", "Password controls are refused", "Text input requires an editable field",
            "The field is read-only", "The field became disabled", "The command is not in the required open menu", "The Explorer view changed", "Routine target/context changed" })
            if (message.StartsWith(category, StringComparison.OrdinalIgnoreCase)) return category;
        foreach (var kind in new[] { "ActionRefusedException", "AfterInputException", "BackgroundNotApplicableException", "EmergencyStopException", "InvalidOperationException", "ArgumentException", "IOException", "JsonException", "TimeoutException", "Win32Exception" })
            if (message == kind || message.StartsWith(kind + ":", StringComparison.Ordinal)) return kind;
        return "Diagnostic detail omitted; inspect status and technique";
    }
    internal static string? DebugAttempt(string? text)
    {
        if (text == null) return null;
        var verdict = Regex.Match(text, @"^(?<method>.+?) (?<verdict>[✓✗~=])(?:\s|$)");
        if (verdict.Success && KnownTechniques.Contains(verdict.Groups["method"].Value))
            return verdict.Groups["method"].Value + " " + verdict.Groups["verdict"].Value;
        return DebugDiagnostic(text);
    }
    // Closed list of historical technique names (some contain spaces), shared by attempts and method/technique fields.
    static readonly string[] KnownTechniques = ["BN_CLICKED", "CB_SETCURSEL", "LB_SETCURSEL", "CB_SHOWDROPDOWN", "TCM_SETCURFOCUS", "EM_REPLACESEL", "DevTools", "DevTools insertText", "DevTools wheel", "DevTools wheel (proven page)", "UIA Invoke", "UIA Invoke (open)", "UIA Toggle", "UIA Select", "UIA focus", "UIA Legacy", "UIA Value", "UIA ExpandCollapse", "UIA Scroll", "UIA ScrollItem", "UIA ScrollIntoView", "click by messages", "messages with simulated focus", "focus without activation", "keystrokes", "scroll bar lines", "simulated activation", "UIA Expand", "UIA Expand/Collapse", "UIA Legacy Value", "UIA context menu", "UIA default action", "WM_COMMAND menu", "double-click by messages", "keystrokes without activation", "messages", "messages without activation", "right-click by messages", "wheel by messages", "wheel off screen"];
    internal static string? DebugCategorical(string field, string text)
    {
        string[] allowed = field switch
        {
            "action" or "do" => ["click", "set", "clear", "type", "setValue", "typeText", "key", "secondary", "select", "scroll", "hover", "drag", "wait", "focus", "menu", "contextMenu", "check", "uncheck", "double_click", "right_click", "expand", "collapse", "restore", "minimize", "maximize", "close", "bottom", "front", "move", "resize", "launch", "openSettings", "openFolder", "activate", "window"],
            "show" or "mode" => ["auto", "both", "image", "elements", "text", "changes", "none", "background", "foreground", "physical", "native"],
            "direction" => ["up", "down", "left", "right", "into_view"],
            "status" => ["success", "verified", "already", "already-satisfied", "waited", "unknown", "unverified", "unmet", "refused", "interrupted", "stopped", "deferred", "confirmation-required", "sent", "sent-unconfirmed", "paused", "before-input", "not-applicable", "acknowledged", "refused-before-input", "returned", "input-uncertain"],
            "foregroundReturned" => ["restored", "none", "kept-by-julien", "unchanged", "failed", "target-gone", "not-taken", "refused", "held-for-interaction", "kept-requested"],
            _ => [],
        };
        return allowed.Contains(text) ? text : null;
    }
    internal static JsonObject DebugFields(JsonObject source, IEnumerable<string> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            if (source[field] is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text)) result[field] = field is "action" or "do" or "show" or "mode" or "direction" or "status" or "foregroundReturned" ? DebugCategorical(field, text) :
                    field == "preuve" && new[] { "auto", "invoque", "absente", "impossible" }.Contains(text) ? text :
                    field is "error" or "message" or "preuve" or "error_type" ? DebugDiagnostic(text) :
                    field is "method" or "technique" && (KnownTechniques.Contains(text) || Regex.IsMatch(text, @"^[A-Za-z][A-Za-z0-9_.:+/-]{0,159}$")) ? text : null;
                else if (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number) result[field] = value.DeepClone();
            }
            else if (field == "attempts" && source[field] is JsonArray attempts)
                result[field] = new JsonArray(attempts.Take(50).Select(attempt => attempt is JsonObject entry ? (JsonNode)DebugFields(entry, DebugStepFields.Where(k => k != "attempts")) :
                    attempt is JsonValue v && v.TryGetValue<string>(out var text) ? JsonValue.Create(DebugAttempt(text)) : null).ToArray());
        }
        return result;
    }
    static JsonObject DebugRecord(JsonObject raw, bool journal = false)
    {
        var ok = journal ? Str(raw, "issue") is "✓" or "=" : Bool(raw, "ok");
        var steps = journal ? new JsonArray(new JsonObject { ["method"] = Str(raw, "technique"), ["status"] = Str(raw, "statut") }) :
            new JsonArray((raw["steps"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(step => (JsonNode)DebugFields(step, DebugStepFields)).ToArray());
        var number = Number(raw, "ms", double.NaN);
        var duration = double.IsFinite(number) ? JsonValue.Create(number) : null;
        return new JsonObject { ["ts"] = Str(raw, "ts"), ["tool"] = journal ? Str(raw, "action") : Str(raw, "tool"), ["ms"] = duration, ["ok"] = ok,
            ["error"] = ok ? null : JsonValue.Create(DebugDiagnostic(journal ? Str(raw, "erreur", Str(raw, "statut")) : Str(raw, "error"))),
            ["args"] = DebugFields(raw["args"] as JsonObject ?? new JsonObject(), ["foreground", "interaction", "count", "wait_s", "settle_ms", "show", "mode", "direction", "action"]), ["steps"] = steps };
    }
    internal static IEnumerable<string> DebugRoots(SessionState state)
    {
        yield return Path.Combine(NativeStateRoot(state.Agent), "logs");
        // A custom home is an isolation boundary: never read the user's history through it.
        if (state.Agent == "claude" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME")))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "nexus-claude", "logs");
    }
    internal static string DebugJournalRoot(SessionState state)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME")) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME")))
            return Path.Combine(NativeStateRoot(state.Agent), "journal", "windows", state.Agent);
        return Journal.JournalRoot(state.Agent);
    }
    internal static JsonArray ReadDebugRecords(IEnumerable<string> roots, string journalRoot, int count, bool errorsOnly)
    {
        var candidates = new List<(DateTimeOffset Time, int Sequence, JsonObject Record)>();
        var sequence = 0;
        foreach (var (directory, journal) in roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (path, false)).Append((journalRoot, true)))
        {
            // The shared journal has one row per gesture. Prefer full call records, otherwise an act batch
            // would occupy several of count's slots and appear twice. Keep the journal as migration fallback.
            if (journal && candidates.Count > 0) continue;
            if (!Directory.Exists(directory)) continue;
            var found = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(p => p, StringComparer.Ordinal))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var lines = new List<string>(); string? line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                for (var i = lines.Count - 1; i >= 0; i--)
                {
                    JsonObject? raw; try { raw = JsonNode.Parse(lines[i]) as JsonObject; } catch (JsonException) { continue; }
                    if (raw == null) continue;
                    var record = DebugRecord(raw, journal);
                    if (errorsOnly && Bool(record, "ok")) continue;
                    var time = DateTimeOffset.TryParse(Str(record, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp) ? stamp : DateTimeOffset.MinValue;
                    candidates.Add((time, sequence++, record));
                    if (++found >= count) break;
                }
                if (found >= count) break;
            }
        }
        return new JsonArray(candidates.OrderByDescending(item => item.Time).ThenBy(item => item.Sequence).Take(count).Reverse().Select(item => (JsonNode)item.Record).ToArray());
    }
    internal static JsonObject LogsNative(SessionState state, JsonObject args)
    {
        var records = ReadDebugRecords(DebugRoots(state), DebugJournalRoot(state), (int)Math.Clamp(Number(args, "count", Number(args, "limit", 15)), 1, 100), Bool(args, "errors_only"));
        var status = new JsonArray($"Server nexus-windows {Version}, pid {Environment.ProcessId}: native common engine", $"Emergency stop: {Math.Ceiling(EmergencyStop.RemainingSeconds())} s remaining",
            "Native diagnostics and preserved historical records; typed text and authorization values are omitted.");
        return NativeResponse(new JsonObject { ["version"] = Version, ["pid"] = Environment.ProcessId, ["status"] = status, ["records"] = records });
    }
    internal static void WriteDebugRecord(SessionState state, string name, JsonObject args, JsonObject? response, long milliseconds, Exception? error = null)
    {
        try
        {
            var data = response?["structuredContent"] as JsonObject;
            var status = Str(data, "status");
            var paused = status == "paused" && Bool(data, "paused") && !string.IsNullOrWhiteSpace(Str(data, "continuation"));
            var explicitFailure = data?["success"] is JsonValue success && success.TryGetValue<bool>(out var succeeded) && !succeeded ||
                data?["verified"] is JsonValue verified && verified.TryGetValue<bool>(out var proved) && !proved ||
                data?["opened"] is JsonValue opened && opened.TryGetValue<bool>(out var wasOpened) && !wasOpened;
            var unresolved = status is "unknown" or "unverified" or "unmet" or "refused" or "interrupted" or "stopped" or "deferred" or "confirmation-required" or "sent-unconfirmed";
            var tool = CanonicalNormalizeToolName(name);
            var ok = error == null && !Bool(response, "isError") && (paused || !explicitFailure && !unresolved || tool == "act" && SentWithoutExpectOnly(data));
            if (!new[] { "windows", "look", "act", "launch", "window", "notifications", "clipboard", "logs" }.Contains(tool)) tool = "invalid";
            var raw = new JsonObject { ["ts"] = DateTimeOffset.UtcNow.ToString("O"), ["tool"] = tool, ["ms"] = milliseconds, ["ok"] = ok,
                ["error"] = error != null ? DebugException(error) : ok ? null : DebugDiagnostic(Str(data, "status", "Unverified result")), ["args"] = args.DeepClone(), ["steps"] = data?["steps"]?.DeepClone() ?? new JsonArray() };
            var record = DebugRecord(raw);
            var directory = Path.Combine(NativeStateRoot(state.Agent), "logs"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), record.ToJsonString(JsonOut) + "\n", new UTF8Encoding(false));
        }
        catch { /* Diagnostic persistence never changes or repeats the operation. */ }
    }
    internal static string DebugException(Exception error)
    {
        var category = DebugDiagnostic(error.Message);
        return category == "Diagnostic detail omitted; inspect status and technique" ? error.GetType().Name : category ?? error.GetType().Name;
    }
    // A batch that ran every gesture and whose last one was sent without an expect is the normal outcome of that
    // request, not a failure: kept out of errors_only. Anything else stays a failure: a batch stopped early (its later
    // gestures never ran), an earlier step that failed, a paused routine, or a send whose outcome is truly unknown
    // (error_type, "Window command may have been sent"). The response itself still says unverified: look again.
    internal static bool SentWithoutExpectOnly(JsonObject? data)
    {
        if (data == null || Str(data, "status") != "unverified" || data["steps"] is not JsonArray steps || steps.Count == 0) return false;
        var total = Number(data["progress"] as JsonObject, "total", -1);
        if (total != steps.Count || Number(data["progress"] as JsonObject, "nextStep", -1) != steps.Count - 1) return false;
        var results = steps.OfType<JsonObject>().ToList();
        if (results.Count != steps.Count) return false;
        var last = results[^1];
        return Str(last, "status") == "sent-unconfirmed" && last["error_type"] == null &&
            Str(last, "message").StartsWith("Input sent; no exact postcondition was supplied", StringComparison.Ordinal) &&
            results.Take(results.Count - 1).All(step => Bool(step, "success"));
    }
    static JsonObject CallToolLogged(SessionState state, string name, JsonObject args, bool modern)
    {
        if (name == "logs") return CallTool(state, name, args, modern);
        var timer = Stopwatch.StartNew();
        try { var result = CallTool(state, name, args, modern); WriteDebugRecord(state, name, args, result, timer.ElapsedMilliseconds); return result; }
        catch (Exception error)
        {
            WriteDebugRecord(state, name, args, null, timer.ElapsedMilliseconds, error);
            if (Environment.GetEnvironmentVariable("NEXUS_WINDOWS_DEBUG_STACK") == "1") Console.Error.WriteLine(error.StackTrace);
            throw;
        }
    }
}
