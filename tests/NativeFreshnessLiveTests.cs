using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public sealed class NativeFreshnessLiveTests
{
    [Fact]
    public void Real_native_streaming_target_and_context_changes_are_checked_before_any_input()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXUS_WINDOWS_FRESHNESS_LIVE") == "1", "Opt-in isolated UIA fixture: NEXUS_WINDOWS_FRESHNESS_LIVE=1.");
        _ = SelfTest.Engine;
        using var fixture = new QuietFixture();
        // Existing STOP self-checks intentionally latch process-wide state. Use the public stdio transport in its own
        // child/test directory; never reset or override that latch to make a desktop test run.
        using var client = new IsolatedMcpClient();
        JsonObject Call(string name, JsonObject args) => client.Call(name, args);
        JsonObject Look()
        {
            var result = Call("look", new JsonObject { ["window"] = fixture.Window.ToInt64(), ["format"] = "json", ["mode"] = "text", ["max_elements"] = 2000 });
            Assert.False(result["isError"].BOrFalse(), result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }
        JsonObject Act(JsonObject seen, int expectedCount) => Call("act", new JsonObject {
            ["observation"] = seen["observation"]!.DeepClone(), ["steps"] = new JsonArray(new JsonObject {
                ["kind"] = "click", ["selector"] = new JsonObject { ["role"] = "button", ["automationId"] = "101" },
                ["fallback"] = false, ["expect"] = new JsonObject { ["selector"] = new JsonObject {
                    ["role"] = "text", ["name"] = "Applied " + expectedCount }, ["exists"] = true } }) });

        var seen = Look(); fixture.Command(1); // New STATIC text only, in the separate streaming pane.
        var streamed = SelfTest.Node(Program.Execute(JsonSerializer.SerializeToElement(new {
            operation = "inspect", windowId = fixture.Window.ToInt64(), app = seen["app"]!.GetValue<string>(), expectedPid = Environment.ProcessId })));
        Assert.True(streamed["elements"]!.AsArray().Count > seen["elements"]!.AsArray().Count, "The real UIA tree must acquire a text node before the action.");
        var accepted = Act(seen, 1);
        Assert.False(accepted["isError"].BOrFalse(), accepted.ToJsonString());
        var outcome = accepted["structuredContent"]!;
        Assert.Equal("verified", outcome["steps"]![0]!["status"].S());
        Assert.False(outcome["steps"]![0]!["foregroundTaken"].BOrFalse());
        Assert.Equal(1, fixture.Clicks);
        foreach (var change in new[] { 2, 3, 4, 5 }) // Reused target with new name; parent; sibling overlay; replacement.
        {
            seen = Look(); fixture.Command(1); fixture.Command(change);
            var refused = Act(seen, 2);
            Assert.True(refused["isError"].BOrFalse(), refused.ToJsonString());
            Assert.Equal(1, fixture.Clicks);
        }
        var final = Probe();
        Assert.NotEqual(fixture.Window.ToInt64(), final["foreground"].L());
        Assert.Equal(0, fixture.Activations);
    }

    static JsonNode Probe() => SelfTest.Node(Program.Execute(JsonSerializer.SerializeToElement(new { operation = "probe" })));

    sealed class IsolatedMcpClient : IDisposable
    {
        readonly string root = Directory.CreateTempSubdirectory("nexuswindows-freshness-").FullName;
        readonly Process process;
        int id;
        public IsolatedMcpClient()
        {
            var info = new ProcessStartInfo(SelfTest.Engine) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--mcp", "--agent", "codex" }) info.ArgumentList.Add(arg);
            info.Environment["NEXUS_SHARED_HOME"] = root;
            info.Environment["NEXUS_WINDOWS_HOME"] = Path.Combine(root, "state");
            info.Environment["NEXUS_JOURNAL_HOME"] = Path.Combine(root, "journal");
            process = Process.Start(info)!;
            process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        }
        public JsonObject Call(string name, JsonObject args)
        {
            process.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id,
                ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args } }.ToJsonString());
            process.StandardInput.Flush();
            var line = process.StandardOutput.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromSeconds(15)) || line.Result == null) throw new TimeoutException("Isolated MCP fixture response timed out.");
            return JsonNode.Parse(line.Result)!["result"]!.AsObject();
        }
        public void Dispose()
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(5000)) throw new TimeoutException("Isolated MCP child did not exit after EOF.");
            process.Dispose(); Directory.Delete(root, recursive: true);
        }
    }

    // Self-owned Win32 windows only. No activation, mouse, keyboard, real application, desktop settings or remote calls.
    sealed class QuietFixture : IDisposable
    {
        const uint CommandMessage = 0x8001;
        readonly Thread thread;
        readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly WindowProc handler;
        readonly string className = "NexusFreshnessFixture-" + Guid.NewGuid().ToString("N");
        nint controls, streaming, button, status, rootWindow;
        int clicks, activations;
        public nint Window { get; }
        public int Clicks => Volatile.Read(ref clicks);
        public int Activations => Volatile.Read(ref activations);

        public QuietFixture()
        {
            handler = Handle;
            thread = new Thread(Run) { IsBackground = true, Name = "Isolated native freshness fixture" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Window = ready.Task.GetAwaiter().GetResult();
        }
        void Run()
        {
            try
            {
                var cls = new WindowClass { Proc = handler, Instance = GetModuleHandleW(null), ClassName = className };
                if (RegisterClassW(ref cls) == 0) throw new InvalidOperationException("Isolated fixture class was not registered.");
                var root = CreateWindowExW(0x08000000 /* no activation; ordinary app window for MCP inventory */, className, "Nexus native freshness fixture", 0x00CF0000, 80, 80, 640, 360, 0, 0, cls.Instance, 0);
                if (root == 0) throw new InvalidOperationException("Isolated fixture window was not created.");
                rootWindow = root;
                controls = Child(root, className, "Controls", 10, 10, 200, 280, 0);
                streaming = Child(root, className, "Streaming", 250, 10, 300, 280, 0);
                button = Child(controls, "BUTTON", "Apply fixture", 10, 20, 150, 30, 101);
                status = Child(controls, "STATIC", "Applied 0", 10, 70, 150, 24, 102);
                _ = Child(streaming, "STATIC", "First token", 10, 20, 180, 24, 201);
                // Show at the bottom in one call; never briefly place the fixture above the user's applications.
                SetWindowPos(root, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, 0x53 /* show, no move, size or activation */);
                ready.SetResult(root);
                while (GetMessageW(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessageW(ref message); }
                UnregisterClassW(className, cls.Instance);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }
        nint Child(nint parent, string cls, string text, int x, int y, int width, int height, int id) =>
            CreateWindowExW(0, cls, text, 0x50000000 /* child, visible */, x, y, width, height, parent, id, GetModuleHandleW(null), 0);
        nint Handle(nint hwnd, uint message, nint wparam, nint lparam)
        {
            if (message == 0x06 && hwnd == rootWindow && (wparam.ToInt64() & 0xffff) != 0) Interlocked.Increment(ref activations);
            if (message == 0x111 && (wparam.ToInt64() & 0xffff) == 101)
            {
                SetWindowTextW(status, "Applied " + Interlocked.Increment(ref clicks)); return 0;
            }
            if (message == CommandMessage)
            {
                switch ((int)wparam)
                {
                    case 1: Child(streaming, "STATIC", "Next token", 10, 50, 180, 24, 202); break;
                    case 2: SetWindowTextW(button, "Different link purpose"); break;
                    case 3: SetWindowTextW(controls, "Different parent context"); break;
                    case 4: Child(hwnd, className, "", 300, 100, 120, 100, 301); break;
                    case 5: DestroyWindow(button); button = Child(controls, "BUTTON", "Different link purpose", 10, 20, 150, 30, 103); break;
                }
                return 0;
            }
            if (message == 0x10) { DestroyWindow(hwnd); PostQuitMessage(0); return 0; }
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        public void Command(int command) => SendMessageW(Window, CommandMessage, command, 0);
        public void Dispose()
        {
            if (thread.IsAlive) { SendMessageW(Window, 0x10, 0, 0); if (!thread.Join(5000)) throw new TimeoutException("The isolated fixture did not close."); }
            GC.KeepAlive(handler);
        }
        delegate nint WindowProc(nint hwnd, uint message, nint wparam, nint lparam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WindowClass { public uint Style; public WindowProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; }
        [StructLayout(LayoutKind.Sequential)] struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WindowClass cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClassW(string cls, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint ex, string cls, string text, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern nint DefWindowProcW(nint window, uint message, nint wparam, nint lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SendMessageW(nint window, uint message, nint wparam, nint lparam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(nint window, string text);
        [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint DispatchMessageW(ref Message message);
    }
}

static class FreshnessJsonExtensions
{
    public static bool BOrFalse(this JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var result) && result;
}
