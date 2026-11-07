// The shared emergency stop and foreground token protocols (commun/emergency-stop.md, commun/foreground-token.md),
// checked on real engine processes (formerly the real-helper cases of stop.test.mjs and foreground.test.mjs, run by
// the retired Node adapter of the Codex profile). The stop cases use a home of their own (NEXUS_SHARED_HOME), never the
// real %USERPROFILE%\.nexus\stop, and the guard there never takes the real hotkey. The foreground token is the PC's
// real lock (%USERPROFILE%\.nexus\foreground.lock): those cases run with test.ps1 -Full only, since they hold it.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class SharedProtocolProcessTests
{
    static readonly UTF8Encoding Utf8 = new(false);

    static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "nexuswindows-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    static void DeleteHome(string home)
    {
        try { Directory.Delete(home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>One engine process with its stdout read line by line in the background.</summary>
    sealed class Engine : IDisposable
    {
        readonly Process process;
        readonly BlockingCollection<string> lines = new();
        public Engine(string home, params string[] args)
        {
            var start = new ProcessStartInfo(SelfTest.Engine)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["NEXUS_SHARED_HOME"] = home;
            process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            _ = Task.Run(() =>
            {
                try { for (string? line; (line = process.StandardOutput.ReadLine()) != null;) lines.Add(line); }
                catch (IOException) { }
                finally { lines.CompleteAdding(); }
            });
        }
        public int Id => process.Id;
        public string? Line(int timeoutMs) => lines.TryTake(out var line, timeoutMs) ? line : null;
        public JsonNode NextJson(int timeoutMs = 10_000) =>
            JsonNode.Parse(Line(timeoutMs) ?? throw new TimeoutException("no line from the engine")) ?? throw new InvalidDataException("empty line");
        public JsonNode WaitEvent(string name, int timeoutMs = 8000)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
                if (Line((int)Math.Max(1, timeoutMs - clock.ElapsedMilliseconds)) is { } line && JsonNode.Parse(line) is { } json && json["event"]?.GetValue<string>() == name)
                    return json;
            throw new TimeoutException("guard event missing: " + name);
        }
        public void Send(string text) { process.StandardInput.Write(text); process.StandardInput.Flush(); }
        public void CloseInput() => process.StandardInput.Close();
        public int Exit(int timeoutMs = 20_000)
        {
            Assert.True(process.WaitForExit(timeoutMs), "engine still running");
            return process.ExitCode;
        }
        public void Kill() { if (!process.HasExited) process.Kill(); process.WaitForExit(10_000); }
        public void Dispose() { Kill(); process.Dispose(); }
    }

    /// <summary>One request on standard input (or a mode with no input), then the last JSON line printed.</summary>
    static (int Code, JsonNode Reply) Run(string home, string input, params string[] args)
    {
        using var engine = new Engine(home, args);
        engine.Send(input);
        engine.CloseInput();
        string? last = null;
        for (string? line; (line = engine.Line(20_000)) != null;) last = line;
        var code = engine.Exit();
        return (code, JsonNode.Parse(last ?? throw new InvalidDataException("no reply")) ?? throw new InvalidDataException("empty reply"));
    }

    static void Press(string home) => File.WriteAllText(Path.Combine(home, "stop"), JsonSerializer.Serialize(new
    {
        by = "Test", pid = Environment.ProcessId, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, hold_s = 120,
        nonce = Guid.NewGuid().ToString("N")[..12],
    }));

    const string Stopped = "Arrêt d'urgence demandé par Julien";
    static readonly string Click = JsonSerializer.Serialize(new { operation = "click", windowId = 0, app = "none", selector = new { name = "x" }, runtimeId = Array.Empty<int>() });

    [Fact]
    public void A_recent_stop_refuses_the_foreground_token_and_UIA_actions_and_an_old_stop_is_ignored()
    {
        var home = NewHome();
        try
        {
            Press(home);
            var (code, token) = Run(home, "", "--foreground-token", "stop test");
            Assert.Equal(1, code);
            Assert.Equal("EmergencyStopException", token["error"]!.GetValue<string>());
            Assert.Contains(Stopped, token["message"]!.GetValue<string>());
            Assert.Matches(@"plus aucune action pendant encore \d+ s ; ne pas contourner", token["message"]!.GetValue<string>());

            var (_, action) = Run(home, Click);
            Assert.Equal("EmergencyStopException", action["error"]!.GetValue<string>());

            // A stop older than its hold time is ignored: the refusal then comes from the missing window, not the stop.
            File.SetLastWriteTimeUtc(Path.Combine(home, "stop"), DateTime.UtcNow.AddSeconds(-(120 + 30)));
            var (_, after) = Run(home, Click);
            Assert.NotEqual("EmergencyStopException", after["error"]?.GetValue<string>());
        }
        finally { DeleteHome(home); }
    }

    [Fact]
    public void A_guard_in_a_test_home_leaves_the_hotkey_alone_and_writes_the_protocol_file_on_a_press()
    {
        var home = NewHome();
        try
        {
            using (var guard = new Engine(home, "--stop-guard", "test"))
            {
                Assert.Equal("disabled", guard.WaitEvent("hotkey")["state"]!.GetValue<string>());
                guard.Send("press\n");
                var stop = guard.WaitEvent("stop");
                Assert.True(stop["written"]!.GetValue<bool>());
                var written = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "stop")))!;
                Assert.Equal("NexusCodex", written["by"]!.GetValue<string>());
                Assert.Equal(120, written["hold_s"]!.GetValue<double>());
                Assert.True(written["pid"]!.GetValue<int>() > 0 && written["at"]!.GetValue<double>() > 0);
                Assert.Matches(new Regex("^[0-9a-f]{12}$"), written["nonce"]!.GetValue<string>());

                // Another engine process of the same home sees the stop at once.
                var (code, token) = Run(home, "", "--foreground-token", "after press");
                Assert.Equal(1, code);
                Assert.Equal("EmergencyStopException", token["error"]!.GetValue<string>());

                guard.CloseInput();
                Assert.Equal(0, guard.Exit());
            }
        }
        finally { DeleteHome(home); }
    }

    static string LockPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "foreground.lock");

    static string ReadLock()
    {
        using var stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_real_foreground_token_excludes_another_process_defers_it_and_is_free_after_release()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXUS_TESTS_FULL") == "1", "Real shared foreground token: test.ps1 -Full.");
        var home = NewHome();
        try
        {
            using var holder = new Engine(home, "--foreground-token", "lease integration test");
            Assert.True(holder.NextJson()["ok"]!.GetValue<bool>());
            var diagnostic = JsonNode.Parse(ReadLock())!;
            Assert.Equal("NexusCodex", diagnostic["owner"]!.GetValue<string>());
            Assert.Equal("lease integration test", diagnostic["purpose"]!.GetValue<string>());
            Assert.Equal(holder.Id, diagnostic["pid"]!.GetValue<int>());
            Assert.True(diagnostic["since"]!.GetValue<long>() > 0);

            // A second process (stdin kept open: EOF would abandon its request) waits up to 8 s, then defers and names
            // the holder; it never got the token.
            JsonNode deferred;
            using (var second = new Engine(home, "--foreground-token", "must defer"))
            {
                var clock = Stopwatch.StartNew();
                deferred = second.NextJson(15_000);
                Assert.True(clock.ElapsedMilliseconds >= 7000, "deferred before the 8 s wait");
                Assert.Equal(1, second.Exit());
            }
            Assert.False(deferred["ok"]!.GetValue<bool>());
            Assert.True(deferred["message"]!.GetValue<string>().Contains("lease integration test"), deferred.ToJsonString());

            holder.CloseInput();
            Assert.True(holder.NextJson()["released"]!.GetValue<bool>());
            Assert.Equal(0, holder.Exit());
            Assert.Equal("", ReadLock());

            using var next = new Engine(home, "--foreground-token", "reacquire after release");
            Assert.True(next.NextJson()["ok"]!.GetValue<bool>());
            next.CloseInput();
            Assert.Equal(0, next.Exit());
            Assert.Equal("", ReadLock());
        }
        finally { DeleteHome(home); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_waiter_gets_the_real_foreground_token_when_the_holder_releases_it_or_dies(bool kill)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXUS_TESTS_FULL") == "1", "Real shared foreground token: test.ps1 -Full.");
        var home = NewHome();
        try
        {
            using var holder = new Engine(home, "--foreground-token", "crash test");
            Assert.True(holder.NextJson()["ok"]!.GetValue<bool>());
            using var waiter = new Engine(home, "--foreground-token", "wait test");
            Assert.Null(waiter.Line(300));
            if (kill) holder.Kill(); else holder.CloseInput();
            Assert.True(waiter.NextJson(8000)["ok"]!.GetValue<bool>());
            waiter.CloseInput();
            Assert.Equal(0, waiter.Exit());
        }
        finally { DeleteHome(home); }
    }
}
