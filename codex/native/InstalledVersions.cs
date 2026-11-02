using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Earlier engine versions removed at install and update (02/11: 31 versions, 433 MB had piled up in native\; nothing
// removed them). NexusWindows.exe --prune-installed, run by get.ps1 and the source installer once the new version is
// recorded, under their installation mutex:
//   - keeps the installed version (installation.json), every version a running process executes, and every version a
//     client or daemon declaration still names (an entry written before the stable launcher, a daemon spec);
//   - removes the other native\<sha-256> folders, the launchers set aside (NexusWindows.exe.old-*) once their session
//     has ended, and an interrupted get.ps1 staging folder (stage-*);
//   - removes the disposable browser profiles of launch(devtools:true) (state\<agent>\devtools-profiles) whose browser
//     has closed: the engine removes one only when it closes the browser itself.
// A folder still in use is renamed away before deletion, so it is never left half deleted: Windows refuses the rename
// while a program runs from it or holds one of its files open.
static class InstalledVersions
{
    internal sealed record Outcome(List<string> Removed, List<string> Kept, long Bytes);

    static readonly Regex Hash = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    static readonly Regex Named = new(@"nexus-windows(?:\\\\|\\|/)+native(?:\\\\|\\|/)+([a-f0-9]{64})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // A launch(devtools:true) profile younger than this may belong to a browser still starting.
    internal static readonly TimeSpan ProfileGrace = TimeSpan.FromHours(1.618);

    internal static int Command()
    {
        try
        {
            var outcome = Prune(Registration.Profile.Current(), RunningExecutables(), DateTimeOffset.UtcNow);
            Console.WriteLine($"Earlier files removed: {outcome.Removed.Count} ({outcome.Bytes / (1024 * 1024)} MB); kept: {(outcome.Kept.Count == 0 ? "none" : string.Join(", ", outcome.Kept))}.");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            // Never a failed installation: the next one prunes again.
            Console.WriteLine("Earlier versions not pruned: " + error.Message); // stdout: Windows PowerShell stops on stderr
            return 0;
        }
    }

    // Paths of the NexusWindows programs running now (the engines and the launchers of every session).
    internal static List<string> RunningExecutables()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName("NexusWindows"))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path) paths.Add(Path.GetFullPath(path)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return paths;
    }

    internal static Outcome Prune(Registration.Profile profile, IEnumerable<string> running, DateTimeOffset now)
    {
        var home = Path.GetDirectoryName(profile.NativeRoot)!;
        var removed = new List<string>(); var kept = new List<string>(); long bytes = 0;
        var manifest = Path.Combine(home, "installation.json");
        // Without a readable record of the installed version, nothing is known to be earlier: nothing is removed.
        if (!File.Exists(manifest)) return new Outcome(removed, ["no installation.json"], 0);
        var installed = JsonNode.Parse(File.ReadAllText(manifest))?["NativeHash"]?.GetValue<string>() ?? "";
        if (!Hash.IsMatch(installed)) return new Outcome(removed, ["no installed version recorded"], 0);
        var runningPaths = running.Select(p => Path.GetFullPath(p)).ToList();
        var declared = DeclaredVersions(profile);

        void Remove(string path, string label)
        {
            var size = Size(path);
            if (TryDelete(path)) { removed.Add(label); bytes += size; } else kept.Add(label + " (in use)");
        }

        if (Directory.Exists(profile.NativeRoot))
        {
            foreach (var folder in Directory.GetDirectories(profile.NativeRoot))
            {
                var name = Path.GetFileName(folder);
                if (name.StartsWith(".prune-", StringComparison.Ordinal)) { Remove(folder, "interrupted removal"); continue; }
                if (!Hash.IsMatch(name) || IsLink(folder)) continue; // not an engine version this product wrote
                if (name == installed) continue;
                var prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
                if (runningPaths.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { kept.Add(name[..12] + " (running)"); continue; }
                if (declared.Contains(name)) { kept.Add(name[..12] + " (declared)"); continue; }
                Remove(folder, name[..12]);
            }
        }
        if (Directory.Exists(home))
        {
            foreach (var aside in Directory.GetFiles(home, "NexusWindows.exe.old-*"))
            {
                if (runningPaths.Contains(Path.GetFullPath(aside), StringComparer.OrdinalIgnoreCase)) { kept.Add(Path.GetFileName(aside) + " (running)"); continue; }
                var size = new FileInfo(aside).Length;
                try { File.Delete(aside); removed.Add(Path.GetFileName(aside)); bytes += size; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { kept.Add(Path.GetFileName(aside) + " (in use)"); }
            }
            // A staging folder of another installation running now is recent: only an old one is a leftover.
            foreach (var stage in Directory.GetDirectories(home, "stage-*").Where(s => now - Directory.GetLastWriteTimeUtc(s) >= ProfileGrace).Concat(Directory.GetDirectories(home, ".prune-*")))
                if (!IsLink(stage)) Remove(stage, Path.GetFileName(stage));
            var state = Path.Combine(home, "state");
            if (Directory.Exists(state) && !IsLink(state))
                foreach (var agentFolder in Directory.GetDirectories(state))
                {
                    var agent = Path.GetFileName(agentFolder);
                    var profiles = Path.Combine(agentFolder, "devtools-profiles");
                    if (!Directory.Exists(profiles) || IsLink(agentFolder) || IsLink(profiles)) continue;
                    foreach (var browser in Directory.GetDirectories(profiles))
                    {
                        if (Path.GetFileName(browser).StartsWith(".prune-", StringComparison.Ordinal) && !IsLink(browser)) { Remove(browser, "interrupted removal"); continue; }
                        if (!OwnedProfile(browser, agent, now)) continue;
                        Remove(browser, "browser profile " + Path.GetFileName(browser));
                    }
                }
        }
        return new Outcome(removed, kept, bytes);
    }

    // The same ownership rule as the engine's own removal (McpLaunchCompatibility.ClaudeRemoveOwnedProfile): its marker
    // names this product and agent and the folder, and nothing inside is a link; and it is older than the grace period.
    static bool OwnedProfile(string path, string agent, DateTimeOffset now)
    {
        try
        {
            if (IsLink(path)) return false;
            var markerPath = Path.Combine(path, ".nexus-profile");
            if (!File.Exists(markerPath)) return false;
            var marker = JsonNode.Parse(File.ReadAllText(markerPath));
            if (marker?["owner"]?.GetValue<string>() != "nexus-windows-" + agent || marker["id"]?.GetValue<string>() != Path.GetFileName(path)) return false;
            if (marker["created"] is not JsonValue created || !DateTimeOffset.TryParse(created.GetValue<string>(), out var at) || now - at < ProfileGrace) return false;
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var directory))
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return false; }
    }

    // Versions named by a declaration: Claude Code (user scope and plugin copies), Claude Desktop, Codex (config and
    // plugin copies), Antigravity, and the Nexus daemon specs.
    internal static HashSet<string> DeclaredVersions(Registration.Profile profile)
    {
        var files = new List<string>
        {
            Path.Combine(profile.Home, ".claude.json"),
            Path.Combine(profile.CodexHome, "config.toml"),
            Path.Combine(profile.Home, ".gemini", "config", "mcp_config.json"),
            Path.Combine(profile.Home, ".gemini", "antigravity", "mcp_config.json"),
        };
        files.AddRange(Registration.DesktopFolders(profile).Select(f => Path.Combine(f, "claude_desktop_config.json")));
        void Folder(string root, string pattern, SearchOption option)
        {
            if (!Directory.Exists(root)) return;
            try { files.AddRange(Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true, MaxRecursionDepth = 6, AttributesToSkip = FileAttributes.ReparsePoint })); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Folder(Path.Combine(profile.Home, ".claude", "plugins", "cache"), ".mcp.json", SearchOption.AllDirectories);
        Folder(Path.Combine(profile.CodexHome, "plugins"), ".mcp.json", SearchOption.AllDirectories);
        Folder(Path.Combine(profile.CodexHome, "plugins"), ".codex-mcp.json", SearchOption.AllDirectories);
        Folder(Path.Combine(profile.Home, ".gemini", "config", "plugins"), "mcp_config.json", SearchOption.AllDirectories);
        Folder(Path.Combine(profile.Home, ".nexus", "daemons", "specs"), "*.json", SearchOption.TopDirectoryOnly);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 16 * 1024 * 1024) continue;
                foreach (Match match in Named.Matches(File.ReadAllText(file))) versions.Add(match.Groups[1].Value.ToLowerInvariant());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return versions;
    }

    static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static long Size(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Renamed away first (refused by Windows while it is in use), then deleted: never half removed.
    static bool TryDelete(string folder)
    {
        var parent = Path.GetDirectoryName(folder)!;
        var name = Path.GetFileName(folder);
        var aside = name.StartsWith(".prune-", StringComparison.Ordinal) ? folder : Path.Combine(parent, ".prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (aside != folder) Directory.Move(folder, aside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        try { Directory.Delete(aside, recursive: true); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; } // left aside: removed at the next pass
    }
}
