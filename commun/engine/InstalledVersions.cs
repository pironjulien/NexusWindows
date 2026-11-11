using System.Diagnostics;
using System.Runtime.InteropServices;
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
//   - removes the source installer's leftovers (commun\native-runtime.ps1): its build\<sha-256>-<guid> and
//     build\launcher-<sha-256>-<guid> staging folders once older than the grace period, and its launcher\<sha-256>
//     builds other than the one the stable launcher is a copy of.
// The engine also runs this pass itself (StartBackgroundPass, since 3.17.12): a version an AI session was running at
// the last installation is kept then, and nothing else would remove it until the next version. An MCP server started
// from the installed version runs it in the background, 20 s after its start and at most once an hour, under the same
// installation mutex (skipped while an installation holds it), with the same scan and lock.
// A running engine is protected twice, since either guard alone can miss:
//   1. the scan: the image path of every process (QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION,
//      which also answers for an elevated process); a process named like the engine that cannot be queried at all
//      (another account, SYSTEM) keeps every version, since where it runs from is unknown;
//   2. the lock: every engine holds its version folder's engine.lock open for its whole life without delete sharing.
// A folder is renamed away before deletion, so it is never left half deleted. Windows does NOT refuse renaming the folder
// of a running program (its image is mapped with delete sharing): it refuses it only while a file inside is held open
// without delete sharing, which engine.lock guarantees; that refusal is reported as "in use" and the folder kept whole.
static partial class InstalledVersions
{
    internal sealed record Outcome(List<string> Removed, List<string> Kept, long Bytes);
    internal sealed record Scan(List<string> Paths, int Unidentified);
    internal const string LockName = "engine.lock";
    const string EngineName = "NexusWindows";
    static FileStream? ownVersion;

    // Held from the first line of Main until the process ends (closed by Windows): an engine started from a version
    // folder (native\<sha-256>) keeps that folder from being moved or removed under it, whatever the scan sees.
    internal static void HoldOwnVersion() => ownVersion ??= Hold(Environment.ProcessPath);

