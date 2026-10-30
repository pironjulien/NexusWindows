using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class McpServer
{
    static readonly string[] DebugStepFields = ["n", "index", "do", "action", "method", "technique", "status", "success", "verified", "ok", "already", "preuve", "error_type", "ms", "foregroundTaken", "foregroundReturned", "error", "message", "attempts"];
    static string? DebugDiagnostic(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        // Errors can echo a caller's key, target or typed text without quotes or punctuation.
        // Keep only known fixed diagnostic categories, never an arbitrary free-text suffix.
        foreach (var category in new[] { "Target not found", "Target missing", "Application identity changed", "Window identity changed", "Emergency stop", "Unknown modifier", "Unknown key", "Incomplete key combination", "User resumed", "Interaction budget expired" })
            if (message.StartsWith(category, StringComparison.OrdinalIgnoreCase)) return category;
        foreach (var kind in new[] { "ActionRefusedException", "AfterInputException", "BackgroundNotApplicableException", "EmergencyStopException", "InvalidOperationException", "ArgumentException", "IOException", "JsonException", "TimeoutException", "Win32Exception" })
            if (message == kind || message.StartsWith(kind + ":", StringComparison.Ordinal)) return kind;
        return "Diagnostic detail omitted; inspect status and technique";
    }
    static string? DebugAttempt(string? text)
    {
        if (text == null) return null;
        var verdict = Regex.Match(text, @"^(?<method>.+?) (?<verdict>[✓✗~=])(?:\s|$)");
        if (verdict.Success && KnownTechniques.Contains(verdict.Groups["method"].Value))
            return verdict.Groups["method"].Value + " " + verdict.Groups["verdict"].Value;
        return DebugDiagnostic(text);
    }
    // Closed list of historical technique names (some contain spaces), shared by attempts and method/technique fields.
    static readonly string[] KnownTechniques = ["BN_CLICKED", "CB_SETCURSEL", "LB_SETCURSEL", "CB_SHOWDROPDOWN", "TCM_SETCURFOCUS", "EM_REPLACESEL", "DevTools", "DevTools insertText", "DevTools wheel", "DevTools wheel (proven page)", "UIA Invoke", "UIA Invoke (open)", "UIA Toggle", "UIA Select", "UIA focus", "UIA Legacy", "UIA Value", "UIA ExpandCollapse", "UIA Scroll", "UIA ScrollItem", "UIA ScrollIntoView", "click by messages", "messages with simulated focus", "focus without activation", "keystrokes", "scroll bar lines", "simulated activation", "UIA Expand", "UIA Expand/Collapse", "UIA Legacy Value", "UIA context menu", "UIA default action", "WM_COMMAND menu", "double-click by messages", "keystrokes without activation", "messages", "messages without activation", "right-click by messages", "wheel by messages", "wheel off screen"];
    static string? DebugCategorical(string field, string text)
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
    static JsonObject DebugFields(JsonObject source, IEnumerable<string> fields)
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
    static IEnumerable<string> DebugRoots(SessionState state)
    {
        yield return Path.Combine(NativeStateRoot(state.Agent), "logs");
        // A custom home is an isolation boundary: never read the user's history through it.
        if (state.Agent == "claude" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME")))
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "nexus-claude", "logs");
    }
    static string DebugJournalRoot(SessionState state)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME")) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME")))
            return Path.Combine(NativeStateRoot(state.Agent), "journal", "windows", state.Agent);
        return Journal.JournalRoot(state.Agent);
    }
    static JsonArray ReadDebugRecords(IEnumerable<string> roots, string journalRoot, int count, bool errorsOnly)
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
    static JsonObject LogsNative(SessionState state, JsonObject args)
    {
        var records = ReadDebugRecords(DebugRoots(state), DebugJournalRoot(state), (int)Math.Clamp(Number(args, "count", Number(args, "limit", 15)), 1, 100), Bool(args, "errors_only"));
        var status = new JsonArray($"Server nexus-windows {Version}, pid {Environment.ProcessId}: native common engine", $"Emergency stop: {Math.Ceiling(EmergencyStop.RemainingSeconds())} s remaining",
            "Native diagnostics and preserved historical records; typed text and authorization values are omitted.");
        return NativeResponse(new JsonObject { ["version"] = Version, ["pid"] = Environment.ProcessId, ["status"] = status, ["records"] = records });
    }
    static void WriteDebugRecord(SessionState state, string name, JsonObject args, JsonObject? response, long milliseconds, Exception? error = null)
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
            var ok = error == null && !Bool(response, "isError") && (paused || !explicitFailure && !unresolved);
            var tool = CanonicalNormalizeToolName(name);
            if (!new[] { "windows", "look", "act", "launch", "window", "notifications", "logs" }.Contains(tool)) tool = "invalid";
            var raw = new JsonObject { ["ts"] = DateTimeOffset.UtcNow.ToString("O"), ["tool"] = tool, ["ms"] = milliseconds, ["ok"] = ok,
                ["error"] = error != null ? error.GetType().Name : ok ? null : DebugDiagnostic(Str(data, "status", "Unverified result")), ["args"] = args.DeepClone(), ["steps"] = data?["steps"]?.DeepClone() ?? new JsonArray() };
            var record = DebugRecord(raw);
            var directory = Path.Combine(NativeStateRoot(state.Agent), "logs"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.jsonl"), record.ToJsonString(JsonOut) + "\n", new UTF8Encoding(false));
        }
        catch { /* Diagnostic persistence never changes or repeats the operation. */ }
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
    public static int LogsSelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-logs-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root);
            var historical = Path.Combine(root, "historical"); Directory.CreateDirectory(historical);
            var legacy = """{"ts":"2026-10-29T12:00:00Z","tool":"act","ms":9,"ok":false,"error":"Target missing: \"private-target\"","args":{"text":"typed-secret","accord":"authorization-secret","foreground":false},"steps":[{"do":"click","method":"uia:Invoke","attempts":[{"method":"win32","text":"attempt-secret"}]}]}""";
            var historicalFile = Path.Combine(historical, "2026-10-29.jsonl"); File.WriteAllText(historicalFile, legacy + "\n{partial\n");
            var before = File.ReadAllBytes(historicalFile);
            var state = new SessionState("claude");
            WriteDebugRecord(state, "act", new JsonObject { ["text"] = "new-typed-secret" }, new JsonObject { ["structuredContent"] = new JsonObject { ["success"] = true, ["steps"] = new JsonArray(new JsonObject { ["technique"] = "background:win32", ["status"] = "verified", ["text"] = "step-secret" }) } }, 3);
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            File.WriteAllText(Path.Combine(root, "journal", "2026-10.jsonl"), """{"ts":"2026-10-29T12:00:00Z","action":"click","issue":"~","statut":"unknown"}""" + "\n");
            var roots = new[] { historical, Path.Combine(root, "logs") };
            var all = ReadDebugRecords(roots, Path.Combine(root, "journal"), 10, false);
            var errors = ReadDebugRecords(roots, Path.Combine(root, "journal"), 1, true);
            var serialized = all.ToJsonString();
            void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException("Logs selftest: " + reason); }
            Assert(all.Count == 2 && errors.Count == 1 && !Bool(errors[0], "ok"), "legacy/native merge and errors_only");
            Assert(Number(all[0], "ms", -1) == 9 && Number(all[1], "ms", -1) == 3 && Number(DebugFields(new JsonObject { ["index"] = 7 }, DebugStepFields), "index", -1) == 7, "durations and native numeric step identifiers retained");
            Assert(ReadDebugRecords(roots, Path.Combine(root, "journal"), 1, false).Count == 1, "count");
            Assert(serialized.Contains("uia:Invoke") && serialized.Contains("background:win32") && serialized.Contains("win32"), "diagnostic techniques retained");
            Assert(!serialized.Contains("secret") && !serialized.Contains("private-target"), "typed data and permissions excluded");
            Assert(before.SequenceEqual(File.ReadAllBytes(historicalFile)), "legacy history is read-only");
            Assert(DebugRoots(state).SequenceEqual([Path.Combine(root, "logs")]), "custom home isolated");
            Assert(ReadDebugRecords([], Path.Combine(root, "journal"), 10, false).Count == 1, "journal fallback without full history");
            WriteDebugRecord(state, "act", new JsonObject(), null, 1, new InvalidOperationException("Unknown modifier typed-secret"));
            foreach (var tool in new[] { "launch", "window" }) WriteDebugRecord(state, tool, new JsonObject(), new JsonObject { ["structuredContent"] = new JsonObject { ["status"] = "unknown", ["opened"] = null, ["verified"] = false } }, 2);
            WriteDebugRecord(state, "act", new JsonObject(), new JsonObject { ["structuredContent"] = new JsonObject { ["status"] = "paused", ["paused"] = true, ["success"] = false, ["continuation"] = "fixture-token" } }, 3);
            var final = ReadDebugRecords(roots, Path.Combine(root, "journal"), 20, false);
            Assert(final.Count == 6 && ReadDebugRecords(roots, Path.Combine(root, "journal"), 20, true).Count == 4, "unknown is a diagnostic failure; normal pause is not");
            Assert(!final.ToJsonString().Contains("typed-secret"), "unquoted exception content excluded by the real writer");
            Assert(DebugFields(new JsonObject { ["technique"] = "UIA Invoke", ["method"] = "messages without activation" }, DebugStepFields).ToJsonString() == """{"method":"messages without activation","technique":"UIA Invoke"}""", "historical technique names with spaces are kept");
            Assert(DebugDiagnostic("Unknown modifier typed-secret") == "Unknown modifier" && !DebugFields(new JsonObject { ["message"] = "unquoted-step-secret", ["attempts"] = new JsonArray("unquoted-attempt-secret") }, DebugStepFields).ToJsonString().Contains("secret"), "step and attempt diagnostic content excluded");
            var historicalVerdicts = DebugFields(new JsonObject { ["ok"] = true, ["already"] = true, ["preuve"] = "unquoted-proof-secret", ["error_type"] = "TimeoutException" }, DebugStepFields);
            Assert(Bool(historicalVerdicts, "ok") && Bool(historicalVerdicts, "already") && Str(historicalVerdicts, "error_type") == "TimeoutException" && !historicalVerdicts.ToJsonString().Contains("secret"), "legacy verdicts retained and proof text redacted");
            Assert(DebugAttempt("UIA Invoke ✗ unquoted-attempt-secret") == "UIA Invoke ✗" && DebugAttempt("DevTools insertText ✓") == "DevTools insertText ✓" && !DebugAttempt("unknown-attempt-secret ✗ private")!.Contains("secret"), "real legacy attempt strings retain known technique/verdict only");
            foreach (var proof in new[] { "auto", "invoque", "absente", "impossible" }) Assert(Str(DebugFields(new JsonObject { ["preuve"] = proof }, DebugStepFields), "preuve") == proof, "fixed legacy proof category retained");
            WriteDebugRecord(state, "window", new JsonObject { ["action"] = "unquoted-private-value", ["show"] = "private-show", ["direction"] = "private-direction" }, null, 1, new ActionRefusedException("invalid private input"));
            Assert(!ReadDebugRecords(roots, Path.Combine(root, "journal"), 30, false).ToJsonString().Contains("private-value"), "unknown action values never persisted");
            Assert(!DebugFields(new JsonObject { ["foreground"] = "private-foreground", ["count"] = "private-count", ["ms"] = "private-duration" }, ["foreground", "count", "ms"]).ToJsonString().Contains("private-"), "invalid scalar strings never persisted");
            foreach (var value in new[] { "refused-before-input", "returned", "input-uncertain" }) Assert(DebugCategorical("status", value) == value, "fixed fallback status retained");
            foreach (var value in new[] { "not-taken", "refused", "held-for-interaction", "kept-requested" }) Assert(DebugCategorical("foregroundReturned", value) == value, "fixed foreground result retained");
            var journalHome = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME");
            try
            {
                Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", Path.Combine(root, "empty-isolated-home")); Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", null);
                Assert(DebugJournalRoot(state) == Path.Combine(root, "empty-isolated-home", "journal", "windows", "claude"), "custom home does not fall back to the user's shared journal");
                Assert(LogsNative(state, new JsonObject())["structuredContent"]!["records"]!.AsArray().Count == 0, "public logs route is isolated with an empty custom home");
            }
            finally { Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", root); Environment.SetEnvironmentVariable("NEXUS_JOURNAL_HOME", journalHome); }
            Console.WriteLine("{\"ok\":true,\"history\":true,\"readonly\":true,\"typedTextOmitted\":true,\"techniques\":true}"); return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_WINDOWS_HOME", previous);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected logs fixture path");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
