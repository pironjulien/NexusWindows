using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static partial class Capture
{
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(nint handle, int index, [Out] char[] value, uint length, out uint needed);

    static void CheckDesktopCapture()
    {
        var desktop = OpenInputDesktop(0, false, 1 /* DESKTOP_READOBJECTS */);
        if (desktop == 0) throw new ActionRefusedException("Secure or unavailable input desktop: no screenshot taken");
        try
        {
            var name = new char[256];
            if (!GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, name, (uint)name.Length * 2, out _) || Win32.Terminated(name) != "Default")
                throw new ActionRefusedException("Only the normal user desktop may be captured");
        }
        finally { CloseDesktop(desktop); }
        string? refusal = null;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd)) return true;
            var pid = Win32.ProcessOf(hwnd);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                refusal = GardeFous.BlockReason(process.ProcessName, Win32.ClassName(hwnd));
                return refusal == null;
            }
            catch (ArgumentException) { return true; }
            catch (System.ComponentModel.Win32Exception) { refusal = "A visible application's identity is unavailable"; return false; }
        }, 0);
        if (refusal != null) throw new ActionRefusedException(refusal + "; full-screen capture refused");
    }
    public static object Screen(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("Capture directory must be absolute");
        CheckDesktopCapture();
        var previous = Win32.SetThreadDpiAwarenessContext(-4);
        try
        {
            var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
            var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4) throw new CaptureUnavailableException("Desktop dimensions unavailable");
            var screen = Win32.GetDC(0); var dc = Win32.CreateCompatibleDC(screen);
            if (screen == 0 || dc == 0) { if (screen != 0) _ = Win32.ReleaseDC(0, screen); throw new CaptureUnavailableException("Desktop device context unavailable"); }
            byte[] pixels;
            try
            {
                var header = new Win32.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                var bitmap = Win32.CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
                if (bitmap == 0) throw new CaptureUnavailableException("Desktop bitmap allocation failed");
                var old = Win32.SelectObject(dc, bitmap);
                try
                {
                    if (!BitBlt(dc, 0, 0, width, height, screen, left, top, 0x00CC0020 | 0x40000000)) throw new CaptureUnavailableException("Desktop BitBlt failed");
                    Win32.GdiFlush(); pixels = new byte[width * height * 4]; Marshal.Copy(bits, pixels, 0, pixels.Length);
                    for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                }
                finally { Win32.SelectObject(dc, old); Win32.DeleteObject(bitmap); }
            }
            finally { Win32.DeleteDC(dc); _ = Win32.ReleaseDC(0, screen); }
            // A manager/UAC appearing during acquisition invalidates the whole capture; no file is published.
            CheckDesktopCapture();
            var encoded = Encode(pixels, width, height); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + encoded.Image.Extension); File.WriteAllBytes(file, encoded.Image.Bytes); CaptureRetention.Prune(directory);
            return new CaptureScreenJson(ok: true, method: "desktop-bitblt", path: file, width: encoded.Width, height: encoded.Height,
                mimeType: encoded.Image.MimeType, size_bytes: encoded.Image.Bytes.Length, encoding: encoded.How,
                frame: new CaptureWindowJson2(left: left, top: top, width: width, height: height), full_screen: true);
        }
        finally { Win32.SetThreadDpiAwarenessContext(previous); }
    }
}
