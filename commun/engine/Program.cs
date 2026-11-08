using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

static partial class Program
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetApplicationUserModelId(nint process, ref uint length, [Out] char[]? appId);
    // Reads: allowed during the emergency stop, they send no input.
    static readonly HashSet<string> ReadOnlyOperations = new(StringComparer.Ordinal) { "inspect", "capture", "ocr", "related", "windows", "probe", "alive", "table" };
    static readonly HashSet<string> ActionOperations = new(StringComparer.Ordinal) { "click", "setValue", "secondary", "select", "typeText", "physical", "focus", "realizeNamed", "key" };
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // This engine's own executable, started again for its helper children (--com-worker, --steam-startup-info,
    // --stop-guard). The in-process xUnit tests point it at the built engine instead of the test host.
    internal static string HelperExecutable { get; set; } = Environment.ProcessPath ?? "";
    internal static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    // Hard block: shared garde-fous.json policy (GardeFous.cs). Consoles, terminals and administration tools are free
    // since the agreement of 18/10; only the secure desktop, Windows credentials, password managers and Codex's own
    // application (its permission requests) stay blocked.
    static string? Exclusion(string processName, nint hwnd) => GardeFous.BlockReason(processName, Win32.ClassName(hwnd));
    // Console windows: never followed as a dialog that appeared during an action (they stay drivable if they are bound
    // explicitly).
    static readonly HashSet<string> ConsoleClasses = new(StringComparer.OrdinalIgnoreCase)
    { "ConsoleWindowClass", "PseudoConsoleWindow", "CASCADIA_HOSTING_WINDOW_CLASS" };

    [MTAThread]
    static int Main(string[] args)
    {
        // Its version folder stays whole while it runs (InstalledVersions.cs): held until the process ends.
        InstalledVersions.HoldOwnVersion();
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length is 2 or 3 && args[0] == "--foreground-token")
        {
            // Optional third argument: target window (hwnd). The user's active window is noted as soon as the token is
            // taken and given back to them at the end, under the token, if the target still holds the foreground (ForegroundReturn).
            var target = args.Length == 3 && long.TryParse(args[2], out var hwnd) ? (nint)hwnd : 0;
            bool acquired = false;
            try
            {
                // EOF cancels waiting and releases ownership when the caller dies.
                // Console's synchronized reader can block inside ReadToEndAsync.
                // Keep EOF monitoring on a worker so acquisition can proceed.
                var abandoned = Task.Run(() => Console.In.ReadToEnd());
                using var token = ForegroundToken.Acquire(args[1], abandoned);
                acquired = true;
                var noted = Win32.GetForegroundWindow();
                Write(new ProgramMainJson(ok: true, foreground: (long)noted));
                Console.Out.Flush();
                var said = abandoned.GetAwaiter().GetResult();
                // Terminal acknowledgement of a normal release, always written (target or not): without it, the caller
                // takes the closing for a loss of the token (Codex's review, 21/10).
                string? state = null;
                if (target != 0)
                {
                    // "keep": the caller saw the user take the keyboard or mouse back during the lease (its presence tells
                    // the agent's injections apart, which LastInput does not); their choice is kept.
                    state = said.Contains("keep") ? "kept-by-julien" : ForegroundReturn.GiveBack(noted, target);
                }
                try { Write(new ProgramMainJson2(released: true, giveBack: state)); Console.Out.Flush(); } catch (IOException) { } // caller already gone
                return 0;
            }
            catch (Exception error) when (!acquired) { Write(new ProgramMainJson3(ok: false, error: error.GetType().Name, message: error.Message)); return 1; }
        }
        if (args.Length is 3 or 4 && args[0] == "--window-lock") return WindowLock.Hold(args);
        if (args.Length == 2 && args[0] == "--stop-guard") return EmergencyStop.Guard(args[1]);
        // Process of the UI Automation patterns (see Background.cs).
        if (args.SequenceEqual(new[] { "--com-worker" })) return ComWorker.Run();
        if (args.Length == 2 && args[0] == "--steam-startup-info") return Steam.WriteStartupInfo(args[1]); // bench child without a GUI
        if (args.Length == 1 && args[0] == "--migrate-routines") return McpServer.MigrateRoutines();
        // Declaration to the MCP clients (Registration.cs): installers and get.ps1, never a session.
        if (args.Length >= 2 && args[0] == "--register") return Registration.Command(args);
        // Earlier engine versions and leftovers (InstalledVersions.cs): installers, after recording the new version.
        if (args.Length == 1 && args[0] == "--prune-installed") return InstalledVersions.Command();
        if (args.Length == 2 && args[0] == "--refresh-installed" && args[1] == "antigravity")
            return Registration.RefreshInstalledCommand();
        if (args.Length >= 1 && args[0] == "--mcp")
        {
            var agent = "codex";
            for (var i = 1; i < args.Length; i++)
            {
                if (args.Length == 3 && args[i] == "--agent" && i == 1) { agent = args[++i]; }
                else if (args.Length == 2 && args[i].StartsWith("--agent=", StringComparison.Ordinal)) { agent = args[i]["--agent=".Length..]; }
                else { Console.Error.WriteLine("Expected --mcp [--agent codex|claude|antigravity]"); return 2; }
            }
            return McpServer.Run(agent);
        }
        // The low-level Codex provider has a fixed profile. Only the MCP process entrypoint selects another installed agent.
        GardeFous.CurrentAgent = "codex";
        Environment.SetEnvironmentVariable("NEXUS_AGENT", "codex");
        ChromiumInput.TextBudget = ChromiumInput.Budget;
        if (args.SequenceEqual(new[] { "--server" }))
        {
            // One sequential MTA worker. Every request reacquires the window and controls.
            // EOF ends the worker if its client disappears; the client also enforces deadlines.
            EmergencyStop.Watch();
            string? line;
            while ((line = Console.ReadLine()) != null) ProcessRequest(line, true);
            return 0;
        }
        return ProcessRequest(Console.In.ReadToEnd(), false);
    }
    static int ProcessRequest(string input, bool server)
    {
        string? requestId = null;
        object result;
        int code = 0;
        InputStarted = false; // a request rejected before Execute never inherits the previous one's state
        try
        {
            if (input.Length > 262144) throw new InvalidOperationException("Request is too large");
            using var json = JsonDocument.Parse(input);
            var request = json.RootElement;
            requestId = Text(request, "requestId");
            if (server && !Guid.TryParse(requestId, out _)) throw new InvalidOperationException("Request identity is required");
            if (request.TryGetProperty("agent", out var agentProp) && agentProp.GetString() is { Length: > 0 } reqAgent && !reqAgent.Equals(GardeFous.CurrentAgent, StringComparison.OrdinalIgnoreCase))
                throw new ActionRefusedException("Agent profile is fixed at process startup; a request cannot change it");
            if (request.TryGetProperty("policyFile", out var policy))
            {
                var file = policy.GetString();
                if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                    throw new InvalidDataException("Policy path must be absolute");
                // COM children inherit this path too, with the full Windows environment.
                Environment.SetEnvironmentVariable("NEXUS_GARDE_FOUS", file);
            }
            result = Execute(request);
        }
        catch (Exception error)
        {
            // inputStarted: an input may have left before the error (Codex's review of 24/10); the client makes it
            // terminal (unknown outcome, accord consumed, no fallback), whatever the type of the exception.
            result = new ProgramProcessRequestJson(ok: false, error: error.GetType().Name, message: error.Message, inputStarted: InputStarted);
            code = 1;
        }
        if (server) Write(new ProgramProcessRequestJson2(requestId: requestId, result: result)); else Write(result);
        Console.Out.Flush();
        return code;
    }
    // True as soon as an input may have left (RunAction, menu WM_COMMAND): before that, any error of an action comes
    // before the input.
    [ThreadStatic] internal static bool InputStarted;
    internal static object Execute(JsonElement request)
    {
        InputStarted = false;
        var op = Text(request, "operation");
        try { return ExecuteRequest(request, op); }
        catch (Exception error) when (!ReadOnlyOperations.Contains(op) && !InputStarted && error is not (EmergencyStopException or ActionRefusedException or JulienActiveException)
            && request.TryGetProperty("windowId", out var w) && w.ValueKind == JsonValueKind.Number && !IsWindow((nint)w.GetInt64()))
        {
            // Window closed during the request, before any input: refused (nothing was sent), never an unknown result, and
            // never a physical fallback towards a vanished window.
            throw new ActionRefusedException("Window closed during the request; nothing was sent: " + error.Message);
        }
    }
    static object ExecuteRequest(JsonElement request, string op)
    {
        var action = !ReadOnlyOperations.Contains(op);
        // Emergency stop: refuse every action before touching the window; inspection stays allowed.
        if (action) EmergencyStop.ThrowIfStopped();
        if (op == "probe")
        {
            var probe = Win32.Probe();
            return new ProgramExecuteRequestJson(ok: true, foreground: (long)probe.Foreground, foregroundPid: probe.Foreground == 0 ? 0 : Win32.ProcessOf(probe.Foreground),
                cursor: new ProgramExecuteRequestJson2(x: probe.Cursor.X, y: probe.Cursor.Y), idleMs: unchecked(Win32.GetTickCount() - probe.LastInput));
        }
        if (op == "windows") return ListWindows(request.GetProperty("processId").GetInt32(), request.TryGetProperty("includeHidden", out var hidden) && hidden.GetBoolean());
        if (op == "launch") return Launch(request.GetProperty("target").GetString() ?? "", Text(request, "args"),
            request.TryGetProperty("quietMs", out var lq) && lq.ValueKind == JsonValueKind.Number ? lq.GetInt32() : null,
            request.TryGetProperty("devtools", out var ld) && ld.GetBoolean(), request.TryGetProperty("restart", out var lr) && lr.GetBoolean());
        if (op == "openSettings") return OpenSettings(request.GetProperty("uri").GetString() ?? "",
            request.TryGetProperty("quietMs", out var oq) && oq.ValueKind == JsonValueKind.Number ? oq.GetInt32() : null);
        // A refusal before any input is never an unknown result: no input was sent.
        Exception Refuse(string message) => action ? new ActionRefusedException(message) : new InvalidOperationException(message);
        var hwnd = (nint)request.GetProperty("windowId").GetInt64();
        var app = request.GetProperty("app").GetString() ?? "";
        // Identity imposed by the session (a window Sky does not know): same process, same owner, at each request.
        long? expectedPid = request.TryGetProperty("expectedPid", out var ep) && ep.ValueKind == JsonValueKind.Number ? ep.GetInt64() : null;
        long? expectedOwner = request.TryGetProperty("expectedOwner", out var eo) && eo.ValueKind == JsonValueKind.Number ? eo.GetInt64() : null;
        long? expectedAnchor = request.TryGetProperty("expectedAnchor", out var ea) && ea.ValueKind == JsonValueKind.Number ? ea.GetInt64() : null;
        if (op == "alive") return Alive(hwnd, app, expectedPid, expectedOwner, expectedAnchor);
        if (!IsWindow(hwnd)) throw Refuse("Window no longer exists");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) throw Refuse("Window closed during the request; nothing was sent");
        if (expectedPid is { } wantedPid && pid != wantedPid) throw Refuse("Window identity changed (other process); nothing was sent");
        if (expectedOwner is { } wantedOwner && (long)Win32.GetWindow(hwnd, Win32.GW_OWNER) != wantedOwner) throw Refuse("Window identity changed (other owner); nothing was sent");
        if (expectedAnchor is { } wantedAnchor && !OwnedThrough(hwnd, wantedAnchor)) throw Refuse("Window identity changed (no longer owned by the followed window); nothing was sent");
        Process OpenProcess()
        {
            try { return Process.GetProcessById((int)pid); }
            catch (ArgumentException) { throw Refuse("The window's process ended during the request; nothing was sent"); }
        }
        using var process = OpenProcess();
        if (request.TryGetProperty("expectedProcessStartedUtc", out var expectedBirth) && expectedBirth.ValueKind == JsonValueKind.String &&
            (expectedBirth.GetString() is not { Length: > 0 } birth || ProcessStartedUtc(pid) != birth))
            throw Refuse("Window process birth changed; nothing was sent");
        if (Exclusion(process.ProcessName, hwnd) is { } refusal) throw Refuse(refusal);
        var actualApp = AppId(process);
        UiTree? full = null, identity = null;
        try
        {
            UiTree Full() => full ??= ComUia.Shared.ReadTree(hwnd);
            var contentPid = (int)pid;
            if (!string.Equals(app, actualApp, StringComparison.OrdinalIgnoreCase) && process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var nativeContent = StoreContentProcess(hwnd, app);
                var matches = new List<int>();
                if (nativeContent != 0) matches.Add(nativeContent);
                foreach (var candidate in (nativeContent == 0 ? Full().Nodes.Select(n => n.ProcessId).Where(p => p != 0).Distinct() : Enumerable.Empty<int>()))
                {
                    using var child = Process.GetProcessById(candidate);
                    if (string.Equals(AppId(child), app, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
                }
                if (matches.Count == 1) { actualApp = app; contentPid = matches[0]; }
            }
            if (!string.Equals(app, actualApp, StringComparison.OrdinalIgnoreCase))
                throw Refuse($"Window application identity differs: {actualApp}");
            var storeResume = op is "inspect" or "capture" || action ? ResumeStoreIfNeeded(hwnd, contentPid, actualApp) : new StoreResume(false, "not-needed");
            bool? storeContentReady = null;
            if (storeResume.Requested)
            {
                storeContentReady = false;
                full?.Dispose(); full = null;
                var readyClock = Stopwatch.StartNew(); var previousCount = -1;
                while (true)
                {
                    if (Win32.ProcessOf(hwnd) != pid || process.HasExited
                        || !StoreApplicationMatches(hwnd, contentPid, actualApp)
                        || process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && StoreContentProcess(hwnd, actualApp) != contentPid)
                        throw Refuse("Window identity changed after Store resume");
                    var count = process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                        ? Full().Nodes.Count(n => n.ProcessId != 0 && n.ProcessId != pid) : Math.Max(0, Full().Nodes.Count - 1);
                    if (count > 0 && count == previousCount) { storeContentReady = true; break; }
                    if (readyClock.ElapsedMilliseconds + 150 >= 2000) break;
                    previousCount = count; full?.Dispose(); full = null; Thread.Sleep(150);
                }
            }
            if (op == "capture")
            {
                int[]? zoom = request.TryGetProperty("zoom", out var z) && z.ValueKind == JsonValueKind.Array
                    ? z.EnumerateArray().Select(v => v.GetInt32()).ToArray() : null;
                // Numbers (Marks.cs): [{index, bounds:[x,y,w,h] screen, scope: carrying HWND or 0}] and frame read with the tree.
                List<Mark>? marks = null;
                Win32.Rect? treeFrame = null;
                if (request.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Array)
                {
                    marks = [];
                    foreach (var e in m.EnumerateArray().Take(400))
                    {
                        if (!e.TryGetProperty("bounds", out var b) || b.ValueKind != JsonValueKind.Array || b.GetArrayLength() != 4) continue;
                        var v = b.EnumerateArray().Select(x => (int)Math.Round(x.GetDouble())).ToArray();
                        if (v[2] <= 0 || v[3] <= 0) continue;
                        marks.Add(new Mark(e.GetProperty("index").GetInt32(), v[0], v[1], v[2], v[3], e.TryGetProperty("scope", out var s) ? s.GetInt64() : 0));
                    }
                    if (request.TryGetProperty("treeFrame", out var t) && t.ValueKind == JsonValueKind.Object)
                        treeFrame = new Win32.Rect { Left = t.GetProperty("left").GetInt32(), Top = t.GetProperty("top").GetInt32(),
                            Right = t.GetProperty("left").GetInt32() + t.GetProperty("width").GetInt32(), Bottom = t.GetProperty("top").GetInt32() + t.GetProperty("height").GetInt32() };
                }
                return Capture.Window(hwnd, Text(request, "directory"), zoom, marks, treeFrame);
            }
            // OCR of THIS window's last capture (path returned by capture), read only.
            if (op == "ocr") return Capture.Recognize(Text(request, "path"));
            if (op == "related") return Related(hwnd, pid);
            if (op == "table")
            {
                var runtimeId = request.GetProperty("runtimeId").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var hint = request.TryGetProperty("nativeWindow", out var nw) && nw.ValueKind == JsonValueKind.Number ? (nint)nw.GetInt64() : 0;
                var maxRows = request.TryGetProperty("maxRows", out var mr) && mr.ValueKind == JsonValueKind.Number ? Math.Clamp(mr.GetInt32(), 1, 600) : 100;
                var read = ComUia.Shared.ReadTable(hwnd, runtimeId, hint, maxRows);
                return read == null ? new ProgramExecuteRequestJson3(ok: true, grid: false) :
                    new ProgramExecuteRequestJson4(ok: true, grid: true, headers: read.Headers, rows: read.Rows, rowCount: read.RowCount, columnCount: read.ColumnCount);
            }
            if (op == "menu") return MenuCommand(hwnd, Text(request, "path"));
            if (op == "key" && !request.TryGetProperty("selector", out _)) return KeyAction(hwnd, Text(request, "key"), request);
            if (op == "point") return PointAction(hwnd, request);
            // The helper's real mouse and keyboard, under the token held by the caller (Physical.cs).
            // Without a native target: a capture point, a key or text. With a target: resolved below, then read again under
            // the token after coming to the foreground.
            if (op == "physical" && !request.TryGetProperty("selector", out _))
            {
                if (Text(request, "action") == "drag" && McpServer.ClaudeDevtoolsInScope(hwnd)) return PointAction(hwnd, request);
                return Physical.Act(hwnd, request, () => InputStarted = true);
            }
            if (op == "activate") return Physical.Activate(hwnd, () => InputStarted = true);
            if (op == "close") return CloseWindow(hwnd, () => InputStarted = true);
            if (op == "manageWindow") return WindowManagement.Act(hwnd, request, () => InputStarted = true);
            if (op == "navigateFolder") return Explorer.Navigate(hwnd, Text(request, "path"), () => InputStarted = true);
            if (action && !ActionOperations.Contains(op)) throw new InvalidOperationException("Unknown native operation");
            if (op == "realizeNamed") return RealizeVirtualized(hwnd, request, Full().Nodes);
            if (op == "inspect")
            {
                var clock = Stopwatch.StartNew();
                var windowBirth = ProcessStartedUtc(pid); var contentBirth = ProcessStartedUtc((uint)contentPid);
                var frameHost = process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
                void ValidateWindow()
                {
                    if (!IsWindow(hwnd) || Win32.ProcessOf(hwnd) != pid || process.HasExited
                        || windowBirth != null && ProcessStartedUtc(pid) != windowBirth
                        || contentBirth != null && ProcessStartedUtc((uint)contentPid) != contentBirth
                        || expectedOwner is { } ownerNow && (long)Win32.GetWindow(hwnd, Win32.GW_OWNER) != ownerNow
                        || expectedAnchor is { } anchorNow && !OwnedThrough(hwnd, anchorNow)
                        || !StoreApplicationMatches(hwnd, contentPid, actualApp))
                        throw Refuse("Window or content identity changed during inspection; inspect again");
                }
                void ValidateContent(List<Node> nodes)
                {
                    ValidateWindow();
                    if (!frameHost || contentPid == pid) return;
                    var nativeContent = StoreContentProcess(hwnd, actualApp);
                    if (nativeContent != 0)
                    {
                        if (nativeContent != contentPid) throw Refuse("ApplicationFrameHost content changed during inspection");
                        return; // Native parentage also proves identity while a resumed app is still publishing its tree.
                    }
                    if (HasForeignContent(hwnd, pid)) throw Refuse("ApplicationFrameHost content is ambiguous during inspection");
                    var matches = nodes.Select(n => n.ProcessId).Where(p => p != 0 && p != pid).Distinct()
                        .Where(p => StoreApplicationMatches(hwnd, p, actualApp)).ToArray();
                    if (matches.Length != 1 || matches[0] != contentPid)
                        throw Refuse("ApplicationFrameHost content could not be revalidated from the fresh tree");
                }
                // Association and ResumeStore may have populated Full already. That preparatory tree is never used
                // as the observation: a real new ReadTree must occur between the two frame measurements.
                full?.Dispose(); full = null;
                var observation = ReadInspection(() => Capture.Area(hwnd), () => (full = ComUia.Shared.ReadTree(hwnd)).Nodes,
                    ValidateWindow, ValidateContent);
                return Inspect(observation.Nodes, contentPid, actualApp, pid, clock.ElapsedMilliseconds, observation.Frame, storeResume, storeContentReady);
            }

            // Actions. Native observation (runtime id and ancestor chain given): read-back limited to the target and its
            // ancestors, compared with what the session saw. Sky observation: full tree, selector.
            // Anything impossible at this stage comes before the input: next technique allowed, or refused.
            var mode = Text(request, "mode", "background");
            if (mode is not ("background" or "foreground")) throw new InvalidOperationException("Unknown action mode");
            var selector = request.GetProperty("selector");
            // WinForms menu item (MenuStrip, ContextMenuStrip context menu): its UIA identities change at each read
            // (shared bench of 21/10: "Target is no longer present"). Named by its NAME, unique in the bound window, and
            // invoked through UIA (ComWorker.MenuPath), like the menu path.
            // Stable constraints kept (Codex's review of 22/10): name, automation id, and the menu's context (observed
            // ancestor menu items, in order); an ancestor the selector requires must appear there.
            if (op == "click" && mode == "background" && Win32.ClassName(hwnd).StartsWith("WindowsForms10.", StringComparison.Ordinal) &&
                selector.TryGetProperty("role", out var menuRole) && menuRole.GetString() == "menuitem" &&
                selector.TryGetProperty("name", out var menuName) && menuName.GetString() is { Length: > 0 })
            {
                if (!Win32.IsWindowEnabled(hwnd)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
                var observed = request.TryGetProperty("ancestry", out var anc0) && anc0.ValueKind == JsonValueKind.Array ? anc0.EnumerateArray().ToList() : [];
                // Command of an open context or drop-down menu: resolved in THIS popup (HWND read at the observation), still
                // open, of the same process and bound to the followed window; otherwise refused (observe again).
                var scope = (nint)MenuSearch.Scope(hwnd, observed);
                // Menu required by the selector (ancestor.window: the one the right click opened): the scope MUST be it.
                if (selector.TryGetProperty("ancestor", out var within) && within.TryGetProperty("window", out var menu) &&
                    menu.ValueKind == JsonValueKind.Number && menu.GetInt64() != (long)scope)
                    throw new ActionRefusedException("The command is not in the required open menu; inspect again");
                if (scope != hwnd && !(Win32.IsWindowVisible(scope) && Win32.ProcessOf(scope) == Win32.ProcessOf(hwnd) && Win32.IsTransientPopup(scope, Win32.ClassName(scope), hwnd)))
                    throw new ActionRefusedException("The open menu of this command is closed or no longer linked to the window; inspect again");
                return MenuItemByPath(scope, MenuSearch.Segments(observed, selector));
            }
            // Root: geometric root node of the action window (the chosen modal dialog, otherwise the tree's root).
            (Node Target, List<Node> Nodes, nint Window, Node Root) Resolve()
            {
            Node target;
            List<Node> nodes;
            var window = hwnd;
            Node root;
            try
            {
                var rid = request.TryGetProperty("runtimeId", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null;
                var ancestry = request.TryGetProperty("ancestry", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : null;
                var hint = request.TryGetProperty("nativeWindow", out var h) && h.ValueKind == JsonValueKind.Number ? (nint)h.GetInt64() : 0;
                if (rid != null && ancestry != null)
                {
                    // Synthetic node (option of a classic combo box): identity read again on the combo box that carries it
                    // (last real ancestor), then its options read again through messages.
                    var synthetic = ComUia.IsSynthetic(rid);
                    var anchor = synthetic ? ancestry.FindLastIndex(e => !ComUia.IsSynthetic(RuntimeIdOf(e))) : -1;
                    if (synthetic && anchor < 0) throw new ActionRefusedException("Control identity changed; inspect again");
                    try { identity = synthetic ? ComUia.Shared.ReadIdentity(hwnd, RuntimeIdOf(ancestry[anchor]), (nint)(uint)rid[1]) : ComUia.Shared.ReadIdentity(hwnd, rid, hint); }
                    catch (Exception error) when (error is not (EmergencyStopException or ActionRefusedException or BackgroundNotApplicableException))
                    {
                        throw new BackgroundNotApplicableException("Accessibility identity unavailable for a background action: " + error.Message);
                    }
                    nodes = identity.Nodes;
                    if (synthetic)
                    {
                        if (ComUia.IsClassicCombo(nodes[^1])) ComUia.SynthesizeCombo(identity, nodes[^1]);
                        else if (ComUia.IsClassicListBox(nodes[^1])) ComUia.SynthesizeListBox(identity, nodes[^1]);
                        else throw new ActionRefusedException("Control identity changed; inspect again");
                    }
                    target = nodes.FirstOrDefault(n => n.RuntimeId.SequenceEqual(rid)) ?? throw new ActionRefusedException("Control identity changed; inspect again");
                    if (!Matches(target, selector) || (selector.TryGetProperty("ancestor", out var anc) && !target.Ancestors.Any(i => Matches(nodes[i], anc))))
                        throw new ActionRefusedException("Control identity changed; inspect again");
                    if (!SameAncestry(nodes, target, ancestry))
                    {
                        // Element exposed at two places of the tree (Explorer context menu, bench of 28/10): the limited
                        // read-back follows the element's UIA parent, the full read (same walk as the observation,
                        // duplicates set aside by ComUia.ReadTree) keeps its first place. The observed chain is therefore
                        // compared with the full read; different there too: refused, as before.
                        if (synthetic) throw new ActionRefusedException("Control ancestry changed; inspect again");
                        var all = Full().Nodes;
                        var again = all.FirstOrDefault(n => n.RuntimeId.SequenceEqual(rid));
                        if (again == null || !Matches(again, selector) ||
                            (selector.TryGetProperty("ancestor", out var anc2) && !again.Ancestors.Any(i => Matches(all[i], anc2))) ||
                            !SameAncestry(all, again, ancestry))
                            throw new ActionRefusedException("Control ancestry changed; inspect again");
                        (target, nodes) = (again, all);
                    }
                }
                else
                {
                    try { nodes = Full().Nodes; }
                    catch (Exception error) when (error is not EmergencyStopException)
                    {
                        throw new BackgroundNotApplicableException("Accessibility tree unavailable for a background action: " + error.Message);
                    }
                    var targets = nodes.Where(n => Matches(n, selector) && (!selector.TryGetProperty("ancestor", out var anc) || n.Ancestors.Any(i => Matches(nodes[i], anc)))).ToArray();
                    // Namesake label of a single control set aside (no role).
                    if (targets.Length > 1 && !selector.TryGetProperty("role", out _) && targets.Count(n => n.Role != "text") == 1)
                        targets = targets.Where(n => n.Role != "text").ToArray();
                    if (targets.Length != 1) throw new BackgroundNotApplicableException($"Semantic target count is {targets.Length}; expected one");
                    target = targets[0];
                    if (rid != null && !rid.SequenceEqual(target.RuntimeId)) throw new ActionRefusedException("Control identity changed; inspect again");
                }
                ClassicCombos(nodes);
                EmergencyStop.ThrowIfStopped(); // Reading the tree can take time: check again just before the input.
                if (target.Disabled) throw new ActionRefusedException("Target or its window is disabled (a modal dialog may be open)");
                if (nodes[0].Disabled)
                {
                    // Window disabled by its modal dialog (bench of 28/10, Paint: « Enregistrer sous »): UIA files the dialog
                    // under its owner. A target INSIDE that dialog (Windows' active modal dialog, owned, same application,
                    // visible) acts on the dialog, as if it had been targeted; any other target is refused.
                    var chain = target.Ancestors.Select(i => nodes[i]).Append(target).Select(n => ((long)n.NativeWindow, n.Disabled)).ToList();
                    var box = ModalBox(hwnd, process.ProcessName, chain.Select(n => n.Item1).ToList());
                    var boxIndex = ModalBoxIndex(chain, (long)box);
                    if (boxIndex < 0) throw new ActionRefusedException("Target or its window is disabled (a modal dialog may be open)");
                    window = box;
                    // Reference rectangle: the dialog's, never its owner's (Codex's review: a dialog outside the owner's
                    // frame made its target "off-screen").
                    root = boxIndex < target.Ancestors.Length ? nodes[target.Ancestors[boxIndex]] : target;
                }
                else root = nodes[0];
                if (target.Password) throw new ActionRefusedException("Password controls require the dedicated supported workflow");
                if (op == "secondary" && Text(request, "action") != "ScrollIntoView" && !target.Actions.Contains(Text(request, "action"))) throw new ActionRefusedException("Secondary action is not exposed");
                // Visible first (same rule as the other agents): an element out of view with nothing overlapping its window
                // is never clicked by target; scroll it, then observe again.
                if (op is "click" or "physical" && HiddenOffscreen(target, root)) throw new ActionRefusedException("Target is off-screen with no visible part in its window; scroll it into view and inspect again");
            }
            catch (Exception error) when (error is not (EmergencyStopException or ActionRefusedException or BackgroundNotApplicableException))
            {
                throw new BackgroundNotApplicableException("Target resolution failed before input: " + error.Message);
            }
            return (target, nodes, window, root);
            }
            var (target, nodes, actWindow, _) = Resolve();
            if (op == "focus")
            {
                return NativeFocus(actWindow, request, () =>
                {
                    if (!IsWindow(hwnd) || Win32.ProcessOf(hwnd) != pid || process.HasExited)
                        throw new ActionRefusedException("Window identity changed while waiting for focus; nothing was sent");
                    identity?.Dispose(); identity = null; full?.Dispose(); full = null;
                    var fresh = Resolve();
                    if (fresh.Window != actWindow) throw new ActionRefusedException("The window holding the focus target changed; inspect again");
                    return fresh.Target;
                });
            }
            if (op == "physical")
            {
                // Physical click on a native target: read again under the token AFTER coming to the foreground (identity,
                // ancestry, fresh rectangle), at the center of its visible part; changed or hidden: refused before input.
                return Physical.Act(actWindow, request, () => InputStarted = true, () =>
                {
                    identity?.Dispose(); identity = null; full?.Dispose(); full = null;
                    var (fresh, _, freshWindow, freshRoot) = Resolve();
                    if (freshWindow != actWindow) throw new ActionRefusedException("The window holding the target changed; inspect again");
                    return VisibleCenter(fresh, freshRoot);
                });
            }
            return RunAction(op, mode, target, nodes, actWindow, request);
        }
        finally { identity?.Dispose(); full?.Dispose(); }
    }

    // Center of the target's visible part in its window (UIA rectangles [x, y, width, height], real pixels).
    internal static (int X, int Y) VisibleCenter(Node target, Node root)
    {
        if (target.Bounds is not [var x, var y, var w, var h] || w <= 0 || h <= 0) throw new ActionRefusedException("The native target has no on-screen rectangle: observe a native screenshot");
        double left = x, top = y, right = x + w, bottom = y + h;
        if (root.Bounds is [var rx, var ry, var rw, var rh] && rw > 0 && rh > 0)
            (left, top, right, bottom) = (Math.Max(left, rx), Math.Max(top, ry), Math.Min(right, rx + rw), Math.Min(bottom, ry + rh));
        if (right <= left || bottom <= top) throw new ActionRefusedException("Target is off-screen with no visible part in its window; scroll it into view and inspect again");
        return ((int)Math.Round((left + right) / 2), (int)Math.Round((top + bottom) / 2));
    }

    internal static (List<Node> Nodes, Win32.Rect? Frame) ReadInspection(Func<Win32.Rect?> area, Func<List<Node>> readFresh,
        Action validateWindow, Action<List<Node>> validateContent)
    {
        validateWindow(); var before = area();
        validateWindow(); var nodes = readFresh();
        validateContent(nodes); var after = area();
        validateContent(nodes);
        // Movement/resizing during the actual fresh read invalidates image coordinates; absent areas stay absent.
        var frame = before is { } a && after is { } b && a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom
            ? b : (Win32.Rect?)null;
        return (nodes, frame);
    }
    static ProgramInspectJson Inspect(List<Node> nodes, int contentPid, string actualApp, uint pid, long treeMs, Win32.Rect? frame = null,
        StoreResume? storeResume = null, bool? storeContentReady = null)
    {
        ClassicCombos(nodes);
        var tree = new StringBuilder();
        foreach (var n in nodes)
        {
            tree.Append(' ', n.Depth * 2).Append(n.Index).Append(' ').Append(n.Role);
            if (n.Disabled) tree.Append(" (disabled)");
            if (n.Selected == true) tree.Append(" (selected)");
            if (n.Checked == true) tree.Append(" (checked)");
            if (n.Expanded == true) tree.Append(" (expanded)");
            if (n.Expanded == false) tree.Append(" (collapsed)");
            if (n.Offscreen) tree.Append(" (offscreen)");
            tree.Append(' ').Append(Clean(n.Name));
            if (n.Value is { Length: > 0 }) tree.Append(" Value: ").Append(Clean(n.Value));
            if (n.ValueTruncated) tree.Append(" [value truncated]");
            if (n.AutomationId.Length > 0) tree.Append(" ID: ").Append(Clean(n.AutomationId));
            if (n.Actions.Count > 0) tree.Append(" Secondary Actions: ").Append(string.Join(", ", n.Actions));
            tree.AppendLine();
        }
        // Some providers mark both a container and its descendant as focused.
        var focused = nodes.Where(n => n.Focused)
            .OrderByDescending(n => n.Role is "edit" or "document").ThenByDescending(n => n.Depth).FirstOrDefault();
        var document = focused?.Value ?? "";
        var documentTruncated = focused?.ValueTruncated ?? false;
        if (focused != null && !focused.Password && focused.HasText && ComUia.DocumentText(focused.Element) is { } text)
        {
            document = text;
            documentTruncated = document.Length > ComUia.MaxText;
            if (documentTruncated) document = document[..ComUia.MaxText];
        }
        var executable = ImagePath(contentPid) ?? throw new InvalidOperationException("Application binary unavailable");
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable + ":" + File.GetLastWriteTimeUtc(executable).Ticks + ":" + new FileInfo(executable).Length))).ToLowerInvariant();
        return new ProgramInspectJson(ok: true, processId: pid, processStartedUtc: ProcessStartedUtc(pid), contentProcessId: contentPid,
            contentProcessStartedUtc: ProcessStartedUtc((uint)contentPid), app: actualApp, revision: revision, treeMs: treeMs, publication: ComUia.Shared.LastPublication,
            storeResume: storeResume, storeContentReady: storeContentReady,
            frame: frame is { } r ? new CaptureWindowJson2(left: r.Left, top: r.Top, width: r.Width, height: r.Height) : null,
            accessibility: new ProgramInspectJson2(tree: tree.ToString(), focused_element: focused == null ? null : $"{focused.Index} {focused.Role} {focused.Name}", document_text: document, document_truncated: documentTruncated),
            elements: nodes.Select(n => new ProgramInspectJson3(index: n.Index, depth: n.Depth * 2, role: n.Role, name: n.Name, automationId: n.AutomationId,
                value: n.Value, valueTruncated: n.ValueTruncated, description: n.Description, disabled: n.Disabled, selected: n.Selected, @checked: n.Checked,
                expanded: n.Expanded, focused: n.Focused, visible: !n.Offscreen && n.Bounds.Length == 4 && n.Bounds[2] > 0 && n.Bounds[3] > 0, password: n.Password, actions: n.Actions, ancestors: n.Ancestors, runtimeId: n.RuntimeId,
                nativeWindow: (long)n.NativeWindow, bounds: n.Bounds,
                accessKey: n.AccessKey, isDefault: n.IsDefault, invoke: n.HasInvoke, flags: Flags(n))).ToList()); // before the tree is released
    }

    internal static string[] Flags(Node n) => (n.Offscreen, n.Unreadable) switch
    {
        (true, true) => ["offscreen", "unreadable"], (true, false) => ["offscreen"], (false, true) => ["unreadable"], _ => [],
    };

    // A continuation must refuse a missing identity; neither the PID nor a UIA runtime id alone tell apart a process
    // replaced after a long pause. An ordinary observation stays readable if Windows refuses that time.
    internal static string? ProcessStartedUtc(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue) return null;
        try { using var process = Process.GetProcessById((int)pid); return process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    // Win32/WinForms combo box (COMBOBOX class, except CBS_SIMPLE): the open state is read through CB_GETDROPPEDSTATE,
    // and opening or closing goes through CB_SHOWDROPDOWN (Background.cs), whether or not the UIA provider exposes the
    // ExpandCollapse pattern.
    static void ClassicCombos(List<Node> nodes)
    {
        foreach (var n in nodes)
        {
            if (!ComUia.IsClassicCombo(n) || Win32.Send(n.NativeWindow, Win32.CB_GETDROPPEDSTATE) is not { } dropped) continue;
            n.Expanded = dropped != 0;
            n.Actions.RemoveAll(a => a is "Expand" or "Collapse");
            n.Actions.Add(dropped != 0 ? "Collapse" : "Expand");
        }
    }

    // Out of view (IsOffscreen) and with no part in its window's rectangle (root); rectangles [x, y, w, h].
    static bool HiddenOffscreen(Node target, Node root)
    {
        if (!target.Offscreen) return false;
        if (target.Bounds.Length != 4 || root.Bounds.Length != 4) return true;
        var (x, y, w, h) = (target.Bounds[0], target.Bounds[1], target.Bounds[2], target.Bounds[3]);
        var (rx, ry, rw, rh) = (root.Bounds[0], root.Bounds[1], root.Bounds[2], root.Bounds[3]);
        return x >= rx + rw || y >= ry + rh || x + w <= rx || y + h <= ry;
    }

    // The chain read again (root .. parent of the target) must be exactly the one the session observed.
    static bool SameAncestry(List<Node> nodes, Node target, List<JsonElement> expected)
    {
        if (expected.Count != target.Ancestors.Length) return false;
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var n = nodes[target.Ancestors[i]];
            if (Text(e, "role") != n.Role || Text(e, "automationId") != n.AutomationId || Text(e, "name") != n.Name) return false;
            if (!RuntimeIdOf(e).SequenceEqual(n.RuntimeId)) return false;
        }
        return true;
    }
    // Active modal dialog of a disabled window, according to Windows (GW_ENABLEDPOPUP: the window Windows activates in
    // place of its owner): owned by it, of the same application, visible, active and allowed by the shared policy; 0
    // otherwise.
    static nint ModalBox(nint hwnd, string processName, IReadOnlyList<long> chain)
    {
        if (Win32.IsWindowEnabled(hwnd)) return 0;
        var ownerPid = Win32.ProcessOf(hwnd);
        bool Valid(long candidate)
        {
            var box = (nint)candidate;
            if (box == 0 || box == hwnd || !IsWindow(box)) return false;
            if (!ModalBoxEligible(Win32.IsWindowVisible(box), Win32.IsWindowEnabled(box), OwnedThrough(box, (long)hwnd), Win32.ProcessOf(box), ownerPid, pid => ImagePath((int)pid))) return false;
            return Exclusion(processName, box) == null;
        }
        return (nint)ModalBoxCandidate((long)Win32.GetWindow(hwnd, Win32.GW_ENABLEDPOPUP), chain, w => (long)Win32.GetAncestor((nint)w, Win32.GA_ROOT), Valid);
    }
    // A box the target window owns, visible and enabled, from the same application: the same process, or another process of
    // the same executable. Chrome shows its « Sélectionner le répertoire de l'extension » box from its file-dialog utility
    // process, not from the window's process (04/11, pid 42636 vs 30592): "Target or its window is disabled". A box of
    // another executable is never routed to, even when owned. Pure, tested offline.
    internal static bool ModalBoxEligible(bool visible, bool enabled, bool owned, uint boxPid, uint ownerPid, Func<uint, string?> imagePath)
    {
        if (!visible || !enabled || !owned || boxPid == 0 || ownerPid == 0) return false;
        if (boxPid == ownerPid) return true;
        var (box, owner) = (imagePath(boxPid), imagePath(ownerPid));
        return box is { Length: > 0 } && owner is { Length: > 0 } && string.Equals(Path.GetFullPath(box), Path.GetFullPath(owner), StringComparison.OrdinalIgnoreCase);
    }
    // The modal box holding the target: the owner's first enabled popup (Paint, 28/10), otherwise the top-level window of
    // the target's own chain. Chrome owns other enabled popups (bubbles, hidden widgets) that come first, so its
    // « Sélectionner le répertoire de l'extension » box was refused, "window is disabled" (04/11). Pure, tested offline.
    internal static long ModalBoxCandidate(long firstPopup, IReadOnlyList<long> chain, Func<long, long> root, Func<long, bool> valid)
    {
        if (firstPopup != 0 && valid(firstPopup)) return firstPopup;
        foreach (var window in chain.Skip(1))
        {
            if (window == 0) continue;
            var top = root(window);
            if (top != 0 && valid(top)) return top;
        }
        return 0;
    }
    // Chain root..target (native window, disabled): index of the node of the modal dialog `box` on that chain, the root
    // (owner) being disabled and the dialog not; -1 otherwise (target outside the dialog, or no dialog). Pure function,
    // tested offline (tests/ProgramTests.cs).
    internal static int ModalBoxIndex(IReadOnlyList<(long NativeWindow, bool Disabled)> chain, long box)
    {
        if (box == 0 || chain.Count < 2 || !chain[0].Disabled) return -1;
        for (var i = chain.Count - 1; i >= 1; i--)
            if (chain[i].NativeWindow == box) return chain[i].Disabled ? -1 : i;
        return -1;
    }
    static int[] RuntimeIdOf(JsonElement e) =>
        e.TryGetProperty("runtimeId", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];

    // Top-level windows bound to the reference window: owned (chain of owners) or of the same process. Choosing to
    // follow one of them is up to the session (appeared during the action, or owned).
    // "popup": a popup without a title bar that does not activate (drop-down list, tooltip, WPF popup) or the list of a
    // Win32 combo box (ComboLBox): a transient part of the window, read through its owner's tree and never followed as
    // a new window.
    static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase) { "tooltips_class32", "IME", "MSCTFIME UI", "SysShadow", "ForegroundStaging" };
    static ProgramRelatedJson2 Related(nint bound, uint boundPid)
    {
        var windows = new List<object>();
        var z = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            z++;
            if (hwnd == bound || !Win32.IsWindowVisible(hwnd)) return true;
            var chain = new List<long>();
            for (var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER); owner != 0 && chain.Count < 16; owner = Win32.GetWindow(owner, Win32.GW_OWNER)) chain.Add(owner);
            var pid = Win32.ProcessOf(hwnd);
            if (pid != boundPid && !chain.Contains(bound)) return true;
            var cls = Win32.ClassName(hwnd);
            if (IgnoredClasses.Contains(cls) || ConsoleClasses.Contains(cls) || !Win32.GetWindowRect(hwnd, out var rect) || rect.Empty) return true;
            windows.Add(new ProgramRelatedJson(id: (long)hwnd, pid: pid, ownerChain: chain, className: cls, title: Win32.Title(hwnd), enabled: Win32.IsWindowEnabled(hwnd), popup: Win32.IsTransientPopup(hwnd, cls, bound), z: z));
            return true;
        }, 0);
        return new ProgramRelatedJson2(ok: true, boundEnabled: Win32.IsWindowEnabled(bound), boundPid: boundPid, windows: windows);
    }
    // Settings page (ms-settings:...) opened as Start > Run would, under the shared foreground token. Started by a
    // background process, Windows often opens it HIDDEN (a CoreWindow never shown, measured on 23/10); if it comes in
    // front, the foreground is given back to the user (ForegroundReturn, which takes nothing back if they touched the
    // keyboard or mouse). Returns the Settings window (the shown frame, otherwise the hidden one), its application and
    // its process. Only the ms-settings: scheme is allowed: no other protocol (call, send, payment) or program (pass 2
    // of 22/10: Sky refused ms-settings:display).
    static object OpenSettings(string uri, int? quietMs)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(uri, "^ms-settings:[A-Za-z0-9-]*$"))
            throw new ActionRefusedException("Only ms-settings: pages can be opened here; nothing was launched");
        // Shared policy read BEFORE the launch (unreadable: refused, fail closed) and applied to the Settings target.
        if (GardeFous.BlockReason("SystemSettings", "Windows.UI.Core.CoreWindow") is { } blocked) throw new ActionRefusedException(blocked + "; nothing was launched");
        using var token = ForegroundToken.Acquire("open a Settings page");
        JulienPause.ThrowIfActive(quietMs); // waiting for the token may take a while: the user's pause is read again under the token
        EmergencyStop.ThrowIfStopped();
        var before = Win32.Probe();
        string giveBack = "none";
        // The foreground is given back EACH time Settings takes it, until the end, error included (Codex's review: a
        // single restore left Settings in front if it took the foreground again).
        void ReturnIfTaken()
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != 0 && fg != before.Foreground && IsSettings(fg)) giveBack = ForegroundReturn.GiveBack(before.Foreground, fg, before.LastInput);
        }
        nint found = 0;
        var stable = false;
        try
        {
            try { UserApplication.Start(new ProcessStartInfo(uri), () => InputStarted = true); }
            catch (UserApplicationUnavailableException error) { throw new ActionRefusedException(error.Message); }
            var clock = Stopwatch.StartNew();
            long foundAt = 0;
            while (clock.ElapsedMilliseconds < 5000)
            {
                ReturnIfTaken();
                var current = SettingsWindow();
                if (current == 0) { found = 0; foundAt = 0; } // candidate gone: nothing is kept
                else if (current != found) { found = current; foundAt = clock.ElapsedMilliseconds; }
                if (found != 0 && clock.ElapsedMilliseconds - foundAt >= 600) { stable = true; break; } // the shown frame replaces the hidden one
                Thread.Sleep(30);
            }
        }
        finally { ReturnIfTaken(); }
        // Identity attested again just before returning, and returned as attested: process read BEFORE the attestation,
        // read again AFTER; if it changed (recycled HWND), unknown outcome, never the new owner (Codex's review). A window
        // not stable for 600 ms before the deadline: unknown too.
        if (!stable || found == 0) return new ProgramOpenSettingsJson(ok: true, opened: (bool?)null, window: 0L, foregroundReturned: giveBack);
        var pid = Win32.ProcessOf(found);
        if (pid == 0 || !IsWindow(found) || !IsSettings(found) || Win32.ProcessOf(found) != pid)
            return new ProgramOpenSettingsJson(ok: true, opened: (bool?)null, window: 0L, foregroundReturned: giveBack);
        // Same identity as the window inventory (AppWindows): a shown frame is named after its hosted content (SystemSettings),
        // with the frame's own pid; otherwise the launch result never matched windows and was refused as "identity changed".
        using var process = Process.GetProcessById((int)ContentPid((long)found, pid));
        if (Win32.ProcessOf(found) != pid) return new ProgramOpenSettingsJson(ok: true, opened: (bool?)null, window: 0L, foregroundReturned: giveBack);
        return new ProgramOpenSettingsJson2(ok: true, opened: (bool?)true, window: (long)found, title: Win32.Title(found), app: AppId(process), pid: pid,
            hidden: !Win32.IsWindowVisible(found), foregroundReturned: giveBack);
    }
    static bool IsSettings(nint hwnd)
    {
        if (ProcessName(Win32.ProcessOf(hwnd)) == "SystemSettings") return true;
        var hosted = false; // ApplicationFrameHost frame: its content (CoreWindow) belongs to SystemSettings
        Win32.EnumChildWindows(hwnd, (child, _) => { if (ProcessName(Win32.ProcessOf(child)) == "SystemSettings") { hosted = true; return false; } return true; }, 0);
        return hosted;
    }
    static nint SettingsWindow()
    {
        nint shown = 0, hidden = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.ClassName(hwnd) == "ApplicationFrameWindow" && Win32.IsWindowVisible(hwnd) && IsSettings(hwnd)) { shown = hwnd; return false; }
            if (hidden == 0 && Win32.ClassName(hwnd) == "Windows.UI.Core.CoreWindow" && ProcessName(Win32.ProcessOf(hwnd)) == "SystemSettings") hidden = hwnd;
            return true;
        }, 0);
        return shown != 0 ? shown : hidden;
    }
    // Process of a window's content: the CoreWindow of an ApplicationFrameHost frame (Store application), otherwise its own.
    static long ContentPid(long id, long pid)
    {
        if (!ProcessName((uint)pid).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return pid;
        var core = Win32.FindWindowExW((nint)id, 0, "Windows.UI.Core.CoreWindow", null);
        return core != 0 && Win32.ProcessOf(core) is var content and not 0 ? content : pid;
    }
    internal static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch (ArgumentException) { return ""; }
    }

    // Existence of the bound window, without reading it: "gone" only if Windows says the HWND is destroyed (or without a
    // process); an HWND taken over by another application: "other" (the old one no longer exists under that number).
    // Never inferred from a transport error, an absence in Sky or a hidden window (Codex's review, 22/10).
    // Is the followed window (anchor) still in the chain of owners?
    static bool OwnedThrough(nint hwnd, long anchor)
    {
        var depth = 0;
        for (var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER); owner != 0 && depth < 16; owner = Win32.GetWindow(owner, Win32.GW_OWNER), depth++)
            if ((long)owner == anchor) return true;
        return false;
    }
    static object Alive(nint hwnd, string app, long? expectedPid = null, long? expectedOwner = null, long? expectedAnchor = null)
    {
        if (!IsWindow(hwnd)) return new ProgramAliveJson(ok: true, state: "gone");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return new ProgramAliveJson(ok: true, state: "gone");
        // HWND taken over by another window (another process or another owner): the old one no longer exists.
        if ((expectedPid is { } wantedPid && pid != wantedPid) || (expectedOwner is { } wantedOwner && (long)Win32.GetWindow(hwnd, Win32.GW_OWNER) != wantedOwner)
            || (expectedAnchor is { } wantedAnchor && !OwnedThrough(hwnd, wantedAnchor)))
            return new ProgramAliveJson(ok: true, state: "other");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var same = string.Equals(AppId(process), app, StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
            return new ProgramAliveJson2(ok: true, state: same ? "alive" : "other", visible: Win32.IsWindowVisible(hwnd));
        }
        catch (ArgumentException) { return new ProgramAliveJson(ok: true, state: "gone"); }
    }
    // Desktop inventory without Sky (standalone MCP server): top-level application windows, visible and not cloaked by
    // DWM, like the taskbar (no tool window without WS_EX_APPWINDOW, no owned window).
    // A UWP window is hosted by ApplicationFrameHost: its application is that of the child CoreWindow.
    // The inventory also lists dialogs: an owned, visible, titled, non-tool window (Chrome's « Ouvrir » #32770 dialog,
    // without WS_EX_APPWINDOW, seen missing on 25/10 by NexusWindows for Claude), with its owner.
    // The launch compares application windows only (a dialog is not a launched application).
    static ProgramListAllWindowsJson ListAllWindows(bool includeHidden = false) => new ProgramListAllWindowsJson(ok: true, windows: AppWindows(dialogs: true, includeHidden: includeHidden).Select(w => w.Owner == 0
        ? (object)new ProgramListAllWindowsJson2(id: w.Id, app: w.App, title: w.Title, pid: w.Pid)
        : new ProgramListAllWindowsJson3(id: w.Id, app: w.App, title: w.Title, pid: w.Pid, owner: w.Owner, ownerTitle: Win32.Title((nint)w.Owner))));
    static List<(long Id, string App, string Title, long Pid, long Owner)> AppWindows(bool dialogs = false, bool includeHidden = false)
    {
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        var windows = new List<(long, string, string, long, long)>();
        var handles = new List<nint>();
        Win32.EnumWindows((window, _) => { handles.Add(window); return true; }, 0);
        var minimizedTitles = handles.Where(w => Win32.ClassName(w) == "ApplicationFrameWindow" && Win32.IsIconic(w))
            .Select(Win32.Title).ToHashSet(StringComparer.Ordinal);
        var hiddenStore = handles.Where(StorePackages.IsHiddenStoreWindow)
            .Where(w => !minimizedTitles.Contains(Win32.Title(w))).ToHashSet();
        var hiddenTitles = hiddenStore.Select(Win32.Title).ToHashSet(StringComparer.Ordinal);
        var apps = new Dictionary<uint, (string App, string Name)?>();
        (string App, string Name)? Identity(uint pid)
        {
            if (apps.TryGetValue(pid, out var known)) return known;
            (string, string)? found = null;
            try { using var p = Process.GetProcessById((int)pid); found = (AppId(p), p.ProcessName); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return apps[pid] = found;
        }
        Win32.EnumWindows((hwnd, _) =>
        {
            var hiddenApplication = hiddenStore.Contains(hwnd) || includeHidden && StorePackages.IsHiddenStoreWindow(hwnd);
            if (!includeHidden && !Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd) && !hiddenApplication) return true;
            var ex = Win32.GetWindowLongW(hwnd, GWL_EXSTYLE);
            var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER);
            var dialog = (ex & WS_EX_APPWINDOW) == 0 && owner != 0;
            if (!hiddenApplication && (ex & WS_EX_APPWINDOW) == 0 && ((ex & WS_EX_TOOLWINDOW) != 0 || (dialog && !(dialogs && Win32.IsWindowVisible(owner))))) return true;
            var cls = Win32.ClassName(hwnd);
            var title = Win32.Title(hwnd);
            if (IgnoredClasses.Contains(cls) || title.Length == 0) return true;
            var pid = Win32.ProcessOf(hwnd);
            if (cls == "ApplicationFrameWindow" && hiddenTitles.Contains(title) && !Win32.IsIconic(hwnd) && !HasForeignContent(hwnd, pid)) return true;
            if (Identity(pid) is not { } identity) return true;
            if (identity.Name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var core = Win32.FindWindowExW(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
                if (core != 0 && Identity(Win32.ProcessOf(core)) is { } content) identity = content;
            }
            if (GardeFous.BlockReason(identity.Name, cls) == null) windows.Add(((long)hwnd, identity.App, title, (long)pid, dialog ? (long)owner : 0));
            return true;
        }, 0);
        return windows;
    }
    // Launch without Sky: application (name, path, AUMID), folder or document, through the shell, under the shared token;
    // the foreground is given back each time another window takes it (a new one, or an instance already open that
    // receives the document), until the end. Addresses: ms-settings: only (another address may call, send or pay).
    // Result: the new application windows, stable for 600 ms and read again at the end. A window is attributed to the
    // launch only on proof (a started process, or a process born after the launch that is the targeted application);
    // otherwise it is returned as a candidate and the outcome stays unknown (Codex's reviews of 23/10).
    // Chromium/Electron application (shared recipe of the bench, 24/10): started as is and covered from its start, it
    // neither draws nor publishes its page to accessibility (Logi Options+ purple behind a maximized Claude; "covered
    // launch" bench: selector not found, 0/3). Recognized by its resources, never by its name, it gets occlusion
    // tracking turned off and accessibility forced; a single --disable-features (Chromium keeps only the last one). Only
    // for an executable given by its path; the model's arguments (files) come after, never a switch (LaunchArgsProblem).
    internal const string ChromiumSwitches = "--disable-features=CalculateNativeWinOcclusion --force-renderer-accessibility";

    internal static bool IsChromiumApp(string exePath)
    {
        var folder = System.IO.Path.GetDirectoryName(exePath) ?? "";
        return exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && (
            File.Exists(System.IO.Path.Combine(folder, "resources", "app.asar"))
            || Directory.Exists(System.IO.Path.Combine(folder, "resources", "app"))
            || File.Exists(System.IO.Path.Combine(folder, "chrome_100_percent.pak")));
    }

    internal static ProcessStartInfo LaunchInfo(string start, string? exePath, string args = "")
    {
        if (Steam.IsExecutable(exePath)) return Steam.StartInfo(exePath!, args, false);
        var info = new ProcessStartInfo(start) { UseShellExecute = true, Arguments = args };
        if (exePath != null && File.Exists(exePath) && IsChromiumApp(exePath))
        {
            info.Arguments = args.Length > 0 ? ChromiumSwitches + " " + args : ChromiumSwitches;
            info.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? "";
        }
        return info;
    }

    const string BareApp = @"^[A-Za-z0-9_-]+(\.exe)?$";
    // Launch line (program, arguments), as the shell would read it (pass 2 of 28/10: « EditeurTest.exe note.txt » and
    // « "C:\...\EditeurTest.exe" "C:\...\note.txt" » refused without a launch, the whole line taken for a path).
    // The whole string first: an existing file or folder with spaces is never split. Otherwise: a quoted program
    // followed by arguments, the shortest prefix (cut at a space) that is an existing .exe by its absolute path, or a
    // bare application name followed by arguments. Arguments of the separate field come after those of the line; they
    // are passed as is to the program (ShellExecute), split only for the guards (CommandLineToArgvW).
    internal static (string Target, string Args) SplitLaunch(string raw, string args)
    {
        raw = raw.Trim();
        args = args.Trim();
        var target = raw;
        var inline = "";
        if (raw.StartsWith('"'))
        {
            var end = raw.IndexOf('"', 1);
            if (end > 1 && (end == raw.Length - 1 || char.IsWhiteSpace(raw[end + 1]))) (target, inline) = (raw[1..end], raw[(end + 1)..].Trim());
        }
        else if (!File.Exists(raw) && !Directory.Exists(raw))
        {
            var first = true;
            foreach (System.Text.RegularExpressions.Match space in System.Text.RegularExpressions.Regex.Matches(raw, @"\s+"))
            {
                var head = raw[..space.Index];
                var bare = first && System.Text.RegularExpressions.Regex.IsMatch(head, BareApp, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                first = false;
                if (bare || (head.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.Path.IsPathFullyQualified(head) && File.Exists(head)))
                {
                    (target, inline) = (head, raw[(space.Index + space.Length)..]);
                    break;
                }
            }
        }
        return (target, string.Join(" ", new[] { inline, args }.Where(part => part.Length > 0)));
    }

    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint CommandLineToArgvW(string commandLine, out int count);
    [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint memory);
    // Arguments split as Windows splits them for the launched program ("x": program name, separate rules).
    internal static List<string> SplitArgs(string args)
    {
        if (args.Trim().Length == 0) return new();
        var argv = CommandLineToArgvW("x " + args, out var count);
        if (argv == 0) throw new ActionRefusedException("Unreadable arguments; nothing was launched");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "").ToList(); }
        finally { LocalFree(argv); }
    }

    // Program of a bare name as the shell finds it: App Paths (user then machine), then the PATH. Null: not found.
    internal static string? ResolveApp(string name)
    {
        var file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + file);
            if (key?.GetValue(null) is string registered && registered.Trim().Trim('"') is { Length: > 0 } path && File.Exists(Environment.ExpandEnvironmentVariables(path)))
                return Environment.ExpandEnvironmentVariables(path);
        }
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), file);
                if (System.IO.Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // Chromium/Electron browser or application: resources next to the executable, or in its version folder (Chrome and
    // Edge: Application\<version>\resources.pak).
    internal static bool IsChromiumFamily(string exePath)
    {
        if (IsChromiumApp(exePath)) return true;
        var folder = System.IO.Path.GetDirectoryName(exePath) ?? "";
        try { return File.Exists(System.IO.Path.Combine(folder, "resources.pak")) || Directory.EnumerateDirectories(folder).Any(d => File.Exists(System.IO.Path.Combine(d, "resources.pak"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Reason to refuse a program's arguments (null otherwise): for Chromium/Electron, files or words, never a switch (a
    // DevTools port, profile or extension would open the user's sessions to a control channel; the tool's own switches
    // stay its own). A bare program not found: its switches cannot be judged, refused.
    internal static string? LaunchArgsProblem(string? program, string args)
    {
        if (Steam.IsExecutable(program)) return Steam.ArgsProblem(args);
        var switchArg = SplitArgs(args).FirstOrDefault(a => a.StartsWith('-') || a.StartsWith('/'));
        if (switchArg == null) return null;
        if (program == null) return $"Switch \"{switchArg}\" for an application name Windows does not resolve: give the program's absolute path";
        return IsChromiumFamily(program) ? $"Switch \"{switchArg}\" refused for a Chromium/Electron application (control channel or profile): only files as arguments" : null;
    }

    // Launch prepared without launching anything: program, arguments and refusal (path, address, arguments), also read by
    // tests/LaunchTests.cs.
    internal sealed record LaunchPlan(string Target, string Args, string Start, bool IsPath, bool Aumid, bool IsExe, string Stem, string? ExePath);
    internal static LaunchPlan PlanLaunch(string raw, string rawArgs)
    {
        var (target, args) = SplitLaunch(raw, rawArgs);
        if (target.Length == 0) throw new ActionRefusedException("Nothing to launch; nothing was launched");
        // Absolute path according to Windows (drive, UNC with \ or /): .NET's classification, not a regex subset.
        var isPath = System.IO.Path.IsPathFullyQualified(target);
        if (!isPath && System.Text.RegularExpressions.Regex.IsMatch(target, @"^[A-Za-z][A-Za-z0-9+.-]*:"))
            throw new ActionRefusedException("Only ms-settings: addresses are launched; nothing was launched");
        // AUMID of a Store application ("Microsoft.Paint_8wekyb3d8bbwe!App"): through the shell's Applications folder.
        var aumid = !isPath && target.Contains('!') && !target.Contains('\\') && !target.Contains('/');
        // Apart from an absolute path and an AUMID, only a bare application name is accepted ("notepad", "winword.exe"): a
        // relative path, a file name or a name Windows would normalize (trailing dot or space) would be resolved from the
        // helper's current folder, hence ambiguous; refused, an absolute path is asked for (Codex's reviews of 23/10).
        if (!isPath && !aumid && !System.Text.RegularExpressions.Regex.IsMatch(target, BareApp, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ActionRefusedException("Give an absolute path for a file or folder (or an application name); nothing was launched");
        var start = aumid ? @"shell:AppsFolder\" + target : target;
        // Targeted application: executable (name or .exe path) or AUMID. A forbidden application (password manager) is
        // refused before any launch; a document or a folder is never judged by its file name.
        var isExe = !aumid && (!isPath || target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var stem = isExe ? System.IO.Path.GetFileNameWithoutExtension(target) : "";
        var exePath = isExe && isPath ? System.IO.Path.GetFullPath(target) : null;
        if (isExe && Steam.Resolve(target, exePath ?? ResolveApp(target)) is { } steamExe) { exePath = steamExe; start = steamExe; }
        if (args.Length > 0)
        {
            if (!isExe) throw new ActionRefusedException("Arguments go only to a program (.exe or application name); a document, folder or Store app opens alone; nothing was launched");
            if (LaunchArgsProblem(exePath ?? ResolveApp(target), args) is { } problem) throw new ActionRefusedException(problem + "; nothing was launched");
        }
        return new LaunchPlan(target, args, start, isPath, aumid, isExe, stem, exePath);
    }

    static object Launch(string raw, string rawArgs, int? quietMs, bool devtools = false, bool restart = false)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            if (devtools || restart) throw new ActionRefusedException("devtools and restart are supported only for the genuine Steam client; nothing was launched");
            if (rawArgs.Trim().Length > 0) throw new ActionRefusedException("A Settings page takes no arguments; nothing was launched");
            return OpenSettings(trimmed, quietMs);
        }
        var plan = PlanLaunch(raw, rawArgs);
        var (target, isPath, aumid, isExe, stem, exePath, start) = (plan.Target, plan.IsPath, plan.Aumid, plan.IsExe, plan.Stem, plan.ExePath, plan.Start);
        if (!Steam.IsExecutable(exePath) && (devtools || restart)) throw new ActionRefusedException("devtools and restart are supported only for the genuine Steam client; nothing was launched");
        // Targeted folder (an existing absolute path): its identity on disk, for the "folder" proof (pass 2 of 28/10: the
        // Explorer window opened on the folder stayed a mere candidate, unknown outcome).
        var folder = isPath && Directory.Exists(target) ? Explorer.Identity(target) : null;
        var names = new List<string>();
        if (isExe) names.Add(stem);
        if (aumid)
        {
            // "AgileBits.1Password_xxx!App": whole package name without the publisher, compared with the forbidden processes.
            var package = target.Split('!')[0].Split('_')[0];
            names.Add(package);
            names.Add(package.Split('.').Last());
        }
        foreach (var name in names)
            if (name.Length > 0 && GardeFous.BlockReason(name, "") is { } blocked) throw new ActionRefusedException(blocked + "; nothing was launched");
        if (Steam.IsExecutable(exePath)) return LaunchSteam(exePath!, plan.Args, devtools, restart, quietMs);
        using var token = ForegroundToken.Acquire("launch an application");
        JulienPause.ThrowIfActive(quietMs);
        EmergencyStop.ThrowIfStopped();
        // Inventory taken under the token, just before the launch: a window that appeared while waiting for the token is
        // never taken for the launched one.
        var before = new HashSet<long>(AppWindows().Select(w => w.Id));
        var probe = Win32.Probe();
        string giveBack = "none";
        void ReturnIfTaken()
        {
            var fg = Win32.GetForegroundWindow();
            // Even another window of the user's process (a document opened in their instance): their window is given back.
            if (fg != 0 && fg != probe.Foreground) giveBack = ForegroundReturn.GiveBack(probe.Foreground, fg, probe.LastInput, sameProcess: true);
        }
        var launchedAt = DateTime.Now;
        // The desktop broker returns no PID: attribution by a process BORN AFTER the launch that really is the targeted
        // application (same executable path, same process name for a bare name, same AUMID), or a new Explorer window that
        // shows the requested folder (read back through the shell, identity on disk). A file name or a title proves
        // nothing: a document or another match stays a candidate.
        string? Proof((long Id, string App, string Title, long Pid, long Owner) w)
        {
            if (folder is { } shown) return Explorer.Shows((nint)w.Id, shown) ? "folder" : null;
            if (!isExe && !aumid) return null;
            try
            {
                // Store application hosted by ApplicationFrameHost: the process of its content (CoreWindow), never that of
                // the frame, born well before the launch (pass 2 of 28/10: an application launched by its AUMID was never
                // attributed).
                using var p = Process.GetProcessById((int)ContentPid(w.Id, w.Pid));
                if (p.StartTime < launchedAt.AddMilliseconds(-50)) return null;
                if (aumid) return w.App.Equals(target, StringComparison.OrdinalIgnoreCase) ? "application" : null;
                if (exePath != null) return w.App.Equals("process:" + exePath, StringComparison.OrdinalIgnoreCase) ? "application" : null;
                return p.ProcessName.Equals(stem, StringComparison.OrdinalIgnoreCase) ? "application" : null;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }
        List<(long Id, string App, string Title, long Pid, long Owner)> found = new();
        bool stable = false;
        try
        {
            try
            {
                UserApplication.Start(LaunchInfo(start, exePath, plan.Args), () => InputStarted = true);
            }
            catch (UserApplicationUnavailableException error) { throw new ActionRefusedException(error.Message); }
            catch (System.ComponentModel.Win32Exception error) when (!InputStarted) { throw new ActionRefusedException("Nothing to launch at this name or path (" + error.Message + "); nothing was launched"); }
            var clock = Stopwatch.StartNew();
            string seen = ""; long seenAt = 0;
            while (clock.ElapsedMilliseconds < 15000)
            {
                ReturnIfTaken();
                var fresh = AppWindows().Where(w => !before.Contains(w.Id)).ToList();
                var key = string.Join(",", fresh.Select(w => $"{w.Id}:{w.Pid}"));
                if (key != seen) { seen = key; seenAt = clock.ElapsedMilliseconds; found = fresh; }
                if (fresh.Count > 0 && clock.ElapsedMilliseconds - seenAt >= 600) { stable = true; break; }
                Thread.Sleep(50);
            }
            // Explorer opens its window on « Accueil » then goes to the folder (pass 2 of 28/10): the shown folder is read
            // again for up to 3 s, reads only.
            if (stable && folder is { } wanted)
            {
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 3000 && !found.Any(w => Explorer.Shows((nint)w.Id, wanted))) { ReturnIfTaken(); Thread.Sleep(100); }
            }
        }
        finally { ReturnIfTaken(); }
        // Final read-back: each window still there, of the same process (a recycled HWND is never kept).
        if (stable)
        {
            var now = AppWindows().ToDictionary(w => w.Id);
            stable = found.All(w => now.TryGetValue(w.Id, out var again) && again.Pid == w.Pid);
        }
        var proofs = stable ? found.Select(w => (Window: w, Proof: Proof(w))).Where(p => p.Proof != null).ToDictionary(p => p.Window.Id, p => p.Proof) : new();
        var mine = found.Where(w => proofs.ContainsKey(w.Id)).ToList();
        var candidates = found.Where(w => !mine.Contains(w)).ToList();
        object Shape((long Id, string App, string Title, long Pid, long Owner) w) => new ProgramLaunchJson(id: w.Id, app: w.App, title: w.Title, pid: w.Pid,
            // Proof of the attribution: "process" (process returned by the shell), "folder" (Explorer window that shows the
            // requested folder) or "application" (the targeted application, born after the request: another instance
            // started at the same moment could be taken for it).
            proof: proofs.TryGetValue(w.Id, out var proof) ? proof : null);
        return new ProgramLaunchJson2(ok: true, opened: mine.Count > 0 ? (bool?)true : null,
            windows: mine.Select(Shape), candidates: candidates.Select(Shape), foregroundReturned: giveBack);
    }
    // Closing: WM_CLOSE posted once (never replayed), then read back for 3 s: closed, hidden, or still open with the
    // window it opened (save?) to look at.
    static object CloseWindow(nint hwnd, Action started)
    {
        started();
        if (!Win32.PostMessageW(hwnd, 0x0010, 0, 0)) throw new ActionRefusedException("The close message could not be posted; nothing was sent");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            if (!IsWindow(hwnd)) return new ProgramAliveJson(ok: true, state: "closed");
            Thread.Sleep(50);
        }
        if (!Win32.IsWindowVisible(hwnd)) return new ProgramAliveJson(ok: true, state: "hidden");
        var prompts = new List<object>();
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.IsWindowVisible(h) && Win32.GetWindow(h, Win32.GW_OWNER) == hwnd) prompts.Add(new ProgramCloseWindowJson(id: (long)h, title: Win32.Title(h)));
            return true;
        }, 0);
        return new ProgramCloseWindowJson2(ok: true, state: "open", prompts: prompts);
    }
    static object ListWindows(int processId, bool includeHidden = false)
    {
        if (processId == 0) return ListAllWindows(includeHidden);
        using var process = Process.GetProcessById(processId);
        var app = AppId(process);
        var windows = new List<object>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.IsWindowVisible(hwnd) && Win32.ProcessOf(hwnd) == processId)
            {
                var cls = Win32.ClassName(hwnd);
                if (!IgnoredClasses.Contains(cls) && GardeFous.BlockReason(process.ProcessName, cls) == null) windows.Add(new ProgramListWindowsJson(id: (long)hwnd, app: app, title: Win32.Title(hwnd)));
            }
            return true;
        }, 0);
        return new ProgramListWindowsJson2(ok: true, windows: windows);
    }
    static void Write(object value) => Console.WriteLine(EngineJson.Serialize(value, Json));
    static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    internal static string AppId(Process process)
    {
        uint length = 0;
        if (GetApplicationUserModelId(process.Handle, ref length, null) == 122 && length > 0)
        {
            var id = new char[length];
            if (GetApplicationUserModelId(process.Handle, ref length, id) == 0) return Win32.Terminated(id);
        }
        return "process:" + (ImagePath(process.Id) ?? throw new InvalidOperationException("Process path unavailable"));
    }
    // Path of the executable (QueryFullProcessImageName: without enumerating the process's modules).
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);
    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }
    static bool Matches(Node node, JsonElement selector)
    {
        bool hasField = false;
        foreach (var p in selector.EnumerateObject())
        {
            if (p.Name == "ancestor") continue;
            if (p.Name == "window") { hasField = true; if (p.Value.ValueKind != JsonValueKind.Number || (long)node.NativeWindow != p.Value.GetInt64()) return false; continue; }
            var actual = p.Name switch { "name" => node.Name, "role" => node.Role, "automationId" => node.AutomationId, _ => throw new InvalidOperationException("Unknown selector field") };
            hasField = true; if (p.Name == "name" ? !TargetName.Same(actual, p.Value.GetString()) : actual != p.Value.GetString()) return false;
        }
        if (!hasField) throw new InvalidOperationException("Empty native selector");
        return true;
    }
}
