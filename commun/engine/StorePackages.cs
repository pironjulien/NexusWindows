using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Only query state and Resume are exposed. No debugging, suspension, termination or persistent PLM setting.
static unsafe partial class StorePackages
{
    static readonly Guid ClassId = new("b1aec16f-2383-4852-b0e9-8f0b1dc66b4d"), InterfaceId = new("f27c3930-8029-4ad1-94e3-3dba417810c1");
    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "textinputhost", "searchhost", "shellexperiencehost", "startmenuexperiencehost", "shellhost", "lockapp", "explorer", "tabtip", "widgets", "screenclippinghost" };
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint process);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetPackageFullName(nint process, ref uint length, [Out] char[]? name);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    internal static string FullName(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == 0) return "";
        try
        {
            uint length = 0;
            if (GetPackageFullName(handle, ref length, null) != 122 || length == 0) return "";
            var name = new char[length];
            return GetPackageFullName(handle, ref length, name) == 0 ? Win32.Terminated(name) : "";
        }
        finally { CloseHandle(handle); }
    }
    internal static bool HiddenKind(string className, bool visible, bool cloaked, string title, string processName) =>
        className == "Windows.UI.Core.CoreWindow" && visible && cloaked && title.Length > 0
        && title != "DesktopWindowXamlSource" && !ShellProcesses.Contains(Path.GetFileNameWithoutExtension(processName));
    internal static bool IsHiddenStoreWindow(nint window)
    {
        if (Win32.ClassName(window) != "Windows.UI.Core.CoreWindow" || !Win32.IsWindowVisible(window) || !Win32.IsCloaked(window)) return false;
        try
        {
            using var process = Process.GetProcessById((int)Win32.ProcessOf(window));
            return HiddenKind(Win32.ClassName(window), true, true, Win32.Title(window), process.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal sealed class Settings : IDisposable
    {
        nint pointer;
        public Settings() { ComUia.Check(CoCreateInstance(ClassId, 0, 5 /* INPROC_SERVER | LOCAL_SERVER */, InterfaceId, out pointer)); }
        public int State(string package)
        {
            int state;
            fixed (char* name = package)
                ComUia.Check(((delegate* unmanaged[Stdcall]<nint, char*, int*, int>)(*(nint**)pointer)[15])(pointer, name, &state));
            return state;
        }
        public void Resume(string package)
        {
            fixed (char* name = package)
                ComUia.Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)pointer)[6])(pointer, name));
        }
        public void Dispose() { var old = pointer; pointer = 0; ComUia.Release(old); }
    }
    internal static (int Before, int After, bool Requested) ResumeSuspended(Func<int> state, Action resume, Action validate)
    {
        validate(); var before = state();
        if (before is not (2 or 3)) return (before, before, false);
        validate(); resume(); validate();
        return (before, state(), true);
    }
}

static partial class Program
{
    internal static bool StoreApplicationMatches(nint window, int pid, string expectedApp)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Exclusion(process.ProcessName, window) == null && AppId(process).Equals(expectedApp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    static int StoreContentProcess(nint window, string expectedApp)
    {
        var candidates = new HashSet<int>(); var host = Win32.ProcessOf(window);
        // Native parentage, not a title-only association with an unrelated CoreWindow.
        Win32.EnumChildWindows(window, (child, _) =>
        {
            var pid = (int)Win32.ProcessOf(child);
            if (pid != 0 && pid != host && StoreApplicationMatches(window, pid, expectedApp)) candidates.Add(pid);
            return true;
        }, 0);
        return candidates.Count == 1 ? candidates.Single() : 0;
    }
    internal sealed record StoreResume(bool Requested, string Status, int? StateAfter = null);
    static StoreResume ResumeStoreIfNeeded(nint window, int contentPid, string expectedApp)
    {
        var package = StorePackages.FullName(contentPid);
        if (package.Length == 0) return new(false, "not-packaged");
        if (EmergencyStop.RemainingSeconds() > 0) return new(false, "emergency-stop");
        var pid = Win32.ProcessOf(window); var birth = ProcessStartedUtc(pid); var contentBirth = ProcessStartedUtc((uint)contentPid);
        if (birth == null || contentBirth == null) return new(false, "identity-unavailable");
        using (var host = Process.GetProcessById((int)pid))
            if (host.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                && StoreContentProcess(window, expectedApp) != contentPid)
                return new(false, "native-association-unavailable");
        InputStarted = true; // A transport loss can follow Resume; never pretend it was only a query.
        try
        {
            var result = ComBridge.CallData(new ProgramResumeStoreIfNeededJson(op: "resumeStore", windowId: (long)window, expectedWindowPid: pid,
                expectedWindowBirth: birth, contentPid: contentPid, expectedContentBirth: contentBirth, expectedApp: expectedApp, package: package));
            InputStarted = result.GetProperty("inputSent").GetBoolean();
            return new(result.GetProperty("requested").GetBoolean(), "queried", result.GetProperty("stateAfter").GetInt32());
        }
        catch (Exception error) when (error.Data["beforeInput"] is true)
        {
            InputStarted = false;
            if (Win32.ProcessOf(window) != pid || ProcessStartedUtc(pid) != birth
                || ProcessStartedUtc((uint)contentPid) != contentBirth || StorePackages.FullName(contentPid) != package
                || !StoreApplicationMatches(window, contentPid, expectedApp))
                throw new ActionRefusedException("Store identity changed while querying its state; nothing was read or sent");
            // Optional observation support is unavailable. No package was resumed; the real tree is still read.
            return new(false, error is EmergencyStopException ? "emergency-stop" : "unavailable");
        }
    }
    static bool HasForeignContent(nint window, uint host)
    {
        var found = false;
        Win32.EnumChildWindows(window, (child, _) => { if (Win32.ProcessOf(child) is var pid && pid != 0 && pid != host) { found = true; return false; } return true; }, 0);
        return found;
    }
}

static partial class ComWorker
{
    static ComWorkerResumeStoreJson ResumeStore(nint window, JsonElement request)
    {
        var content = request.GetProperty("contentPid").GetInt32();
        var package = request.GetProperty("package").GetString() ?? "";
        void Validate()
        {
            RequireWindowIdentity(window, request);
            if (package.Length == 0 || StorePackages.FullName(content) != package
                || Program.ProcessStartedUtc((uint)content) != request.GetProperty("expectedContentBirth").GetString()
                || !Program.StoreApplicationMatches(window, content, request.GetProperty("expectedApp").GetString() ?? ""))
                throw Before("Store package identity changed; nothing was resumed");
        }
        using var settings = new StorePackages.Settings();
        var result = StorePackages.ResumeSuspended(() => settings.State(package), () => { BeforeAct(); settings.Resume(package); }, Validate);
        return new ComWorkerResumeStoreJson(ok: true, requested: result.Requested, stateBefore: result.Before, stateAfter: result.After,
            inputSent: Acted, technique: "background:package", mechanism: "IPackageDebugSettings.Resume", effect: Acted ? "sent" : "none");
    }
}
