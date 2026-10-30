using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Declares NexusWindows to its MCP clients (replaces the Python installers and setup scripts):
//   NexusWindows.exe --register claude|codex|antigravity|all [--check] [--remove] [--no-desktop]
//                    [--plugin-source <dir>] [--guide <SKILL.md>]
// Every declaration names the STABLE launcher %USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe with
// `--mcp --agent <client>`, never a versioned engine folder: an update never has to touch a client again.
// Only entries this product wrote (any earlier form: uvx/PyPI, npx, the Python venv, a versioned native folder, the
// clone launchers) are replaced or removed; a custom or disabled entry is kept exactly. Each changed file is backed up
// first (%USERPROFILE%\.nexus\nexus-windows\backups) and replaced atomically. --check writes nothing and fails when a
// change would be made. The last output line is `NEXUS_PACK_RESULT <client>=<state>; ...` (Nexus pack contract).
static class Registration
{
    internal const string Name = "nexus-windows";
    internal enum Mode { Install, Check, Remove }

    internal sealed record Profile(string Home, string Roaming, string Local, string CodexHome, string? ClaudeCli)
    {
        public string Stable => Path.Combine(Home, ".nexus", "nexus-windows", "NexusWindows.exe");
        public string NativeRoot => Path.Combine(Home, ".nexus", "nexus-windows", "native");
        public string Backups => Path.Combine(Home, ".nexus", "nexus-windows", "backups");