    internal static FileStream? Hold(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (folder is null || !Hash.IsMatch(Path.GetFileName(folder)) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "native", StringComparison.OrdinalIgnoreCase)) return null;
        try { return new FileStream(Path.Combine(folder, LockName), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A folder this account cannot add a file to (written by another account): the engine file itself, held the
            // same way (its image mapping does not conflict with a read-shared handle).
            try { return new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    static readonly Regex Hash = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    // An engine version (native\) or a source installer's launcher build (launcher\) named by a declaration.
    static readonly Regex Named = new(@"nexus-windows(?:\\\\|\\|/)+(?:native|launcher)(?:\\\\|\\|/)+([a-f0-9]{64})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex Staging = new("^(?:launcher-)?[a-f0-9]{64}-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    // A launch(devtools:true) profile younger than this may belong to a browser still starting.
    internal static readonly TimeSpan ProfileGrace = TimeSpan.FromHours(1.618);

    internal static int Command()
    {
        try
        {
            var scan = RunningExecutables();
            var profile = Registration.Profile.Current();
            var now = DateTimeOffset.UtcNow;
            var outcome = Prune(profile, scan.Paths, now, scan.Unidentified);
            WriteStamp(profile, now);
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

    // The installers' mutex (get.ps1, commun\native-runtime.ps1): no pass while a version is being installed or removed.
    internal const string InstallationMutex = @"Local\NexusWindows-CommonRuntime";
    // A pass costs a scan of every process and a read of every client declaration (0.1 to 0.7 s measured on 11/11 with
    // 350 processes and 283 declaration files), and AI apps start MCP servers many times an hour: once an hour is enough.
    internal static readonly TimeSpan PassInterval = TimeSpan.FromHours(1);
    // Short-lived servers (a client's health check) never pay for a pass; a session's first requests are not slowed.
    static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
    internal enum Pass { NotInstalledEngine, Recent, InstallationRunning, Done }

    // MCP server start: the pass on a background thread, below normal priority; whatever it meets is reported on stderr,
    // never thrown at the server, which neither waits for it nor depends on it.
    internal static void StartBackgroundPass()
    {
        var executable = Environment.ProcessPath;
        var thread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(StartDelay);
                StartupPass(Registration.Profile.Current(), executable, RunningExecutables, DateTimeOffset.UtcNow);
            }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("NexusWindows: earlier versions not pruned: " + error.Message); } catch (IOException) { }
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "nexus-prune-installed" };
        thread.Start();
    }

    // Only the installed version prunes (installation.json names the folder this engine runs from): an earlier engine
    // still running, a build output or a test copy never does. The installed version and this engine's own are always
    // kept by Prune itself (installation.json, the scan and engine.lock).
    internal static (Pass State, Outcome? Outcome) StartupPass(Registration.Profile profile, string? executable, Func<Scan> scan, DateTimeOffset now, string mutexName = InstallationMutex)
    {
        if (!IsInstalledEngine(profile, executable)) return (Pass.NotInstalledEngine, null);
        if (Recent(profile, now)) return (Pass.Recent, null);
        using var mutex = new Mutex(false, mutexName);
        bool held;
        try { held = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; } // an installer or a pass ended without releasing it: ours now
        if (!held) return (Pass.InstallationRunning, null);
        try
        {
            // Read again under the mutex: an installation may have just recorded another version, another engine may
            // have just run its pass.
            if (!IsInstalledEngine(profile, executable)) return (Pass.NotInstalledEngine, null);
            if (Recent(profile, now)) return (Pass.Recent, null);
            var running = scan();
            var outcome = Prune(profile, running.Paths, now, running.Unidentified);
            WriteStamp(profile, now);
            return (Pass.Done, outcome);
        }
        finally { mutex.ReleaseMutex(); }
    }

    static bool IsInstalledEngine(Registration.Profile profile, string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return false;
        try
        {
            var manifest = Path.Combine(Path.GetDirectoryName(profile.NativeRoot)!, "installation.json");
            if (!File.Exists(manifest)) return false;
            var installed = JsonNode.Parse(File.ReadAllText(manifest))?["NativeHash"]?.GetValue<string>() ?? "";
            return Hash.IsMatch(installed) && string.Equals(Path.GetFullPath(executable),
                Path.GetFullPath(Path.Combine(profile.NativeRoot, installed, "NexusWindows.exe")), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    // The last pass, engine or installer: state\prune-installed.json beside the installation.
    internal static string StampPath(Registration.Profile profile) => Path.Combine(Path.GetDirectoryName(profile.NativeRoot)!, "state", "prune-installed.json");

    static bool Recent(Registration.Profile profile, DateTimeOffset now)
    {
        try
        {
            var path = StampPath(profile);
            if (!File.Exists(path)) return false;
            var at = JsonNode.Parse(File.ReadAllText(path))?["lastPass"]?.GetValue<string>();
            // A stamp ahead of the clock (the clock was set back) is not trusted.
            return DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                && last <= now && now - last < PassInterval;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }

    static void WriteStamp(Registration.Profile profile, DateTimeOffset now)
    {
        var path = StampPath(profile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, "{\"lastPass\":\"" + now.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"}\n");
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);

    // Image paths of every process running now, whatever its name (engines, launchers, a renamed copy), read with
    // PROCESS_QUERY_LIMITED_INFORMATION: granted on elevated processes of this account, where Process.MainModule (module
    // enumeration, PROCESS_VM_READ) fails. Unidentified: processes named like the engine whose path cannot be read.
    internal static Scan RunningExecutables()
    {
        var paths = new List<string>(); var unidentified = 0;
        var buffer = new char[32768];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var named = string.Equals(process.ProcessName, EngineName, StringComparison.OrdinalIgnoreCase);
                var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process.Id);
                if (handle == 0)
                {
                    // ERROR_INVALID_PARAMETER: the process ended since the snapshot.
                    if (named && Marshal.GetLastPInvokeError() != 87) unidentified++;
                    continue;
                }
                try
                {
                    var size = (uint)buffer.Length;
                    if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) paths.Add(Path.GetFullPath(new string(buffer, 0, (int)size)));
                    else if (named && (!GetExitCodeProcess(handle, out var code) || code == 259 /* STILL_ACTIVE */)) unidentified++;
                }
                finally { CloseHandle(handle); }
            }
        }
        return new Scan(paths, unidentified);
    }

    internal static Outcome Prune(Registration.Profile profile, IEnumerable<string> running, DateTimeOffset now, int unidentified = 0)
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
            switch (TryDelete(path))
            {
                case Removal.Removed: removed.Add(label); bytes += size; break;
                case Removal.SetAside: kept.Add(label + " (set aside, removed at the next pass)"); break;
                default: kept.Add(label + " (in use)"); break;
            }
        }

        // An engine or launcher runs from a place the scan could not read: no version or launcher is known to be unused.
        if (unidentified > 0) kept.Add($"every earlier version and launcher ({unidentified} engine process(es) not identified)");
        if (Directory.Exists(profile.NativeRoot) && unidentified == 0)
        {
            foreach (var folder in Directory.GetDirectories(profile.NativeRoot))
            {
                var name = Path.GetFileName(folder);
                var interrupted = name.StartsWith(".prune-", StringComparison.Ordinal);
                if ((!interrupted && !Hash.IsMatch(name)) || IsLink(folder)) continue; // not an engine version this product wrote
                if (name == installed) continue;
                var label = interrupted ? "interrupted removal" : name[..12];
                var prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
                if (runningPaths.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { kept.Add(label + " (running)"); continue; }
                if (!interrupted && declared.Contains(name)) { kept.Add(label + " (declared)"); continue; }
                Remove(folder, label);
            }
        }
        if (Directory.Exists(home))
        {
            foreach (var aside in Directory.GetFiles(home, "NexusWindows.exe.old-*"))
            {
                if (unidentified > 0) continue;
                if (runningPaths.Contains(Path.GetFullPath(aside), StringComparer.OrdinalIgnoreCase)) { kept.Add(Path.GetFileName(aside) + " (running)"); continue; }
                var size = new FileInfo(aside).Length;
                try { File.Delete(aside); removed.Add(Path.GetFileName(aside)); bytes += size; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { kept.Add(Path.GetFileName(aside) + " (in use)"); }
            }
            // A staging folder of another installation running now is recent: only an old one is a leftover.
            foreach (var stage in Directory.GetDirectories(home, "stage-*").Where(s => now - Directory.GetLastWriteTimeUtc(s) >= ProfileGrace).Concat(Directory.GetDirectories(home, ".prune-*")))
                if (!IsLink(stage)) Remove(stage, Path.GetFileName(stage));
            if (unidentified == 0) SourceInstallerLeftovers();
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

        bool RunsFrom(string folder)
        {
            var prefix = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            return runningPaths.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        // commun\native-runtime.ps1 (source installation, since 3.9.0) builds in build\<sha-256>-<guid> and
        // build\launcher-<sha-256>-<guid>, and keeps each launcher it built in launcher\<sha-256>. A staging folder of an
        // installation running now is recent; the launcher build the stable launcher is a copy of stays, since the
        // source installer checks it (-CheckOnly). Only these names are touched; nothing a process runs from.
        void SourceInstallerLeftovers()
        {
            var build = Path.Combine(home, "build");
            if (Directory.Exists(build) && !IsLink(build))
            {
                foreach (var folder in Directory.GetDirectories(build))
                {
                    var name = Path.GetFileName(folder);
                    var interrupted = name.StartsWith(".prune-", StringComparison.Ordinal);
                    if ((!interrupted && !Staging.IsMatch(name)) || IsLink(folder)) continue;
                    if (!interrupted && now - Directory.GetLastWriteTimeUtc(folder) < ProfileGrace) continue;
                    var label = interrupted ? "interrupted removal" : "build " + name[..Math.Min(name.Length, name.StartsWith("launcher-", StringComparison.Ordinal) ? 21 : 12)];
                    if (RunsFrom(folder)) { kept.Add(label + " (running)"); continue; }
                    Remove(folder, label);
                }
                RemoveIfEmpty(build);
            }
            var launchers = Path.Combine(home, "launcher");
            if (Directory.Exists(launchers) && !IsLink(launchers))
            {
                var stable = Path.Combine(home, "NexusWindows.exe");
                var current = File.Exists(stable) ? FileHash(stable) : null;
                foreach (var folder in Directory.GetDirectories(launchers))
                {
                    var name = Path.GetFileName(folder);
                    var interrupted = name.StartsWith(".prune-", StringComparison.Ordinal);
                    if ((!interrupted && !Hash.IsMatch(name)) || IsLink(folder)) continue;
                    var label = interrupted ? "interrupted removal" : "launcher " + name[..12];
                    var built = Path.Combine(folder, "NexusWindows.exe");
                    if (!interrupted && current is not null && File.Exists(built) && FileHash(built) is { } hash && hash.SequenceEqual(current)) continue;
                    if (RunsFrom(folder)) { kept.Add(label + " (running)"); continue; }
                    if (!interrupted && declared.Contains(name)) { kept.Add(label + " (declared)"); continue; }
                    Remove(folder, label);
                }
                RemoveIfEmpty(launchers);
            }
        }
    }

    static void RemoveIfEmpty(string folder)
    {
        try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    static byte[]? FileHash(string path)
    {
        try { using var stream = File.OpenRead(path); return System.Security.Cryptography.SHA256.HashData(stream); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
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

    internal enum Removal { Removed, SetAside, InUse }

    // Renamed away first, then deleted: never half removed. Windows refuses the rename only while a file inside is open
    // without delete sharing (an engine's engine.lock, a browser's profile files), not merely because a program runs from
    // the folder. A folder already set aside is renamed again, the same check before its deletion. A folder moved but
    // not deleted (a file inside still mapped or held with delete sharing) is not counted removed: it stays set aside
    // under .prune-* and the next pass removes it.
    internal static Removal TryDelete(string folder)
    {
        var aside = Path.Combine(Path.GetDirectoryName(folder)!, ".prune-" + Guid.NewGuid().ToString("N"));
        try { Directory.Move(folder, aside); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Removal.InUse; }
        try { Directory.Delete(aside, recursive: true); return Removal.Removed; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Removal.SetAside; }
    }
}
