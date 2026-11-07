// Win32 calls that used runtime marshalling (a structure carrying a delegate or an inline string), now blittable and
// source-generated for trimming and Native AOT: the process snapshot and the presence window's registration.
using System.Runtime.InteropServices;

namespace NexusWindows.Tests;

public partial class BlittableInteropTests
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowExW(nint parent, nint after, string? cls, string? title);

    [Fact]
    public void The_process_snapshot_reads_each_executable_name_from_its_inline_buffer()
    {
        var rows = Steam.Table();
        var self = Assert.Single(rows, row => row.Pid == (uint)Environment.ProcessId);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), self.Name, ignoreCase: true);
        Assert.NotEqual(0u, self.Parent);
        Assert.Contains(rows, row => row.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || row.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_presence_window_registers_its_class_with_an_unmanaged_procedure_and_is_message_only()
    {
        // A message-only window (HWND_MESSAGE): never shown. CreateWindowExW sends its creation messages through the
        // UnmanagedCallersOnly procedure, so the window exists only if that procedure answered them.
        Presence.Start(_ => { });
        var window = (nint)0;
        for (var i = 0; i < 100 && window == 0; i++)
        {
            window = FindWindowExW(-3 /* HWND_MESSAGE */, 0, "NexusCodexPresence", null);
            if (window == 0) await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.NotEqual(0, window);
    }
}
