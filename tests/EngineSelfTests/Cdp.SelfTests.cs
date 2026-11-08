// Offline self-checks of commun/engine/Cdp.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Cdp;

static partial class CdpSelfTests
{
    // Sec-WebSocket-Accept of the fixture servers: RFC 6455 section 4.2.2 fixes SHA-1 (a handshake check, not a security hash).
#pragma warning disable CA5350
    internal static string WebSocketAccept(string key) => Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350

    // Offline check (tests): stdin = {"title", "targets", "port", "commandLine", "pid", "listeners": [{"address",
    // "pid"}]}; output {"url"} or {"error"}, "port" (read in the command line) and "owned".
    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var root = input.RootElement;
        var port = PortFrom(root.TryGetProperty("commandLine", out var c) ? c.GetString() : null);
        bool? owned = root.TryGetProperty("pid", out var p)
            ? OwnsPort(p.GetUInt32(), root.GetProperty("listeners").EnumerateArray().Select(l => (l.GetProperty("address").GetString()!, l.GetProperty("pid").GetUInt32())))
            : null;
        var pagePort = root.TryGetProperty("port", out var pp) ? pp.GetInt32() : 9222;
        object result;
        try { result = new CdpSelfTestJson(url: PickPage(root.GetProperty("targets"), root.GetProperty("title").GetString() ?? "", pagePort), port: port, owned: owned); }
        catch (BackgroundNotApplicableException error) { result = new CdpSelfTestJson2(error: error.Message, port: port, owned: owned); }
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }

    // Offline check (tests): stdin = {"surfaces": [[left, top, right, bottom]], "point": [x, y], "inner": [w, h],
    // "ratio"}; output {"index", "gap"} or {"error"}.
    public static int SurfaceSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var root = input.RootElement;
        var surfaces = root.GetProperty("surfaces").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetInt32()).ToArray())
            .Select(a => new Win32.Rect { Left = a[0], Top = a[1], Right = a[2], Bottom = a[3] }).ToList();
        var point = root.GetProperty("point").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var inner = root.GetProperty("inner").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        object result;
        try
        {
            var (index, gw, gh) = PickSurface(surfaces, point[0], point[1], inner[0], inner[1], root.GetProperty("ratio").GetDouble());
            result = new CdpSurfaceSelfTestJson(index: index, gap: new[] { gw, gh });
        }
        catch (BackgroundNotApplicableException error) { result = new CdpSurfaceSelfTestJson2(error: error.Message); }
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }

    // Offline check (tests) of the title proof on a simulated page and window, DECOUPLED (the window title is not derived
    // from the page). stdin = {"scenario"}: "nominal", "lost-pose" (setting applied, answer lost, connection broken),
    // "title-changed", "dead" (two connections broken after the setting), "never-shown", "tab-switched" (another tab is
    // shown during the cleanup), "page-retitled" (the page changes its own title during the proof), "profile-confusion"
    // (tab B "Labo - Personnel" comes in front). Output: {"ok"} or {"error"}, and the final "pageTitle".
    public static int ProveSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var scenario = input.RootElement.GetProperty("scenario").GetString();
        var page = "Labo";
        var shown = "Labo";
        var (broken, posedOnce) = (false, false);
        var stuck = scenario is "hwnd-stuck" or "lost-pose-hwnd-stuck";
        JsonElement Value(object value) => EngineTestJson.SerializeToElement(new CdpProveSelfTestJson(value: value));
        JsonElement Run(string expression, bool primary)
        {
            if (primary && broken || !primary && scenario == "dead") throw new InvalidOperationException("connection lost");
            if (scenario == "restore-error-object" && expression.StartsWith("(() => { if (document.title === ", StringComparison.Ordinal))
                return EngineTestJson.SerializeToElement(new CdpProveSelfTestJson2(type: "object", subtype: "error", description: "TypeError"));
            if (expression == "document.title") return Value(page);
            var m = Regex.Match(expression, "^\\(\\(\\) => \\{ if \\(document.title (!==|===) (\"[^\"]*\")\\) (?:return false; )?document.title = (\"[^\"]*\");");
            if (!m.Success) throw new InvalidOperationException("unexpected expression");
            var (op, compared, assigned) = (m.Groups[1].Value, EngineTestJson.Deserialize<string>(m.Groups[2].Value)!, EngineTestJson.Deserialize<string>(m.Groups[3].Value)!);
            if (op == "!==")
            {
                if (scenario == "title-changed") shown = page = "Autre";
                if (page != compared) return Value(false);
                shown = page = assigned;
                posedOnce = true;
                if (scenario is "lost-pose" or "dead" or "lost-pose-hwnd-stuck") { broken = true; throw new InvalidOperationException("reply lost"); }
                return Value(true);
            }
            if (page == compared) page = assigned;
            shown = scenario == "tab-switched" ? "Onglet B" : scenario == "profile-confusion" ? page + " - Personnel" : page;
            return Value(true);
        }
        var dressing = scenario == "profile-confusion" ? " - Google Chrome" : " - Microsoft Edge";
        string Window()
        {
            if (stuck && posedOnce) return "nexus-test" + dressing; // the window title stays on the nonce
            if (scenario == "page-retitled" && shown.StartsWith("nexus-", StringComparison.Ordinal)) { var nonce = shown; page = "Nouveau"; shown = "Nouveau"; return nonce + dressing; }
            return (scenario == "never-shown" && shown.StartsWith("nexus-", StringComparison.Ordinal) ? "Labo" : shown) + dressing;
        }
        object result;
        try { Prove(e => Run(e, true), e => Run(e, false), Window, "nexus-test"); result = new CdpProveSelfTestJson3(ok: true, pageTitle: page); }
        catch (Exception error) { result = new CdpProveSelfTestJson4(error: error.Message, kind: error.GetType().Name, wheelKind: BeforeInput(error).GetType().Name, pageTitle: page); }
        Console.WriteLine(EngineTestJson.Serialize(result));
        return 0;
    }

    // Proof C (Codex's review of 22/10) on a REAL browser (a window started with a DevTools port): the title proof and its
    // cleanup go through the real connections, breaks caused at the critical points. stdin = {"hwnd", "scenario"}:
    // "nominal"; "lost-pose" (setting APPLIED then connection cut without an answer); "dead" (on top of that, any new
    // connection refused); "legit-retitle" (the page changes its own title during the proof); "tab-switch" (another tab
    // comes in front just before the cleanup). Output: outcome (ok or error and its type), fresh connections opened, DOM
    // title and WINDOW title before and after; "dead": original title put back afterwards by the check itself (stated).
    // Never a wheel sent.
    public static int ProveLiveSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var window = (nint)input.RootElement.GetProperty("hwnd").GetInt64();
        var scenario = input.RootElement.GetProperty("scenario").GetString();
        var pid = Win32.ProcessOf(window);
        var port = PortFrom(CommandLine(pid)) ?? throw new InvalidOperationException("No DevTools port on this window");
        if (!OwnsPort(pid, Listeners(port))) throw new InvalidOperationException("Port not owned by the window process");
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
        string url;
        using (var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult()))
            url = PickPage(targets.RootElement, Win32.Title(window), port);
        var real = FreshEvaluator(url);
        var original = Text(real("document.title"));
        var windowBefore = Win32.Title(window);
        var primary = new Session(url);
        var (dropped, freshOpened, retitled) = (false, 0, false);
        string? otherTab = null;
        JsonElement Evaluate(string expression)
        {
            if (dropped) throw new InvalidOperationException("primary connection lost");
            if (scenario == "tab-switch" && otherTab is null && expression.StartsWith("(() => { if (document.title === ", StringComparison.Ordinal))
            {
                using var opened = JsonDocument.Parse(http.PutAsync($"http://127.0.0.1:{port}/json/new?about:blank", null).GetAwaiter().GetResult()
                    .Content.ReadAsStringAsync().GetAwaiter().GetResult());
                otherTab = opened.RootElement.GetProperty("id").GetString();
                Thread.Sleep(400);
            }
            var result = primary.Evaluate(expression);
            if (scenario is "lost-pose" or "dead" && expression.Contains("return false; document.title = ", StringComparison.Ordinal))
            {
                dropped = true;
                primary.Dispose();
                throw new InvalidOperationException("reply lost after the pose");
            }
            return result;
        }
        JsonElement Fresh(string expression)
        {
            if (scenario == "dead") throw new InvalidOperationException("new connection refused");
            freshOpened++;
            return real(expression);
        }
        string Shown()
        {
            var shown = Win32.Title(window);
            if (scenario == "legit-retitle" && !retitled && shown.StartsWith("nexus-", StringComparison.Ordinal))
            {
                retitled = true;
                real("document.title = 'Titre legitime'");
            }
            return shown;
        }
        string outcome, kind = "";
        try { Prove(Evaluate, Fresh, Shown); outcome = "ok"; }
        catch (Exception error) { outcome = error.Message; kind = error.GetType().Name; }
        finally { if (!dropped) primary.Dispose(); }
        var domAfter = Text(real("document.title"));
        Thread.Sleep(300);
        var windowAfter = Win32.Title(window);
        string? restoredByTest = null;
        if (otherTab is not null) http.GetAsync($"http://127.0.0.1:{port}/json/close/{otherTab}").GetAwaiter().GetResult();
        if (domAfter is not null && domAfter.StartsWith("nexus-", StringComparison.Ordinal))
        {
            real($"document.title = {EngineTestJson.Serialize(original)}");
            restoredByTest = original;
        }
        Console.WriteLine(EngineTestJson.Serialize(new CdpProveLiveSelfTestJson(scenario: scenario, outcome: outcome, kind: kind, freshOpened: freshOpened, original: original, windowBefore: windowBefore, domAfter: domAfter, windowAfter: windowAfter, restoredByTest: restoredByTest, tabOpened: otherTab is not null)));
        return 0;
    }

    // Traces D (Codex's review of 22/10), through the REAL connection (Session) against an instrumented local DevTools
    // server: "ack-lost": the wheel is received then the connection cut without an answer; "stop-during-prepare": the
    // emergency stop is set during the preparation (evaluation). Output: {"outcome", "started", "dispatched", "evaluated"}.
    // Requires NEXUS_SHARED_HOME (test folder): the real stop file is never written.
    public static int WheelTraceSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var scenario = input.RootElement.GetProperty("scenario").GetString();
        if (!EmergencyStop.TestDirectory) { Console.WriteLine(EngineTestJson.Serialize(new CdpSurfaceSelfTestJson2(error: "NEXUS_SHARED_HOME required"))); return 1; }
        var (port, dispatched, evaluated, server) = InstrumentedServer();
        var started = 0;
        string outcome;
        try
        {
            using var session = new Session($"ws://127.0.0.1:{port}/devtools/page/test");
            session.Evaluate("document.title"); // preparation (preuve, mesures)
            if (scenario == "stop-during-prepare") File.WriteAllText(Path.Combine(EmergencyStop.Directory, "stop"), "selftest");
            Dispatch(session.Call, new CdpWheelJson(type: "mouseWheel", x: 10, y: 10, deltaX: 0, deltaY: 120), () => started++);
            outcome = "acknowledged";
        }
        catch (EmergencyStopException) { outcome = "stopped"; }
        catch (InvalidOperationException error) when (error.Message.Contains("outcome unknown")) { outcome = "unknown"; }
        server.Join(2000);
        Console.WriteLine(EngineTestJson.Serialize(new CdpWheelTraceSelfTestJson2(outcome: outcome, started: started, dispatched: dispatched.Count, evaluated: evaluated.Count)));
        return 0;
    }

    // Local DevTools server: answers Runtime.evaluate; on Input.dispatchMouseEvent, counts then cuts without an answer.
    internal static (int Port, List<string> Dispatched, List<string> Evaluated, Thread Server) InstrumentedServer()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var (dispatched, evaluated) = (new List<string>(), new List<string>());
        var server = new Thread(() =>
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                Handshake(stream);
                while (ReadFrame(stream) is { } data)
                {
                    using var message = JsonDocument.Parse(data);
                    var method = message.RootElement.GetProperty("method").GetString()!;
                    if (method == "Input.dispatchMouseEvent") { dispatched.Add(method); client.Client.Close(); break; }
                    evaluated.Add(message.RootElement.GetProperty("params").GetProperty("expression").GetString()!);
                    WriteFrame(stream, EngineTestJson.SerializeToUtf8Bytes(new CdpInstrumentedServerJson(id: message.RootElement.GetProperty("id").GetInt32(), result: new CdpInstrumentedServerJson2(result: new CdpInstrumentedServerJson3(type: "string", value: "Labo")))));
                }
            }
            catch (Exception) { }
            finally { listener.Stop(); }
        }) { IsBackground = true };
        server.Start();
        return (port, dispatched, evaluated, server);
    }

    internal static void Handshake(System.Net.Sockets.NetworkStream stream)
    {
        var request = new List<byte>();
        while (request.Count < 4 || request[^4] != '\r' || request[^3] != '\n' || request[^2] != '\r' || request[^1] != '\n') request.Add((byte)stream.ReadByte());
        var key = Regex.Match(System.Text.Encoding.ASCII.GetString(request.ToArray()), @"Sec-WebSocket-Key: (\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
        var accept = WebSocketAccept(key);
        stream.Write(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
    }

    internal static byte[]? ReadFrame(System.Net.Sockets.NetworkStream stream)
    {
        var b0 = stream.ReadByte(); var b1 = stream.ReadByte();
        if (b0 < 0 || (b0 & 0x0F) == 8) return null;
        long length = b1 & 0x7F;
        if (length == 126) length = (stream.ReadByte() << 8) | stream.ReadByte();
        var mask = new byte[4]; stream.ReadExactly(mask);
        var data = new byte[length]; stream.ReadExactly(data);
        for (var i = 0; i < data.Length; i++) data[i] ^= mask[i % 4];
        return data;
    }

    internal static void WriteFrame(System.Net.Sockets.NetworkStream stream, byte[] reply)
    {
        var frame = new List<byte> { 0x81 };
        if (reply.Length < 126) frame.Add((byte)reply.Length); else { frame.Add(126); frame.Add((byte)(reply.Length >> 8)); frame.Add((byte)reply.Length); }
        frame.AddRange(reply);
        stream.Write(frame.ToArray());
    }

    // Offline check (tests) of the emergency cleanup through the REAL connection factory (FreshEvaluator, Session) against
    // a minimal local WebSocket server that answers Runtime.evaluate. Output: {"cleaned", "received"}.
    public static int SessionSelfTest()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = new Thread(() =>
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var request = new List<byte>();
                while (request.Count < 4 || request[^4] != '\r' || request[^3] != '\n' || request[^2] != '\r' || request[^1] != '\n') request.Add((byte)stream.ReadByte());
                var key = Regex.Match(System.Text.Encoding.ASCII.GetString(request.ToArray()), @"Sec-WebSocket-Key: (\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
                var accept = WebSocketAccept(key);
                var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                stream.Write(head);
                while (true)
                {
                    var b0 = stream.ReadByte(); var b1 = stream.ReadByte();
                    if (b0 < 0 || (b0 & 0x0F) == 8) break;
                    long length = b1 & 0x7F;
                    if (length == 126) length = (stream.ReadByte() << 8) | stream.ReadByte();
                    var mask = new byte[4]; stream.ReadExactly(mask);
                    var data = new byte[length]; stream.ReadExactly(data);
                    for (var i = 0; i < data.Length; i++) data[i] ^= mask[i % 4];
                    using var message = JsonDocument.Parse(data);
                    received.Add(message.RootElement.GetProperty("params").GetProperty("expression").GetString()!);
                    var reply = EngineTestJson.SerializeToUtf8Bytes(new CdpSessionSelfTestJson(id: message.RootElement.GetProperty("id").GetInt32(), result: new CdpSessionSelfTestJson2(result: new CdpSessionSelfTestJson3(type: "boolean", value: true))));
                    var frame = new List<byte> { 0x81 };
                    if (reply.Length < 126) frame.Add((byte)reply.Length); else { frame.Add(126); frame.Add((byte)(reply.Length >> 8)); frame.Add((byte)reply.Length); }
                    frame.AddRange(reply);
                    stream.Write(frame.ToArray());
                }
            }
            catch (Exception) { }
        }) { IsBackground = true };
        server.Start();
        var restore = "(() => { if (document.title === \"nexus-test\") document.title = \"Labo\"; return true; })()";
        var cleaned = Clean(_ => throw new InvalidOperationException("primary connection lost"), FreshEvaluator($"ws://127.0.0.1:{port}/devtools/page/test"), restore);
        server.Join(2000);
        listener.Stop();
        Console.WriteLine(EngineTestJson.Serialize(new CdpSessionSelfTestJson4(cleaned: cleaned, received: received)));
        return 0;
    }
}
