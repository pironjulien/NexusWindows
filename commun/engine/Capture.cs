using System.Diagnostics;
using System.IO;
using System.IO.Compression;

sealed class CaptureUnavailableException(string message) : InvalidOperationException(message);

// Capture of a window without bringing it forward: PrintWindow(PW_RENDERFULLCONTENT), even when covered (not minimized).
// Physical coordinates (thread in per-monitor v2 DPI), image cropped to the DWM frame (without the invisible shadow).
// An application that is not DPI aware draws itself at its logical size: a bitmap at that size, then enlarged to the
// displayed size (otherwise the content is at 80 % with a black band, NexusClaude's measurement). PrintWindow failed or
// a uniform image: Windows.Graphics.Capture of the window itself (Wgc.cs, method "wgc"); still uniform = empty (blank),
// image unavailable, never a copy of the screen (it would show the window on top of a covered window).
// Shared image rule of the Nexus products (27/10): long side <= 2000 px and <= 2.56 Mpx (2,500 GPT tiles in high detail;
// Claude Code downscales by itself beyond 2000 px), lossless WebP (WebP.cs) as long as it fits in 3 MB, then the fallback
// of 06/11 (ImageEncoding.cs: 2 then 3 bits removed, then JPEG 82, 72, 60 at the same size); never more than 3 MB
// (downscaling only if no step fits). The image can therefore be
// smaller than the window: `frame` stays the SCREEN frame and a point x,y of the image targets
// frame.left + x * frame.width / width (McpSession.NativePoint).
static partial class Capture
{
    const int MaxSide = 2000;
    const double MaxPixels = 2.56e6;

    const uint PW_RENDERFULLCONTENT = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    static readonly nint PerMonitorAwareV2 = -4;

    // zoom: SCREEN area [left, top, width, height] (physical pixels) read at full resolution and enlarged up to x2 (small
    // text), within the same caps; `frame` is then that area, and a point of the zoomed image targets the screen as for
    // any native image (nativeScreen).
    const double ZoomMaxEnlarge = 2.0;