        public static Profile Current()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 } u ? u : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roaming = Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } r ? r : Path.Combine(home, "AppData", "Roaming");
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(home, "AppData", "Local");
            var codex = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } c ? c : Path.Combine(home, ".codex");
            // NEXUS_WINDOWS_NO_CLI=1 (tests of get.ps1 in a throwaway profile): never the real Claude CLI.
            var cli = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_NO_CLI") == "1" ? null : FindOnPath("claude");
            return new Profile(home, roaming, local, codex, cli);
        }
    }

    // Drift found by --check: nothing was written.
    sealed class Drift(string message) : Exception(message);

    internal static int Command(string[] args)
    {
        string? client = null, pluginSource = null, guide = null;
        var mode = Mode.Install; var desktop = true;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--register" when i + 1 < args.Length: client = args[++i]; break;
                case "--check": mode = Mode.Check; break;
                case "--remove": mode = Mode.Remove; break;
                case "--no-desktop": desktop = false; break;
                case "--plugin-source" when i + 1 < args.Length: pluginSource = Path.GetFullPath(args[++i]); break;
                case "--guide" when i + 1 < args.Length: guide = Path.GetFullPath(args[++i]); break;
                default: Console.Error.WriteLine("Usage: --register claude|codex|antigravity|all [--check] [--remove] [--no-desktop] [--plugin-source DIR] [--guide FILE]"); return 2;
            }
        }
        var clients = client switch
        {
            "all" => new[] { "claude", "codex", "antigravity" },
            "claude" or "codex" or "antigravity" => new[] { client },
            _ => null,
        };
        if (clients is null) { Console.Error.WriteLine("Expected a client: claude, codex, antigravity or all."); return 2; }
        var results = Run(Profile.Current(), clients, mode, desktop, pluginSource, guide);
        foreach (var (key, value) in results) Console.WriteLine($"{key}: {value}");
        Console.WriteLine("NEXUS_PACK_RESULT " + string.Join("; ", results.Select(r => $"{r.Key}={r.Value}")));
        return results.Values.Any(v => v.StartsWith("failed", StringComparison.Ordinal) || v.StartsWith("not aligned", StringComparison.Ordinal)) ? 1 : 0;
    }

    internal static Dictionary<string, string> Run(Profile profile, IEnumerable<string> clients, Mode mode, bool desktop = true, string? pluginSource = null, string? guide = null)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        void Try(string key, Func<string> action)
        {
            try { results[key] = action(); }
            catch (Drift drift) { results[key] = "not aligned: " + drift.Message; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or FormatException)
            {
                results[key] = "failed: " + error.Message;
            }
        }
        foreach (var client in clients)
        {
            switch (client)
            {
                case "claude":
                    Try("Claude Code", () => ClaudeCode(profile, mode));
                    if (desktop) Try("Claude Desktop", () => ClaudeDesktop(profile, mode));
                    break;
                case "codex": Try("Codex", () => Codex(profile, mode)); break;
                case "antigravity":
                    Try("Antigravity", () => Antigravity(profile, mode, pluginSource ?? Path.Combine(AppContext.BaseDirectory, "plugins", "antigravity"), guide));
                    break;
                default: throw new ArgumentException("Unknown client " + client);
            }
        }
        return results;
    }

    // ---------------------------------------------------------------- ownership of an existing declaration

    static readonly Regex Hash = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    internal static JsonArray Arguments(string agent) => new("--mcp", "--agent", agent);

    static string[] Strings(JsonNode? node) =>
        node is JsonArray array && array.All(a => a is JsonValue v && v.TryGetValue<string>(out _)) ? array.Select(a => a!.GetValue<string>()).ToArray() : [];

    static string Expand(Profile profile, string command) =>
        command.Replace("${USERPROFILE}", profile.Home, StringComparison.OrdinalIgnoreCase).Replace("%USERPROFILE%", profile.Home, StringComparison.OrdinalIgnoreCase);

    // The stable launcher, or an immutable native\<sha-256>\NexusWindows.exe of this profile.
    internal static bool IsOurEngine(Profile profile, string command)
    {
        command = Expand(profile, command);
        if (!Path.IsPathFullyQualified(command)) return false;
        var full = Path.GetFullPath(command);
        if (string.Equals(full, Path.GetFullPath(profile.Stable), StringComparison.OrdinalIgnoreCase)) return true;
        var relative = Path.GetRelativePath(profile.NativeRoot, full).Split(Path.DirectorySeparatorChar);
        return relative.Length == 2 && Hash.IsMatch(relative[0]) && string.Equals(relative[1], "NexusWindows.exe", StringComparison.OrdinalIgnoreCase);
    }

    // True when this product wrote the entry (any version). Disabled or extended entries are the user's.
    internal static bool Owned(Profile profile, JsonNode? node, string agent)
    {
        if (node is not JsonObject server) return false;
        if (server["disabled"] is JsonValue d && d.TryGetValue<bool>(out var disabled) && disabled) return false;
        if (server["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled) && !enabled) return false;
        var command = server["command"] is JsonValue c && c.TryGetValue<string>(out var text) ? text : "";
        var args = Strings(server["args"]);
        var env = server["env"] as JsonObject;
        var name = Path.GetFileName(command.Replace('/', '\\')).ToLowerInvariant();
        bool Keys(params string[] allowed) => server.All(p => allowed.Contains(p.Key, StringComparer.Ordinal) || p.Key is "type" or "disabled" or "enabled");
        bool EnvOnly(params string[] allowed) => env is null || env.All(p => allowed.Contains(p.Key, StringComparer.Ordinal));
        if (name == "nexuswindows.exe")
            return Keys("command", "args", "startup_timeout_sec", "env") && (env is null || env.Count == 0) && IsOurEngine(profile, command) && args.SequenceEqual(["--mcp", "--agent", agent]);
        if (name is "uvx" or "uvx.exe")
            return Keys("command", "args", "env") && EnvOnly("PYTHONUTF8") && args.Length == 3 && args[0] == "--python" &&
                   args[2] is "nexus-windows-claude" or "nexus-windows-claude@latest" or "nexus-windows-antigravity" or "nexus-windows-antigravity@latest" or "nexus-windows" or "nexus-windows@latest";
        if (name is "npx" or "npx.cmd" or "npx.exe")
            return Keys("command", "args", "startup_timeout_sec") && args.Length == 2 && args[0] is "-y" or "--yes" && args[1] is "nexus-windows-codex" or "nexus-windows-codex@latest";
        if (name is "python.exe" && args.SequenceEqual(["-m", "nexus_windows"]))
            return Keys("command", "args", "env") && EnvOnly("PYTHONUTF8", "PYTHONPYCACHEPREFIX") &&
                   Expand(profile, command).Replace('/', '\\').EndsWith(@"\.claude\nexus-claude\venv\Scripts\python.exe", StringComparison.OrdinalIgnoreCase);
        if (name is "pwsh" or "pwsh.exe" or "powershell.exe")
            return Keys("command", "args", "env") && (env is null || env.Count == 0) && args.Length == 3 && args[0] == "-NoProfile" && args[1] == "-File" &&
                   args[2].Replace('/', '\\').EndsWith(@"\agy\scripts\start-computer-mcp.ps1", StringComparison.OrdinalIgnoreCase);
        if (name is "node" or "node.exe")
            return Keys("command", "args", "startup_timeout_sec") && args.Length == 1 && args[0].Replace('/', '\\').EndsWith(@"\bin\nexus-windows-codex.mjs", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // ---------------------------------------------------------------- files

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(false);

    static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return [];
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException(path + " is not a JSON object.");
    }

    static string Serialize(JsonNode node) => node.ToJsonString(Indented) + "\n";

    static void Write(Profile profile, Mode mode, string path, string content, bool backup = true)
    {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF') == content) return;
        if (mode == Mode.Check) throw new Drift(path);
        if (backup && File.Exists(path))
        {
            Directory.CreateDirectory(profile.Backups);
            File.Copy(path, Path.Combine(profile.Backups, $"{Path.GetFileName(Path.GetDirectoryName(path))}-{Path.GetFileName(path)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, Utf8); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string? FindOnPath(string program)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var extension in new[] { ".exe", ".cmd" })
            {
                try { var candidate = Path.Combine(dir, program + extension); if (File.Exists(candidate)) return candidate; }
                catch (ArgumentException) { }
            }
        return null;
    }

    static bool SameJson(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    // ---------------------------------------------------------------- Claude Code

    static string ClaudeCode(Profile profile, Mode mode)
    {
        var desired = new JsonObject { ["type"] = "stdio", ["command"] = profile.Stable, ["args"] = Arguments("claude") };
        var claudeDir = Path.Combine(profile.Home, ".claude");
        var userConfig = Path.Combine(profile.Home, ".claude.json");
        var enabled = ReadObject(Path.Combine(claudeDir, "settings.json"))["enabledPlugins"] as JsonObject;
        var plugin = enabled?.Any(p => p.Key.Split('@')[0] == Name && p.Value is JsonValue v && v.TryGetValue<bool>(out var on) && on) == true;
        var state = new List<string>();
        if (plugin)
        {
            // The plugin declares the server: its installed copies (plugin cache) are pointed at the stable launcher.
            var cache = Path.GetFullPath(Path.Combine(claudeDir, "plugins", "cache")) + Path.DirectorySeparatorChar;
            var installed = ReadObject(Path.Combine(claudeDir, "plugins", "installed_plugins.json"))["plugins"] as JsonObject;
            var changed = 0;
            foreach (var (key, entries) in installed ?? [])
            {
                if (key.Split('@')[0] != Name || entries is not JsonArray list) continue;
                foreach (var entry in list)
                {
                    if (entry?["installPath"] is not JsonValue p || !p.TryGetValue<string>(out var installPath)) continue;
                    var full = Path.GetFullPath(installPath);
                    if (!full.StartsWith(cache, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Claude plugin copy outside the plugin cache: " + full);
                    var file = Path.Combine(full, ".mcp.json");
                    if (!File.Exists(file)) continue;
                    var data = ReadObject(file);
                    var servers = data["mcpServers"] as JsonObject;
                    var current = servers?[Name];
                    if (servers is null || !Owned(profile, current, "claude") || (mode != Mode.Remove && SameJson(current, desired))) continue;
                    if (mode == Mode.Remove) continue; // the plugin owns its declaration; it is removed with the plugin
                    servers[Name] = desired.DeepClone();
                    Write(profile, mode, file, Serialize(data), backup: false);
                    changed++;
                }
            }
            state.Add(changed > 0 ? $"plugin ({changed} copy updated)" : "plugin");
            // A user-scope entry next to the enabled plugin would start a second server.
            var user = ReadObject(userConfig)["mcpServers"]?[Name];
            if (user is not null && Owned(profile, user, "claude")) { RemoveClaudeUserEntry(profile, mode, userConfig); state.Add("earlier user entry removed"); }
            return string.Join(", ", state);
        }
        var existing = ReadObject(userConfig)["mcpServers"]?[Name];
        if (mode == Mode.Remove)
        {
            if (existing is null) return "absent";
            if (!Owned(profile, existing, "claude")) return "kept (your own entry)";
            RemoveClaudeUserEntry(profile, mode, userConfig);
            return "removed";
        }
        if (existing is not null && !Owned(profile, existing, "claude")) return "kept (your own entry)";
        if (existing is not null && SameJson(existing, desired)) return "already declared";
        if (mode == Mode.Check) throw new Drift(existing is null ? "not declared" : "earlier declaration");
        if (profile.ClaudeCli is { } cli)
        {
            if (existing is not null) RunClaude(cli, "mcp", "remove", Name, "--scope", "user");
            var (code, output) = RunClaude(cli, "mcp", "add", "--scope", "user", "--transport", "stdio", Name, "--", profile.Stable, "--mcp", "--agent", "claude");
            return code == 0 ? "declared" : "failed: " + output.Trim()[..Math.Min(200, output.Trim().Length)];
        }
        if (!File.Exists(userConfig)) return "absent";
        var config = ReadObject(userConfig);
        (config["mcpServers"] as JsonObject ?? (JsonObject)(config["mcpServers"] = new JsonObject()))[Name] = desired;
        Write(profile, mode, userConfig, Serialize(config));
        return "declared";
    }

    static void RemoveClaudeUserEntry(Profile profile, Mode mode, string userConfig)
    {
        if (mode == Mode.Check) throw new Drift("earlier user-scope entry beside the plugin");
        if (profile.ClaudeCli is { } cli && RunClaude(cli, "mcp", "remove", Name, "--scope", "user").Code == 0) return;
        var config = ReadObject(userConfig);
        (config["mcpServers"] as JsonObject)?.Remove(Name);
        Write(profile, mode, userConfig, Serialize(config));
    }

    static (int Code, string Output) RunClaude(string cli, params string[] args)
    {
        var start = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000)) { process.Kill(true); return (-1, "claude CLI timed out"); }
        return (process.ExitCode, output.Result + error);
    }

    // ---------------------------------------------------------------- Claude Desktop

    // Classic install, the Store (MSIX) layouts (AppData redirected into the package, or %LOCALAPPDATA%\Claude-Data\<pkg>);
    // a folder the app has left holds a ".user-data-left.<package>" marker and is no longer read.
    internal static IEnumerable<string> DesktopFolders(Profile profile)
    {
        var folders = new List<string> { Path.Combine(profile.Roaming, "Claude") };
        var packages = Path.Combine(profile.Local, "Packages");
        if (Directory.Exists(packages))
            folders.AddRange(Directory.GetDirectories(packages, "*Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude")).Order(StringComparer.OrdinalIgnoreCase));
        var data = Path.Combine(profile.Local, "Claude-Data");
        if (Directory.Exists(data)) folders.AddRange(Directory.GetDirectories(data).Order(StringComparer.OrdinalIgnoreCase));
        return folders.Where(f => Directory.Exists(f) && !Directory.EnumerateFiles(f, ".user-data-left.*").Any());
    }

    static string ClaudeDesktop(Profile profile, Mode mode)
    {
        var desired = new JsonObject { ["command"] = profile.Stable, ["args"] = Arguments("claude") };
        var results = new List<string>();
        foreach (var folder in DesktopFolders(profile))
        {
            var path = Path.Combine(folder, "claude_desktop_config.json");
            var data = ReadObject(path);
            var servers = data["mcpServers"] as JsonObject;
            var current = servers?[Name];
            if (current is not null && !Owned(profile, current, "claude")) { results.Add("kept (your own entry)"); continue; }
            if (mode == Mode.Remove)
            {
                if (current is null) continue;
                servers!.Remove(Name); Write(profile, mode, path, Serialize(data)); results.Add("removed"); continue;
            }
            if (current is not null && SameJson(current, desired)) { results.Add("already declared"); continue; }
            if (servers is null) data["mcpServers"] = servers = [];
            servers[Name] = desired.DeepClone();
            Write(profile, mode, path, Serialize(data));
            results.Add("declared");
        }
        return results.Count == 0 ? "absent" : string.Join(", ", results);
    }

    // ---------------------------------------------------------------- Codex (config.toml)

    static readonly Regex TomlHeader = new(@"^\s*\[\[?\s*(?<path>[^\]#]+?)\s*\]\]?\s*(#.*)?$", RegexOptions.CultureInvariant);

    static string TomlKeyPath(string path) =>
        string.Join('.', Regex.Matches(path, "\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*'|[A-Za-z0-9_-]+").Select(m => m.Value.Trim('"', '\'')));

    static string TomlString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    internal static string CodexBlock(Profile profile) =>
        $"[mcp_servers.{Name}]\ncommand = {TomlString(profile.Stable)}\nargs = [\"--mcp\", \"--agent\", \"codex\"]\nstartup_timeout_sec = 120\n";

    // Minimal reader for the values this product ever wrote: strings, arrays of strings, integers and booleans.
    static JsonNode? TomlValue(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+#[^""']*$", "");
        if (text.StartsWith('"') && text.EndsWith('"') && text.Length >= 2) return JsonValue.Create(Regex.Unescape(text[1..^1]));
        if (text.StartsWith('\'') && text.EndsWith('\'') && text.Length >= 2) return JsonValue.Create(text[1..^1]);
        if (text is "true" or "false") return JsonValue.Create(text == "true");
        if (long.TryParse(text, out var number)) return JsonValue.Create(number);
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            var items = new JsonArray();
            foreach (Match m in Regex.Matches(text[1..^1], "\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*'|[^,\\s]+"))
                if (TomlValue(m.Value) is { } item) items.Add(item); else return null;
            return items;
        }
        return null;
    }

    static string Codex(Profile profile, Mode mode)
    {
        var path = Path.Combine(profile.CodexHome, "config.toml");
        var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF') : "";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        // A plugin declaration (enabled or not) owns the server and its guide: left to the plugin and its installer.
        if (lines.Any(l => TomlHeader.Match(l) is { Success: true } h && TomlKeyPath(h.Groups["path"].Value).StartsWith("plugins." + Name + "@", StringComparison.Ordinal)) ||
            Regex.IsMatch(text, @"^\s*plugins\s*=.*" + Regex.Escape(Name) + "@", RegexOptions.Multiline))
            return "plugin";
        var starts = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var h = TomlHeader.Match(lines[i]);
            if (!h.Success) continue;
            var key = TomlKeyPath(h.Groups["path"].Value);
            if (key == "mcp_servers." + Name) starts.Add(i);
            else if (key.StartsWith("mcp_servers." + Name + ".", StringComparison.Ordinal)) return "kept (your own entry)";
        }
        if (Regex.IsMatch(text, @"^\s*(mcp_servers\s*=|[""']?" + Regex.Escape(Name) + @"[""']?\s*=\s*\{|mcp_servers\s*\.\s*[""']?" + Regex.Escape(Name) + @"[""']?\s*\.)", RegexOptions.Multiline))
            return "kept (your own entry)";
        if (starts.Count > 1) throw new InvalidDataException("config.toml declares nexus-windows twice: nothing was changed.");
        var block = CodexBlock(profile);
        if (starts.Count == 0)
        {
            if (mode == Mode.Remove) return "absent";
            if (mode == Mode.Check) throw new Drift("not declared");
            var appended = text.Length == 0 ? block : text.TrimEnd('\n', '\r') + "\n\n" + block;
            Write(profile, mode, path, appended);
            return "declared";
        }
        var start = starts[0];
        var end = start + 1;
        while (end < lines.Count && !TomlHeader.IsMatch(lines[end])) end++;
        var server = new JsonObject();
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0 || TomlValue(line[(eq + 1)..]) is not { } value) return "kept (your own entry)";
            server[line[..eq].Trim().Trim('"', '\'')] = value;
        }
        if (!Owned(profile, server, "codex")) return "kept (your own entry)";
        var trailing = end;
        while (trailing > start + 1 && lines[trailing - 1].Trim().Length == 0) trailing--;
        var current = string.Join("\n", lines.Skip(start).Take(trailing - start)) + "\n";
        if (mode != Mode.Remove && current == block) return "already declared";
        var replacement = mode == Mode.Remove ? [] : block.TrimEnd('\n').Split('\n');
        lines.RemoveRange(start, trailing - start);
        lines.InsertRange(start, replacement);
        var result = string.Join("\n", lines);
        if (mode == Mode.Remove) result = Regex.Replace(result, @"\n{3,}", "\n\n").TrimStart('\n');
        Write(profile, mode, path, result);
        return mode == Mode.Remove ? "removed" : "updated";
    }

    // ---------------------------------------------------------------- Antigravity (plugin nexus-windows)

    static readonly string[] LegacyPlugins = ["nexus-computer"];
    static readonly string[] GlobalDuplicates = [Name, "nexus-computer", "nexus-windows-antigravity"];
    const string Guide = @"skills\nexus-windows\SKILL.md";

    static string Antigravity(Profile profile, Mode mode, string source, string? guide)
    {
        var root = Path.Combine(profile.Home, ".gemini", "config");
        var target = Path.Combine(root, "plugins", Name);
        if (mode == Mode.Remove)
        {
            var removed = false;
            foreach (var folder in LegacyPlugins.Select(n => Path.Combine(root, "plugins", n)).Prepend(target))
                if (Directory.Exists(folder)) { RemoveTree(folder); removed = true; }
            return removed ? "removed (restart Antigravity)" : "absent";
        }
        // Antigravity never started on this PC (no ~/.gemini): nothing is created for it.
        if (!Directory.Exists(Path.Combine(profile.Home, ".gemini"))) return "absent";
        if (!File.Exists(Path.Combine(source, "plugin.json"))) throw new FileNotFoundException("plugin.json of nexus-windows not found in " + source);
        var changes = 0;
        void Put(string path, byte[] data)
        {
            if (File.Exists(path))
            {
                var current = File.ReadAllBytes(path);
                if (current.AsSpan().SequenceEqual(data)) return;
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try { if (SameJson(JsonNode.Parse(current), JsonNode.Parse(data))) return; } catch (JsonException) { }
                }
            }
            if (mode == Mode.Check) throw new Drift(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Name + "-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            changes++;
        }
        void Delete(string path) { if (mode == Mode.Check) throw new Drift("obsolete file: " + path); File.Delete(path); changes++; }

        // 0. Former name: a "disabled" chosen under it is carried over (a discovered plugin is otherwise active), then
        //    the former plugin is emptied BEFORE the new one is written: never two declarations of the same engine.
        var settings = Path.Combine(root, "config.json");
        JsonObject? config = null;
        try { config = File.Exists(settings) ? ReadObject(settings) : null; } catch (JsonException) { }
        if (config?["plugins"] is JsonObject plugins && !plugins.ContainsKey(Name) &&
            LegacyPlugins.Any(n => plugins[n] is JsonObject p && p["enabled"] is JsonValue v && v.TryGetValue<bool>(out var on) && !on))
        {
            plugins[Name] = new JsonObject { ["enabled"] = false };
            Put(settings, Utf8.GetBytes(Serialize(config)));
        }
        foreach (var legacy in LegacyPlugins.Select(n => Path.Combine(root, "plugins", n)).Where(Directory.Exists))
        {
            if (Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories).Any())
            {
                if (mode == Mode.Check) throw new Drift("former plugin still installed: " + legacy);
                changes++;
            }
            if (mode != Mode.Check) RemoveTree(legacy);
        }
        // 1. Plugin files, byte for byte; files the shipped version no longer has are removed.
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, file);
            if (Path.GetFileName(relative).Equals("mcp_config.json", StringComparison.OrdinalIgnoreCase)) continue;
            shipped.Add(relative);
            Put(Path.Combine(target, relative), guide is not null && relative.Equals(Guide, StringComparison.OrdinalIgnoreCase) ? File.ReadAllBytes(guide) : File.ReadAllBytes(file));
        }
        if (!shipped.Contains("plugin.json") || !shipped.Contains(Guide)) throw new FileNotFoundException("plugin files not readable in " + source);
        if (Directory.Exists(target))
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = Path.GetRelativePath(target, file);
                if (!Path.GetFileName(relative).Equals("mcp_config.json", StringComparison.OrdinalIgnoreCase) && !shipped.Contains(relative)) Delete(file);
            }
            if (mode != Mode.Check)
                foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        // 2. The single declaration of the server; a custom or disabled one is kept exactly (guide still updated).
        var desired = new JsonObject { ["command"] = profile.Stable, ["args"] = Arguments("antigravity") };
        var declarationFile = Path.Combine(target, "mcp_config.json");
        var declaration = File.Exists(declarationFile) ? ReadObject(declarationFile) : new JsonObject();
        var servers = declaration["mcpServers"] as JsonObject;
        if (servers?[Name] is { } existing && !Owned(profile, existing, "antigravity")) { /* kept */ }
        else
        {
            if (servers is null) declaration["mcpServers"] = servers = [];
            servers[Name] = desired;
        }
        Put(declarationFile, Utf8.GetBytes(Serialize(declaration)));
        // 3. Earlier duplicates: the same server in the global mcp_config.json, the guide copied into global skills.
        var global = Path.Combine(root, "mcp_config.json");
        if (File.Exists(global) && ReadObject(global) is { } content && content["mcpServers"] is JsonObject globalServers &&
            GlobalDuplicates.Any(n => Owned(profile, globalServers[n], "antigravity")))
        {
            foreach (var n in GlobalDuplicates) if (Owned(profile, globalServers[n], "antigravity")) globalServers.Remove(n);
            Put(global, Utf8.GetBytes(Serialize(content)));
        }
        foreach (var n in LegacyPlugins.Prepend(Name))
        {
            var skill = Path.Combine(root, "skills", n, "SKILL.md");
            if (!File.Exists(skill)) continue;
            Delete(skill);
            if (!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(skill)!).Any()) Directory.Delete(Path.GetDirectoryName(skill)!);
        }
        var state = mode == Mode.Check ? "up to date" : changes > 0 ? "installed (restart Antigravity or open a new conversation)" : "already up to date";
        var disabledByUser = (config ?? (File.Exists(settings) ? ReadObject(settings) : null))?["plugins"]?[Name] is JsonObject entry &&
                             entry["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var isOn) && !isOn;
        return disabledByUser ? state + "; turned off in Antigravity (Customizations > Installed)" : state;
    }

    // Antigravity open keeps a handle on the plugin folder itself: emptied, it declares nothing and goes at the next
    // pass. A locked FILE, however, fails the installation.
    static void RemoveTree(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList()) File.Delete(file);
        try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
