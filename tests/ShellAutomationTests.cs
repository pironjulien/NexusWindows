// The shell's automation objects (Shell.Application, ShellWindows, the Start menu's AppsFolder) reached through the
// source-generated IDispatch of ShellDispatch instead of the C# dynamic binder: real objects, read only, nothing is
// launched or navigated.
using System.Runtime.InteropServices;

namespace NexusWindows.Tests;

public partial class ShellAutomationTests
{
    [LibraryImport("user32.dll")] private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] private static partial nint GetDesktopWindow();

    [Fact]
    public void The_start_menu_applications_are_read_by_name_through_the_generated_dispatch_interface()
    {
        var apps = McpServer.ClaudeStartApps();
        Assert.NotEmpty(apps);
        Assert.All(apps, app => { Assert.False(string.IsNullOrWhiteSpace(app.Name)); Assert.False(string.IsNullOrWhiteSpace(app.Id)); });
        // ExtendedProperty(System.AppUserModel.ID) answers for packaged applications: their ids carry the "!App" form.
        Assert.Contains(apps, app => app.Id.Contains('!', StringComparison.Ordinal));
    }

    [Fact]
    public void The_shell_window_inventory_is_read_and_a_window_without_a_folder_view_is_not_a_tab()
    {
        Assert.SkipWhen(GetShellWindow() == 0, "No interactive Explorer shell in this session.");
        // Windows(), Count, Item(i) and each view's HWND are read; the desktop window holds no shell folder view.
        Assert.Throws<BackgroundNotApplicableException>(() => Explorer.SingleTab(GetDesktopWindow()));
        Assert.False(Explorer.Shows(GetDesktopWindow(), (0, 0, 0)));
    }

    [Fact]
    public void The_desktop_broker_finds_the_interactive_shell_dispatch_by_reference_arguments()
    {
        Assert.SkipWhen(GetShellWindow() == 0, "No interactive Explorer shell in this session.");
        // FindWindowSW(ref CSIDL_DESKTOP, ref missing, SWC_DESKTOP, out hwnd, SWFO_NEEDDISPATCH), then the view's
        // Application: the IShellDispatch2 that launches user applications, here only read.
        using var application = UserApplication.DesktopApplication();
        using var windows = application.CallObject("Windows");
        Assert.NotNull(windows);
        Assert.IsType<int>(windows.Get("Count"));
    }
}