    // The window's whole SCREEN frame (DWM frame bounded to the window, physical pixels): the one Window returns as
    // windowFrame. inspect reads it again before and after the tree; marks are drawn only if the capture sees the same frame.
    public static Win32.Rect? Area(nint hwnd)
    {
        var previous = Win32.SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            if (Win32.IsIconic(hwnd) || !Win32.GetWindowRect(hwnd, out var window) || window.Empty) return null;
            var frame = Win32.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var f, 16) == 0 && !f.Empty ? f : window;
            var crop = frame.Intersect(window);
            return crop.Empty ? window : crop;
        }
        finally { Win32.SetThreadDpiAwarenessContext(previous); }
    }

    static bool Same(Win32.Rect a, Win32.Rect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // marks: numbered elements (Marks.cs), treeFrame: frame read with the tree; absent or different from the captured
    // frame (window moved between the two reads) → no marks, the reason is returned.
    public static object Window(nint hwnd, string directory, int[]? zoom = null, List<Mark>? marks = null, Win32.Rect? treeFrame = null)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidOperationException("Capture directory must be absolute");
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var synced = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(synced) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(synced).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Captures never go to a synced folder");
        }
        var timer = Stopwatch.StartNew();
        if (Win32.IsIconic(hwnd)) throw new CaptureUnavailableException("Window is minimized: nothing to draw");
        var previous = Win32.SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            if (!Win32.GetWindowRect(hwnd, out var window) || window.Empty) throw new CaptureUnavailableException("Window has no size");
            var frame = Win32.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var f, 16) == 0 && !f.Empty ? f : window;
            var scale = Scale(hwnd);
            int width = window.Width, height = window.Height;
            if (scale != 1.0) { width = Math.Max(1, (int)Math.Round(width / scale)); height = Math.Max(1, (int)Math.Round(height / scale)); }
            if ((long)width * height > 64L * 1024 * 1024) throw new CaptureUnavailableException("Window is too large to capture");
            var crop = frame.Intersect(window);
            if (crop.Empty) crop = window;
            byte[]? pixels = null;
            CaptureUnavailableException? printFailure = null;
            try
            {
                pixels = Print(hwnd, width, height);
                if (scale != 1.0) { pixels = Resize(pixels, width, height, window.Width, window.Height); width = window.Width; height = window.Height; }
                pixels = Crop(pixels, width, crop.Left - window.Left, crop.Top - window.Top, crop.Width, crop.Height);
            }
            catch (CaptureUnavailableException error) { printFailure = error; }
            var method = scale == 1.0 ? "printwindow" : "printwindow-dpi-scaled";
            string? wgcError = null;
            if (pixels == null || Statistics(pixels, crop.Width, crop.Height).Deviation < 1.5)
            {
                try
                {
                    var grabbed = Wgc.Window(hwnd, crop.Width, crop.Height);
                    if (Statistics(grabbed, crop.Width, crop.Height).Deviation >= 1.5 || pixels == null) { pixels = grabbed; method = "wgc"; }
                    else wgcError = "uniform image too";
                }
                catch (Exception error) when (error is CaptureUnavailableException or WinRtException or TimeoutException or DllNotFoundException or EntryPointNotFoundException)
                {
                    wgcError = error.Message;
                    if (pixels == null) throw new CaptureUnavailableException($"{printFailure!.Message}; Windows.Graphics.Capture: {error.Message}");
                }
            }
            var enlarge = 1.0;
            var windowArea = crop; // the window's WHOLE frame: the one the movement guards compare (Pointer, Physical)
            if (zoom != null)
            {
                if (zoom.Length != 4 || zoom[2] <= 0 || zoom[3] <= 0) throw new ActionRefusedException("Zoom area must be [left, top, width, height] with a size");
                var area = new Win32.Rect { Left = zoom[0], Top = zoom[1], Right = zoom[0] + zoom[2], Bottom = zoom[1] + zoom[3] }.Intersect(crop);
                if (area.Empty) throw new ActionRefusedException("Zoom area is outside the window");
                pixels = Crop(pixels, crop.Width, area.Left - crop.Left, area.Top - crop.Top, area.Width, area.Height);
                crop = area;
                enlarge = ZoomMaxEnlarge;
            }
            var (deviation, uniformity) = Statistics(pixels, crop.Width, crop.Height); // before any mark
            var blank = deviation < 1.5;
            MarkReport? report = null;
            Func<byte[], int, int, byte[]>? annotate = null;
            if (marks != null)
            {
                report = new MarkReport();
                // Frame read again AFTER PrintWindow too: a window maximized during the capture would leave marks at the
                // old place (Codex's review of 24/10).
                var printed = Area(hwnd);
                if (treeFrame is not { } t || !Same(t, windowArea) || printed is not { } p || !Same(p, windowArea))
                    report.Reason = "window moved or resized between the tree read and the capture: no numbers";
                else
                {
                    var scoped = Marks.Scoped(hwnd, marks, report);
                    var area = crop;
                    annotate = (image, w, h) => Marks.Draw(image, w, h, area, scoped, report);
                }
            }
            string? file = null;
            int imageWidth = crop.Width, imageHeight = crop.Height;
            var encoding = "";
            var mimeType = "image/webp";
            if (!blank)
            {
                var (encoded, w, h, how) = Encode(pixels, crop.Width, crop.Height, enlarge, annotate);
                imageWidth = w; imageHeight = h; encoding = how; mimeType = encoded.MimeType;
                Directory.CreateDirectory(directory);
                file = Path.Combine(directory, Guid.NewGuid().ToString("N") + encoded.Extension);
                File.WriteAllBytes(file, encoded.Bytes);
                lock (LastGate) Last = (file, pixels, crop); // full resolution, without marks: read by Recognize
                CaptureRetention.Prune(directory);
            }
            return new CaptureWindowJson(
                ok: true, method: method, wgcError: wgcError, path: file, width: imageWidth, height: imageHeight, scale: scale, blank: blank, encoding: encoding, mimeType: mimeType,
                deviation: Math.Round(deviation, 2), uniformity: Math.Round(uniformity, 3), ms: timer.ElapsedMilliseconds,
                frame: new CaptureWindowJson2(left: crop.Left, top: crop.Top, width: crop.Width, height: crop.Height),
                windowFrame: new CaptureWindowJson2(left: windowArea.Left, top: windowArea.Top, width: windowArea.Width, height: windowArea.Height),
                marks: report == null ? null : blank ? new MarkReport { Reason = "blank capture: no numbers" }.Json() : report.Json()
            );
        }
        finally { Win32.SetThreadDpiAwarenessContext(previous); }
    }

    // Last image written, at full resolution (a single one, replaced at each capture): OCR reads it afterwards, only if the
    // UI Automation tree turns out too poor (MCP session), without capturing again or reading the WebP back.
    static readonly object LastGate = new();
    static (string Path, byte[] Pixels, Win32.Rect Area)? Last;

    // Text lines of the image `path` (this process's last capture), positions in SCREEN pixels.
    public static object Recognize(string path)
    {
        (string Path, byte[] Pixels, Win32.Rect Area) last;
        lock (LastGate)
        {
            if (Last is not { } l || !string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase))
                return new CaptureRecognizeJson(ok: true, lines: Array.Empty<object>(), unavailable: "image no longer in the helper's memory", missing: false);
            last = l;
        }
        var (lines, unavailable, ms) = Ocr.Read(last.Pixels, last.Area.Width, last.Area.Height);
        return new CaptureRecognizeJson2(
            ok: true, ms: ms, unavailable: unavailable, missing: unavailable != null && Ocr.Missing,
            lines: lines.Select(x => new CaptureRecognizeJson3(text: x.Text, left: last.Area.Left + x.Left, top: last.Area.Top + x.Top, width: x.Width, height: x.Height)).ToArray()
        );
    }

    // Factor between the displayed (physical) size and the size the application draws.
    internal static double Scale(nint hwnd)
    {
        var awareness = Win32.GetAwarenessFromDpiAwarenessContext(Win32.GetWindowDpiAwarenessContext(hwnd));
        if (awareness >= 2) return 1.0; // per monitor: draws in physical pixels
        var monitor = Win32.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (Win32.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) != 0 || dpi == 0) return 1.0;
        // Drawing DPI of THIS window (96 if not DPI aware, the system DPI of its context otherwise), never the process's: a
        // SYSTEM_AWARE thread of a process that is not DPI aware draws at 120 (Codex's review of 22/10 07:59).
        double drawn = Win32.GetDpiForWindow(hwnd);
        if (drawn <= 0) return 1.0;
        var scale = dpi / drawn;
        return Math.Abs(scale - 1.0) < 0.01 ? 1.0 : scale;
    }

    static byte[] Print(nint hwnd, int width, int height)
    {
        var screen = Win32.GetDC(0);
        var dc = Win32.CreateCompatibleDC(screen);
        _ = Win32.ReleaseDC(0, screen);
        if (dc == 0) throw new CaptureUnavailableException("No memory device context");
        try
        {
            var header = new Win32.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var bitmap = Win32.CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new CaptureUnavailableException("CreateDIBSection failed");
            var old = Win32.SelectObject(dc, bitmap);
            try
            {
                if (!Win32.PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)) throw new CaptureUnavailableException("PrintWindow failed");
                Win32.GdiFlush();
                var pixels = new byte[width * height * 4];
                System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
                return pixels;
            }
            finally { Win32.SelectObject(dc, old); Win32.DeleteObject(bitmap); }
        }
        finally { Win32.DeleteDC(dc); }
    }

    // Size and encoding of the image sent to the model (shared rule above).
    // annotate: numbers drawn on the already downscaled image, at each attempt (constant size in the final image).
    internal static (ImageEncoding.Encoded Image, int Width, int Height, string How) Encode(byte[] pixels, int width, int height, double enlarge = 1.0, Func<byte[], int, int, byte[]>? annotate = null, int limit = ImageEncoding.Limit)
    {
        var fit = Math.Min(enlarge, Math.Min(MaxSide / (double)Math.Max(width, height), Math.Sqrt(MaxPixels / ((double)width * height))));
        for (var attempt = 0; ; attempt++)
        {
            int w = Math.Max(1, (int)Math.Floor(width * fit)), h = Math.Max(1, (int)Math.Floor(height * fit));
            var image = w == width && h == height ? pixels : w > width ? Resize(pixels, width, height, w, h) : Shrink(pixels, width, height, w, h);
            if (annotate != null) image = annotate(image, w, h);
            // Shared chain at the same size; only if nothing fits, a downscaled image (x0.85) and the chain again.
            var encoded = ImageEncoding.Fit(image, w, h, limit);
            if (encoded == null && (w <= 64 || attempt >= 8)) encoded = ImageEncoding.Smallest(image, w, h);
            if (encoded is { } done) return (done, w, h, done.How + (fit != 1.0 ? $"-{w}x{h}" : ""));
            fit *= 0.85;
        }
    }

    // Downscale by area averaging (BGRA): each target pixel averages the source pixels it covers.
    internal static byte[] Shrink(byte[] source, int sw, int sh, int dw, int dh)
    {
        var result = new byte[dw * dh * 4];
        var sx = (double)sw / dw;
        var sy = (double)sh / dh;
        for (var y = 0; y < dh; y++)
        {
            double top = y * sy, bottom = Math.Min(sh, (y + 1) * sy);
            for (var x = 0; x < dw; x++)
            {
                double left = x * sx, right = Math.Min(sw, (x + 1) * sx);
                double b = 0, g = 0, r = 0, total = 0;
                for (var yy = (int)top; yy < Math.Ceiling(bottom); yy++)
                {
                    var wy = Math.Min(bottom, yy + 1) - Math.Max(top, yy);
                    for (var xx = (int)left; xx < Math.Ceiling(right); xx++)
                    {
                        var wx = Math.Min(right, xx + 1) - Math.Max(left, xx);
                        var weight = wx * wy;
                        var i = (yy * sw + xx) * 4;
                        b += source[i] * weight; g += source[i + 1] * weight; r += source[i + 2] * weight; total += weight;
                    }
                }
                var o = (y * dw + x) * 4;
                result[o] = (byte)Math.Round(b / total); result[o + 1] = (byte)Math.Round(g / total);
                result[o + 2] = (byte)Math.Round(r / total); result[o + 3] = 255;
            }
        }
        return result;
    }

    // Bilinear BGRA enlargement (application that is not DPI aware).
    static byte[] Resize(byte[] source, int sw, int sh, int dw, int dh)
    {
        var result = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var fy = Math.Max(0, (y + 0.5) * sh / dh - 0.5);
            int y0 = Math.Min((int)fy, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
            var wy = fy - y0;
            for (var x = 0; x < dw; x++)
            {
                var fx = Math.Max(0, (x + 0.5) * sw / dw - 0.5);
                int x0 = Math.Min((int)fx, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                var wx = fx - x0;
                for (var c = 0; c < 4; c++)
                {
                    double a = source[(y0 * sw + x0) * 4 + c], b = source[(y0 * sw + x1) * 4 + c];
                    double d = source[(y1 * sw + x0) * 4 + c], e = source[(y1 * sw + x1) * 4 + c];
                    result[(y * dw + x) * 4 + c] = (byte)Math.Round((a * (1 - wx) + b * wx) * (1 - wy) + (d * (1 - wx) + e * wx) * wy);
                }
            }
        }
        return result;
    }

    static byte[] Crop(byte[] source, int sourceWidth, int left, int top, int width, int height)
    {
        if (left == 0 && top == 0 && width == sourceWidth && source.Length == width * height * 4) return source;
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(source, ((top + y) * sourceWidth + left) * 4, result, y * width * 4, width * 4);
        return result;
    }

    // Standard deviation of the luminance over a 64 x 64 point grid; < 1.5 = uniform image (black, white or empty).
    public static (double Deviation, double Uniformity) Statistics(byte[] bgra, int width, int height)
    {
        const int grid = 64;
        var histogram = new int[256];
        double sum = 0, squares = 0;
        var samples = 0;
        for (var gy = 0; gy < grid; gy++)
            for (var gx = 0; gx < grid; gx++)
            {
                var x = (int)((gx + 0.5) * width / grid);
                var y = (int)((gy + 0.5) * height / grid);
                var i = (Math.Min(y, height - 1) * width + Math.Min(x, width - 1)) * 4;
                var luminance = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
                sum += luminance; squares += luminance * luminance; samples++;
                histogram[(int)luminance]++;
            }
        var mean = sum / samples;
        return (Math.Sqrt(Math.Max(0, squares / samples - mean * mean)), histogram.Max() / (double)samples);
    }

    // Minimal 8-bit RGB PNG (filter 0, zlib): no imaging dependency.
    public static byte[] Png(byte[] bgra, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    row[1 + x * 3] = bgra[i + 2]; row[2 + x * 3] = bgra[i + 1]; row[3 + x * 3] = bgra[i];
                }
                z.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBig(header, 0, (uint)width); WriteBig(header, 4, (uint)height);
        header[8] = 8; header[9] = 2; // 8 bits, RVB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }
    static void WriteBig(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16); buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
    }
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
    static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4]; WriteBig(length, 0, (uint)data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes); output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var tail = new byte[4]; WriteBig(tail, 0, crc ^ 0xFFFFFFFFu);
        output.Write(tail);
    }
}
