// Offline self-checks of commun/engine/ChromiumReveal.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using static ChromiumReveal;

static partial class ChromiumRevealSelfTests
{
    internal static int SelfTest()
    {
        var checks = 0;
        void Require(bool value, string why) { checks++; if (!value) throw new InvalidOperationException("Chromium reveal selftest: " + why); }
        Require(Refusal(true, false, false, false, true) == null, "a covered, normal, inactive window may be revealed");
        Require(Refusal(false, false, false, false, true) != null, "a hidden window is left alone");
        Require(Refusal(true, true, false, false, true) != null, "a window the user minimized is never restored");
        Require(Refusal(true, false, true, false, false) != null && Refusal(true, false, true, false, true) == null, "the window in front is left alone, unless something covers it (active but hidden: bench, 05/11)");
        Require(Refusal(true, false, false, true, true) != null, "a cloaked window is left alone");
        Require(Refusal(true, false, false, false, false) == null, "a partly covered window that never rendered its page is revealed too (bench, 05/11)");
        Require(Refusal(true, false, false, false, true, browser: false) != null && Browsers.Contains("chrome") && Browsers.Contains("msedge") && !Browsers.Contains("Claude"), "only browsers: an Electron app is never minimized");
        Require(Occludes(0x200100, false, 0, 0), "a Chrome window (no redirection bitmap) covers");
        Require(Occludes(0x280100, true, LWA_ALPHA, 255), "an Electron window layered at full alpha covers (Claude, 05/11)");
        Require(!Occludes(0x280100, true, LWA_ALPHA, 200), "a translucent window does not cover");
        Require(!Occludes(0x280100, true, LWA_COLORKEY, 0), "a colour-keyed window does not cover");
        Require(!Occludes(0x80000, false, 0, 0), "a window drawn by UpdateLayeredWindow does not cover");
        Require(!Occludes(0x0A0800A8, false, 0, 0), "the shell handwriting canvas (click-through tool window) does not cover");
        var maximized = new Win32.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1380 };
        Require(TkWheel.Covered(maximized, [(maximized, 0)]), "a maximized window covers a maximized window, corners included");
        Require(!TkWheel.Covered(maximized, [(maximized, 12)]), "a rounded cover leaves the corners visible");
        return checks;
    }
}
