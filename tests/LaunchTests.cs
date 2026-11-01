// Launch planning of the engine, offline: nothing is started, the "programs" are empty files of a temporary folder
// (formerly launch.test.mjs, steam.test.mjs and native-launch-compat.test.mjs over --launch-selftest,
// --steam-selftest and --launch-compat-selftest).
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

sealed class Bench : IDisposable
{
    public readonly string Root = Directory.CreateTempSubdirectory("nexus launch ").FullName;
    public string File(string relative, string body = "")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, body);
        return path;
    }
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

[Collection("Engine")]
public class LaunchTests
{
    static JsonArray Plan(object cases) => SelfTest.Json(Program.LaunchSelfTest, cases).AsArray();

    sealed class Editor : IDisposable
    {
        public readonly Bench B = new();
        public string EditorExe, Spaced, Note, SpacedNote, Electron, Browser;
        public Editor()
        {
            EditorExe = B.File(@"NexusBanc\EditeurTest.exe");
            Spaced = B.File(@"Mes Outils\Editeur Test.exe");
            Note = B.File(@"passe2\note.txt", "x");
            SpacedNote = B.File(@"passe 2\ma note.txt", "x");
            B.File(@"Appli\resources\app.asar"); Electron = B.File(@"Appli\Appli.exe");
            B.File(@"Navigateur\Application\156.0.1\resources.pak"); Browser = B.File(@"Navigateur\Application\browser.exe");
        }
        public void Dispose() => B.Dispose();
    }

    [Fact]
    public void A_program_followed_by_its_arguments_is_launched_with_them_quoted_or_not_spaces_kept()
    {
        using var b = new Editor();
        var r = Plan(new object[] {
            new { target = $"{b.EditorExe} {b.Note}" }, new { target = $"\"{b.EditorExe}\" \"{b.Note}\"" }, new { target = b.EditorExe, args = $"\"{b.Note}\"" },
            new { target = $"\"{b.Spaced}\" \"{b.SpacedNote}\"" }, new { target = $"{b.Spaced} \"{b.SpacedNote}\"" }, new { target = $"\"{b.Spaced}\"", args = $"\"{b.SpacedNote}\"" } });
        Assert.Equal([b.EditorExe, b.Note, b.EditorExe, b.Note], new[] { r[0]!["target"].S(), r[0]!["args"].S(), r[0]!["start"].S(), r[0]!["arguments"].S() });
        Assert.Equal([b.EditorExe, $"\"{b.Note}\"", b.EditorExe], new[] { r[1]!["target"].S(), r[1]!["args"].S(), r[1]!["exePath"].S() });
        Assert.Equal([b.EditorExe, $"\"{b.Note}\""], new[] { r[2]!["target"].S(), r[2]!["arguments"].S() });
        foreach (var one in r.Skip(3))
        {
            Assert.Equal(b.Spaced, one!["target"].S()); Assert.True(one["isExe"].B());
            Assert.Equal(b.Spaced, one["exePath"].S()); Assert.Equal($"\"{b.SpacedNote}\"", one["arguments"].S());
        }
    }

    [Fact]
    public void An_existing_path_with_spaces_is_never_cut_and_quotes_around_a_lone_path_are_only_quotes()
    {
        using var b = new Editor();
        var folder = Path.Combine(b.B.Root, "Mes Outils");
        var r = Plan(new object[] { new { target = b.Spaced }, new { target = b.SpacedNote }, new { target = $"\"{b.SpacedNote}\"" }, new { target = folder },
            new { target = $"notepad \"{b.SpacedNote}\"" } });
        Assert.Equal((b.Spaced, "", true), (r[0]!["target"].S(), r[0]!["args"].S(), r[0]!["isExe"].B()));
        Assert.Equal((b.SpacedNote, "", false), (r[1]!["target"].S(), r[1]!["args"].S(), r[1]!["isExe"].B()));
        Assert.Equal((b.SpacedNote, ""), (r[2]!["target"].S(), r[2]!["args"].S()));
        Assert.Equal((folder, ""), (r[3]!["target"].S(), r[3]!["args"].S()));
        Assert.Equal(("notepad", $"\"{b.SpacedNote}\"", "notepad"), (r[4]!["target"].S(), r[4]!["args"].S(), r[4]!["start"].S()));
    }

