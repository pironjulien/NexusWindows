using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Le client Valve emploie CEF dans steamwebhelper, pas les commutateurs Chromium d'une application Electron.
// Aucun lancement/arret implicite d'un client deja ouvert : sa session reste celle de Julien.
static class Steam
{
    public const int ShutdownWaitMs = 60000, StartWaitMs = 90000, DevtoolsWaitMs = 10000, DevtoolsPort = 8080;
    public const string ViewSwitches = "-cef-disable-occlusion -cef-force-accessibility";
    static readonly HashSet<string> OwnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "gldriverquery.exe", "gldriverquery64.exe", "vulkandriverquery.exe", "vulkandriverquery64.exe", "conhost.exe"
    };
    static readonly HashSet<string> ControlSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "load-extension", "remote-debugging-port", "remote-debugging-pipe", "remote-debugging-address", "remote-allow-origins",
        "disable-web-security", "inspect", "inspect-brk", "inspect-port", "cef-enable-debugging", "devtools-port",
        "user-data-dir", "profile-directory", "shutdown", "restart"
    };
    public sealed record ProcessRow(uint Pid, uint Parent, string Name);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, Pid; public nuint DefaultHeap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(nint table, ref int size, bool sorted, int family, int tableClass, uint reserved);
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern void GetStartupInfoW(out StartupInfo startup);

    public static void StartNoActivate(ProcessStartInfo info, Action? started = null) =>
        UserApplication.Start(info, started ?? (() => { }), 4 /* SW_SHOWNOACTIVATE */);
    public static int WriteStartupInfo(string path)
    {
        GetStartupInfoW(out var startup);
        File.WriteAllText(path, JsonSerializer.Serialize(new { flags = startup.Flags, showWindow = startup.ShowWindow })); return 0;
    }

    public static string? Canonical(string? path)
    {
        if (path == null || !Path.IsPathFullyQualified(path)) return null;
        using var handle = CreateFileW(path, 0, 7 /* lecture/ecriture/suppression partagees */, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(32768);
        var size = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity) return null;
        var value = buffer.ToString();
        return value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + value[8..]
            : value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    public static bool IsExecutable(string? path)
    {
        path = Canonical(path);
        if (path == null || !File.Exists(path) || !Path.GetFileName(path).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = Path.GetDirectoryName(path)!;
        return File.Exists(Path.Combine(folder, "steamclient64.dll")) || File.Exists(Path.Combine(folder, "steamui.dll"));
    }
    public static string? FindExecutable()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamExe") is string registered)
        {
            var path = Environment.ExpandEnvironmentVariables(registered.Replace('/', '\\').Trim('"'));
            if (IsExecutable(path)) return Canonical(path);
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe");
        return IsExecutable(fallback) ? Canonical(fallback) : null;
    }
    public static string? Resolve(string target, string? resolved)
    {
        if (IsExecutable(resolved)) return Canonical(resolved);
        if (!Path.IsPathFullyQualified(target) && (target.Equals("Steam", StringComparison.OrdinalIgnoreCase) || target.Equals("steam.exe", StringComparison.OrdinalIgnoreCase)))
            return FindExecutable();
        return null;
    }
    public static string? ArgsProblem(string args)
    {
        if (args.Contains("single-argument", StringComparison.OrdinalIgnoreCase)) return "--single-argument would neutralize Steam's startup switches";
        foreach (var token in Program.SplitArgs(args))
        {
            if (token == "--") return "-- would neutralize Steam's startup switches";
            var key = token.StartsWith("--") ? token[2..] : token.StartsWith('-') || token.StartsWith('/') ? token[1..] : "";
            if (ControlSwitches.Contains(key.Split('=')[0])) return $"Switch \"{token}\" opens a control channel or selects a profile; use devtools=true explicitly for Steam";
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri) && uri.Scheme.Equals("steam", StringComparison.OrdinalIgnoreCase)
                && new[] { "exit", "quit", "shutdown", "restart" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                return "Steam closing URIs bypass explicit restart and its running-game check";
        }
        return null;
    }
    public static ProcessStartInfo StartInfo(string exe, string args, bool devtools) => new(exe)
    {
        UseShellExecute = false, CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(exe)!,
        Arguments = string.Join(" ", new[] { args.Trim(), ViewSwitches, devtools ? "-cef-enable-debugging" : "" }.Where(x => x.Length > 0))
    };
    public static List<ProcessRow> Table()
    {
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == -1) throw new ActionRefusedException("Steam process inventory is unavailable; nothing was restarted");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            if (!Process32FirstW(snapshot, ref entry)) throw new ActionRefusedException("Steam process inventory is unavailable; nothing was restarted");
            var rows = new List<ProcessRow>();
            do { rows.Add(new(entry.Pid, entry.Parent, entry.Exe)); } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18 /* ERROR_NO_MORE_FILES */) throw new ActionRefusedException("Steam process inventory changed unreadably; nothing was restarted");
            return rows;
        }
        finally { CloseHandle(snapshot); }
    }
    static string? ProcessPath(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.MainModule?.FileName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
    public static HashSet<uint> Roots(string exe, IEnumerable<ProcessRow> table) => table
        .Where(p => p.Name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && string.Equals(Canonical(ProcessPath(p.Pid)), exe, StringComparison.OrdinalIgnoreCase))
        .Select(p => p.Pid).ToHashSet();
    public static List<ProcessRow> Descendants(IEnumerable<uint> roots, IReadOnlyList<ProcessRow> table)
    {
        var seen = roots.ToHashSet(); var queue = new Queue<uint>(seen); var found = new List<ProcessRow>();
        var children = table.ToLookup(p => p.Parent);
        while (queue.TryDequeue(out var parent))
            foreach (var row in children[parent]) if (seen.Add(row.Pid)) { found.Add(row); queue.Enqueue(row.Pid); }
        return found;
    }
    static bool Under(string path, string folder) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    static bool Internal(ProcessRow row, string exe, Func<uint, string?>? pathOf = null)
    {
        if (!OwnNames.Contains(row.Name)) return false;
        var path = pathOf == null ? Canonical(ProcessPath(row.Pid)) : pathOf(row.Pid);
        if (path == null) return false; // un descendant illisible n'est jamais suppose etre Steam lui-meme
        if (!Path.GetFileName(path).Equals(row.Name, StringComparison.OrdinalIgnoreCase)) return false;
        return row.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(path, Path.Combine(Environment.SystemDirectory, "conhost.exe"), StringComparison.OrdinalIgnoreCase)
            : Under(path, Path.GetDirectoryName(exe)!);
    }
    public static string[] Games(string exe, IReadOnlyList<ProcessRow> table, IEnumerable<uint> roots, Func<uint, string?>? pathOf = null) => Descendants(roots, table)
        .Where(p => !Internal(p, exe, pathOf)).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public static HashSet<uint> Helpers(string exe, IReadOnlyList<ProcessRow> table, IEnumerable<uint> roots) => Descendants(roots, table)
        .Where(p => p.Name.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) && Internal(p, exe)).Select(p => p.Pid).ToHashSet();
    public static HashSet<uint> InstalledHelpers(string exe, IReadOnlyList<ProcessRow> table) => table
        .Where(p => p.Name.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) && Internal(p, exe)).Select(p => p.Pid).ToHashSet();
    public static bool OwnsLoopback(IEnumerable<(string Address, uint Pid)> listeners, HashSet<uint> helpers)
    {
        var all = listeners.ToList();
        return all.Any(l => l.Address == "127.0.0.1") && all.All(l => l.Address == "127.0.0.1" && helpers.Contains(l.Pid));
    }
    public static bool CdpReply(JsonElement reply, int port) => reply.ValueKind == JsonValueKind.Object
        && reply.TryGetProperty("webSocketDebuggerUrl", out var endpoint) && endpoint.ValueKind == JsonValueKind.String
        && Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var url) && url.Scheme == "ws" && url.Host == "127.0.0.1" && url.Port == port
        && !string.IsNullOrWhiteSpace(url.AbsolutePath.Trim('/')) && url.UserInfo.Length == 0;
    public static bool VersionReply(JsonElement reply) => reply.ValueKind == JsonValueKind.Object
        && reply.TryGetProperty("protocolVersion", out var protocol) && protocol.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(protocol.GetString())
        && reply.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(product.GetString());
    public static bool ProbeCdp(string url)
    {
        try
        {
            using var socket = new ClientWebSocket(); socket.Options.Proxy = null;
            using var cancel = new CancellationTokenSource(500); // toute la preuve : connexion, requete et reponse
            socket.ConnectAsync(new Uri(url), cancel.Token).GetAwaiter().GetResult();
            socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.getVersion\",\"params\":{}}"), WebSocketMessageType.Text, true, cancel.Token).GetAwaiter().GetResult();
            var buffer = new byte[4096];
            while (!cancel.IsCancellationRequested)
            {
                using var message = new MemoryStream(); WebSocketReceiveResult part;
                do
                {
                    part = socket.ReceiveAsync(buffer, cancel.Token).GetAwaiter().GetResult();
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 65536) return false;
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var reply = JsonDocument.Parse(message.ToArray()); var root = reply.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var got) || got != 1) continue;
                return !root.TryGetProperty("error", out _) && root.TryGetProperty("result", out var result) && VersionReply(result);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or IOException or InvalidOperationException) { }
        return false;
    }
    public static int PickWindow(IReadOnlyList<string> titles)
    {
        var titled = titles.Select((title, index) => (title, index)).Where(w => w.title.Trim().Equals("Steam", StringComparison.OrdinalIgnoreCase)).Select(w => w.index).ToArray();
        if (titled.Length > 1 || titled.Length == 0 && titles.Count > 1) throw new ActionRefusedException("Steam window is ambiguous; select its verified window from windows, never guess the first one");
        return titled.Length == 1 ? titled[0] : titles.Count == 1 ? 0 : -1;
    }
    public static string? ReuseCommand(string args, bool hasWindow) => args.Trim().Length > 0 ? args : hasWindow ? null : "steam://open/main";
    public static void RequireReusableEndpoint(bool devtools, int? port)
    {
        if (devtools && port == null) throw new ActionRefusedException("Steam is already running without a verified local DevTools endpoint; restart=true requires explicit user authorization to close Steam and interrupt downloads");
    }
    public static List<(string Address, uint Pid, int Port)> Listeners()
    {
        var found = new List<(string, uint, int)>();
        foreach (var family in new[] { 2 /* AF_INET */, 23 /* AF_INET6 */ })
        {
            var size = 0; var queried = GetExtendedTcpTable(0, ref size, false, family, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (queried != 0 && queried != 122) throw new ActionRefusedException("Steam TCP listener inventory is unavailable; the port is not assumed free");
            if (size == 0) continue;
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, family, 3, 0) != 0) throw new ActionRefusedException("Steam TCP listener inventory changed unreadably; the port is not assumed free");
                var count = Marshal.ReadInt32(table); var rowSize = family == 2 ? 24 : 56;
                if (count < 0 || count > (size - 4) / rowSize) throw new ActionRefusedException("Steam TCP listener inventory is malformed; the port is not assumed free");
                for (var i = 0; i < count; i++)
                {
                    var row = table + 4 + i * rowSize;
                    var raw = (uint)Marshal.ReadInt32(row, family == 2 ? 8 : 20);
                    var port = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                    string address;
                    if (family == 2) address = new IPAddress((uint)Marshal.ReadInt32(row, 4)).ToString();
                    else { var bytes = new byte[16]; Marshal.Copy(row, bytes, 0, 16); address = new IPAddress(bytes).ToString(); }
                    found.Add((address, (uint)Marshal.ReadInt32(row, family == 2 ? 20 : 52), port));
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return found;
    }
    public static int? DebuggingPort(string exe, HashSet<uint> helpers)
    {
        var all = Listeners(); var budget = Stopwatch.StartNew();
        foreach (var port in all.Where(l => l.Address == "127.0.0.1" && helpers.Contains(l.Pid)).Select(l => l.Port).Distinct())
        {
            EmergencyStop.ThrowIfStopped(); if (budget.ElapsedMilliseconds >= 1000) break;
            if (!OwnsLoopback(all.Where(l => l.Port == port).Select(l => (l.Address, l.Pid)), helpers)) continue;
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
                using var reply = JsonDocument.Parse(client.GetStringAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult());
                // PID et socket relus apres HTTP : un simple serveur JSON ayant pris le port ne suffit pas.
                if (CdpReply(reply.RootElement, port) && ProbeCdp(reply.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!))
                {
                    var current = Table(); var currentHelpers = Helpers(exe, current, Roots(exe, current));
                    if (OwnsLoopback(Listeners().Where(l => l.Port == port).Select(l => (l.Address, l.Pid)), currentHelpers)) return port;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        return null;
    }
    public static string? PortConflict(HashSet<uint> helpers) => Listeners().Any(l => l.Port == DevtoolsPort && (!helpers.Contains(l.Pid) || l.Address != "127.0.0.1"))
        ? "Steam DevTools port 8080 belongs to another process or is not restricted to 127.0.0.1; nothing was restarted" : null;

    // Portage et tests partagent les memes decisions : aucun processus, reseau ou fenetre n'est touche par ce mode.
    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            try
            {
                var kind = c.GetProperty("kind").GetString();
                if (kind == "plan")
                {
                    var exe = c.GetProperty("exe").GetString() ?? ""; var args = c.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                    if (!IsExecutable(exe)) throw new ActionRefusedException("Executable is not the genuine Steam client");
                    if (ArgsProblem(args) is { } problem) throw new ActionRefusedException(problem);
                    var info = StartInfo(exe, args, c.TryGetProperty("devtools", out var d) && d.GetBoolean());
                    results.Add(new { arguments = info.Arguments, fileName = info.FileName, useShellExecute = info.UseShellExecute });
                }
                else if (kind == "tree")
                {
                    var rows = c.GetProperty("processes").EnumerateArray().Select(p => new ProcessRow(p.GetProperty("pid").GetUInt32(), p.GetProperty("parent").GetUInt32(), p.GetProperty("name").GetString() ?? "")).ToList();
                    var paths = c.GetProperty("processes").EnumerateArray().ToDictionary(p => p.GetProperty("pid").GetUInt32(), p => p.TryGetProperty("path", out var path) ? path.GetString() : null);
                    var roots = c.GetProperty("roots").EnumerateArray().Select(p => p.GetUInt32()).ToArray();
                    results.Add(new { descendants = Descendants(roots, rows).Select(p => p.Pid), games = Games(c.GetProperty("exe").GetString() ?? "", rows, roots, p => paths.GetValueOrDefault(p)) });
                }
                else if (kind == "endpoint")
                {
                    var helpers = c.GetProperty("helpers").EnumerateArray().Select(p => p.GetUInt32()).ToHashSet();
                    var listeners = c.GetProperty("listeners").EnumerateArray().Select(l => (l.GetProperty("address").GetString() ?? "", l.GetProperty("pid").GetUInt32())).ToList();
                    results.Add(new { owned = OwnsLoopback(listeners, helpers), cdp = CdpReply(c.GetProperty("reply"), DevtoolsPort) });
                }
                else if (kind == "window") results.Add(new { index = PickWindow(c.GetProperty("titles").EnumerateArray().Select(t => t.GetString() ?? "").ToArray()) });
                else if (kind == "reuse")
                {
                    var args = c.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                    if (ArgsProblem(args) is { } problem) throw new ActionRefusedException(problem);
                    RequireReusableEndpoint(c.TryGetProperty("devtools", out var d) && d.GetBoolean(), c.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null);
                    var index = PickWindow(c.GetProperty("titles").EnumerateArray().Select(t => t.GetString() ?? "").ToArray());
                    results.Add(new { command = ReuseCommand(args, index >= 0) });
                }
                else if (kind == "cdp")
                {
                    var url = c.GetProperty("url").GetString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.UserInfo.Length != 0) throw new ActionRefusedException("Selftest CDP endpoint must be loopback");
                    results.Add(new { alive = ProbeCdp(url) });
                }
                else if (kind == "startup")
                {
                    var path = Path.Combine(Path.GetTempPath(), "nexus-steam-startup-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        StartNoActivate(new ProcessStartInfo(Environment.ProcessPath!, "--steam-startup-info \"" + path + "\""));
                        var wait = Stopwatch.StartNew();
                        while (!File.Exists(path) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(25);
                        if (!File.Exists(path)) throw new InvalidOperationException("Startup selftest child did not report; outcome unknown");
                        using var reply = JsonDocument.Parse(File.ReadAllText(path)); results.Add(reply.RootElement.Clone());
                    }
                    finally { File.Delete(path); }
                }
                else throw new InvalidOperationException("Unknown Steam selftest");
            }
            catch (ActionRefusedException e) { results.Add(new { refused = e.Message }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(results)); return 0;
    }
}

