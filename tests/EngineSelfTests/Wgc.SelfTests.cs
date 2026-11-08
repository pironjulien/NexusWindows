// Offline self-checks of commun/engine/Wgc.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Wgc;

static partial class WgcSelfTests
{
    // Self-test: the self-test's own STATIC window, off every screen (never visible or active), captured by WGC then read
    // back by OCR; 40 captures after that, the process's handles compared (a leak: failure).
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint SendMessageW(nint hwnd, uint msg, nint wparam, nint lparam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(in Msg msg);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint msg, nint wparam, nint lparam);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint Hwnd; public uint Message; public nint W, L; public uint Time; public int X, Y; }

    public static int SelfTest()
    {
        const int width = 640, height = 160;
        nint window = 0;
        uint thread = 0;
        using var ready = new ManualResetEventSlim();
        var pump = new Thread(() =>
        {
            thread = GetCurrentThreadId();
            // Tool window without activation, shown at the VERY BOTTOM of the Z order (under the user's windows: the real
            // case of a covered window); off screen, DWM would not compose it (blank image measured on 27/10).
            var left = 160;
            window = CreateWindowExW(0x08000080 /* NOACTIVATE | TOOLWINDOW */, "STATIC", "Nexus WGC 2026", // text of the STATIC control
                0x80000000 | 0x1 /* POPUP | SS_CENTER */,
                left, 160, width, height, 0, 0, 0, 0);
            SendMessageW(window, 0x0030 /* WM_SETFONT */, CreateFontW(-56, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"), 1);
            SetWindowPos(window, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /* NOSIZE NOMOVE NOACTIVATE SHOWWINDOW */);
            UpdateWindow(window);
            ready.Set();
            while (true)
            {
                while (PeekMessageW(out var m, 0, 0, 0, 1))
                {
                    if (m.Message == 0x0012 /* WM_QUIT */) { DestroyWindow(window); return; }
                    TranslateMessage(m); DispatchMessageW(m);
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        pump.SetApartmentState(ApartmentState.STA);
        pump.Start();
        ready.Wait();
        Thread.Sleep(300); // premiere composition DWM
        var failures = 0;
        try
        {
            var timer = Stopwatch.StartNew();
            var pixels = Window(window, width, height);
            var first = timer.ElapsedMilliseconds;
            var (deviation, _) = Capture.Statistics(pixels, width, height);
            if (deviation < 1.5) { Console.WriteLine($"fail blank capture (deviation {deviation:0.00})"); failures++; }
            else Console.WriteLine($"ok capture {width}x{height} in {first} ms (deviation {deviation:0.0})");
            var (lines, reason, _) = Ocr.Read(pixels, width, height);
            if (reason != null) Console.WriteLine("skip ocr " + reason);
            else if (!lines.Any(l => l.Text.Contains("WGC", StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("fail content: " + string.Join(" | ", lines.Select(l => l.Text))); failures++; }
            else Console.WriteLine("ok content read back by OCR: " + string.Join(" | ", lines.Select(l => l.Text)));
            for (var i = 0; i < 5; i++) Window(window, width, height); // steady state before measuring
            GC.Collect(); GC.WaitForPendingFinalizers();
            var before = Process.GetCurrentProcess().HandleCount;
            timer.Restart();
            for (var i = 0; i < 40; i++) Window(window, width, height);
            var each = timer.ElapsedMilliseconds / 40.0;
            GC.Collect(); GC.WaitForPendingFinalizers();
            var after = Process.GetCurrentProcess().HandleCount;
            if (after - before > 8) { Console.WriteLine($"fail handles {before} -> {after} over 40 captures"); failures++; }
            else Console.WriteLine($"ok handles {before} -> {after} over 40 captures ({each:0.0} ms each)");
        }
        catch (Exception error) { Console.WriteLine($"fail {error.GetType().Name}: {error.Message}"); failures++; }
        finally { PostThreadMessageW(thread, 0x0012, 0, 0); pump.Join(2000); }
        return failures == 0 ? 0 : 1;
    }
}
