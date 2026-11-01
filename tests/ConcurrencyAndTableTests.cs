// Window lock between agents (two real processes), full tree reads in turn, the UIA gesture chosen under a point where posted clicks are ignored,
// and table reading from an observed tree. No window is shown and no input is sent.
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class ConcurrencyAndTableTests
{
    // Three engine processes in a home of their own (an emergency stop latched by another test never applies to them).
    [Fact]
    public void A_second_agent_waits_then_is_refused_and_gets_the_window_once_the_holder_dies()
    {
        const string window = "2117533697";
        var home = Path.Combine(Path.GetTempPath(), "nexuswindows-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Process Start(string agent, int waitMs)
        {
            var start = new ProcessStartInfo(SelfTest.Engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--window-lock", window, agent, waitMs.ToString() }) start.ArgumentList.Add(arg);
            start.Environment["NEXUS_SHARED_HOME"] = home;
            return Process.Start(start)!;
        }
        using var holder = Start("codex", 0);
        try
        {
            Assert.Contains("\"ok\":true", holder.StandardOutput.ReadLine());
            var clock = Stopwatch.StartNew();
            using (var second = Start("claude", 600))
            {
                var refusal = second.StandardOutput.ReadLine()!;
                Assert.True(second.WaitForExit(10_000));
                Assert.True(clock.ElapsedMilliseconds >= 550, "waits before refusing");
                Assert.Equal(1, second.ExitCode);
                Assert.Contains("\"ok\":false", refusal);
                Assert.Contains("Window busy: codex (pid " + holder.Id, refusal);
            }
            // The holder dies without releasing: the OS frees the lock and the next agent gets it at once.
            holder.Kill();
            Assert.True(holder.WaitForExit(10_000));
            using var third = Start("antigravity", 2000);
            Assert.Contains("\"ok\":true", third.StandardOutput.ReadLine());
            third.StandardInput.Close();
            Assert.True(third.WaitForExit(10_000));
            Assert.Equal(0, third.ExitCode);
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }

    // Full tree reads in turn: a second reader waits while the first holds the gate, then goes ahead; a reader that waited
    // too long reads anyway, ungated. Two threads, two handles on the same lock file in the tests' own shared home.
    [Fact]
    public void Full_tree_reads_wait_their_turn_then_go_ahead_ungated_after_the_bound()
    {
        var first = UiaReadGate.Enter();
        Assert.True(first.Gated);
        var clock = Stopwatch.StartNew();
        var late = Task.Run(() => { using var gate = UiaReadGate.Enter(300); return (gate.Gated, clock.ElapsedMilliseconds); }).Result;
        Assert.False(late.Gated); Assert.True(late.ElapsedMilliseconds >= 280, "waits up to its bound");
        var turn = Task.Run(() => { using var gate = UiaReadGate.Enter(5000); return (gate.Gated, clock.ElapsedMilliseconds); });
        Thread.Sleep(200);
        Assert.False(turn.IsCompleted, "waits while the gate is held");
        first.Dispose();
        Assert.True(turn.Wait(2000) && turn.Result.Gated, "gets the gate once released");
        using (var again = UiaReadGate.Enter()) using (var nested = UiaReadGate.Enter()) Assert.Same(again, nested);
    }

    static JsonObject Element(int index, string role, string name = "", string? value = null, int[]? ancestors = null, bool invoke = false, bool? toggled = null, bool? selected = null, bool disabled = false) => new()
    {
        ["index"] = index, ["role"] = role, ["name"] = name, ["value"] = value, ["visible"] = true, ["disabled"] = disabled,
        ["invoke"] = invoke, ["checked"] = toggled, ["selected"] = selected,
        ["ancestors"] = new JsonArray((ancestors ?? []).Select(a => (JsonNode)a).ToArray()),
    };

    [Fact]
    public void The_point_gesture_is_the_single_enabled_actionable_element_under_the_point()
    {
        var pane = Element(0, "pane"); var button = Element(1, "button", "OK", invoke: true); var label = Element(2, "text", "OK");
        Assert.Same(button, McpServer.PointGesture([pane, button, label]));
        Assert.Same(button, McpServer.PointGesture([pane, Element(3, "checkbox", "Off", toggled: true, disabled: true), button]));
        Assert.Null(McpServer.PointGesture([pane, label]));
        // A list item holding a check box: two gestures under the point, ambiguous, so the point stays a point.
        Assert.Null(McpServer.PointGesture([Element(4, "listitem", "Row", selected: false), Element(5, "checkbox", "Done", toggled: false)]));
        Assert.Null(McpServer.PointGesture([Element(6, "window", "App", invoke: true)]));
    }

    [Fact]
    public void A_table_without_grid_reads_headers_then_rows_from_the_tree()
    {
        var all = new List<JsonObject>
        {
            Element(0, "window", "Task Manager"),
            Element(1, "list", "Processes", ancestors: [0]),
            Element(2, "header", "", ancestors: [0, 1]),
            Element(3, "headeritem", "Name", ancestors: [0, 1, 2]),
            Element(4, "headeritem", "CPU", ancestors: [0, 1, 2]),
            Element(5, "dataitem", "Explorer", ancestors: [0, 1]),
            Element(6, "text", "Explorer", ancestors: [0, 1, 5]),
            Element(7, "edit", "CPU", value: "0,4 %", ancestors: [0, 1, 5]),
            Element(8, "dataitem", "Claude", ancestors: [0, 1]),
            Element(9, "edit", "CPU", value: "2 | 3 %", ancestors: [0, 1, 8]),
            Element(10, "listitem", "Outside", ancestors: [0]),
        };
        var table = McpServer.TreeTable(all, 1, 10);
        Assert.Equal("tree", table["source"]!.GetValue<string>());
        Assert.Equal(2, table["rowCount"]!.GetValue<int>());
        Assert.Equal(["headers: Name | CPU", "1. Explorer | 0,4 %", "2. Claude | 2 / 3 %"], McpServer.TableLines(table));
        Assert.Single(McpServer.TreeTable(all, 1, 1)["rows"]!.AsArray());
        Assert.Equal(["No row."], McpServer.TableLines(McpServer.TreeTable(all, 10, 10)));
    }

    // The tree a WinForms DataGridView exposes (read on 31/10): no GridPattern, header cells in a top row, one data item
    // per cell inside each row, a row header cell in each row. Explorer dates carry invisible bidirectional marks.
    [Fact]
    public void A_datagridview_tree_reads_one_row_per_row_container_with_its_column_headers()
    {
        var all = new List<JsonObject>
        {
            Element(2, "table", "Commandes"),
            Element(3, "custom", "Top row", ancestors: [2]),
            Element(4, "header", "Top left header cell", ancestors: [2, 3]),
            Element(5, "header", "Produit", value: "Produit", ancestors: [2, 3]),
            Element(6, "header", "Prix", value: "Prix", ancestors: [2, 3]),
            Element(8, "custom", "Row 0", ancestors: [2]),
            Element(9, "header", "Row 0", ancestors: [2, 8]),
            Element(10, "dataitem", "Produit Row 0", value: "Clavier", ancestors: [2, 8]),
            Element(11, "dataitem", "Prix Row 0", value: "49,90", ancestors: [2, 8]),
            Element(13, "custom", "Row 1", ancestors: [2]),
            Element(14, "header", "Row 1", ancestors: [2, 13]),
            Element(15, "dataitem", "Produit Row 1", value: "Souris", ancestors: [2, 13]),
            Element(16, "dataitem", "Prix Row 1", value: "‎31/‎10/‎2026", ancestors: [2, 13]),
        };
        Assert.Equal(["headers: Produit | Prix", "1. Clavier | 49,90", "2. Souris | 31/10/2026"], McpServer.TableLines(McpServer.TreeTable(all, 2, 10)));
    }
}