static partial class Program
{
    static object LaunchSteam(string exe, string args, bool devtools, bool restart, int? quietMs)
    {
        if (Steam.ArgsProblem(args) is { } problem) throw new ActionRefusedException(problem + "; nothing was launched");
        using var token = ForegroundToken.Acquire("launch Steam");
        JulienPause.ThrowIfActive(quietMs); EmergencyStop.ThrowIfStopped();
        var probe = Win32.Probe(); var giveBack = "none";
        var table = Steam.Table(); var roots = Steam.Roots(exe, table); var helpers = Steam.Helpers(exe, table, roots);
        var knownHelpers = new HashSet<uint>(helpers);
        var running = roots.Count > 0; var reused = running && !restart; var restarted = false;
        var notes = new List<string>(); int? port = null;
        var requestedArgsTransmitted = false;
        (long Id, string App, string Title, long Pid, long Owner)? FindWindow()
        {
            var windows = AppWindows().Where(w => helpers.Contains((uint)w.Pid)).ToList();
            var index = Steam.PickWindow(windows.Select(w => w.Title).ToArray());
            return index >= 0 ? windows[index] : null;
        }
        void Refresh() { table = Steam.Table(); roots = Steam.Roots(exe, table); helpers = Steam.Helpers(exe, table, roots); knownHelpers.UnionWith(helpers); }
        void ReturnIfTaken()
        {
            var foreground = Win32.GetForegroundWindow();
            var current = Win32.ProcessOf(foreground);
            if (foreground != 0 && foreground != probe.Foreground && (roots.Contains(current) || helpers.Contains(current)
                || knownHelpers.Contains(current) && Steam.InstalledHelpers(exe, table).Contains(current)))
                giveBack = ForegroundReturn.GiveBack(probe.Foreground, foreground, probe.LastInput, sameProcess: true);
        }
        void Start(ProcessStartInfo info)
        {
            EmergencyStop.ThrowIfStopped(); JulienPause.ThrowIfActive(quietMs);
            try { Steam.StartNoActivate(info, () => InputStarted = true); }
            catch (UserApplicationUnavailableException error) { throw new ActionRefusedException(error.Message); }
        }
        var viewApplied = !reused;
        try
        {
            if (reused && devtools)
            {
                port = Steam.DebuggingPort(exe, helpers);
                Steam.RequireReusableEndpoint(devtools, port);
            }
            if (!reused && devtools && Steam.PortConflict(helpers) is { } conflict) throw new ActionRefusedException(conflict);
            if (running && restart)
            {
                // Relire les jeux immediatement avant l'unique demande de fermeture propre.
                Refresh(); var games = Steam.Games(exe, table, roots);
                if (games.Length > 0) throw new ActionRefusedException("Steam restart refused: game or launcher running under Steam: " + string.Join(", ", games));
                Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
                var shutdown = Stopwatch.StartNew();
                var previous = roots.Concat(helpers).ToHashSet();
                while (shutdown.ElapsedMilliseconds < Steam.ShutdownWaitMs)
                {
                    EmergencyStop.ThrowIfStopped(); Refresh(); ReturnIfTaken();
                    // Des helpers peuvent survivre a leur parent ou apparaitre pendant sa fermeture : attendre ceux
                    // du meme client aussi, sans les arreter et sans attribuer leur port a la nouvelle session.
                    previous.UnionWith(Steam.InstalledHelpers(exe, table));
                    if (roots.Count == 0 && !table.Any(p => previous.Contains(p.Pid))) break;
                    Thread.Sleep(100);
                }
                if (roots.Count > 0 || table.Any(p => previous.Contains(p.Pid))) throw new InvalidOperationException("Steam did not close within 60 s (confirmation window?): nothing was restarted; never force-close or replay");
                restarted = true;
            }
            if (reused)
            {
                notes.Add("Steam was already running: startup flags were not changed; restart=true closes Steam and interrupts downloads and requires explicit user authorization.");
                if (Steam.ReuseCommand(args, FindWindow() != null) is { } command)
                {
                    Start(new ProcessStartInfo(exe, command) { UseShellExecute = false, CreateNoWindow = true });
                    requestedArgsTransmitted = args.Trim().Length > 0;
                }
            }
            else
            {
                Refresh();
                // Un client relance entre la verification et maintenant n'est jamais pris pour un nouveau depart.
                if (roots.Count > 0) throw new InvalidOperationException("Steam started during launch preparation; startup flags were not applied; observe before deciding");
                if (devtools && Steam.PortConflict(helpers) is { } laterConflict) throw new InvalidOperationException(laterConflict);
                Start(Steam.StartInfo(exe, args, devtools));
                requestedArgsTransmitted = args.Trim().Length > 0;
            }
            var wait = Stopwatch.StartNew(); long seen = 0, seenPid = 0, seenAt = 0;
            (long Id, string App, string Title, long Pid, long Owner)? found = null;
            while (wait.ElapsedMilliseconds < (reused ? 15000 : Steam.StartWaitMs))
            {
                EmergencyStop.ThrowIfStopped(); Refresh(); ReturnIfTaken(); found = FindWindow();
                if (found is { } w)
                {
                    if (w.Id != seen || w.Pid != seenPid) { seen = w.Id; seenPid = w.Pid; seenAt = wait.ElapsedMilliseconds; }
                    else if (wait.ElapsedMilliseconds - seenAt >= 600) break;
                }
                else { seen = 0; seenPid = 0; }
                Thread.Sleep(100);
            }
            if (devtools && !reused)
            {
                var debugWait = Stopwatch.StartNew();
                while (debugWait.ElapsedMilliseconds < Steam.DevtoolsWaitMs && port == null)
                {
                    EmergencyStop.ThrowIfStopped(); Refresh(); ReturnIfTaken(); port = Steam.DebuggingPort(exe, helpers);
                    if (port == null) Thread.Sleep(100);
                }
            }
            if (devtools) notes.Add(port != null
                ? "Steam DevTools exposes the current Steam session to local programs while this client remains open; endpoint verified on 127.0.0.1, owned by Steam's helper."
                : "Steam was started but no owned loopback DevTools endpoint was verified; CDP is unavailable, never assume port 8080 is usable.");
            if (requestedArgsTransmitted) notes.Add("The requested Steam arguments were transmitted once. Attesting a Steam window does not prove the requested game or page opened; observe that effect separately, never replay blindly.");
            Refresh(); var final = FindWindow(); ReturnIfTaken();
            var proved = found is { } old && final is { } now && old.Id == now.Id && old.Pid == now.Pid && seen != 0 && wait.ElapsedMilliseconds - seenAt >= 600;
            object[] windows = proved && final is { } mine ? new object[] { new { id = mine.Id, app = mine.App, title = mine.Title, pid = mine.Pid, proof = "steam" } } : Array.Empty<object>();
            return new { ok = true, opened = proved ? (bool?)true : null, windows, candidates = Array.Empty<object>(), foregroundReturned = giveBack,
                reason = proved ? null : "No stable Steam window was attested; update or sign-in may still be in progress. Observe before deciding; never launch again blindly.",
                steam = new { reused, restarted, requestedArgsTransmitted, viewSwitchesApplied = viewApplied, devtoolsRequested = devtools, devtoolsPort = port, notes } };
        }
        finally { ReturnIfTaken(); }
    }
}
