using System.Diagnostics;
using System.Runtime.InteropServices;

// Native Windows OCR (Windows.Media.Ocr) as a fallback for a window without a useful UI Automation tree (Tk, canvas,
// game): text lines of the image with their rectangle, which the MCP session makes targetable at that image's points
// (McpSession). Behaviour reference: NexusWindows Claude (ocr.py, 10 to 21 ms per window in the lab of 20/10). Read only
// and local: nothing leaves the PC. No OCR language installed or WinRT unavailable: OCR is unavailable, never a tool
// error.
static unsafe partial class Ocr
{
    public const int MaxLines = 120; // a whole page of text has no place in a list of elements
    const int TimeoutMs = 5000;
    const int Bgra8 = 87, WriteAccess = 2;

    static readonly Guid IOcrEngineStatics = new("5bffa85a-3384-3540-9940-699120d428a8");
    static readonly Guid ISoftwareBitmapFactory = new("c99feb69-2d62-4d47-a6b3-4fdb6a07fdf8");
    static readonly Guid IBitmapBuffer = new("a53e04c4-399c-438c-b28f-a63a6b83d1a1");
    static readonly Guid IMemoryBuffer = new("fbc4dd2a-245b-11e4-af98-689423260cf8");
    static readonly Guid IMemoryBufferByteAccess = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");

    public sealed record Line(string Text, int Left, int Top, int Width, int Height);

    static readonly object Gate = new();
    static nint engine, bitmaps;
    static uint maxSide;
    static string? unavailable;
    // True ONLY when the absence of OCR is proven: no OCR language (TryCreateFromUserProfileLanguages returns null), WinRT
    // class not registered, combase without WinRT. Any other failure (allocation, SoftwareBitmap activation, stuck
    // engine) stays a failure: missing is false, the MCP session refuses the gesture that needs the read (Codex's review
    // of 27/10), and creation is tried again at the next image.
    public static bool Missing { get; private set; }
    const int ClassNotRegistered = unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG

    static string Short(string text) => text.Length <= 160 ? text : text[..160];

    [StructLayout(LayoutKind.Sequential)] struct PlaneDescription { public int StartIndex, Width, Height, Stride; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float X, Y, Width, Height; }

