// Offline self-checks of commun/engine/Ocr.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Ocr;

static partial class OcrSelfTests
{
    // Offline self-test: text drawn by GDI into an in-memory image, read back by the real engine; nothing on screen.
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOutW(nint dc, int x, int y, string text, int length);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PatBlt(nint dc, int x, int y, int w, int h, uint rop);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);

    public static int SelfTest()
    {
        const int width = 900, height = 200;
        const string expected = "NexusWindows OCR 2026";
        var screen = Win32.GetDC(0);
        var dc = Win32.CreateCompatibleDC(screen);
        _ = Win32.ReleaseDC(0, screen);
        var header = new Win32.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dib = Win32.CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
        var old = Win32.SelectObject(dc, dib);
        var font = CreateFontW(-48, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
        var oldFont = Win32.SelectObject(dc, font);
        PatBlt(dc, 0, 0, width, height, 0x00FF0062 /* WHITENESS */);
        _ = SetBkMode(dc, 1);
        TextOutW(dc, 60, 70, expected, expected.Length);
        Win32.GdiFlush();
        var pixels = new byte[width * height * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);
        Win32.SelectObject(dc, oldFont); Win32.DeleteObject(font);
        Win32.SelectObject(dc, old); Win32.DeleteObject(dib); Win32.DeleteDC(dc);
        var failures = 0;
        var (lines, reason, ms) = Read(pixels, width, height);
        if (reason != null) { Console.WriteLine("unavailable " + reason); return 2; }
        var found = lines.FirstOrDefault(l => l.Text.Contains("OCR 2026", StringComparison.OrdinalIgnoreCase));
        if (found == null) { Console.WriteLine("fail text: " + string.Join(" | ", lines.Select(l => l.Text))); failures++; }
        else if (found.Left is < 40 or > 80 || found.Top is < 60 or > 100 || found.Width < 300 || found.Height is < 20 or > 70)
        { Console.WriteLine($"fail rect {found.Left},{found.Top},{found.Width},{found.Height}"); failures++; }
        else Console.WriteLine($"ok text \"{found.Text}\" at {found.Left},{found.Top},{found.Width}x{found.Height} in {ms} ms");
        // Sorting of initialization failures: only a proven absence counts as "missing" (Codex's review of 27/10).
        var classes = new (Exception Error, bool Missing)[]
        {
            (new WinRtException("RoGetActivationFactory Windows.Media.Ocr.OcrEngine", ClassNotRegistered), true),
            (new DllNotFoundException("combase.dll"), true),
            (new WinRtException("WindowsCreateString", unchecked((int)0x8007000E)), false), // E_OUTOFMEMORY
            (new WinRtException("RoGetActivationFactory Windows.Graphics.Imaging.SoftwareBitmap", ClassNotRegistered), false),
            (new WinRtException("OcrEngine.TryCreateFromUserProfileLanguages", unchecked((int)0x80004005)), false),
        };
        var misclassified = classes.Where(c => ProvesAbsence(c.Error) != c.Missing).Select(c => c.Error.Message).ToList();
        if (misclassified.Count > 0) { Console.WriteLine("fail classification: " + string.Join(" | ", misclassified)); failures++; }
        else Console.WriteLine("ok only a proven absence counts as missing");
        // Image larger than the engine's limit: positions scaled back to the given image.
        var wide = new byte[(int)(maxSide + 600) * 40 * 4];
        Array.Fill(wide, (byte)255);
        var (_, wideReason, _) = Read(wide, (int)maxSide + 600, 40);
        if (wideReason != null) { Console.WriteLine("fail wide " + wideReason); failures++; } else Console.WriteLine("ok wide image reduced to the engine limit");
        return failures == 0 ? 0 : 1;
    }
}
