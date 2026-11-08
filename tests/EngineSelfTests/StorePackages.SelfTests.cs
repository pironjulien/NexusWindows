// Offline self-checks of commun/engine/StorePackages.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static StorePackages;

static partial class StorePackagesSelfTests
{
    public static int SelfTest()
    {
        var checks = 0;
        void Assert(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        foreach (var state in new[] { 0, 1, 2, 3, 4 })
        {
            var calls = 0; var current = state;
            var result = ResumeSuspended(() => current, () => { calls++; current = 1; }, () => { });
            Assert(calls == (state is 2 or 3 ? 1 : 0), "Only suspending/suspended packages may be resumed");
            Assert(result.Requested == (calls == 1) && result.Before == state && result.After == current, "State read back honestly");
        }
        var attempts = 0; var threw = false;
        try { ResumeSuspended(() => 3, () => { attempts++; throw new IOException("reply lost"); }, () => { }); }
        catch (IOException) { threw = true; }
        Assert(threw && attempts == 1, "An uncertain Resume is never retried");
        var validations = 0; attempts = 0;
        try { ResumeSuspended(() => 3, () => attempts++, () => { if (++validations == 2) throw new ActionRefusedException("process changed"); }); }
        catch (ActionRefusedException) { }
        Assert(attempts == 0, "Identity change before Resume sends nothing");
        Assert(HiddenKind("Windows.UI.Core.CoreWindow", true, true, "Calculator", "CalculatorApp"), "Hidden application CoreWindow retained");
        Assert(!HiddenKind("Windows.UI.Core.CoreWindow", true, true, "Start", "StartMenuExperienceHost.exe"), "Shell panels excluded");
        Assert(!HiddenKind("Windows.UI.Core.CoreWindow", true, true, "DesktopWindowXamlSource", "App"), "Xaml island excluded");
        Assert(!HiddenKind("Chrome_WidgetWin_1", true, true, "App", "App"), "Other cloaked windows excluded");
        Assert(!HiddenKind("Windows.UI.Core.CoreWindow", false, true, "App", "App"), "Unshown transient windows excluded");
        Console.WriteLine(EngineTestJson.Serialize(new ComWorkerVirtualizationSelfTestJson(ok: true, checks: checks))); return 0;
    }
}