    // Engine created once (languages of the user's profile). Proven absence or stuck engine: kept for the life of the
    // process (unavailable); any other failure: returned for this image only (failure), tried again at the next one.
    static string? failure;
    static bool ProvesAbsence(Exception error) => error is DllNotFoundException or EntryPointNotFoundException
        || (error is WinRtException w && w.HResult32 == ClassNotRegistered && w.Message.StartsWith("RoGetActivationFactory Windows.Media.Ocr.OcrEngine ", StringComparison.Ordinal));
    static bool Ready()
    {
        failure = null;
        if (unavailable != null) return false;
        if (engine != 0) return true;
        try
        {
            var statics = WinRt.Factory("Windows.Media.Ocr.OcrEngine", IOcrEngineStatics);
            try
            {
                uint side;
                WinRt.Check(((delegate* unmanaged<nint, uint*, int>)WinRt.Slot(statics, 6))(statics, &side), "OcrEngine.MaxImageDimension");
                nint created;
                WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(statics, 10))(statics, &created), "OcrEngine.TryCreateFromUserProfileLanguages");
                if (created == 0) { unavailable = "no OCR language installed in Windows"; Missing = true; return false; }
                try { bitmaps = WinRt.Factory("Windows.Graphics.Imaging.SoftwareBitmap", ISoftwareBitmapFactory); }
                catch { WinRt.Release(created); throw; }
                maxSide = side == 0 ? 10000 : side;
                engine = created;
            }
            finally { WinRt.Release(statics); }
            return true;
        }
        catch (Exception error) when (ProvesAbsence(error))
        {
            unavailable = Short($"OCR is not available in this Windows: {error.Message}");
            Missing = true;
            return false;
        }
        catch (Exception error) { failure = Short($"{error.GetType().Name}: {error.Message}"); return false; }
    }

    // Lines read in the BGRA image (positions in pixels of that image); Unavailable: why OCR is absent.
    public static (List<Line> Lines, string? Unavailable, long Ms) Read(byte[] bgra, int width, int height)
    {
        var timer = Stopwatch.StartNew();
        lock (Gate)
        {
            if (!Ready()) return ([], unavailable ?? failure, timer.ElapsedMilliseconds);
            // Engine limit: image reduced, positions scaled back.
            var factor = Math.Max(width, height) > maxSide ? maxSide / (double)Math.Max(width, height) : 1.0;
            int w = width, h = height;
            var pixels = bgra;
            if (factor != 1.0)
            {
                w = Math.Max(1, (int)(width * factor)); h = Math.Max(1, (int)(height * factor));
                pixels = Capture.Shrink(bgra, width, height, w, h);
            }
            nint bitmap = 0;
            var keep = false;
            try
            {
                bitmap = Bitmap(pixels, w, h);
                nint operation;
                WinRt.Check(((delegate* unmanaged<nint, nint, nint*, int>)WinRt.Slot(engine, 6))(engine, bitmap, &operation), "OcrEngine.RecognizeAsync");
                var result = WinRt.Await(operation, TimeoutMs, "OcrEngine.RecognizeAsync");
                try { return (Lines(result, factor), null, timer.ElapsedMilliseconds); }
                finally { WinRt.Release(result); }
            }
            catch (WinRtStuckException error)
            {
                // Stuck engine: the operation still lives and may be reading the bitmap, kept alive; OCR is off for this
                // process (the helper exits after 30 s without a request and starts fresh).
                keep = true;
                unavailable = Short("OCR engine stuck: " + error.Message);
                return ([], unavailable, timer.ElapsedMilliseconds);
            }
            catch (Exception error) { return ([], Short("OCR failed: " + error.Message), timer.ElapsedMilliseconds); }
            finally { if (!keep) WinRt.Close(bitmap); }
        }
    }

    // BGRA8 SoftwareBitmap filled through its buffer (LockBuffer for writing, no row padding: the plane's Stride).
    static nint Bitmap(byte[] pixels, int width, int height)
    {
        nint bitmap, buffer = 0, memory = 0, reference = 0, access = 0;
        WinRt.Check(((delegate* unmanaged<nint, int, int, int, nint*, int>)WinRt.Slot(bitmaps, 6))(bitmaps, Bgra8, width, height, &bitmap), "SoftwareBitmap.Create");
        try
        {
            WinRt.Check(((delegate* unmanaged<nint, int, nint*, int>)WinRt.Slot(bitmap, 15))(bitmap, WriteAccess, &buffer), "SoftwareBitmap.LockBuffer");
            var planes = WinRt.QueryRequired(buffer, IBitmapBuffer, "BitmapBuffer");
            PlaneDescription plane;
            try { WinRt.Check(((delegate* unmanaged<nint, int, PlaneDescription*, int>)WinRt.Slot(planes, 7))(planes, 0, &plane), "BitmapBuffer.GetPlaneDescription"); }
            finally { WinRt.Release(planes); }
            memory = WinRt.QueryRequired(buffer, IMemoryBuffer, "IMemoryBuffer");
            WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(memory, 6))(memory, &reference), "IMemoryBuffer.CreateReference");
            access = WinRt.QueryRequired(reference, IMemoryBufferByteAccess, "IMemoryBufferByteAccess");
            byte* data; uint capacity;
            WinRt.Check(((delegate* unmanaged<nint, byte**, uint*, int>)WinRt.Slot(access, 3))(access, &data, &capacity), "IMemoryBufferByteAccess.GetBuffer");
            var row = width * 4;
            if (plane.Stride < row || (long)plane.StartIndex + (long)plane.Stride * (height - 1) + row > capacity) throw new InvalidOperationException("Bitmap buffer is too small");
            fixed (byte* source = pixels)
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(source + (long)y * row, data + plane.StartIndex + (long)y * plane.Stride, row, row);
                    for (var x = 3; x < row; x += 4) data[plane.StartIndex + (long)y * plane.Stride + x] = 255;
                }
            return bitmap;
        }
        catch { WinRt.Close(bitmap); throw; }
        finally { WinRt.Release(access); WinRt.Close(reference); WinRt.Release(memory); WinRt.Close(buffer); }
    }

    static List<Line> Lines(nint result, double factor)
    {
        var lines = new List<Line>();
        nint vector;
        WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(result, 6))(result, &vector), "OcrResult.Lines");
        try
        {
            uint count;
            WinRt.Check(((delegate* unmanaged<nint, uint*, int>)WinRt.Slot(vector, 7))(vector, &count), "Lines.Size");
            for (uint i = 0; i < count && lines.Count < MaxLines; i++)
            {
                nint line;
                WinRt.Check(((delegate* unmanaged<nint, uint, nint*, int>)WinRt.Slot(vector, 6))(vector, i, &line), "Lines.GetAt");
                try
                {
                    nint text;
                    WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(line, 7))(line, &text), "OcrLine.Text");
                    var value = WinRt.Take(text).Trim();
                    if (value.Length == 0) continue;
                    var box = Bounds(line);
                    if (box is not { } b) continue;
                    lines.Add(new Line(value, (int)Math.Round(b.Left / factor), (int)Math.Round(b.Top / factor),
                        (int)Math.Round((b.Right - b.Left) / factor), (int)Math.Round((b.Bottom - b.Top) / factor)));
                }
                finally { WinRt.Release(line); }
            }
        }
        finally { WinRt.Release(vector); }
        return lines;
    }

    // Rectangle of a line: union of its words' rectangles (the engine gives none for the line).
    static (float Left, float Top, float Right, float Bottom)? Bounds(nint line)
    {
        nint words;
        WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(line, 6))(line, &words), "OcrLine.Words");
        try
        {
            uint count;
            WinRt.Check(((delegate* unmanaged<nint, uint*, int>)WinRt.Slot(words, 7))(words, &count), "Words.Size");
            if (count == 0) return null;
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            for (uint i = 0; i < count; i++)
            {
                nint word;
                WinRt.Check(((delegate* unmanaged<nint, uint, nint*, int>)WinRt.Slot(words, 6))(words, i, &word), "Words.GetAt");
                try
                {
                    RectF r;
                    WinRt.Check(((delegate* unmanaged<nint, RectF*, int>)WinRt.Slot(word, 6))(word, &r), "OcrWord.BoundingRect");
                    left = Math.Min(left, r.X); top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width); bottom = Math.Max(bottom, r.Y + r.Height);
                }
                finally { WinRt.Release(word); }
            }
            return (left, top, right, bottom);
        }
        finally { WinRt.Release(words); }
    }

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