    [Fact]
    public void Arguments_only_for_a_program_never_a_switch_for_a_chromium_or_electron_application()
    {
        using var b = new Editor();
        var r = Plan(new object[] {
            new { target = b.Note, args = "x" }, new { target = b.Electron, args = "--remote-debugging-port=9333" },
            new { target = $"\"{b.Browser}\" --user-data-dir=C:\\x" }, new { target = b.Browser, args = $"\"{b.Note}\" /safe" },
            new { target = "nexus-introuvable-zz", args = "--x" }, new { target = "ms-settings:display", args = "x" },
            new { target = "tel:0600000000" }, new { target = "relatif\\app.exe x" }, new { target = "Microsoft.Paint_8wekyb3d8bbwe!App", args = "x" } });
        Assert.Matches("Arguments go only to a program", r[0]!["refused"].S());
        Assert.Matches(@"Switch ""--remote-debugging-port=9333"" refused for a Chromium/Electron", r[1]!["refused"].S());
        Assert.Matches(@"Switch ""--user-data-dir=C:\\x"" refused", r[2]!["refused"].S());
        Assert.Matches(@"Switch ""/safe"" refused", r[3]!["refused"].S());
        Assert.Matches("does not resolve: give the program's absolute path", r[4]!["refused"].S());
        Assert.Matches("Only ms-settings", r[5]!["refused"].S());
        Assert.Matches("Only ms-settings", r[6]!["refused"].S());
        Assert.Matches("Give an absolute path", r[7]!["refused"].S());
        Assert.Matches("Arguments go only to a program", r[8]!["refused"].S());
        // A file given to an Electron application: after the tool's switches, never in their place.
        var electron = Plan(new object[] { new { target = b.Electron, args = $"\"{b.Note}\"" } })[0]!;
        Assert.Equal($"--disable-features=CalculateNativeWinOcclusion --force-renderer-accessibility \"{b.Note}\"", electron["arguments"].S());
    }

    // ----- Steam (no real client is started or stopped) -----

    static JsonArray Steam(object cases) => SelfTest.Json(global::Steam.SelfTest, cases).AsArray();

    sealed class SteamBench : IDisposable
    {
        public readonly Bench B = new();
        public readonly string Exe, Fake, EditorExe;
        public SteamBench()
        {
            Exe = B.File(@"Valve\steam.exe"); B.File(@"Valve\steamclient64.dll");
            Fake = B.File(@"fake\steam.exe"); EditorExe = B.File(@"Valve\editor.exe");
        }
        public void Dispose() => B.Dispose();
    }

    [Fact]
    public void Steam_startup_uses_its_cef_view_flags_and_debugging_needs_the_option_and_a_genuine_client()
    {
        using var b = new SteamBench();
        var r = Steam(new object[] { new { kind = "plan", exe = b.Exe }, new { kind = "plan", exe = b.Exe, args = "-silent steam://open/library", devtools = true },
            new { kind = "plan", exe = b.Fake }, new { kind = "plan", exe = b.EditorExe } });
        Assert.Equal("-cef-disable-occlusion -cef-force-accessibility", r[0]!["arguments"].S());
        Assert.Equal("-silent steam://open/library -cef-disable-occlusion -cef-force-accessibility -cef-enable-debugging", r[1]!["arguments"].S());
        Assert.Matches("not the genuine Steam", r[2]!["refused"].S());
        Assert.Matches("not the genuine Steam", r[3]!["refused"].S());
        Assert.Equal("note.txt", Plan(new object[] { new { target = b.EditorExe, args = "note.txt" } })[0]!["arguments"].S());
    }

    [Fact]
    public void Steam_startup_requests_show_no_activate_in_a_real_helper_child_with_no_gui()
    {
        var startup = Steam(new object[] { new { kind = "startup" } })[0]!;
        Assert.True((startup["flags"].L() & 1) != 0);
        Assert.Equal(4, startup["showWindow"].I());
    }

    [Fact]
    public void Raw_control_or_profile_switches_neutralizers_and_closing_uris_cannot_bypass_steam_launch_options()
    {
        using var b = new SteamBench();
        string[] rejected = ["-cef-enable-debugging", "--remote-debugging-port=8080", "/remote-debugging-address=0.0.0.0", "--remote-debugging-pipe",
            "--user-data-dir=C:\\x", "--profile-directory=Default", "--", "--single-argument", "-shutdown", "/restart",
            "steam://exit", "steam://quit", "steam://shutdown", "steam://restart"];
        Assert.All(Steam(rejected.Select(args => new { kind = "plan", exe = b.Exe, args }).ToArray()), r => Assert.NotNull(r!["refused"]));
        var ordinary = Steam(new object[] { new { kind = "plan", exe = b.Exe, args = "-silent -cef-force-accessibility steam://open/main" } })[0]!;
        Assert.Matches("^-silent -cef-force-accessibility steam://open/main ", ordinary["arguments"].S());
    }

