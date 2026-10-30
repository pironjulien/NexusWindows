using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;

namespace NexusWindows.Tests;

// Real, hidden Win32 controls: no focus, visible window or input on the user's desktop.
[Collection("Engine")]
public class ClassicComboTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);

    sealed class ComboFixture : IDisposable
    {
        readonly nint root = CreateWindowExW(0, "STATIC", "NexusWindows hidden combo test", 0, 0, 0, 200, 100, 0, 0, 0, 0);
        public readonly nint Window;
        public ComboFixture(bool editable = true)
        {
            Assert.NotEqual(0, root);
            Window = CreateWindowExW(0, "COMBOBOX", "", 0x40000000u | (editable ? 0x42u : 3u), 0, 0, 180, 80, root, 0, 0, 0);
            Assert.NotEqual(0, Window);
        }
        public Node Read(string? cachedValue)
        {
            using var tree = new UiTree();
            var combo = tree.Add(new Node { Role = "combobox", Name = "File name", AutomationId = "", Description = "", Bounds = [], FrameworkId = "Win32", NativeWindow = Window,
                Value = cachedValue, RuntimeId = [42], Ancestors = [] });
            ComUia.SynthesizeCombo(tree, combo);
            return combo;
        }
        public void WriteSyntheticEdit(string value)
        {
            using var tree = new UiTree();
            var combo = tree.Add(new Node { Role = "combobox", Name = "File name", AutomationId = "", Description = "", Bounds = [], FrameworkId = "Win32", NativeWindow = Window,
                Value = "", RuntimeId = [42], Ancestors = [] });
            ComUia.SynthesizeCombo(tree, combo);
            var field = Assert.Single(tree.Nodes, n => n.Role == "edit");
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { value }));
            var result = (Outcome)typeof(Program).GetMethod("BackgroundAction", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, ["setValue", field, tree.Nodes, root, request.RootElement])!;
            Assert.Equal("background:messages", result.Technique);
            Assert.Equal("applied", result.Effect);
        }
        public void SetEdit(string value)
        {
            var edit = Win32.FindWindowExW(Window, 0, "Edit", null);
            Assert.NotEqual(0, edit);
            Assert.NotNull(Win32.SendText(edit, 0x000C /* WM_SETTEXT */, 0, value));
        }
        public void Dispose() => DestroyWindow(root);
    }

    [Theory]
    [InlineData(null, "C:\\packages\\extension.zip")]
    [InlineData("", "C:\\packages\\extension.zip")]
    [InlineData("previous option", "C:\\packages\\extension.zip")]
    [InlineData("previous option", "")]
    public void Editable_combo_value_is_the_current_edit_text_including_empty_text(string? cached, string current)
    {
        using var fixture = new ComboFixture();
        fixture.SetEdit(current);
        Assert.Equal(current, fixture.Read(cached).Value);
    }

    [Fact]
    public void Selection_only_combo_keeps_reading_the_selected_option()
    {
        using var fixture = new ComboFixture(editable: false);
        Assert.NotNull(Win32.SendText(fixture.Window, 0x0143 /* CB_ADDSTRING */, 0, "Selected option"));
        Assert.NotNull(Win32.Send(fixture.Window, Win32.CB_SETCURSEL, 0));
        Assert.Equal("Selected option", fixture.Read("").Value);
    }

    [Fact]
    public void Synthetic_edit_remains_writable_by_background_messages()
    {
        using var fixture = new ComboFixture();
        fixture.WriteSyntheticEdit("C:\\packages\\extension.zip");
        Assert.Equal("C:\\packages\\extension.zip", fixture.Read("").Value);
    }

    [Fact]
    public void Empty_live_edit_text_is_not_replaced_by_a_previous_selected_option()
    {
        using var fixture = new ComboFixture();
        Assert.NotNull(Win32.SendText(fixture.Window, 0x0143 /* CB_ADDSTRING */, 0, "Selected option"));
        Assert.NotNull(Win32.Send(fixture.Window, Win32.CB_SETCURSEL, 0));
        fixture.SetEdit("");
        Assert.Equal((nint)0, Win32.Send(fixture.Window, Win32.CB_GETCURSEL));
        Assert.Equal("", fixture.Read("Selected option").Value);
    }
}
