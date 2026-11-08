// Offline self-checks of commun/engine/Steam.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Steam;

static partial class SteamSelfTests
{
    // Port and tests share the same decisions: no process, network or window is touched in this mode.
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
                    results.Add(new SteamSelfTestJson(arguments: info.Arguments, fileName: info.FileName, useShellExecute: info.UseShellExecute));
                }
                else if (kind == "tree")
                {
                    var rows = c.GetProperty("processes").EnumerateArray().Select(p => new ProcessRow(p.GetProperty("pid").GetUInt32(), p.GetProperty("parent").GetUInt32(), p.GetProperty("name").GetString() ?? "")).ToList();
                    var paths = c.GetProperty("processes").EnumerateArray().ToDictionary(p => p.GetProperty("pid").GetUInt32(), p => p.TryGetProperty("path", out var path) ? path.GetString() : null);
                    var roots = c.GetProperty("roots").EnumerateArray().Select(p => p.GetUInt32()).ToArray();
                    results.Add(new SteamSelfTestJson2(descendants: Descendants(roots, rows).Select(p => p.Pid), games: Games(c.GetProperty("exe").GetString() ?? "", rows, roots, p => paths.GetValueOrDefault(p))));
                }
                else if (kind == "endpoint")
                {
                    var helpers = c.GetProperty("helpers").EnumerateArray().Select(p => p.GetUInt32()).ToHashSet();
                    var listeners = c.GetProperty("listeners").EnumerateArray().Select(l => (l.GetProperty("address").GetString() ?? "", l.GetProperty("pid").GetUInt32())).ToList();
                    results.Add(new SteamSelfTestJson3(owned: OwnsLoopback(listeners, helpers), cdp: CdpReply(c.GetProperty("reply"), DevtoolsPort)));
                }
                else if (kind == "window") results.Add(new CdpClaudeInputSelfTestJson(index: PickWindow(c.GetProperty("titles").EnumerateArray().Select(t => t.GetString() ?? "").ToArray())));
                else if (kind == "reuse")
                {
                    var args = c.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                    if (ArgsProblem(args) is { } problem) throw new ActionRefusedException(problem);
                    RequireReusableEndpoint(c.TryGetProperty("devtools", out var d) && d.GetBoolean(), c.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null);
                    var index = PickWindow(c.GetProperty("titles").EnumerateArray().Select(t => t.GetString() ?? "").ToArray());
                    results.Add(new SteamSelfTestJson4(command: ReuseCommand(args, index >= 0)));
                }
                else if (kind == "cdp")
                {
                    var url = c.GetProperty("url").GetString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "ws" || endpoint.Host != "127.0.0.1" || endpoint.UserInfo.Length != 0) throw new ActionRefusedException("Selftest CDP endpoint must be loopback");
                    results.Add(new SteamSelfTestJson5(alive: ProbeCdp(url)));
                }
                else if (kind == "startup")
                {
                    var path = Path.Combine(Path.GetTempPath(), "nexus-steam-startup-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        StartNoActivate(new ProcessStartInfo(Program.HelperExecutable, "--steam-startup-info \"" + path + "\""));
                        var wait = Stopwatch.StartNew();
                        while (!File.Exists(path) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(25);
                        if (!File.Exists(path)) throw new InvalidOperationException("Startup selftest child did not report; outcome unknown");
                        using var reply = JsonDocument.Parse(File.ReadAllText(path)); results.Add(reply.RootElement.Clone());
                    }
                    finally { File.Delete(path); }
                }
                else throw new InvalidOperationException("Unknown Steam selftest");
            }
            catch (ActionRefusedException e) { results.Add(new ProgramLaunchSelfTestJson2(refused: e.Message)); }
        }
        Console.WriteLine(EngineTestJson.Serialize(results)); return 0;
    }
}
