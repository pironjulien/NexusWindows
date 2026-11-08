using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

// Shared journal of the three NexusWindows profiles (NexusShared/journal/FORMAT.md): one JSONL line per action under
// %USERPROFILE%\.nexus\journal\windows\<agent>\<YYYY-MM>.jsonl, with no screen text (no typed text, label or title).
static class Journal
{
    static readonly Dictionary<string, string> Issues = new(StringComparer.Ordinal)
    {
        ["verified"] = "✓",
        ["already"] = "=",
        ["already-satisfied"] = "=",
        ["waited"] = "✓",
        ["observed"] = "~",
        ["sent-unconfirmed"] = "~",
        ["unmet"] = "~",
        ["unknown"] = "~",
        ["interrupted"] = "~",
        ["failed"] = "✗",
        ["refused"] = "refus",
        ["confirmation-required"] = "refus",
        ["julien-active"] = "différé",
        ["deferred"] = "différé",
        ["stopped"] = "arrêt",
    };

    static readonly HashSet<string> ValidKinds = new(StringComparer.Ordinal)
    {
        "click", "setValue", "typeText", "key", "secondary", "scroll", "drag", "select", "menu", "contextMenu",
        "activate", "openSettings", "openFolder", "launch", "close", "hover", "wait", "window",
    };

    static readonly Regex CutRegex = new(@"[«""“'’(\[:{]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TrimPunctRegex = new(@"^[\s.,;-]+|[\s.,;-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string JournalRoot(string? agent = null)
    {
        var a = (agent ?? GardeFous.CurrentAgent).ToLowerInvariant();
        var baseDir = Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME");
        // ~\.nexus\journal, never AppData: an MSIX host (Claude Desktop) silently redirects AppData writes to its own
        // private copy, which the review, NexusSystem and the other AI apps never see.
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "journal");
        return Path.Combine(baseDir, "windows", a);
    }

    public static string? AppName(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        var raw = (app.StartsWith("process:", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(app["process:".Length..])
            : app).Trim();
        // Shared journal FORMAT.md: an executable or app id, never a document, folder or title (they can be private).
        if (Path.IsPathRooted(raw) && Directory.Exists(raw)) return null;
        if (raw.Contains('\\') || raw.Contains('/')) raw = Path.GetFileName(raw);
        var extension = Path.GetExtension(raw);
        if (extension.Length > 0 && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !raw.Contains('!')) return null;
        var lower = raw.ToLowerInvariant();
        return lower.Length > 0 ? lower : null;
    }

    // Messages whose suffix can echo a caller's value without delimiters: only the fixed category is journaled.
    static readonly string[] ValueBearingErrors = ["Unknown modifier", "Unknown key", "Target not found", "Target missing", "Incomplete key combination"];

    public static string? CleanError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        foreach (var category in ValueBearingErrors)
            if (error.TrimStart().StartsWith(category, StringComparison.OrdinalIgnoreCase)) return category;
        var collapsed = string.Join(' ', error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var head = CutRegex.Split(collapsed, 2)[0];
        var trimmed = TrimPunctRegex.Replace(head, "");
        if (trimmed.Length > 80) trimmed = trimmed[..80];
        return trimmed.Length > 0 ? trimmed : "error";
    }

    public static object BuildLine(
        string agent,
        string version,
        string? session,
        string? app,
        string? kind,
        string? status,
        string? technique,
        long durationMs,
        bool? foreground = null,
        string? error = null,
        DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var k = kind != null && ValidKinds.Contains(kind) ? kind : "invalid";
        var s = status != null && Issues.ContainsKey(status) ? status : "unknown";
        var issue = status != null && Issues.TryGetValue(status, out var iss) ? iss : "~";
        var tech = string.IsNullOrEmpty(technique) ? null : (technique.Length > 60 ? technique[..60] : technique);
        var fg = foreground ?? (tech != null && (tech.StartsWith("physical", StringComparison.OrdinalIgnoreCase) || tech.StartsWith("foreground", StringComparison.OrdinalIgnoreCase)));
        return new JournalBuildLineJson(
            v: 1,
            ts: ts,
            agent: agent.ToLowerInvariant(),
            outil: version,
            appel: session,
            app: AppName(app),
            cadre: (string?)null,
            action: k,
            technique: tech,
            issue: issue,
            statut: s,
            ms: Math.Max(0L, durationMs),
            premier_plan: fg,
            // A verified or already-satisfied step carries its report message (Waited 500 ms...), never an error.
            erreur: issue is "✓" or "=" ? null : CleanError(error)
        );
    }

    public static void WriteEntry(
        string agent,
        string version,
        string? session,
        string? app,
        string? kind,
        string? status,
        string? technique,
        long durationMs,
        bool? foreground = null,
        string? error = null,
        DateTimeOffset? now = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NODE_TEST_CONTEXT")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXUS_JOURNAL_HOME")))
                return;
            var ts = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var line = BuildLine(agent, version, session, app, kind, status, technique, durationMs, foreground, error, ts);
            var dir = JournalRoot(agent);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{ts:yyyy-MM}.jsonl");
            File.AppendAllText(file, EngineJson.Serialize(line, JsonOptions) + "\n", new UTF8Encoding(false));
        }
        catch
        {
            // The journal never blocks an action.
        }
    }
}
