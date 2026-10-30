// Window placement proofs, physical targets, Tk wheel coverage and capture/OCR of the engine, without showing anything
// on the user's screen (formerly window.test.mjs, tkwheel.test.mjs and ocr.test.mjs over --window-selftest,
// --physical-target-selftest, --tk-cover-selftest, --ocr-selftest and --wgc-selftest, plus the --wgc-probe bench tool).
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class WindowTests
{
    static JsonArray Window(string cases) => SelfTest.Json(WindowManagement.SelfTest, cases).AsArray();

    [Fact]
    public void Window_proof_uses_placement_and_state_rather_than_a_changed_image()
    {
        const string before = """{"visible":true,"minimized":false,"maximized":false,"foreground":false,"bottom":false,"left":20,"top":30,"width":600,"height":400}""";
        string Entry(string request, string after)
        {
            var merged = JsonNode.Parse(before)!.AsObject();
            foreach (var (key, value) in JsonNode.Parse(after)!.AsObject()) merged[key] = value!.DeepClone();
            return $$"""{"request":{{request}},"before":{{before}},"after":{{merged.ToJsonString()}}}""";
        }
        var cases = "[" + string.Join(",",
            Entry("""{"action":"move","x":50,"y":60}""", """{"left":50,"top":60}"""),
            Entry("""{"action":"move","x":50,"y":60}""", """{"left":50,"top":60,"width":601}"""),
            Entry("""{"action":"resize","width":800,"height":500}""", """{"width":800,"height":500}"""),
            Entry("""{"action":"resize","width":800,"height":500}""", """{"left":21,"width":800,"height":500}"""),
            Entry("""{"action":"restore"}""", """{"minimized":true}"""), Entry("""{"action":"maximize"}""", """{"maximized":true}"""),
            Entry("""{"action":"front","foreground":true}""", """{"foreground":false}"""), Entry("""{"action":"front"}""", "{}")) + "]";
        var results = Window(cases);
        Assert.Equal([true, false, true, false, false, true, false], results.Take(7).Select(r => r!["reached"].B()));
        Assert.Matches("foreground:true", results[7]!["error"].S());
    }

    [Fact]
    public void Semantic_wheel_accepts_fresh_relocation_and_drag_remains_refused()
    {
        var results = SelfTest.Json(Physical.TargetSelfTest, new[] { "click", "move", "wheel", "drag", "key", "type" });
        SelfTest.Same("[true,true,true,false,false,false]", results);
    }

    [Fact]
    public void Bottom_permits_only_public_shell_identities_and_follows_their_entire_chain()
    {
        static string Entry(string chain, string extra = "") => $$"""{"target":1,"shell":2,"desktop":3,"chain":{{chain}}{{extra}}}""";
        var cases = "[" + string.Join(",",
            Entry("""{"1":0}"""), Entry("""{"1":2,"2":0}"""), Entry("""{"1":3,"3":0}"""), Entry("""{"1":2,"2":3,"3":0}"""),
            Entry("""{"1":4,"4":0}""", ""","visible":false"""),
            Entry("""{"1":4,"4":0}""", ""","className":"Progman","process":"explorer.exe" """),
            Entry("""{"1":2,"2":4,"4":0}"""),
            Entry("""{"1":2,"2":3,"3":2}"""), Entry("""{"1":1}"""),
            Entry("""{"1":4}""").Replace("\"shell\":2,\"desktop\":3", "\"shell\":0,\"desktop\":0")) + "]";
        Assert.Equal([true, true, true, true, false, false, false, false, false, false], Window(cases).Select(r => r!["bottom"].B()));
    }

    [Fact]
    public void Tk_coverage_exact_a_one_pixel_strip_refuses_corners_follow_the_monitor_scale()
    {
        var lines = SelfTest.Lines(TkWheel.SelfTest);
        Assert.Equal(5, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("ok ", line));
    }

    [Fact]
    public void Real_ocr_engine_on_an_in_memory_image_text_and_rectangle()
    {
        Assert.All(SelfTest.Lines(Ocr.SelfTest), line => Assert.StartsWith("ok ", line));
    }

    [Fact]
    public void Wgc_capture_of_a_covered_window_read_back_by_ocr_without_a_lost_handle()
    {
        // A real Windows.Graphics.Capture session needs an interactive desktop with a GPU: run by test.ps1 -Full, as
        // before (hosted CI runners have none).
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXUS_TESTS_FULL") == "1", "Real capture session: test.ps1 -Full.");
        var lines = SelfTest.Lines(Wgc.SelfTest);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("ok ", line));
    }

    [Fact]
    public void Wgc_probe_of_a_given_bench_window()
    {
        // Bench tool: probes the window named by NEXUS_LIVE_HWND (a test window opened by the bench), never another one.
        var hwnd = Environment.GetEnvironmentVariable("NEXUS_LIVE_HWND");
        Assert.SkipWhen(!long.TryParse(hwnd, out var handle), "NEXUS_LIVE_HWND (a bench window) not set.");
        var (code, output) = SelfTest.Text(() => Wgc.Probe((nint)handle));
        Assert.True(code == 0, output);
    }
}
