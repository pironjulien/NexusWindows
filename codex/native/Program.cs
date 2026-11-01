using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

static partial class Program
{
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? appId);
    // Lectures : permises pendant l'arret d'urgence, n'envoient aucune entree.
    static readonly HashSet<string> ReadOnlyOperations = new(StringComparer.Ordinal) { "inspect", "capture", "ocr", "related", "windows", "probe", "alive", "table" };
    static readonly HashSet<string> ActionOperations = new(StringComparer.Ordinal) { "click", "setValue", "secondary", "select", "typeText", "physical", "focus", "realizeNamed", "key" };
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // This engine's own executable, started again for its helper children (--com-worker, --steam-startup-info,
    // --stop-guard). The in-process xUnit tests point it at the built engine instead of the test host.
    internal static string HelperExecutable { get; set; } = Environment.ProcessPath ?? "";
    static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    // Blocage dur : politique commune garde-fous.json (GardeFous.cs). Consoles, terminaux et outils d'administration sont
    // libres depuis l'accord du 18/10 ; seuls restent bloques le bureau securise, les identifiants Windows, les
    // gestionnaires de mots de passe et l'application de Codex lui-meme (ses demandes d'autorisation).
    static string? Exclusion(string processName, nint hwnd) => GardeFous.BlockReason(processName, Win32.ClassName(hwnd));
    // Fenetres console : jamais suivies comme un dialogue apparu pendant une action (elles restent pilotables si on les
    // lie explicitement).
    static readonly HashSet<string> ConsoleClasses = new(StringComparer.OrdinalIgnoreCase)
    { "ConsoleWindowClass", "PseudoConsoleWindow", "CASCADIA_HOSTING_WINDOW_CLASS" };

    [MTAThread]
    static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length is 2 or 3 && args[0] == "--foreground-token")
        {
            // Troisieme argument facultatif : fenetre cible (hwnd). La fenetre active de Julien est notee des le jeton pris
            // et lui est rendue a la fin, sous le jeton, si la cible tient encore le premier plan (ForegroundReturn).
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
                Write(new { ok = true, foreground = (long)noted });
                Console.Out.Flush();
                var said = abandoned.GetAwaiter().GetResult();
                // Accuse terminal de liberation normale, toujours ecrit (cible ou non) : sans lui, l'appelant tient la
                // fermeture pour une perte du jeton (relecture de Codex, 21/10).
                string? state = null;
                if (target != 0)
                {
                    // « keep » : l'appelant a vu Julien reprendre clavier ou souris pendant le bail (sa presence distingue
                    // les injections de l'agent, que LastInput ne distingue pas) ; son choix est garde.
                    state = said.Contains("keep") ? "kept-by-julien" : ForegroundReturn.GiveBack(noted, target);
                }
                try { Write(new { released = true, giveBack = state }); Console.Out.Flush(); } catch (IOException) { } // appelant deja parti
                return 0;
            }
            catch (Exception error) when (!acquired) { Write(new { ok = false, error = error.GetType().Name, message = error.Message }); return 1; }
        }
        if (args.Length is 3 or 4 && args[0] == "--window-lock") return WindowLock.Hold(args);
        if (args.Length == 2 && args[0] == "--stop-guard") return EmergencyStop.Guard(args[1]);
        // Images de l'indicateur (BGRA premultiplie, bout a bout) pour les tests, sans rien afficher.
        if (args.Length == 3 && args[0] == "--indicator-frames" && int.TryParse(args[1], out var diameter) && diameter is >= 24 and <= 512)
        {
            File.WriteAllBytes(args[2], BusyIndicator.RenderFrames(diameter).SelectMany(f => f).ToArray());
            return 0;
        }
        // Processus des motifs UI Automation (voir Background.cs).
        if (args.SequenceEqual(new[] { "--com-worker" })) return ComWorker.Run();
        if (args.Length == 2 && args[0] == "--steam-startup-info") return Steam.WriteStartupInfo(args[1]); // enfant du banc sans GUI
        if (args.Length == 1 && args[0] == "--migrate-routines") return McpServer.MigrateRoutines();
        // Declaration to the MCP clients (Registration.cs): installers and get.ps1, never a session.
        if (args.Length >= 2 && args[0] == "--register") return Registration.Command(args);
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
        InputStarted = false; // une requete rejetee avant Execute n'herite jamais de l'etat de la precedente
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
                // Les enfants COM heritent aussi de ce chemin, avec l'environnement Windows complet.
                Environment.SetEnvironmentVariable("NEXUS_GARDE_FOUS", file);
            }
            result = Execute(request);
        }
        catch (Exception error)
        {
            // inputStarted : une entree a pu partir avant l'erreur (relecture de Codex du 24/10) ; le client la rend
            // terminale (issue inconnue, accord consomme, aucun repli), quel que soit le type de l'exception.
            result = new { ok = false, error = error.GetType().Name, message = error.Message, inputStarted = InputStarted };
            code = 1;
        }
        if (server) Write(new { requestId, result }); else Write(result);
        Console.Out.Flush();
        return code;
    }
    // Vrai des qu'une entree a pu partir (RunAction, WM_COMMAND de menu) : avant, toute erreur d'une action precede
    // l'entree.
    [ThreadStatic] internal static bool InputStarted;
    internal static object Execute(JsonElement request)
    {
        InputStarted = false;
        var op = Text(request, "operation");
        try { return ExecuteRequest(request, op); }
        catch (Exception error) when (!ReadOnlyOperations.Contains(op) && !InputStarted && error is not (EmergencyStopException or ActionRefusedException or JulienActiveException)
            && request.TryGetProperty("windowId", out var w) && w.ValueKind == JsonValueKind.Number && !IsWindow((nint)w.GetInt64()))
        {
            // Fenetre fermee pendant la requete, avant toute entree : refus (rien n'a ete envoye), jamais un resultat
            // inconnu, et jamais un repli physique vers une fenetre disparue.
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
            return new { ok = true, foreground = (long)probe.Foreground, foregroundPid = probe.Foreground == 0 ? 0 : Win32.ProcessOf(probe.Foreground),
                cursor = new { x = probe.Cursor.X, y = probe.Cursor.Y }, idleMs = unchecked(Win32.GetTickCount() - probe.LastInput) };
        }
        if (op == "windows") return ListWindows(request.GetProperty("processId").GetInt32(), request.TryGetProperty("includeHidden", out var hidden) && hidden.GetBoolean());
        if (op == "launch") return Launch(request.GetProperty("target").GetString() ?? "", Text(request, "args"),
            request.TryGetProperty("quietMs", out var lq) && lq.ValueKind == JsonValueKind.Number ? lq.GetInt32() : null,
            request.TryGetProperty("devtools", out var ld) && ld.GetBoolean(), request.TryGetProperty("restart", out var lr) && lr.GetBoolean());
        if (op == "openSettings") return OpenSettings(request.GetProperty("uri").GetString() ?? "",
            request.TryGetProperty("quietMs", out var oq) && oq.ValueKind == JsonValueKind.Number ? oq.GetInt32() : null);
        // Un refus avant toute entree n'est jamais un resultat inconnu : aucune entree n'a ete envoyee.
        Exception Refuse(string message) => action ? new ActionRefusedException(message) : new InvalidOperationException(message);
        var hwnd = (nint)request.GetProperty("windowId").GetInt64();
        var app = request.GetProperty("app").GetString() ?? "";
        // Identite imposee par la session (fenetre absente de Sky) : meme processus, meme proprietaire, a chaque requete.
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
                // Numeros (Marks.cs) : [{index, bounds:[x,y,w,h] ecran, scope: HWND porteur ou 0}] et cadre lu avec l'arbre.
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
            // OCR de la derniere capture de CETTE fenetre (chemin rendu par capture), lecture seule.
            if (op == "ocr") return Capture.Recognize(Text(request, "path"));
            if (op == "related") return Related(hwnd, pid);
            if (op == "table")
            {
                var runtimeId = request.GetProperty("runtimeId").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var hint = request.TryGetProperty("nativeWindow", out var nw) && nw.ValueKind == JsonValueKind.Number ? (nint)nw.GetInt64() : 0;
                var maxRows = request.TryGetProperty("maxRows", out var mr) && mr.ValueKind == JsonValueKind.Number ? Math.Clamp(mr.GetInt32(), 1, 600) : 100;
                var read = ComUia.Shared.ReadTable(hwnd, runtimeId, hint, maxRows);
                return read == null ? new { ok = true, grid = false } :
                    new { ok = true, grid = true, headers = read.Headers, rows = read.Rows, rowCount = read.RowCount, columnCount = read.ColumnCount };
            }
            if (op == "menu") return MenuCommand(hwnd, Text(request, "path"));
            if (op == "key" && !request.TryGetProperty("selector", out _)) return KeyAction(hwnd, Text(request, "key"), request);
            if (op == "point") return PointAction(hwnd, request);
            // Vraie souris et vrai clavier du helper, sous le jeton tenu par l'appelant (Physical.cs).
            // Sans cible native : point d'une capture, touche ou texte. Avec cible : resolue ci-dessous, puis relue sous le
            // jeton apres la mise au premier plan.
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

            // Actions. Observation native (identite d'execution et chaine des ancetres fournies) : relecture limitee a la
            // cible et a ses ancetres, comparee a ce que la session a vu. Observation Sky : arbre complet, selecteur.
            // Toute impossibilite a ce stade precede l'entree : technique suivante permise ou refus.
            var mode = Text(request, "mode", "background");
            if (mode is not ("background" or "foreground")) throw new InvalidOperationException("Unknown action mode");
            var selector = request.GetProperty("selector");
            // Element de menu WinForms (MenuStrip, menu contextuel ContextMenuStrip) : ses identites UIA changent a chaque
            // lecture (banc commun du 21/10 : « Target is no longer present »). Designe par son NOM, unique dans la fenetre
            // liee, et invoque par UIA (ComWorker.MenuPath), comme le chemin de menu.
            // Contraintes stables gardees (relecture de Codex du 22/10) : nom, identifiant d'automatisation, et le contexte du
            // menu (elements de menu ancetres observes, dans l'ordre) ; un ancetre exige par le selecteur doit y figurer.
            if (op == "click" && mode == "background" && Win32.ClassName(hwnd).StartsWith("WindowsForms10.", StringComparison.Ordinal) &&
                selector.TryGetProperty("role", out var menuRole) && menuRole.GetString() == "menuitem" &&
                selector.TryGetProperty("name", out var menuName) && menuName.GetString() is { Length: > 0 })
            {
                if (!Win32.IsWindowEnabled(hwnd)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
                var observed = request.TryGetProperty("ancestry", out var anc0) && anc0.ValueKind == JsonValueKind.Array ? anc0.EnumerateArray().ToList() : [];
                // Commande d'un menu contextuel ou deroulant ouvert : resolue dans CETTE surgissante (HWND lu a l'observation),
                // encore ouverte, du meme processus et liee a la fenetre suivie ; sinon refus (observer de nouveau).
                var scope = (nint)MenuSearch.Scope(hwnd, observed);
                // Menu exige par le selecteur (ancestor.window : celui que le clic droit a ouvert) : la portee DOIT etre lui.
                if (selector.TryGetProperty("ancestor", out var within) && within.TryGetProperty("window", out var menu) &&
                    menu.ValueKind == JsonValueKind.Number && menu.GetInt64() != (long)scope)
                    throw new ActionRefusedException("The command is not in the required open menu; inspect again");
                if (scope != hwnd && !(Win32.IsWindowVisible(scope) && Win32.ProcessOf(scope) == Win32.ProcessOf(hwnd) && Win32.IsTransientPopup(scope, Win32.ClassName(scope), hwnd)))
                    throw new ActionRefusedException("The open menu of this command is closed or no longer linked to the window; inspect again");
                return MenuItemByPath(scope, MenuSearch.Segments(observed, selector));
            }
            // Root : noeud racine geometrique de la fenetre d'action (la boite modale choisie, sinon la racine de l'arbre).
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
                    // Noeud synthetique (option d'une liste deroulante classique) : identite relue sur la liste deroulante qui
                    // le porte (dernier ancetre reel), puis ses options relues par messages.
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
                        // Element expose a deux places de l'arbre (menu contextuel de l'Explorateur, banc du 28/10) : la
                        // relecture limitee suit le parent UIA de l'element, la lecture complete (meme parcours que
                        // l'observation, doublons ecartes par ComUia.ReadTree) garde sa premiere place. La chaine observee est
                        // donc comparee a la lecture complete ; differente la aussi : refus, comme avant.
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
                    // Meme regle que lib/tree.mjs findElements : libelle homonyme d'un seul controle ecarte (sans role).
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
                    // Fenetre desactivee par sa boite modale (banc du 28/10, Paint : « Enregistrer sous ») : UIA range la boite
                    // sous sa proprietaire. Une cible DANS cette boite (boite modale active de Windows, possedee, meme
                    // processus, visible) agit sur la boite, comme si elle avait ete visee ; toute autre cible est refusee.
                    var box = ModalBox(hwnd, process.ProcessName);
                    var chain = target.Ancestors.Select(i => nodes[i]).Append(target).Select(n => ((long)n.NativeWindow, n.Disabled)).ToList();
                    var boxIndex = ModalBoxIndex(chain, (long)box);
                    if (boxIndex < 0) throw new ActionRefusedException("Target or its window is disabled (a modal dialog may be open)");
                    window = box;
                    // Rectangle de reference : celui de la boite, jamais celui de sa proprietaire (relecture de Codex : une
                    // boite hors du cadre de la proprietaire rendait sa cible « off-screen »).
                    root = boxIndex < target.Ancestors.Length ? nodes[target.Ancestors[boxIndex]] : target;
                }
                else root = nodes[0];
                if (target.Password && GardeFous.CurrentAgent != "antigravity") throw new ActionRefusedException("Password controls require the dedicated supported workflow");
                if (op == "secondary" && Text(request, "action") != "ScrollIntoView" && !target.Actions.Contains(Text(request, "action"))) throw new ActionRefusedException("Secondary action is not exposed");
                // Visible d'abord (meme regle que les autres agents) : un element hors champ dont rien ne recoupe sa fenetre
                // n'est jamais clique par cible ; le faire defiler, puis reobserver.
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
                // Clic physique sur une cible native : relue sous le jeton APRES la mise au premier plan (identite,
                // ascendance, rectangle frais), au centre de sa partie visible ; changee ou cachee : refus avant entree.
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

    // Centre de la partie visible de la cible dans sa fenetre (rectangles UIA [x, y, largeur, hauteur], pixels reels).
    static (int X, int Y) VisibleCenter(Node target, Node root)
    {
        if (target.Bounds is not [var x, var y, var w, var h] || w <= 0 || h <= 0) throw new ActionRefusedException("The native target has no on-screen rectangle: observe a native screenshot");
        double left = x, top = y, right = x + w, bottom = y + h;
        if (root.Bounds is [var rx, var ry, var rw, var rh] && rw > 0 && rh > 0)
            (left, top, right, bottom) = (Math.Max(left, rx), Math.Max(top, ry), Math.Min(right, rx + rw), Math.Min(bottom, ry + rh));
        if (right <= left || bottom <= top) throw new ActionRefusedException("Target is off-screen with no visible part in its window; scroll it into view and inspect again");
        return ((int)Math.Round((left + right) / 2), (int)Math.Round((top + bottom) / 2));
    }

    static (List<Node> Nodes, Win32.Rect? Frame) ReadInspection(Func<Win32.Rect?> area, Func<List<Node>> readFresh,
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
    // Offline tests invoke this private seam by reflection, without a new production CLI mode or a UIA provider.
    internal static object InspectionFrameSelfTest()
    {
        var checks = 0;
        void Assert(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var area = new Win32.Rect { Left = 10, Top = 20, Right = 610, Bottom = 420 };
        var stale = new List<Node>(); var fresh = new List<Node>(); var calls = new List<string>(); var reads = 0;
        var observation = ReadInspection(() => { calls.Add("area"); return area; }, () => { calls.Add("read"); reads++; return fresh; },
            () => calls.Add("identity"), nodes => { calls.Add("content"); Assert(ReferenceEquals(nodes, fresh), "Content must be validated against the fresh tree"); });
        Assert(ReferenceEquals(observation.Nodes, fresh) && !ReferenceEquals(observation.Nodes, stale) && reads == 1, "Never return the preparatory cached tree");
        Assert(observation.Frame is { } f && f.Left == area.Left && f.Bottom == area.Bottom, "A stable fresh read has a frame even when a preparatory cache existed");
        Assert(calls.SequenceEqual(new[] { "identity", "area", "identity", "read", "content", "area", "content" }), "Identity and actual read are bracketed by frame measurements");
        var position = 0;
        observation = ReadInspection(() => ++position == 1 ? area : new Win32.Rect { Left = 11, Top = 20, Right = 611, Bottom = 420 }, () => fresh, () => { }, _ => { });
        Assert(observation.Frame == null, "Moving during the fresh read invalidates coordinates");
        foreach (var missingFirst in new[] { true, false })
        {
            position = 0;
            observation = ReadInspection(() => (++position == 1) == missingFirst ? null : area, () => fresh, () => { }, _ => { });
            Assert(observation.Frame == null, "A missing before/after frame never becomes a fabricated frame");
        }
        reads = 0; var refused = false;
        try { ReadInspection(() => area, () => { reads++; return fresh; }, () => throw new ActionRefusedException("window replaced"), _ => { }); }
        catch (ActionRefusedException) { refused = true; }
        Assert(refused && reads == 0, "A replaced window is rejected before reading its contents");
        var captures = 0; refused = false;
        try { ReadInspection(() => { captures++; return area; }, () => fresh, () => { }, _ => { if (captures == 2) throw new ActionRefusedException("content replaced"); }); }
        catch (ActionRefusedException) { refused = true; }
        Assert(refused && captures == 2, "Content changed during the final measurement is rejected even with stable geometry");
        return new { ok = true, checks };
    }
    static object Inspect(List<Node> nodes, int contentPid, string actualApp, uint pid, long treeMs, Win32.Rect? frame = null,
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
        return new { ok = true, processId = pid, processStartedUtc = ProcessStartedUtc(pid), contentProcessId = contentPid,
            contentProcessStartedUtc = ProcessStartedUtc((uint)contentPid), app = actualApp, revision, treeMs, publication = ComUia.Shared.LastPublication,
            storeResume, storeContentReady,
            frame = frame is { } r ? new { left = r.Left, top = r.Top, width = r.Width, height = r.Height } : null,
            accessibility = new { tree = tree.ToString(), focused_element = focused == null ? null : $"{focused.Index} {focused.Role} {focused.Name}", document_text = document, document_truncated = documentTruncated },
            elements = nodes.Select(n => new { index = n.Index, depth = n.Depth * 2, role = n.Role, name = n.Name, automationId = n.AutomationId,
                value = n.Value, valueTruncated = n.ValueTruncated, description = n.Description, disabled = n.Disabled, selected = n.Selected, @checked = n.Checked,
                expanded = n.Expanded, focused = n.Focused, visible = !n.Offscreen && n.Bounds.Length == 4 && n.Bounds[2] > 0 && n.Bounds[3] > 0, password = n.Password, actions = n.Actions, ancestors = n.Ancestors, runtimeId = n.RuntimeId,
                nativeWindow = (long)n.NativeWindow, bounds = n.Bounds,
                accessKey = n.AccessKey, isDefault = n.IsDefault, invoke = n.HasInvoke, flags = n.Offscreen ? new[] { "offscreen" } : Array.Empty<string>() }).ToList() }; // avant la liberation de l'arbre
    }

    // Une continuation doit refuser une identite manquante ; ni le PID ni un runtime UIA ne distinguent seuls un
    // processus remplace apres une longue pause. Une observation ordinaire reste lisible si Windows refuse cette heure.
    internal static string? ProcessStartedUtc(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue) return null;
        try { using var process = Process.GetProcessById((int)pid); return process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    // Liste deroulante Win32/WinForms (classe COMBOBOX, hors CBS_SIMPLE) : l'etat ouvert est relu par CB_GETDROPPEDSTATE,
    // et l'ouverture ou la fermeture se fait par CB_SHOWDROPDOWN (Background.cs), que le fournisseur UIA expose ou non
    // le motif ExpandCollapse.
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

    // Hors champ (IsOffscreen) et sans aucune partie dans le rectangle de sa fenetre (racine) ; rectangles [x, y, l, h].
    static bool HiddenOffscreen(Node target, Node root)
    {
        if (!target.Offscreen) return false;
        if (target.Bounds.Length != 4 || root.Bounds.Length != 4) return true;
        var (x, y, w, h) = (target.Bounds[0], target.Bounds[1], target.Bounds[2], target.Bounds[3]);
        var (rx, ry, rw, rh) = (root.Bounds[0], root.Bounds[1], root.Bounds[2], root.Bounds[3]);
        return x >= rx + rw || y >= ry + rh || x + w <= rx || y + h <= ry;
    }

    // La chaine relue (racine .. parent de la cible) doit etre exactement celle que la session a observee.
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
    // Boite modale active d'une fenetre desactivee, selon Windows (GW_ENABLEDPOPUP : la fenetre que Windows active a la place
    // de sa proprietaire) : possedee par elle, du meme processus, visible, active et permise par la politique commune ; 0 sinon.
    static nint ModalBox(nint hwnd, string processName)
    {
        if (Win32.IsWindowEnabled(hwnd)) return 0;
        var box = Win32.GetWindow(hwnd, Win32.GW_ENABLEDPOPUP);
        if (box == 0 || box == hwnd || !IsWindow(box) || !Win32.IsWindowVisible(box) || !Win32.IsWindowEnabled(box)) return 0;
        if (Win32.ProcessOf(box) != Win32.ProcessOf(hwnd) || !OwnedThrough(box, (long)hwnd)) return 0;
        return Exclusion(processName, box) == null ? box : 0;
    }
    // Chaine racine..cible (fenetre native, desactive) : indice du noeud de la boite modale `box` sur cette chaine, la racine
    // (proprietaire) etant desactivee et la boite non ; -1 sinon (cible hors de la boite, ou pas de boite). Fonction pure,
    // testee hors ligne (tests/ProgramTests.cs).
    internal static int ModalBoxIndex(IReadOnlyList<(long NativeWindow, bool Disabled)> chain, long box)
    {
        if (box == 0 || chain.Count < 2 || !chain[0].Disabled) return -1;
        for (var i = chain.Count - 1; i >= 1; i--)
            if (chain[i].NativeWindow == box) return chain[i].Disabled ? -1 : i;
        return -1;
    }
    internal static int ModalSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = input.RootElement.EnumerateArray().Select(c => ModalBoxIndex(
            c.GetProperty("chain").EnumerateArray().Select(n => (n.GetProperty("window").GetInt64(), n.GetProperty("disabled").GetBoolean())).ToList(),
            c.GetProperty("box").GetInt64())).ToList();
        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
    }
    // Centre visible d'une cible dans le rectangle de la fenetre d'action (racine de l'arbre, ou boite modale), hors ligne.
    internal static int CenterSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            Node Of(string key) => new() { Role = "pane", Name = "", AutomationId = "", Description = "", RuntimeId = [], Ancestors = [],
                Bounds = c.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray() };
            try { var (x, y) = VisibleCenter(Of("target"), Of("root")); results.Add(new[] { x, y }); }
            catch (ActionRefusedException error) { results.Add(error.Message); }
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
    }
    static int[] RuntimeIdOf(JsonElement e) =>
        e.TryGetProperty("runtimeId", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];

    // Fenetres de premier niveau liees a la fenetre de reference : possedees (chaine des proprietaires) ou du meme
    // processus. Le choix de suivre l'une d'elles revient a la session (apparue pendant l'action, ou possedee).
    // « popup » : surgissante sans barre de titre qui ne s'active pas (liste deroulante, infobulle, surgissante WPF) ou
    // liste d'une liste deroulante Win32 (ComboLBox) : partie transitoire de la fenetre, lue par l'arbre de son
    // proprietaire et jamais suivie comme une nouvelle fenetre.
    static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase) { "tooltips_class32", "IME", "MSCTFIME UI", "SysShadow", "ForegroundStaging" };
    static object Related(nint bound, uint boundPid)
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
            windows.Add(new { id = (long)hwnd, pid, ownerChain = chain, className = cls, title = Win32.Title(hwnd), enabled = Win32.IsWindowEnabled(hwnd), popup = Win32.IsTransientPopup(hwnd, cls, bound), z });
            return true;
        }, 0);
        return new { ok = true, boundEnabled = Win32.IsWindowEnabled(bound), boundPid, windows };
    }
    // Page des Parametres (ms-settings:...) ouverte comme le ferait Demarrer > Executer, sous le jeton commun de premier
    // plan. Lancee par un processus d'arriere-plan, Windows l'ouvre souvent MASQUEE (CoreWindow jamais montree, mesure
    // du 23/10) ; si elle vient devant, le premier plan est rendu a Julien (ForegroundReturn, qui ne reprend rien s'il a
    // touche clavier ou souris). Rend la fenetre des Parametres (cadre montre, sinon la masquee), son application et son
    // processus. Seul le schema ms-settings: est permis : aucun autre protocole (appel, envoi, paiement) ni programme
    // (passe 2 du 22/10 : Sky refusait ms-settings:display).
    static object OpenSettings(string uri, int? quietMs)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(uri, "^ms-settings:[A-Za-z0-9-]*$"))
            throw new ActionRefusedException("Only ms-settings: pages can be opened here; nothing was launched");
        // Politique commune lue AVANT le lancement (illisible : refus, fail closed) et appliquee a la cible Parametres.
        if (GardeFous.BlockReason("SystemSettings", "Windows.UI.Core.CoreWindow") is { } blocked) throw new ActionRefusedException(blocked + "; nothing was launched");
        using var token = ForegroundToken.Acquire("open a Settings page");
        JulienPause.ThrowIfActive(quietMs); // l'attente du jeton peut durer : la pause de Julien est relue sous le jeton
        EmergencyStop.ThrowIfStopped();
        var before = Win32.Probe();
        string giveBack = "none";
        // Le premier plan est rendu CHAQUE fois que les Parametres le prennent, jusqu'a la fin, erreur comprise (relecture
        // de Codex : une seule restitution laissait les Parametres devant s'ils le reprenaient).
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
                if (current == 0) { found = 0; foundAt = 0; } // candidat disparu : rien n'est retenu
                else if (current != found) { found = current; foundAt = clock.ElapsedMilliseconds; }
                if (found != 0 && clock.ElapsedMilliseconds - foundAt >= 600) { stable = true; break; } // le cadre montre remplace la masquee
                Thread.Sleep(30);
            }
        }
        finally { ReturnIfTaken(); }
        // Identite reattestee juste avant de rendre, et rendue telle qu'attestee : processus lu AVANT l'attestation, relu
        // APRES ; s'il a change (HWND recycle), issue inconnue, jamais le nouveau proprietaire (relecture de Codex). Fenetre
        // non stabilisee 600 ms avant le delai : inconnue aussi.
        if (!stable || found == 0) return new { ok = true, opened = (bool?)null, window = 0L, foregroundReturned = giveBack };
        var pid = Win32.ProcessOf(found);
        if (pid == 0 || !IsWindow(found) || !IsSettings(found) || Win32.ProcessOf(found) != pid)
            return new { ok = true, opened = (bool?)null, window = 0L, foregroundReturned = giveBack };
        // Same identity as the window inventory (AppWindows): a shown frame is named after its hosted content (SystemSettings),
        // with the frame's own pid; otherwise the launch result never matched windows and was refused as "identity changed".
        using var process = Process.GetProcessById((int)ContentPid((long)found, pid));
        if (Win32.ProcessOf(found) != pid) return new { ok = true, opened = (bool?)null, window = 0L, foregroundReturned = giveBack };
        return new { ok = true, opened = (bool?)true, window = (long)found, title = Win32.Title(found), app = AppId(process), pid,
            hidden = !Win32.IsWindowVisible(found), foregroundReturned = giveBack };
    }
    static bool IsSettings(nint hwnd)
    {
        if (ProcessName(Win32.ProcessOf(hwnd)) == "SystemSettings") return true;
        var hosted = false; // cadre ApplicationFrameHost : son contenu (CoreWindow) appartient a SystemSettings
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
    // Processus du contenu d'une fenetre : la CoreWindow d'un cadre ApplicationFrameHost (application du Store), sinon le sien.
    static long ContentPid(long id, long pid)
    {
        if (!ProcessName((uint)pid).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return pid;
        var core = Win32.FindWindowExW((nint)id, 0, "Windows.UI.Core.CoreWindow", null);
        return core != 0 && Win32.ProcessOf(core) is var content and not 0 ? content : pid;
    }
    static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch (ArgumentException) { return ""; }
    }

    // Existence de la fenetre liee, sans la lire : « gone » seulement si Windows dit le HWND detruit (ou sans
    // processus) ; HWND repris par une autre application : « other » (l'ancienne n'existe plus sous ce numero). Jamais
    // deduit d'une erreur de transport, d'une absence chez Sky ni d'une fenetre masquee (relecture de Codex, 22/10).
    // La fenetre suivie (ancre) est-elle encore dans la chaine des proprietaires ?
    static bool OwnedThrough(nint hwnd, long anchor)
    {
        var depth = 0;
        for (var owner = Win32.GetWindow(hwnd, Win32.GW_OWNER); owner != 0 && depth < 16; owner = Win32.GetWindow(owner, Win32.GW_OWNER), depth++)
            if ((long)owner == anchor) return true;
        return false;
    }
    static object Alive(nint hwnd, string app, long? expectedPid = null, long? expectedOwner = null, long? expectedAnchor = null)
    {
        if (!IsWindow(hwnd)) return new { ok = true, state = "gone" };
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return new { ok = true, state = "gone" };
        // HWND repris par une autre fenetre (autre processus ou autre proprietaire) : l'ancienne n'existe plus.
        if ((expectedPid is { } wantedPid && pid != wantedPid) || (expectedOwner is { } wantedOwner && (long)Win32.GetWindow(hwnd, Win32.GW_OWNER) != wantedOwner)
            || (expectedAnchor is { } wantedAnchor && !OwnedThrough(hwnd, wantedAnchor)))
            return new { ok = true, state = "other" };
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var same = string.Equals(AppId(process), app, StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
            return new { ok = true, state = same ? "alive" : "other", visible = Win32.IsWindowVisible(hwnd) };
        }
        catch (ArgumentException) { return new { ok = true, state = "gone" }; }
    }
    // Inventaire du bureau sans Sky (serveur MCP autonome) : fenetres d'application de premier niveau, visibles et non
    // masquees par DWM, comme la barre des taches (pas de fenetre outil sans WS_EX_APPWINDOW, pas de fenetre possedee).
    // Une fenetre UWP est hebergee par ApplicationFrameHost : son application est celle de la CoreWindow enfant.
    // L'inventaire liste en plus les dialogues : fenetre possedee, visible, titree, non outil (boite « Ouvrir » #32770
    // de Chrome, sans WS_EX_APPWINDOW, vue absente le 25/10 par NexusWindows for Claude), avec son proprietaire.
    // Le lancement compare seulement les fenetres d'application (un dialogue n'est pas une application lancee).
    static object ListAllWindows(bool includeHidden = false) => new { ok = true, windows = AppWindows(dialogs: true, includeHidden: includeHidden).Select(w => w.Owner == 0
        ? (object)new { id = w.Id, app = w.App, title = w.Title, pid = w.Pid }
        : new { id = w.Id, app = w.App, title = w.Title, pid = w.Pid, owner = w.Owner, ownerTitle = Win32.Title((nint)w.Owner) }) };
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
    // Lancement sans Sky : application (nom, chemin, AUMID), dossier ou document, par le shell, sous le jeton commun ; le
    // premier plan est rendu chaque fois qu'une autre fenetre le prend (nouvelle, ou instance deja ouverte qui recoit le
    // document), jusqu'a la fin. Adresses : ms-settings: seules (une autre adresse peut appeler, envoyer ou payer).
    // Resultat : les nouvelles fenetres d'application, stables 600 ms et relues a la fin. Une fenetre n'est attribuee au
    // lancement que sur preuve (processus demarre, ou processus ne apres le lancement qui est l'application visee) ;
    // sinon elle est rendue comme candidate et l'issue reste inconnue (relectures de Codex du 23/10).
    // Application Chromium/Electron (recette commune du banc, 24/10) : lancee telle quelle et recouverte des son
    // demarrage, elle ne se dessine ni ne publie sa page a l'accessibilite (Logi Options+ violet derriere Claude
    // agrandi ; banc « lancement recouvert » : selecteur introuvable, 0/3). Reconnue a ses ressources, jamais a son nom,
    // elle recoit le suivi d'occlusion coupe et l'accessibilite forcee ; un seul --disable-features (Chromium ne retient
    // que le dernier). Seulement pour un executable donne par son chemin ; les arguments du modele (fichiers) viennent
    // apres, jamais un commutateur (LaunchArgsProblem).
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
    // Ligne de lancement (programme, arguments), comme le shell la lirait (passe 2 du 28/10 : « EditeurTest.exe note.txt »
    // et « "C:\...\EditeurTest.exe" "C:\...\note.txt" » refuses sans lancement, la ligne entiere prise pour un chemin).
    // Chaine entiere d'abord : un fichier ou un dossier existant avec espaces n'est jamais coupe. Sinon : programme entre
    // guillemets suivi d'arguments, plus court prefixe (coupe a une espace) qui est un .exe existant par son chemin absolu,
    // ou nom d'application nu suivi d'arguments. Les arguments du champ separe viennent apres ceux de la ligne ; ils sont
    // passes tels quels au programme (ShellExecute), decoupes seulement pour les gardes (CommandLineToArgvW).
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    // Arguments decoupes comme Windows les decoupe pour le programme lance (« x » : nom de programme, regles a part).
    internal static List<string> SplitArgs(string args)
    {
        if (args.Trim().Length == 0) return new();
        var argv = CommandLineToArgvW("x " + args, out var count);
        if (argv == 0) throw new ActionRefusedException("Unreadable arguments; nothing was launched");
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "").ToList(); }
        finally { LocalFree(argv); }
    }

    // Programme d'un nom nu comme le shell le trouve : App Paths (utilisateur puis machine), puis le PATH. Null : introuvable.
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

    // Navigateur ou application Chromium/Electron : ressources a cote de l'executable, ou dans le dossier de sa version
    // (Chrome et Edge : Application\<version>\resources.pak).
    internal static bool IsChromiumFamily(string exePath)
    {
        if (IsChromiumApp(exePath)) return true;
        var folder = System.IO.Path.GetDirectoryName(exePath) ?? "";
        try { return File.Exists(System.IO.Path.Combine(folder, "resources.pak")) || Directory.EnumerateDirectories(folder).Any(d => File.Exists(System.IO.Path.Combine(d, "resources.pak"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // Raison de refuser les arguments d'un programme (null sinon) : pour Chromium/Electron, des fichiers ou des mots, jamais
    // un commutateur (port DevTools, profil, extension ouvriraient les sessions de Julien a un canal de controle ; les
    // commutateurs de l'outil restent les siens). Programme nu introuvable : ses commutateurs ne peuvent etre juges, refuses.
    internal static string? LaunchArgsProblem(string? program, string args)
    {
        if (Steam.IsExecutable(program)) return Steam.ArgsProblem(args);
        var switchArg = SplitArgs(args).FirstOrDefault(a => a.StartsWith('-') || a.StartsWith('/'));
        if (switchArg == null) return null;
        if (program == null) return $"Switch \"{switchArg}\" for an application name Windows does not resolve: give the program's absolute path";
        return IsChromiumFamily(program) ? $"Switch \"{switchArg}\" refused for a Chromium/Electron application (control channel or profile): only files as arguments" : null;
    }

    // Lancement prepare, sans rien lancer : programme, arguments et refus (chemin, adresse, arguments), lu aussi par
    // tests/LaunchTests.cs.
    internal sealed record LaunchPlan(string Target, string Args, string Start, bool IsPath, bool Aumid, bool IsExe, string Stem, string? ExePath);
    internal static LaunchPlan PlanLaunch(string raw, string rawArgs)
    {
        var (target, args) = SplitLaunch(raw, rawArgs);
        if (target.Length == 0) throw new ActionRefusedException("Nothing to launch; nothing was launched");
        // Chemin absolu selon Windows (lecteur, UNC avec \ ou /) : classification de .NET, pas un sous-ensemble regex.
        var isPath = System.IO.Path.IsPathFullyQualified(target);
        if (!isPath && System.Text.RegularExpressions.Regex.IsMatch(target, @"^[A-Za-z][A-Za-z0-9+.-]*:"))
            throw new ActionRefusedException("Only ms-settings: addresses are launched; nothing was launched");
        // AUMID d'une application du Store (« Microsoft.Paint_8wekyb3d8bbwe!App ») : par le dossier Applications du shell.
        var aumid = !isPath && target.Contains('!') && !target.Contains('\\') && !target.Contains('/');
        // Hors chemin absolu et AUMID, seul un nom d'application nu est accepte (« notepad », « winword.exe ») : un chemin
        // relatif, un nom de fichier ou un nom que Windows normaliserait (point ou espace final) serait resolu depuis le
        // dossier courant du helper, donc ambigu ; refuse, un chemin absolu est demande (relectures de Codex du 23/10).
        if (!isPath && !aumid && !System.Text.RegularExpressions.Regex.IsMatch(target, BareApp, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ActionRefusedException("Give an absolute path for a file or folder (or an application name); nothing was launched");
        var start = aumid ? @"shell:AppsFolder\" + target : target;
        // Application visee : executable (nom ou chemin .exe) ou AUMID. Une application interdite (gestionnaire de mots de
        // passe) est refusee avant tout lancement ; un document ou un dossier n'est jamais juge sur son nom de fichier.
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
    internal static int LaunchSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            try
            {
                var plan = PlanLaunch(Text(c, "target"), Text(c, "args"));
                var info = LaunchInfo(plan.Start, plan.ExePath, plan.Args);
                results.Add(new { target = plan.Target, args = plan.Args, isExe = plan.IsExe, exePath = plan.ExePath, start = info.FileName, arguments = info.Arguments });
            }
            catch (ActionRefusedException error) { results.Add(new { refused = error.Message }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
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
        // Dossier vise (chemin absolu existant) : son identite sur le disque, pour la preuve « folder » (passe 2 du 28/10 :
        // la fenetre de l'Explorateur ouverte sur le dossier restait une simple candidate, issue inconnue).
        var folder = isPath && Directory.Exists(target) ? Explorer.Identity(target) : null;
        var names = new List<string>();
        if (isExe) names.Add(stem);
        if (aumid)
        {
            // « AgileBits.1Password_xxx!App » : nom du paquet entier et sans editeur, compares aux processus interdits.
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
        // Inventaire pris sous le jeton, juste avant le lancement : une fenetre apparue pendant l'attente du jeton n'est
        // jamais prise pour celle du lancement.
        var before = new HashSet<long>(AppWindows().Select(w => w.Id));
        var probe = Win32.Probe();
        string giveBack = "none";
        void ReturnIfTaken()
        {
            var fg = Win32.GetForegroundWindow();
            // Meme une autre fenetre du processus de Julien (document ouvert dans son instance) : sa fenetre lui est rendue.
            if (fg != 0 && fg != probe.Foreground) giveBack = ForegroundReturn.GiveBack(probe.Foreground, fg, probe.LastInput, sameProcess: true);
        }
        var launchedAt = DateTime.Now;
        // Le broker du bureau ne rend aucun PID : attribution par un processus NE APRES le lancement et qui est bien
        // l'application visee (meme chemin d'executable, meme nom de processus pour un nom nu, meme AUMID), ou nouvelle
        // fenetre de l'Explorateur qui affiche le dossier demande (relu par le shell, identite sur le disque). Un nom de
        // fichier ou un titre ne prouve rien : document ou autre concordance restent des candidates.
        string? Proof((long Id, string App, string Title, long Pid, long Owner) w)
        {
            if (folder is { } shown) return Explorer.Shows((nint)w.Id, shown) ? "folder" : null;
            if (!isExe && !aumid) return null;
            try
            {
                // Application du Store hebergee par ApplicationFrameHost : le processus de son contenu (CoreWindow), jamais
                // celui du cadre, ne bien avant le lancement (passe 2 du 28/10 : une application lancee par son AUMID
                // n'etait jamais attribuee).
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
            // L'Explorateur ouvre sa fenetre sur « Accueil » puis va au dossier (passe 2 du 28/10) : le dossier affiche est
            // relu jusqu'a 3 s, lectures seules.
            if (stable && folder is { } wanted)
            {
                var wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 3000 && !found.Any(w => Explorer.Shows((nint)w.Id, wanted))) { ReturnIfTaken(); Thread.Sleep(100); }
            }
        }
        finally { ReturnIfTaken(); }
        // Relecture finale : chaque fenetre encore la, du meme processus (un HWND recycle n'est jamais garde).
        if (stable)
        {
            var now = AppWindows().ToDictionary(w => w.Id);
            stable = found.All(w => now.TryGetValue(w.Id, out var again) && again.Pid == w.Pid);
        }
        var proofs = stable ? found.Select(w => (Window: w, Proof: Proof(w))).Where(p => p.Proof != null).ToDictionary(p => p.Window.Id, p => p.Proof) : new();
        var mine = found.Where(w => proofs.ContainsKey(w.Id)).ToList();
        var candidates = found.Where(w => !mine.Contains(w)).ToList();
        object Shape((long Id, string App, string Title, long Pid, long Owner) w) => new { id = w.Id, app = w.App, title = w.Title, pid = w.Pid,
            // Preuve de l'attribution : « process » (processus rendu par le shell), « folder » (fenetre de l'Explorateur qui
            // affiche le dossier demande) ou « application » (application visee, nee apres la demande : une autre instance
            // lancee au meme moment pourrait etre prise pour elle).
            proof = proofs.TryGetValue(w.Id, out var proof) ? proof : null };
        return new { ok = true, opened = mine.Count > 0 ? (bool?)true : null,
            windows = mine.Select(Shape), candidates = candidates.Select(Shape), foregroundReturned = giveBack };
    }
    // Fermeture : WM_CLOSE poste une seule fois (jamais rejoue), puis relu 3 s : fermee, masquee, ou encore ouverte avec
    // la fenetre qu'elle a ouverte (enregistrer ?) a regarder.
    static object CloseWindow(nint hwnd, Action started)
    {
        started();
        if (!Win32.PostMessageW(hwnd, 0x0010, 0, 0)) throw new ActionRefusedException("The close message could not be posted; nothing was sent");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 3000)
        {
            if (!IsWindow(hwnd)) return new { ok = true, state = "closed" };
            Thread.Sleep(50);
        }
        if (!Win32.IsWindowVisible(hwnd)) return new { ok = true, state = "hidden" };
        var prompts = new List<object>();
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.IsWindowVisible(h) && Win32.GetWindow(h, Win32.GW_OWNER) == hwnd) prompts.Add(new { id = (long)h, title = Win32.Title(h) });
            return true;
        }, 0);
        return new { ok = true, state = "open", prompts };
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
                if (!IgnoredClasses.Contains(cls) && GardeFous.BlockReason(process.ProcessName, cls) == null) windows.Add(new { id = (long)hwnd, app, title = Win32.Title(hwnd) });
            }
            return true;
        }, 0);
        return new { ok = true, windows };
    }
    static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    internal static string AppId(Process process)
    {
        uint length = 0;
        if (GetApplicationUserModelId(process.Handle, ref length, null) == 122 && length > 0)
        {
            var id = new StringBuilder((int)length);
            if (GetApplicationUserModelId(process.Handle, ref length, id) == 0) return id.ToString();
        }
        return "process:" + (ImagePath(process.Id) ?? throw new InvalidOperationException("Process path unavailable"));
    }
    // Chemin de l'executable (QueryFullProcessImageName : sans enumerer les modules du processus).
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref uint size);
    static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
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
            hasField = true; if (actual != p.Value.GetString()) return false;
        }
        if (!hasField) throw new InvalidOperationException("Empty native selector");
        return true;
    }
}