    [Fact]
    public void Steam_restart_inventory_finds_nested_games_and_foreign_helpers()
    {
        using var b = new SteamBench();
        var root = b.B.Root;
        var processes = new object[] {
            new { pid = 1, parent = 0, name = "steam.exe", path = b.Exe },
            new { pid = 2, parent = 1, name = "steamwebhelper.exe", path = Path.Combine(root, "Valve", "bin", "steamwebhelper.exe") },
            new { pid = 3, parent = 2, name = "game.exe", path = Path.Combine(root, "games", "game.exe") },
            new { pid = 4, parent = 3, name = "launcher.exe", path = Path.Combine(root, "games", "launcher.exe") },
            new { pid = 5, parent = 1, name = "steamwebhelper.exe", path = Path.Combine(root, "fake", "steamwebhelper.exe") },
            new { pid = 6, parent = 1, name = "steamservice.exe" },
            new { pid = 7, parent = 99, name = "unrelated.exe", path = Path.Combine(root, "unrelated.exe") } };
        var result = Steam(new object[] { new { kind = "tree", exe = b.Exe, roots = new[] { 1 }, processes } })[0]!;
        Assert.Equal([2L, 5, 6, 3, 4], result["descendants"]!.AsArray().Select(n => n.L()));
        Assert.Equal(["game.exe", "launcher.exe", "steamservice.exe", "steamwebhelper.exe"], result["games"].Strings());
    }

    [Fact]
    public void Steam_window_selection_accepts_one_main_window_and_refuses_ambiguous_candidates()
    {
        string[][] titles = [[], ["Sign in"], ["Library", "Steam"], ["Steam", " Steam "], ["Store", "Friends"]];
        var r = Steam(titles.Select(t => new { kind = "window", titles = t }).ToArray());
        SelfTest.Same("""[{"index":-1},{"index":0},{"index":1}]""", new JsonArray(r.Take(3).Select(n => n!.DeepClone()).ToArray()));
        Assert.All(r.Skip(3), n => Assert.Matches("ambiguous", n!["refused"].S()));
    }

    [Fact]
    public void Steam_reuse_preserves_ordinary_args_once_and_checks_devtools_before_transmitting_a_command()
    {
        var r = Steam(new object[] {
            new { kind = "reuse", titles = new[] { "Steam" }, args = "-applaunch 42" },
            new { kind = "reuse", titles = new[] { "Steam" } }, new { kind = "reuse", titles = Array.Empty<string>() },
            new { kind = "reuse", titles = new[] { "Steam" }, args = "steam://open/library", devtools = true },
            new { kind = "reuse", titles = new[] { "Steam" }, args = "steam://open/library", devtools = true, port = 8080 } });
        SelfTest.Same("""[{"command":"-applaunch 42"},{"command":null},{"command":"steam://open/main"}]""", new JsonArray(r.Take(3).Select(n => n!.DeepClone()).ToArray()));
        Assert.Matches("already running without a verified", r[3]!["refused"].S());
        SelfTest.Same("""{"command":"steam://open/library"}""", r[4]);
    }

    [Fact]
    public void Steam_devtools_requires_its_own_helper_on_loopback_and_a_matching_local_websocket_endpoint()
    {
        const string endpoint = "ws://127.0.0.1:8080/devtools/browser/id";
        object[][] listeners = [[new { address = "127.0.0.1", pid = 2 }], [new { address = "127.0.0.1", pid = 9 }], [new { address = "0.0.0.0", pid = 2 }],
            [new { address = "127.0.0.1", pid = 2 }, new { address = "::", pid = 2 }], []];
        var owned = Steam(listeners.Select(l => new { kind = "endpoint", helpers = new[] { 2 }, listeners = l, reply = new { webSocketDebuggerUrl = endpoint } }).ToArray());
        Assert.Equal([true, false, false, false, false], owned.Select(r => r!["owned"].B()));
        string[] wrong = ["ws://foreign.test:8080/id", "ws://127.0.0.1:8081/id", "http://127.0.0.1:8080/id", "ws://user@127.0.0.1:8080/id", "ws://127.0.0.1:8080/"];
        Assert.All(Steam(wrong.Select(url => new { kind = "endpoint", helpers = new[] { 2 }, listeners = listeners[0], reply = new { webSocketDebuggerUrl = url } }).ToArray()),
            r => Assert.False(r!["cdp"]?.GetValue<bool>() ?? false));
    }

