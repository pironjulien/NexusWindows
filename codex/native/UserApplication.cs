using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class UserApplicationUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

// User applications belong to the interactive Windows shell, not to the MCP worker's process tree/job.
// This is the documented Explorer desktop-view -> IShellDispatch2 broker. A local Shell.Application object or
// Process.Start(UseShellExecute=true) still creates an inherited child: neither is a lifetime boundary.
// https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
// No fallback to an inherited process after an unavailable broker, and no retry after ShellExecute was entered.
static unsafe class UserApplication
{
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    static nint* Table(nint value) => *(nint**)value;
    static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    static void Release(nint value) { if (value != 0) Marshal.Release(value); }
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    // Startup preparation performs no launch. Callers set their unknown-outcome marker only at `started`.
    public static void Start(ProcessStartInfo info, Action started, int show = 1 /* SW_SHOWNORMAL */)
    {
        if (string.IsNullOrWhiteSpace(info.FileName)) throw new UserApplicationUnavailableException("An application target is required; nothing was launched");
        if (info.ArgumentList.Count != 0 || info.RedirectStandardInput || info.RedirectStandardOutput || info.RedirectStandardError)
            throw new UserApplicationUnavailableException("The user-application broker takes an argument string and no redirected protocol handles; nothing was launched");
        object? application;
        try { application = DesktopApplication(); }
        catch (Exception error) { throw new UserApplicationUnavailableException("The interactive Explorer launch broker is unavailable; nothing was launched", error); }
        try
        {
            var target = RealPath(info.FileName); var directory = RealPath(info.WorkingDirectory);
            started();
            ((dynamic)application).ShellExecute(target, info.Arguments, directory, "open", show);
        }
        finally { Release(application); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, char* buffer, uint length, uint flags);

    // The path as Explorer sees it. A worker started by a packaged app (Claude Desktop is MSIX) sees its package's
    // virtualized AppData under the ordinary %LOCALAPPDATA% path; Explorer does not, answers "Windows cannot find"
    // in a modal box and the synchronous ShellExecute never returns. The file system names the backing file.
    // Anything else (URI, shell: name, bare program name, missing path) is passed unchanged.
    internal static string RealPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path) && !Directory.Exists(path)) return path;
        // No access right: only the name is read. FILE_FLAG_BACKUP_SEMANTICS opens a directory as well.
        using var handle = CreateFileW(path, 0, 7 /* read | write | delete */, 0, 3 /* OPEN_EXISTING */, 0x02000000, 0);
        if (handle.IsInvalid) return path;
        var buffer = stackalloc char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, 32768, 0);
        if (length == 0 || length >= 32768) return path;
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    static object DesktopApplication()
    {
        object? windows = null, desktop = null, folder = null;
        nint unknown = 0, provider = 0, browser = 0, view = 0, dispatch = 0;
        try
        {
            var shell = GetShellWindow();
            if (shell == 0 || GetWindowThreadProcessId(shell, out var shellPid) == 0 || shellPid == 0)
                throw new InvalidOperationException("There is no interactive desktop shell");
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            windows = Activator.CreateInstance(type) ?? throw new InvalidOperationException("ShellWindows is unavailable");
            object location = 0 /* CSIDL_DESKTOP */, unused = Type.Missing;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref unused, 8 /* SWC_DESKTOP */, out int desktopHwnd, 1 /* SWFO_NEEDDISPATCH */);
            if (desktop == null || GetWindowThreadProcessId((nint)(uint)desktopHwnd, out var desktopPid) == 0 || desktopPid != shellPid)
                throw new InvalidOperationException("The desktop automation object is not owned by the interactive shell");
            unknown = Marshal.GetIUnknownForObject(desktop);
            var serviceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
            Check(Marshal.QueryInterface(unknown, in serviceProvider, out provider));
            var topBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserInterface = new Guid("000214E2-0000-0000-C000-000000000046");
            // IServiceProvider::QueryService, then IShellBrowser::QueryActiveShellView.
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Table(provider)[3])(provider, &topBrowser, &browserInterface, &browser));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(browser)[15])(browser, &view));
            var dispatchInterface = new Guid("00020400-0000-0000-C000-000000000046");
            // IShellView::GetItemObject(SVGIO_BACKGROUND, IID_IDispatch).
            Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Table(view)[15])(view, 0, &dispatchInterface, &dispatch));
            folder = Marshal.GetObjectForIUnknown(dispatch);
            return ((dynamic)folder).Application ?? throw new InvalidOperationException("The desktop has no Shell dispatch");
        }
        finally
        {
            Release(folder); Release(dispatch); Release(view); Release(browser); Release(provider); Release(unknown);
            Release(desktop); Release(windows);
        }
    }
}
