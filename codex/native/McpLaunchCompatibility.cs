using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The launch surface of the three profiles (Claude's historical one), backed only by the native engine. Each profile
// keeps its own block list, journal and DevTools profiles. User applications are dispatched by the interactive shell, never inherited from the MCP job.
static partial class McpServer
{
    sealed record ClaudeStartApp(string Name, string Id, string? Target = null, string Arguments = "");
    sealed record ClaudeLaunchPlan(string Kind, string Target, string Arguments, string? Executable,
        string? AppId, string Name, bool Devtools, bool Restart, string[] Notes);
    static readonly string[] ClaudeBrowserNames = ["msedge.exe", "chrome.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe"];
    static readonly HashSet<string> ClaudeControlSwitches = new(StringComparer.OrdinalIgnoreCase)
    { "load-extension", "remote-debugging-port", "remote-debugging-pipe", "remote-debugging-address", "remote-allow-origins", "disable-web-security", "inspect", "inspect-brk", "inspect-port", "cef-enable-debugging", "devtools-port" };
    static readonly object ClaudeAppsLock = new();
    static List<ClaudeStartApp>? claudeApps;
    static DateTimeOffset claudeAppsAt;
    static readonly ConditionalWeakTable<SessionState, ClaudeLaunchLeases> ClaudeOwnedLaunches = new();
    sealed class ClaudeLaunchLeases { public readonly object Sync = new(); public readonly List<ClaudeDevtoolsLease> Items = []; }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int AssocQueryStringW(uint flags, uint kind, string association, string? extra, StringBuilder? output, ref uint length);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, nint context, in Guid iid, out nint item);
    [DllImport("shell32.dll")] static extern int SHCreateShellItemArrayFromShellItem(nint item, in Guid iid, out nint array);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(nint process, int infoClass, nint buffer, int length, out int returned);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClaudeActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string? args, uint options, out uint pid);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id, nint items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint pid);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id, nint items, out uint pid);
    }
    static void ClaudeRelease(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    static string ClaudeExpandPath(string text)
    {
        text = Environment.ExpandEnvironmentVariables(text.Trim());
        if (text == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (text.StartsWith("~\\") || text.StartsWith("~/")) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..]);
        return text;
    }
    static string ClaudeKnownFolderPath(string id)
    {
        var match = Regex.Match(id, @"^\{([0-9a-fA-F-]{36})\}[\\/](.+)$");
        if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out var folder)) return id;
        if (SHGetKnownFolderPath(in folder, 0, 0, out var value) < 0) return id;
        try { return Path.Combine(Marshal.PtrToStringUni(value)!, match.Groups[2].Value); }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    static string? ClaudeAssociation(string target, uint kind, bool protocol = false)
    {
        uint length = 0;
        var flags = protocol ? 0x1000u /* ASSOCF_IS_PROTOCOL */ : 0u;
        AssocQueryStringW(flags, kind, target, "open", null, ref length);
        if (length == 0 || length > 32768) return null;
        var buffer = new StringBuilder((int)length);
        return AssocQueryStringW(flags, kind, target, "open", buffer, ref length) >= 0 ? buffer.ToString() : null;
    }
    static List<ClaudeStartApp> ClaudeStartApps()
    {
        lock (ClaudeAppsLock)
        {
            if (claudeApps != null && DateTimeOffset.UtcNow - claudeAppsAt < TimeSpan.FromMinutes(10)) return claudeApps;
            object? shell = null, folder = null, items = null;
            var rows = new List<ClaudeStartApp>();
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
                folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
                if (folder == null) throw new InvalidOperationException("Windows AppsFolder unavailable");
                items = ((dynamic)folder).Items();
                for (var i = 0; i < (int)((dynamic)items).Count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        var name = (string)((dynamic)item).Name;
                        var id = (string)((dynamic)item).Path;
                        try { var explicitId = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string; if (!string.IsNullOrEmpty(explicitId)) id = explicitId; } catch (COMException) { }
                        string? target = null; var args = "";
                        try { target = ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string; args = ((dynamic)item).ExtendedProperty("System.Link.Arguments") as string ?? ""; } catch (COMException) { }
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) rows.Add(new(name, id, target, args));
                    }
                    catch (Exception error) when (error is COMException or InvalidCastException) { }
                    finally { ClaudeRelease(item); }
                }
                claudeApps = rows; claudeAppsAt = DateTimeOffset.UtcNow; return rows;
            }
            catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { throw new ActionRefusedException("Start menu unreadable; give the executable's absolute path. Nothing was launched: " + error.GetType().Name); }
            finally { ClaudeRelease(items); ClaudeRelease(folder); ClaudeRelease(shell); }
        }
    }
    static ClaudeStartApp? ClaudePickApp(string wanted, IEnumerable<ClaudeStartApp> apps)
    {
        var rows = apps.ToList();
        var exact = rows.Where(a => a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || a.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 1) throw new ActionRefusedException("Several Start menu applications have this exact name; give their AppID or executable path");
        if (exact.Count == 1) return exact[0];
        var matches = rows.Where(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name.Length).ToList();
        if (matches.Count == 0) return null;
        // The former server picked the shortest matching display name. Equal candidates remain ambiguous.
        if (matches.Count > 1 && matches[0].Name.Length == matches[1].Name.Length) throw new ActionRefusedException("Ambiguous Start menu name; give its exact name or AppID");
        return matches[0];
    }
    static bool ClaudeBrowser(string exe)
    {
        if (ClaudeBrowserNames.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase)) return true;
        var folder = Path.GetDirectoryName(exe);
        for (var i = 0; i < 3 && folder != null; i++, folder = Path.GetDirectoryName(folder))
            if (ClaudeBrowserNames.Concat(new[] { "msedge.dll", "chrome.dll" }).Any(name => File.Exists(Path.Combine(folder, name)))) return true;
        return false;
    }
    static bool ClaudeChromium(string exe) => ClaudeBrowser(exe) || Path.GetFileName(exe).Equals("electron.exe", StringComparison.OrdinalIgnoreCase) || Program.IsChromiumFamily(exe);
    static string? ClaudeSwitchKey(string token)
    {
        var prefix = token.StartsWith("--") ? 2 : token.StartsWith('-') || token.StartsWith('/') ? 1 : 0;
        return prefix > 0 && token.Length > prefix ? token[prefix..].Split('=')[0].ToLowerInvariant() : null;
    }
    static void ClaudeCheckExecutable(string exe, string arguments, bool devtools)
    {
        if (GardeFous.BlockReason(Path.GetFileName(exe), "", GardeFous.CurrentAgent) is { } blocked) throw new ActionRefusedException(blocked);
        var browser = ClaudeBrowser(exe); var chromium = ClaudeChromium(exe);
        if (devtools && browser && !ClaudeBrowserNames.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))
            throw new ActionRefusedException("DevTools needs the browser's main executable; a browser proxy would open the user's profile");
        if (devtools && !chromium) throw new ActionRefusedException("devtools=true requires a verified Chromium or Electron executable");
        if ((chromium || devtools) && arguments.Contains("single-argument", StringComparison.OrdinalIgnoreCase)) throw new ActionRefusedException("single-argument would neutralize the tool's own profile/control switches");
        foreach (var token in Program.SplitArgs(arguments).Select(t => t.Trim()))
        {
            if ((chromium || devtools) && token == "--") throw new ActionRefusedException("-- would neutralize the tool's own profile/control switches");
            var key = ClaudeSwitchKey(token);
            if (key != null && ClaudeControlSwitches.Contains(key)) throw new ActionRefusedException("Launch switch opens a control channel; use devtools=true explicitly");
            if (key is "user-data-dir" or "profile-directory" && (browser || devtools)) throw new ActionRefusedException("A browser profile is never supplied by the model; devtools=true creates a new profile");
        }
    }
    static void ClaudeCheckAppId(string id, string name, string args, bool devtools)
    {
        if (devtools) throw new ActionRefusedException("devtools=true needs the full Chromium/Electron executable path, not an opaque Start menu shortcut");
        foreach (var label in new[] { name, id.Split('!')[0].Split('_')[0], id.Split('!')[0].Split('_')[0].Split('.').Last() })
            if (GardeFous.BlockReason(label, "", GardeFous.CurrentAgent) is { } blocked) throw new ActionRefusedException(blocked);
        if (args.Contains("single-argument", StringComparison.OrdinalIgnoreCase) || Program.SplitArgs(args).Any(t => t.Trim() == "--")) throw new ActionRefusedException("Opaque Start menu arguments may not neutralize the control/profile guards");
        foreach (var token in Program.SplitArgs(args).Select(t => t.Trim()))
            if (ClaudeSwitchKey(token) is { } key && (ClaudeControlSwitches.Contains(key) || key is "user-data-dir" or "profile-directory")) throw new ActionRefusedException("Opaque Start menu arguments may not select a browser profile or control channel");
    }
    static bool ClaudePe(string path)
    {
        try
        {
            using var file = File.OpenRead(path); var header = new byte[4096]; var count = file.Read(header);
            if (count < 0x40 || header[0] != 'M' || header[1] != 'Z') return false;
            var offset = BitConverter.ToInt32(header, 0x3c);
            return offset >= 0 && offset <= count - 0x5e && header[offset] == 'P' && header[offset + 1] == 'E' && header[offset + 2] == 0 && header[offset + 3] == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    static ClaudeLaunchPlan PlanClaudeLaunch(string raw, string args, bool devtools, bool restart, IReadOnlyList<ClaudeStartApp>? catalogue = null)
    {
        raw = ClaudeExpandPath(raw); args = args.Trim();
        if (raw.Length == 0) throw new ActionRefusedException("Nothing to launch");
        if (raw.StartsWith('"') && raw.EndsWith('"') && (File.Exists(raw[1..^1]) || Directory.Exists(raw[1..^1]))) raw = raw[1..^1];
        var scheme = Regex.Match(raw, @"^([a-z][a-z0-9+.-]+):", RegexOptions.IgnoreCase);
        if (scheme.Success && !File.Exists(raw) && !Directory.Exists(raw))
        {
            if (args.Length > 0 || devtools || restart) throw new ActionRefusedException("Opening an address takes no args, devtools or restart");
            return new("uri", raw, "", null, null, scheme.Groups[1].Value.ToLowerInvariant(), false, false, []);
        }
        if ((Directory.Exists(raw) || File.Exists(raw)) && !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !ClaudePe(raw))
        {
            if (args.Length > 0 || devtools || restart) throw new ActionRefusedException("Opening a document or folder takes no args, devtools or restart");
            return new(Directory.Exists(raw) ? "folder" : "document", Path.GetFullPath(raw), "", null, null, Path.GetFileName(raw), false, false, []);
        }
        var exe = File.Exists(raw) ? Path.GetFullPath(raw) : Program.ResolveApp(raw);
        if (exe == null && (raw.Equals("steam", StringComparison.OrdinalIgnoreCase) || raw.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))) exe = Steam.FindExecutable();
        ClaudeStartApp? start = null;
        if (exe == null)
        {
            var apps = catalogue ?? ClaudeStartApps();
            start = ClaudePickApp(raw, apps);
            if (start == null)
            {
                var split = Program.SplitLaunch(raw, args); raw = ClaudeExpandPath(split.Target); args = split.Args;
                exe = File.Exists(raw) ? Path.GetFullPath(raw) : Program.ResolveApp(raw);
                start = exe == null ? ClaudePickApp(raw, apps) : null;
            }
        }
        if (start != null)
        {
            var target = ClaudeExpandPath(start.Target ?? ClaudeKnownFolderPath(start.Id));
            if (File.Exists(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && start.Arguments.Trim().Length == 0) exe = Path.GetFullPath(target);
            else
            {
                if (restart) throw new ActionRefusedException("restart=true is supported only for the genuine Steam client");
                ClaudeCheckAppId(start.Id, start.Name, args, devtools);
                if (start.Id.Contains('!')) return new("packaged", start.Id, args, null, start.Id, start.Name, false, false, []);
                if (File.Exists(target)) ClaudeCheckExecutable(target, "", false);
                return new("shortcut", @"shell:AppsFolder\" + start.Id, "", null, start.Id, start.Name, false, false,
                    args.Length > 0 ? ["Arguments ignored: an existing Start menu shortcut is launched with its own arguments."] : []);
            }
        }
        if (exe == null && raw.Contains('!') && !raw.Contains('\\') && !raw.Contains('/'))
        {
            if (restart) throw new ActionRefusedException("restart=true is supported only for the genuine Steam client");
            ClaudeCheckAppId(raw, raw, args, devtools); return new("packaged", raw, args, null, raw, raw, false, false, []);
        }
        if (exe == null) throw new ActionRefusedException("Neither an executable nor a Start menu application; give the existing absolute path");
        if (Steam.Resolve(raw, exe) is { } steam)
        {
            if (Steam.ArgsProblem(args) is { } problem) throw new ActionRefusedException(problem);
            return new("steam", steam, args, steam, null, "Steam", devtools, restart, []);
        }
        if (restart) throw new ActionRefusedException("restart=true is supported only for the genuine Steam client");
        ClaudeCheckExecutable(exe, args, devtools);
        return new("executable", exe, args, exe, null, start?.Name ?? Path.GetFileName(exe), devtools, false, []);
    }
    static string ClaudeQuote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        return QuoteClaudeArgument(argument);
    }
    static string QuoteClaudeArgument(string argument)
    {
        var result = new StringBuilder("\""); var slash = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slash++; continue; }
            result.Append('\\', c == '"' ? slash * 2 + 1 : slash); result.Append(c); slash = 0;
        }
        result.Append('\\', slash * 2); return result.Append('"').ToString();
    }
    static string ClaudeChromiumArguments(string exe, string arguments, int? port = null, string? profile = null)
    {
        if (!ClaudeChromium(exe)) return arguments;
        var features = new List<string>(); var rest = new List<string>();
        foreach (var token in Program.SplitArgs(arguments).Select(t => t.Trim()))
            if (ClaudeSwitchKey(token) == "disable-features") features.AddRange(token[(token.IndexOf('=') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else rest.Add(token);
        features.Add("CalculateNativeWinOcclusion");
        if (profile != null) features.Add("msImplicitSignin");
        var own = new List<string> { "--disable-features=" + string.Join(',', features.Distinct(StringComparer.Ordinal)), "--force-renderer-accessibility" };
        if (port != null) { own.Add("--remote-debugging-port=" + port); own.Add("--remote-allow-origins=http://127.0.0.1:" + port); }
        if (profile != null) own.AddRange(["--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-sync"]);
        return string.Join(' ', rest.Concat(own).Select(ClaudeQuote));
    }
    static string ClaudeProcessCommandLine(Process process)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (NtQueryInformationProcess(process.Handle, 60, buffer, 65536, out _) < 0) return "";
            var length = (ushort)Marshal.ReadInt16(buffer); var text = Marshal.ReadIntPtr(buffer, 8);
            return text == 0 ? "" : Marshal.PtrToStringUni(text, length / 2) ?? "";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static bool ClaudeSameExecutable(long pid, string executable)
    {
        try { using var process = Process.GetProcessById(checked((int)pid)); return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    static HashSet<uint> ClaudeContentPids(long window)
    {
        var result = new HashSet<uint> { Win32.ProcessOf((nint)window) };
        Win32.EnumChildWindows((nint)window, (child, _) => { result.Add(Win32.ProcessOf(child)); return true; }, 0); result.Remove(0); return result;
    }
    static uint ClaudeActivate(string id, string arguments, string? document = null)
    {
        object? instance = null; nint item = 0, items = 0;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true)!);
            var manager = (IClaudeActivationManager)instance!;
            int hr; uint pid;
            if (document == null) { Program.InputStarted = true; hr = manager.ActivateApplication(id, arguments.Length > 0 ? arguments : null, 2, out pid); }
            else
            {
                var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); var arrayId = new Guid("B63EA76D-1F85-456F-A19C-48159EFA858B");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(document, 0, in itemId, out item));
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item, in arrayId, out items));
                Program.InputStarted = true; hr = manager.ActivateForFile(id, items, "open", out pid);
            }
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return pid;
        }
        finally { if (items != 0) Marshal.Release(items); if (item != 0) Marshal.Release(item); ClaudeRelease(instance); }
    }
    // E_APPLICATION_NOT_REGISTERED: the activation broker rejected the unsupported file contract, before an app
    // could receive that contract. Unlike an arbitrary COM/transport failure, this explicit rejection permits the
    // ordinary file association path (notably packaged desktop apps). An uncertain dispatch is never replayed.
    static bool ClaudeFileContractRejected(int hr) => hr == unchecked((int)0x80270254);
    static bool ClaudeDocumentShown((long Id, string App, string Title, long Pid, long Owner, string OwnerTitle) window, string document)
    {
        var name = Path.GetFileName(document);
        if (window.Title.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var snapshot = ObjectOf(ExecuteInternal(new { operation = "inspect", windowId = window.Id, app = window.App, expectedPid = window.Pid }));
            return Elements(snapshot).OfType<JsonObject>().Any(e => !Bool(e, "password") && Str(e, "name").Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    static string ClaudeNewProfile()
    {
        var root = Path.Combine(NativeStateRoot(GardeFous.CurrentAgent), "devtools-profiles"); Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ActionRefusedException("DevTools profile root may not be a reparse point");
        var path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ".nexus-profile"), JsonSerializer.Serialize(new { created = DateTimeOffset.UtcNow, owner = "nexus-windows-" + GardeFous.CurrentAgent, id = Path.GetFileName(path) }));
        return path;
    }
    static JsonObject LaunchClaudeNative(SessionState state, JsonObject args, bool modern)
    {
        if (state.Agent != GardeFous.CurrentAgent) throw new ActionRefusedException("Launch cannot change the process profile");
        var show = Str(args, "then", "auto");
        if (show is not ("auto" or "elements" or "text" or "image" or "both" or "changes" or "none")) throw new ActionRefusedException("Unknown launch then mode");
        var wait = Number(args, "wait_s", 15);
        if (!double.IsFinite(wait) || wait < 0) throw new ActionRefusedException("wait_s must be finite and nonnegative");
        var plan = PlanClaudeLaunch(Str(args, "app"), Str(args, "args"), Bool(args, "devtools"), Bool(args, "restart"));
        state.CurrentObservation = null; state.CurrentObservationId = null;
        NativeMcpGuard.WaitForPause();
        using var token = ForegroundToken.Acquire("launch application"); NativeMcpGuard.RequirePause(); EmergencyStop.ThrowIfStopped();
        var probe = Win32.Probe(); var returned = "not-taken";
        void ReturnForeground()
        {
            if (Win32.Probe().LastInput != probe.LastInput) return;
            var front = Win32.GetForegroundWindow();
            if (front != 0 && front != probe.Foreground) returned = ForegroundReturn.GiveBack(probe.Foreground, front, probe.LastInput, sameProcess: true);
        }
        if (plan.Kind == "steam" || plan.Kind == "uri" && plan.Name == "ms-settings")
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var request = new JsonObject { ["operation"] = "launch", ["target"] = plan.Target, ["args"] = plan.Arguments, ["devtools"] = plan.Devtools, ["restart"] = plan.Restart, ["quietMs"] = 1618 };
                var result = ObjectOf(ExecuteInternal(request));
                if (plan.Kind == "steam" && plan.Devtools) RegisterClaudeSteamLease(state, result, plan.Executable!);
                var response = ClaudeFinishLaunchGuarded(state, result, show, modern, ReturnForeground, probe.LastInput, () => returned);
                Journal.WriteEntry(state.Agent, Version, state.SessionId, plan.Name, "launch", Bool(result, "opened") ? "verified" : "unknown", "native:" + plan.Kind, timer.ElapsedMilliseconds, false, null);
                return response;
            }
            finally { ReturnForeground(); }
        }
        var before = new HashSet<long>(); Win32.EnumWindows((hwnd, _) => { before.Add((long)hwnd); return true; }, 0);
        var started = DateTime.UtcNow; var clock = Stopwatch.StartNew(); var notes = plan.Notes.ToList();
        var profile = plan.Devtools && ClaudeBrowser(plan.Executable!) ? ClaudeNewProfile() : null;
        int? port = null; uint activatedPid = 0; ClaudeDevtoolsLease? lease = null;
        if (plan.Devtools) { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); }
        var launchArgs = plan.Executable != null ? ClaudeChromiumArguments(plan.Executable, plan.Arguments, port, profile) : plan.Arguments;
        string? associatedExecutable = plan.Kind switch
        {
            "uri" => ClaudeAssociation(plan.Name, 2, true),
            "document" => ClaudeAssociation(Path.GetExtension(plan.Target), 2),
            _ => null,
        };
        var associatedAppId = plan.Kind == "document" ? ClaudeAssociation(Path.GetExtension(plan.Target), 21) : null;
        if (associatedAppId?.Contains('!') != true) associatedAppId = null;
        if (associatedExecutable != null) ClaudeCheckExecutable(associatedExecutable, "", false);
        if (associatedAppId != null) ClaudeCheckAppId(associatedAppId, associatedAppId, "", false);
        var folderIdentity = plan.Kind == "folder" ? Explorer.Identity(plan.Target) : null;
        var selected = new List<(long Id, string App, string Title, long Pid, long Owner, string OwnerTitle)>(); var verified = false;
        var selectedBirths = new Dictionary<long, string?>();
        void Select(List<(long Id, string App, string Title, long Pid, long Owner, string OwnerTitle)> candidates, bool proof)
        {
            selected = candidates; verified = proof; selectedBirths.Clear();
            foreach (var candidate in candidates) selectedBirths[candidate.Id] = Program.ProcessStartedUtc(checked((uint)candidate.Pid));
        }
        try
        {
            if (plan.Kind == "packaged") activatedPid = ClaudeActivate(plan.AppId!, plan.Arguments);
            else if (plan.Kind == "document" && associatedAppId is { } appId)
            {
                ClaudeCheckAppId(appId, appId, "", false);
                try { activatedPid = ClaudeActivate(appId, "", plan.Target); }
                catch (COMException error) when (ClaudeFileContractRejected(error.HResult))
                {
                    notes.Add("The packaged desktop app rejected the file contract; using its ordinary Windows file association.");
                    UserApplication.Start(new ProcessStartInfo(plan.Target), () => Program.InputStarted = true, 4);
                }
            }
            else UserApplication.Start(new ProcessStartInfo(plan.Target) { Arguments = launchArgs, UseShellExecute = false, WorkingDirectory = plan.Executable == null ? "" : Path.GetDirectoryName(plan.Executable)! }, () => Program.InputStarted = true, 4);
            var timeout = Math.Max(1, wait);
            while (clock.Elapsed.TotalSeconds < timeout)
            {
                EmergencyStop.ThrowIfStopped(); ReturnForeground();
                var windows = ListDesktopWindows(false);
                if (plan.Devtools && lease == null)
                {
                    lease = ClaudeFindDevtools(plan.Executable!, port!.Value, profile, started);
                    if (lease != null)
                    {
                        var bag = ClaudeOwnedLaunches.GetValue(state, _ => new ClaudeLaunchLeases()); lock (bag.Sync) bag.Items.Add(lease);
                        lease.Watch();
                    }
                }
                var matches = windows.Where(w => plan.Kind switch
                {
                    "folder" => folderIdentity is { } identity && Explorer.Shows((nint)w.Id, identity),
                    // Reading UIA to prove a document is limited to an attributed handler or a title carrying its
                    // complete filename. Other application windows are never inspected as part of a file launch.
                    "document" => (activatedPid != 0 ? ClaudeContentPids(w.Id).Contains(activatedPid)
                        : associatedAppId != null && w.App.Equals(associatedAppId, StringComparison.OrdinalIgnoreCase)
                        || associatedExecutable != null && ClaudeContentPids(w.Id).Any(pid => ClaudeSameExecutable(pid, associatedExecutable))
                        || w.Title.Contains(Path.GetFileName(plan.Target), StringComparison.OrdinalIgnoreCase)) && ClaudeDocumentShown(w, plan.Target),
                    "packaged" => w.App.Equals(plan.AppId, StringComparison.OrdinalIgnoreCase) || activatedPid != 0 && ClaudeContentPids(w.Id).Contains(activatedPid),
                    "executable" => lease != null ? ClaudeContentPids(w.Id).Contains((uint)lease.Process.Id) : ClaudeSameExecutable(w.Pid, plan.Executable!),
                    "shortcut" => w.App.Equals(plan.AppId, StringComparison.OrdinalIgnoreCase),
                    "uri" => !before.Contains(w.Id) && associatedExecutable != null && ClaudeSameExecutable(w.Pid, associatedExecutable),
                    _ => false,
                }).ToList();
                var fresh = matches.Where(w => !before.Contains(w.Id)).ToList();
                if (fresh.Count > 0 && (!plan.Devtools || lease != null && ClaudePortOwner(lease.Process.Id, port!.Value))) { Select(fresh, plan.Kind != "uri" || associatedExecutable != null); break; }
                if (matches.Count > 0 && clock.Elapsed.TotalSeconds >= 1.5 && (!plan.Devtools || lease != null && ClaudePortOwner(lease.Process.Id, port!.Value))) { Select(matches, plan.Kind is "folder" or "document" or "packaged"); break; }
                Thread.Sleep(50);
            }
            if (plan.Devtools)
            {
                if (lease == null || selected.Count != 1) throw new InvalidOperationException("DevTools launch effect unknown: no unambiguous window/process/port was established; any identified new instance remains guarded. Inspect before another launch");
                lease.Check(); notes.Add(profile == null ? "DevTools on the newly launched application's own session." : "DevTools browser: a new isolated profile, implicit sign-in disabled, no sync; account guard active.");
            }
            if (selected.Any(w => before.Contains(w.Id))) notes.Add("Existing window reused: it may contain the user's other tabs/documents; do not close the whole window.");
            if (selected.Count == 0) notes.Add("Launch dispatched once; no window could be attributed. The application may reuse an existing window; run windows before deciding anything else.");
        }
        finally { ReturnForeground(); }
        var rows = new JsonArray(selected.Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["app"] = w.App, ["title"] = w.Title, ["pid"] = w.Pid,
            ["processStartedUtc"] = selectedBirths[w.Id], ["shared"] = before.Contains(w.Id) }).ToArray());
        var data = new JsonObject { ["ok"] = true, ["opened"] = verified ? JsonValue.Create(true) : null, ["via"] = plan.Kind, ["windows"] = rows,
            ["foregroundReturned"] = returned, ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) };
        if (port != null) data["devtoolsPort"] = port.Value;
        try
        {
            var response = ClaudeFinishLaunchGuarded(state, data, show, modern, ReturnForeground, probe.LastInput, () => returned);
            Journal.WriteEntry(state.Agent, Version, state.SessionId, plan.Kind is "document" or "folder" ? null : plan.Name, "launch", verified ? "verified" : "unknown", "native:" + plan.Kind, clock.ElapsedMilliseconds, false, null);
            return response;
        }
        finally { ReturnForeground(); }
    }
    static JsonObject ClaudeFinishLaunchGuarded(SessionState state, JsonObject data, string show, bool modern, Action giveBack, uint notedInput, Func<string> returned)
    {
        var response = ClaudeFinishLaunch(state, data, show, modern);
        // The former Claude Watch kept returning late activation for 1.6 s after rendering. Keep the shared
        // foreground token for this bounded tail, rather than restoring asynchronously after another AI acquired it.
        var tail = Stopwatch.StartNew();
        while (tail.ElapsedMilliseconds < 1618 && Win32.Probe().LastInput == notedInput)
        { giveBack(); Thread.Sleep(50); }
        giveBack(); data["foregroundReturned"] = returned();
        response["content"]!.AsArray()[0] = TextBlock(data);
        return response;
    }
    static JsonObject ClaudeFinishLaunch(SessionState state, JsonObject data, string show, bool modern)
    {
        var windows = data["windows"] as JsonArray; var rows = windows?.OfType<JsonObject>().ToList() ?? [];
        if (rows.Count == 0 && data["window"] is JsonValue windowValue && windowValue.TryGetValue<long>(out var id) && id != 0)
            rows.Add(new JsonObject { ["id"] = id, ["app"] = data["app"]?.DeepClone(), ["pid"] = data["pid"]?.DeepClone(), ["title"] = data["title"]?.DeepClone(), ["processStartedUtc"] = data["processStartedUtc"]?.DeepClone() });
        var content = new JsonArray { TextBlock(data) };
        if (rows.Count == 1)
        {
            var row = rows[0]; var expected = ((long)Number(row, "id"), Str(row, "app"), Str(row, "title"), (long)Number(row, "pid"));
            var current = ListDesktopWindows(false).Where(w => w.Id == expected.Item1 && w.Pid == expected.Item4 && w.App.Equals(expected.Item2, StringComparison.OrdinalIgnoreCase)).ToList();
            if (current.Count != 1) throw new ActionRefusedException("Launched window identity changed before its observation; launch was already dispatched once, do not replay");
            var birth = Str(row, "processStartedUtc");
            birth = birth.Length == 0 ? Program.ProcessStartedUtc(checked((uint)expected.Item4)) ?? "" : birth;
            if (birth.Length == 0 || Program.ProcessStartedUtc(checked((uint)expected.Item4)) != birth) throw new ActionRefusedException("Launched process identity unavailable or changed before observation; launch was already dispatched once, do not replay");
            state.BoundWindow = expected;
            if (show != "none")
            {
                try
                {
                    var observed = ObserveBoundNative(state, new JsonObject { ["window"] = row["id"]!.DeepClone(), ["show"] = show }, modern, expected, birth);
                    foreach (var block in observed["content"]!.AsArray()) content.Add(block!.DeepClone());
                    data["observation"] = observed["structuredContent"]?.DeepClone();
                }
                catch (Exception error) when (error is not ActionRefusedException && error is (InvalidOperationException or System.ComponentModel.Win32Exception)) { content.Add(TextBlock("Application launched, observation unavailable: " + error.Message)); }
            }
        }
        data["status"] = Bool(data, "opened") ? "opened" : "unknown";
        return new JsonObject { ["content"] = content, ["structuredContent"] = data };
    }
    static ClaudeDevtoolsLease? ClaudeFindDevtools(string executable, int port, string? profile, DateTime started)
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                if (process.StartTime.ToUniversalTime() < started.AddMilliseconds(-50) || !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                var tokens = Program.SplitArgs(ClaudeProcessCommandLine(process));
                if (tokens.Any(t => t.StartsWith("--type=", StringComparison.OrdinalIgnoreCase)) || !tokens.Contains("--remote-debugging-port=" + port, StringComparer.Ordinal)) continue;
                if (profile != null && !tokens.Contains("--user-data-dir=" + profile, StringComparer.OrdinalIgnoreCase)) continue;
                keep = true; matches.Add(process);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { if (!keep) process.Dispose(); }
        }
        if (matches.Count != 1) { foreach (var process in matches) process.Dispose(); return null; }
        return new ClaudeDevtoolsLease(matches[0], executable, port, profile);
    }
    static bool ClaudePortOwner(int pid, int port)
    {
        var listeners = Steam.Listeners().Where(l => l.Port == port).ToList();
        return listeners.Count > 0 && listeners.All(l => l.Pid == pid && l.Address is "127.0.0.1" or "::1");
    }
    sealed class ClaudeSteamIdentity(string executable, List<(Process Process, long Birth)> roots)
    {
        public string Executable { get; } = executable;
        public List<(Process Process, long Birth)> Roots { get; } = roots;
        public HashSet<uint> Helpers()
        {
            var table = Steam.Table(); var live = Steam.Roots(Executable, table);
            var held = new HashSet<uint>();
            foreach (var root in Roots)
            {
                if (root.Process.HasExited || root.Process.StartTime.ToUniversalTime().Ticks != root.Birth
                    || !live.Contains((uint)root.Process.Id)) throw new ActionRefusedException("The registered Steam client identity changed; no further input sent");
                held.Add((uint)root.Process.Id);
            }
            return Steam.Helpers(Executable, table, held);
        }
    }
    static uint ClaudeSteamPortOwner(IEnumerable<(string Address, uint Pid)> listeners, HashSet<uint> helpers)
    {
        var rows = listeners.ToList();
        if (!Steam.OwnsLoopback(rows, helpers)) throw new ActionRefusedException("Steam DevTools listener is not owned exclusively by its registered local helpers");
        var owners = rows.Select(l => l.Pid).Distinct().ToList();
        if (owners.Count != 1) throw new ActionRefusedException("Steam DevTools has multiple process owners; no unique native input lease can be registered");
        return owners[0];
    }
    static void RegisterClaudeSteamLease(SessionState state, JsonObject result, string executable)
    {
        if (state.Agent != GardeFous.CurrentAgent || !Steam.IsExecutable(executable)) throw new ActionRefusedException("Steam session registration needs the genuine client and a fixed profile");
        var port = (int)Number(result["steam"], "devtoolsPort");
        if (port <= 0 || port >= 65536 || !Bool(result["steam"], "devtoolsRequested")) return; // Launch already reports that CDP is unavailable.
        var table = Steam.Table(); var roots = Steam.Roots(executable, table); var held = new List<(Process Process, long Birth)>(); Process? owner = null;
        try
        {
            if (roots.Count == 0) throw new ActionRefusedException("Steam launch returned, but its client identity is unavailable; no DevTools lease registered");
            foreach (var pid in roots)
            {
                var process = Process.GetProcessById(checked((int)pid)); var birth = process.StartTime.ToUniversalTime().Ticks;
                held.Add((process, birth));
                if (!string.Equals(Steam.Canonical(process.MainModule?.FileName), executable, StringComparison.OrdinalIgnoreCase)) throw new ActionRefusedException("Steam client path changed during registration");
            }
            var identity = new ClaudeSteamIdentity(executable, held); var helpers = identity.Helpers();
            var listeners = Steam.Listeners().Where(l => l.Port == port).ToList();
            var ownerPid = ClaudeSteamPortOwner(listeners.Select(l => (l.Address, l.Pid)), helpers);
            owner = Process.GetProcessById(checked((int)ownerPid)); var ownerPath = Steam.Canonical(owner.MainModule?.FileName);
            if (ownerPath == null || !helpers.Contains((uint)owner.Id)) throw new ActionRefusedException("Steam helper identity changed during registration");
            var lease = new ClaudeDevtoolsLease(owner, ownerPath, port, null, identity); lease.Check();
            var bag = ClaudeOwnedLaunches.GetValue(state, _ => new ClaudeLaunchLeases());
            lock (bag.Sync)
            {
                // Reusing this same genuine Steam session refreshes its one registration, not an additional grant.
                foreach (var previous in bag.Items.Where(l => l.OwnSession && l.Process.Id == lease.Process.Id && l.Port == lease.Port).ToList())
                { bag.Items.Remove(previous); previous.ReleaseHandles(); }
                bag.Items.Add(lease);
            }
            result["steam"]!["devtoolsLease"] = "registered-genuine-steam-session";
            held = []; owner = null; // The lease now owns these identity handles.
        }
        finally { owner?.Dispose(); foreach (var root in held) root.Process.Dispose(); }
    }
    sealed class ClaudeDevtoolsLease(Process process, string executable, int port, string? profile, ClaudeSteamIdentity? steam = null)
    {
        public Process Process { get; } = process;
        public int Port { get; } = port;
        public bool OwnSession => steam != null;
        readonly long birth = process.StartTime.ToUniversalTime().Ticks;
        readonly object sync = new();
        string? failure;
        public bool Includes(long window)
        {
            var pids = ClaudeContentPids(window);
            if (steam == null) return pids.Contains((uint)Process.Id);
            return steam.Helpers().Overlaps(pids);
        }
        public void ReleaseHandles() { Process.Dispose(); if (steam != null) foreach (var root in steam.Roots) root.Process.Dispose(); }
        public void Check()
        {
            lock (sync)
            {
                if (failure != null) throw new ActionRefusedException(failure);
                if (Process.HasExited || Process.StartTime.ToUniversalTime().Ticks != birth || !string.Equals(Process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new ActionRefusedException("The DevTools process identity changed; nothing sent");
                if (steam != null)
                {
                    if (!steam.Helpers().Contains((uint)Process.Id) || !ClaudePortOwner(Process.Id, Port)) throw new ActionRefusedException("The registered Steam helper/port ownership changed; nothing sent");
                    return; // Steam's existing signed-in session is the explicitly requested session, never a disposable browser.
                }
                var reason = ClaudeProfileAccount(profile);
                if (reason == null && ClaudePortOwner(Process.Id, Port)) reason = ClaudeDevtoolsAccountPage(Port);
                if (reason == null) return;
                failure = "Account detected in the tool's DevTools instance; closure is not yet confirmed. No further action is permitted. " + reason;
                Process.Kill(entireProcessTree: true); Process.WaitForExit(5000);
                if (Process.HasExited) failure = "Account detected in the tool's DevTools instance; it was closed. No user profile was selected. " + reason;
                if (profile != null && Process.HasExited) ClaudeRemoveOwnedProfile(profile);
                throw new ActionRefusedException(failure);
            }
        }
        public void Watch() => _ = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 15)
            {
                await Task.Delay(500);
                try { if (Process.HasExited) return; Check(); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
            }
        });
    }
    static bool ClaudeHasValue(JsonNode? value) => value switch
    {
        null => false, JsonArray a => a.Count > 0, JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var n) => n != 0,
        _ => true,
    };
    static string? ClaudeProfileAccount(string? profile)
    {
        if (profile == null) return null;
        JsonObject Read(string path)
        {
            if (!File.Exists(path)) return new();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fresh-profile metadata may not traverse a reparse point");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Invalid fresh-profile metadata"); }
                catch (JsonException) when (attempt < 2) { Thread.Sleep(50); }
            }
            throw new InvalidDataException("Fresh-profile metadata cannot be verified");
        }
        var names = new HashSet<string>(StringComparer.Ordinal) { "Default" };
        if (Read(Path.Combine(profile, "Local State"))["profile"]?["info_cache"] is JsonObject cache)
            foreach (var pair in cache)
            {
                if (pair.Value is JsonObject metadata && metadata.Any(k => (k.Key is "user_name" or "gaia_id" || k.Key.StartsWith("edge_account_", StringComparison.Ordinal)) && ClaudeHasValue(k.Value))) return "Account marker in fresh profile Local State";
                if (pair.Key is not ("." or "..") && pair.Key.IndexOfAny(['/', '\\', ':']) < 0) names.Add(pair.Key);
            }
        foreach (var name in names)
        {
            var preferences = Read(Path.Combine(profile, name, "Preferences"));
            if (ClaudeHasValue(preferences["account_info"])) return "Account marker in fresh profile Preferences";
            if (preferences["google"]?["services"] is JsonObject services && new[] { "last_gaia_id", "last_username", "last_signed_in_username", "last_account_id" }.Any(k => ClaudeHasValue(services[k]))) return "Account services marker in fresh profile Preferences";
        }
        return null;
    }
    static string? ClaudeDevtoolsAccountPage(int port)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
            using var reply = http.GetAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult();
            if (!reply.IsSuccessStatusCode) return null;
            var bytes = reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(); if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("DevTools target response too large");
            using var targets = JsonDocument.Parse(bytes);
            return targets.RootElement.EnumerateArray().Any(t => t.TryGetProperty("url", out var u) && new[] { "edge://sync", "edge://signin", "chrome://sync", "chrome://signin" }.Any(prefix => (u.GetString() ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) ? "Browser sign-in/sync page appeared" : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }
    static void ClaudeRemoveOwnedProfile(string path)
    {
        var root = Path.GetFullPath(Path.Combine(NativeStateRoot(GardeFous.CurrentAgent), "devtools-profiles")); path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(path, ".nexus-profile"))) return;
        try
        {
            var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(path, ".nexus-profile")))!;
            if (Str(marker, "owner") != "nexus-windows-" + GardeFous.CurrentAgent || Str(marker, "id") != Path.GetFileName(path)) return;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var directory))
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                { var attrs = File.GetAttributes(child); if ((attrs & FileAttributes.ReparsePoint) != 0) return; if ((attrs & FileAttributes.Directory) != 0) pending.Push(child); }
            Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
    static void GuardClaudeDevtools(SessionState state)
    {
        if (state.BoundWindow == null || !ClaudeOwnedLaunches.TryGetValue(state, out var bag)) return;
        lock (bag.Sync) foreach (var lease in bag.Items) if (lease.Includes(state.BoundWindow.Value.Id)) lease.Check();
    }
    public static int LaunchCompatibilitySelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd()); var results = new JsonArray();
        foreach (var item in input.RootElement.EnumerateArray())
        {
            try
            {
                var value = ObjectOf(item); var apps = (value["catalogue"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new ClaudeStartApp(Str(a, "name"), Str(a, "id"), Str(a, "target") is { Length: > 0 } t ? t : null, Str(a, "args"))).ToList();
                if (value["steamPortFixture"] is JsonObject steam)
                {
                    var helpers = steam["helpers"]!.AsArray().Select(p => checked((uint)p!.GetValue<long>())).ToHashSet();
                    var listeners = steam["listeners"]!.AsArray().OfType<JsonObject>().Select(l => (Str(l, "address"), checked((uint)Number(l, "pid"))));
                    results.Add(new JsonObject { ["steamOwner"] = ClaudeSteamPortOwner(listeners, helpers), ["sessionKind"] = "genuine-steam-session" }); continue;
                }
                var plan = PlanClaudeLaunch(Str(value, "app"), Str(value, "args"), Bool(value, "devtools"), Bool(value, "restart"), apps);
                var data = new JsonObject { ["kind"] = plan.Kind, ["target"] = plan.Target, ["args"] = plan.Arguments, ["executable"] = plan.Executable, ["appId"] = plan.AppId, ["name"] = plan.Name,
                    ["commandArguments"] = plan.Executable == null ? plan.Arguments : ClaudeChromiumArguments(plan.Executable, plan.Arguments, plan.Devtools ? 16183 : null, plan.Devtools && ClaudeBrowser(plan.Executable) ? @"C:\NexusFixture\fresh profile" : null) };
                data["parsedArguments"] = new JsonArray(Program.SplitArgs(Str(data, "commandArguments")).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                if (value["profileFixture"] is JsonValue) data["accountMarker"] = ClaudeProfileAccount(Str(value, "profileFixture"));
                if (value["activationHResult"] != null) data["fileContractFallbackAllowed"] = ClaudeFileContractRejected(checked((int)Number(value, "activationHResult")));
                results.Add(data);
            }
            catch (Exception error) when (error is ActionRefusedException or InvalidOperationException or InvalidDataException or ArgumentException) { results.Add(new JsonObject { ["refused"] = error.Message }); }
        }
        Console.WriteLine(results.ToJsonString()); return 0;
    }
}
