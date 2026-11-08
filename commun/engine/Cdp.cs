using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// Chromium wheel (Chrome, Edge, Electron) through the DevTools protocol, only when the target was started with a
// debugging port (shared bench of 21/10, Codex's recipe B, the same mechanism as nexus-windows for Claude). A "trusted"
// wheel delivered by the page, without activating the window or moving the cursor. Before any input, four proofs
// (Codex's reviews of 21/10):
// 1. the port appears in the command line of the window's process;
// 2. the CONTACTED address (127.0.0.1:port, or a 0.0.0.0:port listener) is listened on by the EXACT PID of the window,
//    and by it alone (never a parent or a child: Electron A starting B with the same port); HTTP and WebSocket stay on
//    that host and port;
// 3. a single page whose decoded title equals the window's (up to the browser suffix);
// 4. ACTIVE proof of the window <-> page binding: a random title set on the page must appear in the title of THIS
//    window within 0.5 s; the original title is always put back before the wheel (visible effect: the window title
//    changes for less than half a second). A page that does not reflect its title (Electron setTitle): refused.
// Otherwise: not applicable, NOTHING is sent. A single send: no answer after the send = unknown outcome, never replayed
// nor followed by another technique.
static partial class Cdp
{
    internal const int TimeoutMs = 2000, ProofMs = 500;
    [LibraryImport("kernel32.dll")] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
    [LibraryImport("ntdll.dll")] private static partial int NtQueryInformationProcess(nint process, int infoClass, nint buffer, int length, out int returned);
    [LibraryImport("iphlpapi.dll")] private static partial uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, uint reserved);

    // Command line of a process (ProcessCommandLineInformation, Windows 8.1+), without WMI; null if unreadable.
    internal static string? CommandLine(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0) return null;
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            if (NtQueryInformationProcess(process, 60, buffer, 65536, out _) < 0) return null;
            var length = (ushort)Marshal.ReadInt16(buffer); // UNICODE_STRING.Length (octets)
            var text = Marshal.ReadIntPtr(buffer, 8);       // UNICODE_STRING.Buffer (in our buffer)
            return length == 0 || text == 0 ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally { Marshal.FreeHGlobal(buffer); CloseHandle(process); }
    }

    // IPv4 TCP listeners of the port: (local address, owning PID).
    internal static List<(string Address, uint Pid)> Listeners(int port)
    {
        var found = new List<(string, uint)>();
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, 2 /* AF_INET */, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
        if (size == 0) return found;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return found;
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + 4 + i * 24; // MIB_TCPROW_OWNER_PID: state, address, port, remote address, remote port, PID
                var raw = (uint)Marshal.ReadInt32(row, 8);
                if ((int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)) != port) continue;
                found.Add((new IPAddress((uint)Marshal.ReadInt32(row, 4)).ToString(), (uint)Marshal.ReadInt32(row, 20)));
            }
        }
        finally { Marshal.FreeHGlobal(table); }
        return found;
    }

    // Is the contacted address (127.0.0.1:port, failing that a 0.0.0.0:port listener) listened on by THIS PID alone?
    public static bool OwnsPort(uint windowPid, IEnumerable<(string Address, uint Pid)> listeners)
    {
        var reached = listeners.Where(l => l.Address is "127.0.0.1" or "0.0.0.0").ToList();
        return reached.Count > 0 && reached.All(l => l.Pid == windowPid);
    }

    // Port 0 ("chosen by Chromium") or absent: no known port.
    public static bool HasPort(nint window) => PortFrom(CommandLine(Win32.ProcessOf(window))) != null;
    public static int? PortFrom(string? commandLine)
    {
        var match = commandLine is null ? null : Regex.Match(commandLine, @"--remote-debugging-port[= ](\d{1,5})(?!\d)");
        return match is { Success: true } && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and < 65536 ? port : null;
    }

    // Window title of a browser: "page - [profile - ]Microsoft Edge|Google Chrome|Chromium"; Edge with tabs writes
    // "page - Profil 1 – Microsoft Edge" (en dash, measured on 22/10, proof C); Electron: the page.
    static readonly Regex BrowserSuffix = new("^ [-–] (?:[^\\r\\n]+? [-–] )?(?:Microsoft​? Edge|Google Chrome|Chromium|Brave)$", RegexOptions.CultureInvariant);
    public static bool TitleMatches(string windowTitle, string pageTitle) =>
        pageTitle.Length > 0 && (windowTitle == pageTitle ||
            windowTitle.StartsWith(pageTitle, StringComparison.Ordinal) && BrowserSuffix.IsMatch(windowTitle[pageTitle.Length..]));

    // Candidate page: decoded title (/json/list is HTML-escaped: "A &amp; B") equal to the window's up to the browser
    // suffix, exactly one; its WebSocket must stay on the proven host and port. Otherwise: refused. The real binding is
    // then actively proven (Prove).
    public static string PickPage(JsonElement targets, string windowTitle, int port)
    {
        var pages = targets.EnumerateArray()
            .Where(t => t.TryGetProperty("type", out var type) && type.GetString() == "page" && t.TryGetProperty("webSocketDebuggerUrl", out _))
            .ToList();
        var matching = pages.Where(t => TitleMatches(windowTitle, WebUtility.HtmlDecode(t.TryGetProperty("title", out var title) ? title.GetString() ?? "" : ""))).ToList();
        if (matching.Count != 1)
            throw new BackgroundNotApplicableException($"No single DevTools page titled like this window ({pages.Count} pages, {matching.Count} matching): nothing was sent");
        if (!Uri.TryCreate(matching[0].GetProperty("webSocketDebuggerUrl").GetString(), UriKind.Absolute, out var url) || url.Scheme != "ws" ||
            url.Host is not ("127.0.0.1" or "localhost") || url.Port != port)
            throw new BackgroundNotApplicableException("DevTools page socket is not on the proven host and port: nothing was sent");
        return $"ws://127.0.0.1:{port}{url.PathAndQuery}";
    }

    // VISIBLE render areas of the whole window (Chrome_RenderWidgetHostHWND), on screen.
    static List<Win32.Rect> Surfaces(nint window)
    {
        var found = new List<Win32.Rect>();
        Win32.EnumChildWindows(window, (child, _) =>
        {
            if (Win32.ClassName(child) == "Chrome_RenderWidgetHostHWND" && Win32.IsWindowVisible(child) && Win32.GetWindowRect(child, out var r) && !r.Empty)
                found.Add(r);
            return true;
        }, 0);
        return found;
    }

    // Rule SHARED by the three NexusWindows profiles (Codex's review of 21/10 at 19:18): exactly ONE visible render area in
    // the whole window, the point inside it, and its physical size equal to round(innerWidth x DPR) / round(innerHeight x
    // DPR) within ceil(DPR) px. Several visible areas (multi-view apps): out of scope, refused. Returns the measured gap.
    public static (int Index, int GapWidth, int GapHeight) PickSurface(IReadOnlyList<Win32.Rect> surfaces, int x, int y, double innerWidth, double innerHeight, double ratio)
    {
        if (!(double.IsFinite(innerWidth) && double.IsFinite(innerHeight) && double.IsFinite(ratio)) || innerWidth <= 0 || innerHeight <= 0 || ratio is <= 0.1 or >= 16)
            throw new BackgroundNotApplicableException("Page size or devicePixelRatio unreadable: nothing was sent");
        if (surfaces.Count != 1)
            throw new BackgroundNotApplicableException($"{surfaces.Count} visible rendered surfaces in the window (multi-view apps are out of scope): nothing was sent");
        var r = surfaces[0];
        if (x < r.Left || y < r.Top || x >= r.Right || y >= r.Bottom)
            throw new BackgroundNotApplicableException("The point is not on the rendered page: nothing was sent");
        var (gapWidth, gapHeight) = (r.Width - (int)Math.Round(innerWidth * ratio), r.Height - (int)Math.Round(innerHeight * ratio));
        var tolerance = (int)Math.Ceiling(ratio);
        if (Math.Abs(gapWidth) > tolerance || Math.Abs(gapHeight) > tolerance)
            throw new BackgroundNotApplicableException($"Rendered surface {r.Width}x{r.Height} is not the proven page ({innerWidth}x{innerHeight} at {ratio}, gap {gapWidth}/{gapHeight} px): nothing was sent");
        return (0, gapWidth, gapHeight);
    }

    // A DevTools connection to a page (one request at a time, answers matched by id; events received on the way are kept
    // for whoever waits for them: Input.dragIntercepted).
    internal sealed class Session : IDisposable
    {
        readonly ClientWebSocket socket = new();
        public readonly List<(string Method, JsonElement Params)> Events = [];
        int id;
        string? fixtureWireSession;
        public Session(string url)
        {
            using var cancel = new CancellationTokenSource(TimeoutMs);
            socket.ConnectAsync(new Uri(url), cancel.Token).GetAwaiter().GetResult();
        }
        public JsonElement Call(string method, object parameters)
        {
            var mine = ++id;
            var payload = EngineJson.SerializeToUtf8Bytes(new CdpCallJson(id: mine, method: method, @params: parameters));
            var trace = method == "Input.insertText" && McpServer.ClaudeFixtureDiagnosticsEnabled();
            int? textLength = null;
            if (trace)
            {
                fixtureWireSession ??= Guid.NewGuid().ToString("N");
                using var parsed = JsonDocument.Parse(payload);
                if (parsed.RootElement.GetProperty("params").TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) textLength = text.GetString()!.Length;
            }
            void Trace(string phase)
            {
                if (trace) Console.Error.WriteLine(EngineJson.Serialize(new CdpCallJson2(fixture: "claude-cdp-wire", session: fixtureWireSession, id: mine, method: method, phase: phase, textLength: textLength)));
            }
            using var cancel = new CancellationTokenSource(TimeoutMs);
            socket.SendAsync(payload, WebSocketMessageType.Text, true, cancel.Token).GetAwaiter().GetResult();
            Trace("sent");
            var buffer = new byte[1 << 16];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = socket.ReceiveAsync(buffer, cancel.Token).GetAwaiter().GetResult();
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var reply = JsonDocument.Parse(message.ToArray());
                if (!reply.RootElement.TryGetProperty("id", out var got))
                {
                    if (reply.RootElement.TryGetProperty("method", out var name) && reply.RootElement.TryGetProperty("params", out var data)) Events.Add((name.GetString() ?? "", data.Clone()));
                    continue;
                }
                if (got.GetInt32() != mine) continue;
                if (reply.RootElement.TryGetProperty("error", out var error)) { Trace("rejected"); throw new InvalidOperationException($"DevTools {method}: {error}"); }
                Trace("acknowledged");
                return reply.RootElement.GetProperty("result").Clone();
            }
        }
        // JavaScript exception (exceptionDetails): never a result (Codex's review of 22/10 11:27).
        public JsonElement Evaluate(string expression)
        {
            var reply = Call("Runtime.evaluate", new CdpEvaluateJson(expression: expression, returnByValue: true));
            if (reply.TryGetProperty("exceptionDetails", out var details)) throw new InvalidOperationException($"DevTools evaluation raised: {details}");
            return reply.GetProperty("result");
        }
        public void Dispose() => socket.Dispose();
    }

    public static Outcome Wheel(nint window, int x, int y, int deltaY, int deltaX, Action started)
    {
        var (cdpX, cdpY) = ScrollPlan.DevTools(deltaY, deltaX);
        var page = Open(window, x, y);
        using (page.Session)
        {
            var wheel = new CdpWheelJson(type: "mouseWheel", x: (x - page.Surface.Left) / page.Ratio, y: (y - page.Surface.Top) / page.Ratio, deltaX: cdpX, deltaY: cdpY);
            Dispatch(page.Session.Call, wheel, started);
            return new("background:devtools", $"Input.dispatchMouseEvent mouseWheel (port {page.Port}; {page.Measured})", "sent");
        }
    }

    // Drag and drop in the page, in the background (shared bench of 01/11: refused without the foreground until then).
    // Button pressed at the start, moves, then released at the arrival. An HTML5 drag (draggable) is intercepted by
    // Chromium (Input.setInterceptDrags) instead of opening the system's drag loop, which would read the real mouse: its
    // data is dropped at the arrival through Input.dispatchDragEvent (dragEnter, dragOver, drop). A mouse drag (pointer,
    // canvas) receives the same mouse events. The same proofs as the wheel before any input; a single send.
    public static Outcome Drag(nint window, int x, int y, int toX, int toY, Action started)
    {
        var page = Open(window, x, y);
        using (page.Session)
        {
            var s = page.Session;
            if (toX < page.Surface.Left || toY < page.Surface.Top || toX >= page.Surface.Right || toY >= page.Surface.Bottom)
                throw new ActionRefusedException("Drop point is outside the page's rendered surface: nothing was sent");
            double Cx(int px) => (px - page.Surface.Left) / page.Ratio;
            double Cy(int py) => (py - page.Surface.Top) / page.Ratio;
            var (ax, ay, bx, by) = (Cx(x), Cy(y), Cx(toX), Cy(toY));
            EmergencyStop.ThrowIfStopped();
            started();
            var mechanism = "mouse drag";
            try
            {
                s.Call("Input.setInterceptDrags", new CdpDragJson(enabled: true));
                s.Call("Input.dispatchMouseEvent", new CdpDragJson2(type: "mouseMoved", x: ax, y: ay));
                s.Call("Input.dispatchMouseEvent", new CdpDragJson3(type: "mousePressed", x: ax, y: ay, button: "left", buttons: 1, clickCount: 1));
                const int Steps = 12;
                for (var i = 1; i <= Steps; i++)
                    s.Call("Input.dispatchMouseEvent", new CdpDragJson4(type: "mouseMoved", x: ax + (bx - ax) * i / Steps, y: ay + (by - ay) * i / Steps, button: "left", buttons: 1));
                s.Evaluate("0"); // one more round trip: the interception emitted at the last move is read
                var intercepted = s.Events.FirstOrDefault(e => e.Method == "Input.dragIntercepted");
                if (intercepted.Method is not null)
                {
                    var data = intercepted.Params.GetProperty("data");
                    foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                        s.Call("Input.dispatchDragEvent", new CdpDragJson5(type: type, x: bx, y: by, data: data));
                    mechanism = "HTML5 drag intercepted, Input.dispatchDragEvent drop";
                }
                s.Call("Input.dispatchMouseEvent", new CdpDragJson3(type: "mouseReleased", x: bx, y: by, button: "left", buttons: 0, clickCount: 1));
            }
            catch (Exception error) { throw new InvalidOperationException("DevTools drag sent without full acknowledgement; outcome unknown, do not replay", error); }
            finally { try { s.Call("Input.setInterceptDrags", new CdpDragJson(enabled: false)); } catch (Exception) { } }
            return new("background:devtools", $"Input.dispatchMouseEvent {mechanism} (port {page.Port}; {page.Measured})", "sent");
        }
    }

    sealed record Page(Session Session, int Port, Win32.Rect Surface, double Ratio, string Measured);

    // Proven DevTools page of the window and render surface under the point: port from the command line, listened on by
    // the window's process alone, page with a proven title, CSS size and DPR. Any failure: not applicable, nothing is sent.
    static Page Open(nint window, int x, int y)
    {
        var pid = Win32.ProcessOf(window);
        var port = PortFrom(CommandLine(pid)) ?? throw new BackgroundNotApplicableException(
            "Chromium ignores a posted wheel or drag (measured) and the target has no DevTools port: nothing was sent");
        if (!OwnsPort(pid, Listeners(port)))
            throw new BackgroundNotApplicableException($"127.0.0.1:{port} is not listened to by the window's own process alone: nothing was sent");
        var surfaces = Surfaces(window);
        if (surfaces.Count != 1)
            throw new BackgroundNotApplicableException($"{surfaces.Count} visible rendered surfaces in the window (multi-view apps are out of scope): nothing was sent");
        string url;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
            using var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult());
            url = PickPage(targets.RootElement, Win32.Title(window), port);
        }
        catch (Exception error) when (error is not BackgroundNotApplicableException)
        {
            throw new BackgroundNotApplicableException($"DevTools port {port} unavailable ({error.GetType().Name}): nothing was sent");
        }
        Session session;
        try { session = new Session(url); }
        catch (Exception error) { throw new BackgroundNotApplicableException($"DevTools page unreachable before input ({error.GetType().Name}): nothing was sent"); }
        try
        {
            Prove(session.Evaluate, FreshEvaluator(url), () => Win32.Title(window));
            var metrics = session.Evaluate("JSON.stringify([window.innerWidth, window.innerHeight, window.devicePixelRatio])");
            var values = metrics.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? EngineJson.Deserialize<double[]>(v.GetString()!) : null;
            if (values is not { Length: 3 }) throw new BackgroundNotApplicableException("Page size or devicePixelRatio unreadable: nothing was sent");
            var (index, gapWidth, gapHeight) = PickSurface(surfaces, x, y, values[0], values[1], values[2]);
            var surface = surfaces[index];
            // Recorded for the bench: CSS size, DPR, the area's rectangle and the measured gap.
            var measured = $"inner {values[0]}x{values[1]} at DPR {values[2]}, surface [{surface.Left},{surface.Top},{surface.Right},{surface.Bottom}], gap {gapWidth}/{gapHeight} px";
            return new Page(session, port, surface, values[2], measured);
        }
        catch (Exception error)
        {
            session.Dispose();
            if (BeforeInput(error) is var mapped && mapped != error) throw mapped;
            throw;
        }
    }

    // SINGLE send of the wheel: emergency stop checked at the last moment (a stop raised during the preparation, proof or
    // measurements, sends nothing); no answer after the send = unknown outcome, never replayed nor followed by another
    // technique (traces D of Codex's review of 22/10, tests/CdpTests.cs).
    internal static void Dispatch(Func<string, object, JsonElement> call, object wheel, Action started)
    {
        EmergencyStop.ThrowIfStopped();
        started();
        try { call("Input.dispatchMouseEvent", wheel); }
        catch (Exception error) { throw new InvalidOperationException("DevTools wheel sent without acknowledgement; outcome unknown, do not replay", error); }
    }

    // An operation on a NEW connection to the page: opens, evaluates THEN closes, in the same call (Codex's review of
    // 19:28: the session was closed before it was used, the emergency cleanup always failed).
    public static Func<string, JsonElement> FreshEvaluator(string url) => expression =>
    {
        using var session = new Session(url);
        return session.Evaluate(expression);
    };

    // Active proof of the window <-> page binding: a random title set on the page must appear in the title of THIS window
    // (up to the browser suffix) within ProofMs. CONDITIONAL setting (the title has not changed since it was read) done
    // UNDER the cleanup: setting applied but answer lost = cleanup anyway. Conditional cleanup (the nonce is still there:
    // a title legitimately changed is never overwritten), tried again on a NEW connection if the first one breaks. Then
    // the page's real FINAL title (document.title) must be the window's: another tab activated during the cleanup no
    // longer passes (Codex's review of 19:28). Any failure: refused, nothing is sent; an impossible cleanup is stated
    // explicitly.
    public static void Prove(Func<string, JsonElement> evaluate, Func<string, JsonElement> fresh, Func<string> windowTitle, string? nonce = null)
    {
        nonce ??= "nexus-" + Guid.NewGuid().ToString("N");
        var title = Text(evaluate("document.title")) ?? throw new BackgroundNotApplicableException("Page title unreadable: nothing was sent");
        var (quotedTitle, quotedNonce) = (EngineJson.Serialize(title), EngineJson.Serialize(nonce));
        var restore = $"(() => {{ if (document.title === {quotedNonce}) document.title = {quotedTitle}; return true; }})()";
        var seen = false;
        string? dressing = null; // the window's EXACT dressing around the page title, observed with the nonce
        var touched = false;     // setting sent: applied, or answer lost (unknown outcome)
        Exception? failure = null;
        try
        {
            touched = true;
            var posed = evaluate($"(() => {{ if (document.title !== {quotedTitle}) return false; document.title = {quotedNonce}; return true; }})()");
            if (!(posed.TryGetProperty("value", out var p) && p.ValueKind == JsonValueKind.True))
            {
                touched = false;
                throw new BackgroundNotApplicableException("Page title changed while being read: nothing was sent");
            }
            var clock = Stopwatch.StartNew();
            while (!seen && clock.ElapsedMilliseconds < ProofMs)
            {
                var shown = windowTitle();
                seen = TitleMatches(shown, nonce);
                if (seen) dressing = shown[nonce.Length..];
                else Thread.Sleep(20);
            }
        }
        catch (Exception error) { failure = error; }
        // CHECKED restore on every path, before raising any error (Codex's review of 22/10 11:27): page then window title;
        // a remaining nonce or an unverifiable restore = terminal refusal.
        if (touched) VerifyCleanup(evaluate, fresh, windowTitle, restore, nonce);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (!seen) throw new BackgroundNotApplicableException("This DevTools page is not proven to be the window (its title never showed up in the window): nothing was sent");
        string? final;
        try { final = Text(evaluate("document.title")); }
        catch (Exception) { final = Text(fresh("document.title")); }
        if (final is null || final.Contains(nonce, StringComparison.Ordinal))
            throw new ActionRefusedException($"Test title « {nonce} » may remain on the page (unreadable after the proof): no wheel was sent and no other technique may follow");
        // Final title + the EXACT dressing seen with the nonce (Codex's review of 19:36): the browser suffix is no longer
        // reinterpreted (tab B "Rapport - Personnel" in front no longer passes for page A "Rapport").
        var back = Stopwatch.StartNew();
        while (windowTitle() != final + dressing)
        {
            if (back.ElapsedMilliseconds >= ProofMs)
                throw new BackgroundNotApplicableException("The window no longer shows the proven page after cleanup (binding changed): nothing was sent");
            Thread.Sleep(20);
        }
    }

    // Restoring the title after setting it: conditional cleanup (two connections), then the PAGE title read again without
    // the nonce, then the WINDOW title without the nonce within ProofMs. Any failure: terminal refusal, the page may be
    // modified; no other technique follows, foreground allowed or not (proof C).
    static void VerifyCleanup(Func<string, JsonElement> evaluate, Func<string, JsonElement> fresh, Func<string> windowTitle, string restore, string nonce)
    {
        ActionRefusedException Terminal(string why) =>
            new($"Test title « {nonce} » may remain ({why}): no wheel was sent and no other technique may follow");
        if (!Clean(evaluate, fresh, restore)) throw Terminal("cleanup failed on two connections");
        string? page;
        try { page = Text(evaluate("document.title")); }
        catch (Exception)
        {
            try { page = Text(fresh("document.title")); }
            catch (Exception) { page = null; }
        }
        if (page is null) throw Terminal("page title unreadable after cleanup");
        if (page.Contains(nonce, StringComparison.Ordinal)) throw Terminal("still on the page after cleanup");
        var clock = Stopwatch.StartNew();
        while (windowTitle().Contains(nonce, StringComparison.Ordinal))
        {
            if (clock.ElapsedMilliseconds >= ProofMs) throw Terminal("still shown in the window title");
            Thread.Sleep(20);
        }
    }

    // Sorting of a preparation error (proof, measurements): refusal, stop and not applicable kept; any other error, before
    // the input, becomes "not applicable" (nothing was sent).
    internal static Exception BeforeInput(Exception error) =>
        error is BackgroundNotApplicableException or ActionRefusedException or EmergencyStopException ? error
            : new BackgroundNotApplicableException($"DevTools page unreachable before input ({error.GetType().Name}): nothing was sent");

    internal static string? Text(JsonElement result) => result.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Conditional cleanup, on the current connection then, if it broke, on a new connection to the same page. Succeeds
    // only if the script returned `true` (an error object or any other value does not count as a cleanup).
    internal static bool Clean(Func<string, JsonElement> evaluate, Func<string, JsonElement> fresh, string restore)
    {
        static bool Done(JsonElement result) => result.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        try { if (Done(evaluate(restore))) return true; }
        catch (Exception) { }
        try { return Done(fresh(restore)); }
        catch (Exception) { return false; }
    }
}
