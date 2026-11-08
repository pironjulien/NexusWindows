// Offline self-checks of commun/engine/CdpClaudeInput.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Net.Http;
using System.Text;
using System.Text.Json;
using static Cdp;

static partial class CdpSelfTests
{
    public static int ClaudeInputSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        if (input.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (input.RootElement.TryGetProperty("scenario", out var scenario)) return DomKeyTraceSelfTest(scenario.GetString()!);
            if (input.RootElement.TryGetProperty("surfaces", out var surfaces))
            {
                try
                {
                    var rectangles = surfaces.EnumerateArray().Select(s => s.EnumerateArray().Select(v => v.GetInt32()).ToArray()).Select(s => new Win32.Rect { Left = s[0], Top = s[1], Right = s[2], Bottom = s[3] }).ToList();
                    var b = input.RootElement.GetProperty("bounds").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    var inner = input.RootElement.GetProperty("inner").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    Console.WriteLine(EngineTestJson.Serialize(new CdpClaudeInputSelfTestJson(index: PickDocumentSurface(rectangles, b, inner[0], inner[1], input.RootElement.GetProperty("ratio").GetDouble()))));
                }
                catch (ActionRefusedException e) { Console.WriteLine(EngineTestJson.Serialize(new CdpSurfaceSelfTestJson2(error: e.Message))); }
                return 0;
            }
            if (input.RootElement.TryGetProperty("scripts", out _))
            {
                Console.WriteLine(EngineTestJson.Serialize(new CdpClaudeInputSelfTestJson2(focusEditable: FocusAt(16, 24, true), focusAny: FocusAt(16, 24, false), selectAll: SelectAllBody, append: AppendBody, select: SelectMutation("Bleu")))); return 0;
            }
        }
        var rows = new List<object>();
        foreach (var value in input.RootElement.EnumerateArray())
        { try { var key = ParseDomKey(value.GetString()!); rows.Add(new CdpClaudeInputSelfTestJson3(key: key.Key, code: key.Code, vk: key.Vk, modifiers: key.Modifiers, text: key.Text)); } catch (Exception e) { rows.Add(new CdpSurfaceSelfTestJson2(error: e.Message)); } }
        Console.WriteLine(EngineTestJson.Serialize(rows)); return 0;
    }

    // Real Session/WebSocket, no GUI. Independent server records exactly the events actually received.
    internal static int DomKeyTraceSelfTest(string scenario)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = new Thread(() =>
        {
            try
            {
                using var client = listener.AcceptTcpClient(); using var stream = client.GetStream(); Handshake(stream);
                while (ReadFrame(stream) is { } bytes)
                {
                    using var message = JsonDocument.Parse(bytes);
                    var type = message.RootElement.GetProperty("params").GetProperty("type").GetString()!;
                    received.Add(type);
                    if (scenario == "ack-lost" && type == "rawKeyDown") { client.Client.Close(); break; }
                    WriteFrame(stream, EngineTestJson.SerializeToUtf8Bytes(new CdpDomKeyTraceSelfTestJson(id: message.RootElement.GetProperty("id").GetInt32(), result: new CdpDomKeyTraceSelfTestJson2())));
                }
            }
            catch (Exception) { }
            finally { listener.Stop(); }
        }) { IsBackground = true }; server.Start();
        var started = 0; var dispatched = 0; var cleanups = 0; var hasSent = false; var focused = true; var blocked = false;
        string outcome;
        using (var session = new Session($"ws://127.0.0.1:{port}/devtools/page/dom-key-test"))
        {
            void Guard()
            { if (scenario == "account-before" || blocked) throw new ActionRefusedException("account/window identity guard"); }
            void Send(object parameters, Action? validate)
            {
                Guard(); validate?.Invoke();
                if (!hasSent) { started++; if (scenario == "grant-refused") throw new ActionRefusedException("contextual grant refused"); }
                Guard(); validate?.Invoke(); hasSent = true; dispatched++;
                session.Call("Input.dispatchKeyEvent", parameters);
                if (scenario == "focus-after-down") focused = false;
                if (scenario == "account-after-down") blocked = true;
            }
            try
            {
                DispatchDomKey(ParseDomKey("enter"), Send, () => hasSent, release =>
                { Guard(); cleanups++; session.Call("Input.dispatchKeyEvent", release); }, () =>
                { if (!focused) throw new ActionRefusedException("DOM focus moved after DOWN"); });
                outcome = "acknowledged";
            }
            catch (Exception) { outcome = hasSent ? "unknown" : "refused-before-input"; }
        }
        server.Join(3000);
        Console.WriteLine(EngineTestJson.Serialize(new CdpDomKeyTraceSelfTestJson3(outcome: outcome, started: started, dispatched: dispatched, cleanups: cleanups, received: received, automaticReplay: false, gui: false))); return 0;
    }
}
