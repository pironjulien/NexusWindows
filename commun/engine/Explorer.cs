using System.IO;
using System.Runtime.InteropServices;

// Navigation of a File Explorer window to a folder, in the background, through the Windows shell (ShellWindows,
// Navigate2): neither keyboard nor foreground. Pass 2 of 22/10: Ctrl+L had no focus in the background, and a foreground
// given back between two reads lost the address bar input.
// Guards (Codex's review of 23/10): a COMPLETE inventory of the shell views (one unreadable view: refused, uniqueness
// would not be proven); a window with ONE tab only (otherwise the targeted tab would be guessed, and it may be the
// user's); view/HWND binding read again just before sending and at each arrival read; folders compared by their identity
// on disk (volume + file id), never by a normalized case (case-sensitive directories).
static partial class Explorer
{
    public static object Navigate(nint hwnd, string path, Action inputStarted)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ActionRefusedException("The folder must be an existing absolute path; nothing was sent");
        var wanted = Identity(path) ?? throw new ActionRefusedException("The folder identity cannot be read; nothing was sent");
        using var tab = SingleTab(hwnd);
        if (Current(tab) is { } before && Identity(before) == wanted)
        {
            // Binding read again AFTER the reads: the success really concerns the bound window (Codex's review).
            if (!Attached(tab, hwnd)) throw new ActionRefusedException("The Explorer view changed while reading it; nothing was sent");
            return new ExplorerNavigateJson(ok: true, technique: "background:shell", mechanism: "Navigate2", effect: "already", path: (string?)before, note: (string?)null);
        }
        // Binding read again just before sending: same view, still the only view of THIS window; then STOP read LAST,
        // as the COM inventory may wait (Codex's review).
        using (var again = SingleTab(hwnd))
            if (!again.SameObject(tab)) throw new ActionRefusedException("The Explorer view changed before input; nothing was sent");
        // The inventory may have waited after reading this HWND: the view's binding is read again last (Codex's review).
        if (!Attached(tab, hwnd)) throw new ActionRefusedException("The Explorer view changed before input; nothing was sent");
        EmergencyStop.ThrowIfStopped();
        inputStarted();
        tab.Call("Navigate2", path);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 4000)
        {
            var shown = Current(tab);
            var arrived = shown is not null && Identity(shown) == wanted;
            // Binding read again AFTER the reads the success rests on: lost, unknown outcome, never replayed.
            if (!Attached(tab, hwnd))
                return new ExplorerNavigateJson(ok: true, technique: "background:shell", mechanism: "Navigate2", effect: "sent", path: (string?)null, note: (string?)"view no longer attached to the bound window");
            if (arrived)
                return new ExplorerNavigateJson(ok: true, technique: "background:shell", mechanism: "Navigate2", effect: "verified", path: shown, note: (string?)null);
            Thread.Sleep(50);
        }
        return new ExplorerNavigateJson(ok: true, technique: "background:shell", mechanism: "Navigate2", effect: "sent", path: Current(tab), note: (string?)null);
    }

    // Does the window (a single tab, bound to it) show this folder? Proof of a folder launch (Program.Launch), read only;
    // an unreadable view, several tabs or an unreadable folder: no (never an assumed proof).
    internal static bool Shows(nint hwnd, (ulong Volume, ulong Low, ulong High) wanted)
    {
        try
        {
            using var tab = SingleTab(hwnd);
            return Current(tab) is { } shown && Identity(shown) == wanted && Attached(tab, hwnd);
        }
        catch (Exception) { return false; }
    }

    // Every view of the shell (Shell.Application.Windows()), read by index; the caller owns the returned tab.
    internal static ShellDispatch SingleTab(nint hwnd)
    {
        using var shell = ShellDispatch.CreateShellApplication();
        using var windows = shell.CallObject("Windows") ?? throw new InvalidOperationException("Shell.Application has no window list");
        var tabs = new List<ShellDispatch>();
        try
        {
            var count = Convert.ToInt32(windows.Get("Count"), System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                ShellDispatch? view = null;
                try { view = windows.CallObject("Item", i); } catch (Exception error) when (error is COMException or InvalidCastException) { }
                var handle = (view is null ? null : HwndOf(view)) ?? throw new ActionRefusedException("A shell view could not be read: the tabs of this window cannot be counted; nothing was sent");
                if (handle == (long)hwnd) { tabs.Add(view!); view = null; }
                view?.Dispose();
            }
            if (tabs.Count == 0) throw new BackgroundNotApplicableException("This window is not a shell folder window");
            if (tabs.Count > 1) throw new ActionRefusedException($"This Explorer window holds {tabs.Count} tabs: the tab to navigate would be guessed; nothing was sent");
            var single = tabs[0]; tabs.Clear();
            return single;
        }
        finally { foreach (var tab in tabs) tab.Dispose(); }
    }

    static bool Attached(ShellDispatch view, nint hwnd) => HwndOf(view) == (long)hwnd;

    static long? HwndOf(ShellDispatch view)
    {
        try { return view.Get("HWND") switch { long value => value, int value => value, uint value => value, _ => null }; } catch { return null; }
    }

    static string? Current(ShellDispatch tab)
    {
        try
        {
            using var document = tab.GetObject("Document");
            using var folder = document?.GetObject("Folder");
            using var self = folder?.GetObject("Self");
            return self?.Get("Path") as string;
        }
        catch { return null; }
    }

    // Identity of a folder on disk: FILE_ID_INFO (64-bit volume + 128-bit file id), unique even on ReFS, where the 64-bit
    // index of BY_HANDLE_FILE_INFORMATION is not (Codex's review). Unreadable: null, and the caller refuses (fails
    // closed).
    internal static (ulong Volume, ulong Low, ulong High)? Identity(string path)
    {
        using var handle = CreateFile(path, 0, 7, 0, 3, 0x02000000, 0); // OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var info, (uint)Marshal.SizeOf<FileIdInfo>())) return null; // FileIdInfo
        return (info.VolumeSerialNumber, info.IdLow, info.IdHigh);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow, IdHigh; // FILE_ID_128
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
}