    [Fact]
    public void Steam_cdp_liveness_exchanges_browser_getVersion_and_a_listening_socket_or_unrelated_json_does_not_prove_cdp()
    {
        string? response = """{"result":{"protocolVersion":"1.3","product":"Chrome/contract"}}""";
        var methods = new List<string>();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; } catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => Serve(client));
            }
        });
        void Serve(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && stream.Read(one, 0, 1) == 1) request.Append((char)one[0]);
            var key = request.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            try
            {
                var header = new byte[2];
                while (stream.ReadAtLeast(header, 2, false) == 2)
                {
                    var length = header[1] & 127;
                    Assert.True(length < 126);
                    var mask = new byte[4]; stream.ReadExactly(mask);
                    var payload = new byte[length]; stream.ReadExactly(payload);
                    for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
                    JsonNode? message;
                    try { message = JsonNode.Parse(payload); } catch (JsonException) { continue; }
                    lock (methods) methods.Add(message!["method"].S());
                    if (response is null) continue;
                    var reply = JsonNode.Parse(response)!.AsObject();
                    reply["id"] = message["id"]!.DeepClone();
                    var body = Encoding.UTF8.GetBytes(reply.ToJsonString());
                    Assert.True(body.Length < 126);
                    stream.Write([0x81, (byte)body.Length, .. body]);
                }
            }
            catch (IOException) { }
        }
        try
        {
            bool Alive() => Steam(new object[] { new { kind = "cdp", url = $"ws://127.0.0.1:{port}/devtools/browser/id" } })[0]!["alive"].B();
            Assert.True(Alive());
            response = """{"result":{"hello":"ordinary JSON"}}"""; Assert.False(Alive());
            response = """{"error":{"message":"unsupported method"}}"""; Assert.False(Alive());
            response = null; Assert.False(Alive());
            lock (methods) Assert.Equal(Enumerable.Repeat("Browser.getVersion", 4), methods);
        }
        finally { stop.Cancel(); listener.Stop(); server.Wait(5000); }
    }

    // ----- Claude profile launch planning (McpLaunchCompatibility.cs) -----

    static JsonArray Compat(object cases) => SelfTest.Json(McpServer.LaunchCompatibilitySelfTest, cases).AsArray();

    [Fact]
    public void Claude_launch_preserves_uri_document_folder_and_explicit_packaged_arguments_without_launching()
    {
        using var b = new Bench();
        var editor = b.File(@"Mes Outils\Editeur Test.exe");
        var document = b.File(@"Documents\été été.txt");
        var r = Compat(new object[] {
            new { app = "https://example.test/a?q=été" }, new { app = "mailto:fixture@example.test?subject=Local" }, new { app = "tel:fixture" },
            new { app = "ms-settings:display" }, new { app = document }, new { app = Path.Combine(b.Root, "Documents") },
            new { app = "Microsoft.Fixture_123!App", args = "fixture value" },
            new { app = "https://example.test", args = "--x" }, new { app = document, devtools = true } });
        foreach (var i in new[] { 0, 1, 2, 3 }) Assert.Equal("uri", r[i]!["kind"].S());
        Assert.Equal("https://example.test/a?q=été", r[0]!["target"].S());
        Assert.Equal("document", r[4]!["kind"].S()); Assert.Equal("folder", r[5]!["kind"].S());
        Assert.Equal("packaged", r[6]!["kind"].S()); Assert.Equal("fixture value", r[6]!["args"].S());
        Assert.Matches("no args", r[7]!["refused"].S()); Assert.Matches("no args", r[8]!["refused"].S());
        var activation = Compat(new object[] { new { app = document, activationHResult = unchecked((int)0x80270254) }, new { app = document, activationHResult = unchecked((int)0x80004005) } });
        Assert.True(activation[0]!["fileContractFallbackAllowed"].B());
        Assert.False(activation[1]!["fileContractFallbackAllowed"].B(), "an uncertain failed activation is never replayed through the shell");
    }

    [Fact]
    public void Claude_launch_resolves_complete_friendly_names_before_command_line_splitting_and_preserves_shortcuts()
    {
        using var b = new Bench();
        var editor = b.File(@"Mes Outils\Editeur Test.exe");
        var electron = b.File(@"Electron\Appli.exe"); b.File(@"Electron\resources\app.asar");
        var catalogue = new object[] {
            new { name = "Mon Editeur Test", id = "Fixture.Editor", target = editor },
            new { name = "Options Fixture", id = "Fixture.Options", target = electron, args = "--existing-choice" },
            new { name = "Calculatrice Fixture", id = "Microsoft.Fixture_123!App" } };
        var r = Compat(new object[] {
            new { app = "Mon Editeur Test", args = "\"été espace.txt\"", catalogue },
            new { app = "Editeur Test", catalogue }, new { app = "Options Fixture", args = "ignored", catalogue },
            new { app = "Calculatrice Fixture", args = "fixture", catalogue },
            new { app = $"\"{editor}\" \"été espace.txt\"", catalogue }, new { app = editor, args = "\"été espace.txt\"" },
            new { app = "Même nom", catalogue = new object[] { new { name = "Même nom", id = "A" }, new { name = "Même nom", id = "B" } } } });
        var (friendly, partial, shortcut, packaged, quoted, field, ambiguous) = (r[0]!, r[1]!, r[2]!, r[3]!, r[4]!, r[5]!, r[6]!);
        Assert.Equal("executable", friendly["kind"].S()); Assert.Equal(editor, friendly["target"].S());
        Assert.Equal(editor, partial["target"].S()); Assert.Equal("\"été espace.txt\"", friendly["args"].S());
        Assert.Equal("shortcut", shortcut["kind"].S()); Assert.Equal(@"shell:AppsFolder\Fixture.Options", shortcut["target"].S());
        Assert.Equal("", shortcut["args"].S()); Assert.Equal("packaged", packaged["kind"].S()); Assert.Equal("fixture", packaged["args"].S());
        Assert.Equal(field["target"].S(), quoted["target"].S()); Assert.Equal(field["args"].S(), quoted["args"].S());
        Assert.Matches("Several Start", ambiguous["refused"].S());
    }

    [Fact]
    public void Claude_chromium_policy_preserves_ordinary_switches_and_rejects_control_or_profile_injection()
    {
        using var b = new Bench();
        var editor = b.File(@"Mes Outils\Editeur Test.exe");
        var chrome = b.File(@"Navigateur\chrome.exe");
        var proxy = b.File(@"Navigateur\chrome_proxy.exe");
        string[] injections = ["--remote-debugging-port=9222", "/LOAD-EXTENSION=x", "--user-data-dir=x", "--profile-directory=Default", "--single-argument=x", "--"];
        var r = Compat(new object[] {
            new { app = chrome, args = "--app=https://example.test --disable-features=A,B \"--disable-features=B,C\" \"été espace.txt\"" },
            new { app = chrome, args = "--new-window https://example.test", devtools = true },
            new { app = proxy, devtools = true }, new { app = editor, devtools = true } }.Concat(injections.Select(args => (object)new { app = chrome, args })).ToArray());
        var normal = r[0]!["parsedArguments"].Strings();
        Assert.Equal("executable", r[0]!["kind"].S());
        Assert.Contains("--app=https://example.test", normal);
        Assert.Contains("été espace.txt", normal);
        Assert.Single(normal, v => v.StartsWith("--disable-features=", StringComparison.Ordinal));
        Assert.Contains("--disable-features=A,B,C,CalculateNativeWinOcclusion", normal);
        var fresh = r[1]!["parsedArguments"].Strings();
        Assert.Contains(@"--user-data-dir=C:\NexusFixture\fresh profile", fresh);
        Assert.Contains("--remote-debugging-port=16183", fresh);
        Assert.Contains("--disable-features=CalculateNativeWinOcclusion,msImplicitSignin", fresh);
        Assert.Contains("--disable-sync", fresh);
        Assert.Matches("main executable", r[2]!["refused"].S()); Assert.Matches("verified Chromium", r[3]!["refused"].S());
        foreach (var value in r.Skip(4)) Assert.NotNull(value!["refused"]);
    }

    [Fact]
    public void Claude_launch_keeps_fixed_profile_hard_blocks_and_rejects_restart_outside_steam()
    {
        using var b = new Bench();
        var editor = b.File(@"Mes Outils\Editeur Test.exe");
        var keeper = b.File(@"PasswordManager\keepass.exe");
        var r = Compat(new object[] { new { app = keeper }, new { app = editor, restart = true },
            new { app = "Fixture_123!App", devtools = true }, new { app = "Fixture_123!App", args = "--remote-debugging-port=9222" } });
        Assert.Matches("shared garde-fous", r[0]!["refused"].S());
        Assert.Matches("Steam", r[1]!["refused"].S()); Assert.Matches("full Chromium", r[2]!["refused"].S());
        Assert.Matches("control channel", r[3]!["refused"].S());
    }

    [Fact]
    public void Claude_fresh_profile_guard_reads_only_marker_locations_and_never_emits_account_values()
    {
        using var b = new Bench();
        var editor = b.File(@"Mes Outils\Editeur Test.exe");
        var profile = Path.Combine(b.Root, "profile");
        var state = b.File(@"profile\Local State", """{"profile":{"info_cache":{"Default":{"user_name":"PRIVATE-FIXTURE-VALUE"}}}}""");
        var account = Compat(new object[] { new { app = editor, profileFixture = profile } })[0]!;
        Assert.Matches("Account marker", account["accountMarker"].S()); Assert.DoesNotContain("PRIVATE-FIXTURE-VALUE", account.ToJsonString());
        File.WriteAllText(state, """{"profile":{"info_cache":{"Default":{"name":"Ordinary profile","user_name":""}}}}""");
        b.File(@"profile\Default\Preferences", """{"account_info":[],"google":{"services":{}}}""");
        Assert.Null(Compat(new object[] { new { app = editor, profileFixture = profile } })[0]!["accountMarker"]);
        b.File(@"profile\Default\Preferences", """{"account_info":[{"value":"PRIVATE-FIXTURE-VALUE"}]}""");
        var preferences = Compat(new object[] { new { app = editor, profileFixture = profile } })[0]!;
        Assert.Matches("Preferences", preferences["accountMarker"].S()); Assert.DoesNotContain("PRIVATE-FIXTURE-VALUE", preferences.ToJsonString());
    }

    [Fact]
    public void Claude_steam_lease_needs_one_genuine_helper_owning_an_exclusive_loopback_listener()
    {
        var r = Compat(new object[] {
            new { steamPortFixture = new { helpers = new[] { 42, 43 }, listeners = new[] { new { address = "127.0.0.1", pid = 42 } } } },
            new { steamPortFixture = new { helpers = new[] { 42 }, listeners = new[] { new { address = "127.0.0.1", pid = 99 } } } },
            new { steamPortFixture = new { helpers = new[] { 42 }, listeners = new[] { new { address = "0.0.0.0", pid = 42 } } } },
            new { steamPortFixture = new { helpers = new[] { 42, 43 }, listeners = new[] { new { address = "127.0.0.1", pid = 42 }, new { address = "127.0.0.1", pid = 43 } } } },
            new { steamPortFixture = new { helpers = new[] { 42 }, listeners = Array.Empty<object>() } } });
        Assert.Equal(42, r[0]!["steamOwner"].I()); Assert.Equal("genuine-steam-session", r[0]!["sessionKind"].S());
        foreach (var result in r.Skip(1)) Assert.NotNull(result!["refused"]);
    }

    // Explorer receives the backing path of a redirected one (a junction here, a packaged app's virtualized AppData
    // on the user's PC: Explorer did not see it, answered "Windows cannot find" in a modal box, and launch hung).
    [Fact]
    public void Explorer_receives_the_backing_path_and_other_targets_unchanged()
    {
        using var b = new Bench();
        var exe = b.File(@"cible\Labo Lancement.exe");
        var link = Path.Combine(b.Root, "lien");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, Path.GetDirectoryName(exe)! }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        { mklink.WaitForExit(); Assert.Equal(0, mklink.ExitCode); }
        try
        {
            var target = UserApplication.RealPath(Path.Combine(link, "Labo Lancement.exe"));
            Assert.Equal(Path.GetFullPath(UserApplication.RealPath(exe)), target, ignoreCase: true);
            Assert.Equal(UserApplication.RealPath(Path.GetDirectoryName(exe)!), UserApplication.RealPath(link), ignoreCase: true);
            Assert.DoesNotContain(@"\\?\", target);
        }
        finally { Directory.Delete(link); } // the junction alone, never its target
        foreach (var unchanged in new[] { "", "notepad.exe", "ms-settings:", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Path.Combine(b.Root, "absent.exe") })
            Assert.Equal(unchanged, UserApplication.RealPath(unchanged));
    }
}
