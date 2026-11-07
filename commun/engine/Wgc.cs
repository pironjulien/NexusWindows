using System.Diagnostics;
using System.Runtime.InteropServices;

// Windows.Graphics.Capture of ONE window, as a fallback only: PrintWindow failed or returned a uniform image (DirectX
// surface, game, some composed windows). The capture reads the window's own DWM surface, with no cursor or yellow border,
// even when covered: never a copy of the screen, which would show the window on top.
// WinRT and Direct3D 11 as raw COM (WinRt.cs): neither a 25 MB projection nor a package. D3D device kept for the process
// (created once). GraphicsCaptureItem.CreateForWindow keeps ~1 kernel handle per call, even once the item is released
// (measured on 27/10: +1 per capture over 300; the same leak as NexusWindows Claude with windows-capture): one item per
// window, kept and reused (16 windows at most, the oldest given back). Pool, session and frame: closed at each capture.
// The self-test measures the handles.
static unsafe partial class Wgc
{
    const int TimeoutMs = 1500;
    const int Bgra8 = 87; // DirectXPixelFormat.B8G8R8A8UIntNormalized

    static readonly Guid IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    static readonly Guid IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    static readonly Guid IFramePoolStatics2 = new("589b103f-6bbc-5df5-a991-02e28b3b66d5");
    static readonly Guid IGraphicsCaptureSession2 = new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
    static readonly Guid IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    static readonly Guid IDirect3DDevice = new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670");
    static readonly Guid IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [LibraryImport("d3d11.dll")] private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint count, uint sdk, nint* device, int* level, nint* context);
    [LibraryImport("d3d11.dll")] private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, nint* graphics);

    [StructLayout(LayoutKind.Sequential)] struct SizeInt32 { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public byte* Data; public uint RowPitch, DepthPitch; }

    static readonly object Gate = new();
    static nint device, context, graphics, interop, pools;
    const int ItemCacheSize = 16;
    static readonly List<(nint Hwnd, nint Item)> Items = [];
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    static nint Item(nint hwnd)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!IsWindow(Items[i].Hwnd)) { WinRt.Release(Items[i].Item); Items.RemoveAt(i); }
        var found = Items.FindIndex(x => x.Hwnd == hwnd);
        if (found >= 0) return Items[found].Item;
        var itemId = IGraphicsCaptureItem;
        nint item;
        WinRt.Check(((delegate* unmanaged<nint, nint, Guid*, nint*, int>)WinRt.Slot(interop, 3))(interop, hwnd, &itemId, &item), "GraphicsCaptureItem.CreateForWindow");
        if (Items.Count >= ItemCacheSize) { WinRt.Release(Items[0].Item); Items.RemoveAt(0); }
        Items.Add((hwnd, item));
        return item;
    }
    static void Forget(nint hwnd)
    {
        var found = Items.FindIndex(x => x.Hwnd == hwnd);
        if (found >= 0) { WinRt.Release(Items[found].Item); Items.RemoveAt(found); }
    }

    static void Prepare()
    {
        if (graphics != 0) return;
        nint d = 0, c = 0, dxgi = 0, inspectable = 0, g = 0, i = 0, p = 0;
        int level;
        // Hardware first (BGRA support for the DWM surface), WARP otherwise (remote session, missing driver).
        var hr = D3D11CreateDevice(0, 1 /* HARDWARE */, 0, 0x20 /* BGRA_SUPPORT */, 0, 0, 7, &d, &level, &c);
        if (hr < 0) WinRt.Check(D3D11CreateDevice(0, 5 /* WARP */, 0, 0x20, 0, 0, 7, &d, &level, &c), "D3D11CreateDevice");
        try
        {
            dxgi = WinRt.QueryRequired(d, IDXGIDevice, "IDXGIDevice");
            WinRt.Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            g = WinRt.QueryRequired(inspectable, IDirect3DDevice, "IDirect3DDevice");
            i = WinRt.Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IGraphicsCaptureItemInterop);
            p = WinRt.Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IFramePoolStatics2);
            // Ownership transferred only after full success; otherwise everything is given back (Codex's review of 27/10).
            (device, context, graphics, interop, pools) = (d, c, g, i, p);
            d = c = g = i = p = 0;
        }
        finally
        {
            WinRt.Release(p); WinRt.Release(i); WinRt.Release(g);
            WinRt.Release(inspectable); WinRt.Release(dxgi); WinRt.Release(c); WinRt.Release(d);
        }
    }

    // Device lost (driver updated, GPU reset): everything is created again at the next capture.
    static void Reset()
    {
        foreach (var (_, item) in Items) WinRt.Release(item);
        Items.Clear();
        WinRt.Release(pools); WinRt.Release(interop); WinRt.Release(graphics); WinRt.Release(context); WinRt.Release(device);
        (device, context, graphics, interop, pools) = (0, 0, 0, 0, 0);
    }

    // BGRA pixels of the window, cropped at the top left to width x height (DWM frame of Capture.Window); an area the
    // image does not cover stays black (and counts in the blank image measure).
    public static byte[] Window(nint hwnd, int width, int height)
    {
        lock (Gate)
        {
            Prepare();
            var reused = Items.Any(x => x.Hwnd == hwnd);
            try { return Grab(Item(hwnd), width, height); }
            catch (WinRtException error) when (error.HResult32 is unchecked((int)0x887A0005) or unchecked((int)0x887A0007)) { Reset(); throw; }
            catch (Exception error) when (reused && error is WinRtException or CaptureUnavailableException)
            {
                // Kept item gone stale (window recreated under the same handle, item closed): a single fresh attempt.
                Forget(hwnd);
                return Grab(Item(hwnd), width, height);
            }
        }
    }

    static byte[] Grab(nint item, int width, int height)
    {
        nint pool = 0, session = 0, frame = 0, surface = 0, access = 0, texture = 0, staging = 0;
        try
        {
            SizeInt32 size;
            WinRt.Check(((delegate* unmanaged<nint, SizeInt32*, int>)WinRt.Slot(item, 7))(item, &size), "GraphicsCaptureItem.Size");
            if (size.Width <= 0 || size.Height <= 0) throw new CaptureUnavailableException("Windows.Graphics.Capture: window has no size");
            WinRt.Check(((delegate* unmanaged<nint, nint, int, int, SizeInt32, nint*, int>)WinRt.Slot(pools, 6))(pools, graphics, Bgra8, 1, size, &pool), "Direct3D11CaptureFramePool.CreateFreeThreaded");
            WinRt.Check(((delegate* unmanaged<nint, nint, nint*, int>)WinRt.Slot(pool, 10))(pool, item, &session), "CreateCaptureSession");
            // No cursor (Windows 10 2004+) nor yellow border (Windows 11); older: setting absent, ignored. Windows may
            // ignore IsBorderRequired=false (Borderless access refused): a border is possible on the window, never promised.
            var s2 = WinRt.Query(session, IGraphicsCaptureSession2);
            if (s2 != 0) { ((delegate* unmanaged<nint, byte, int>)WinRt.Slot(s2, 7))(s2, 0); WinRt.Release(s2); }
            var s3 = WinRt.Query(session, IGraphicsCaptureSession3);
            if (s3 != 0) { ((delegate* unmanaged<nint, byte, int>)WinRt.Slot(s3, 7))(s3, 0); WinRt.Release(s3); }
            WinRt.Check(((delegate* unmanaged<nint, int>)WinRt.Slot(session, 6))(session), "GraphicsCaptureSession.StartCapture");
            var timer = Stopwatch.StartNew();
            while (true)
            {
                WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(pool, 7))(pool, &frame), "TryGetNextFrame");
                if (frame != 0) break;
                if (timer.ElapsedMilliseconds > TimeoutMs) throw new CaptureUnavailableException("Windows.Graphics.Capture: no image received");
                Thread.Sleep(2);
            }
            SizeInt32 content;
            WinRt.Check(((delegate* unmanaged<nint, SizeInt32*, int>)WinRt.Slot(frame, 8))(frame, &content), "Direct3D11CaptureFrame.ContentSize");
            WinRt.Check(((delegate* unmanaged<nint, nint*, int>)WinRt.Slot(frame, 6))(frame, &surface), "Direct3D11CaptureFrame.Surface");
            access = WinRt.QueryRequired(surface, IDirect3DDxgiInterfaceAccess, "IDirect3DDxgiInterfaceAccess");
            var textureId = ID3D11Texture2D;
            WinRt.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)WinRt.Slot(access, 3))(access, &textureId, &texture), "GetInterface(ID3D11Texture2D)");
            TextureDesc desc;
            ((delegate* unmanaged<nint, TextureDesc*, void>)WinRt.Slot(texture, 10))(texture, &desc);
            var copy = desc;
            copy.MipLevels = 1; copy.ArraySize = 1; copy.SampleCount = 1; copy.SampleQuality = 0;
            copy.Usage = 3 /* STAGING */; copy.BindFlags = 0; copy.CpuAccess = 0x20000 /* READ */; copy.MiscFlags = 0;
            WinRt.Check(((delegate* unmanaged<nint, TextureDesc*, nint, nint*, int>)WinRt.Slot(device, 5))(device, &copy, 0, &staging), "CreateTexture2D");
            ((delegate* unmanaged<nint, nint, nint, void>)WinRt.Slot(context, 47))(context, staging, texture); // CopyResource
            Mapped mapped;
            WinRt.Check(((delegate* unmanaged<nint, nint, uint, int, uint, Mapped*, int>)WinRt.Slot(context, 14))(context, staging, 0, 1 /* READ */, 0, &mapped), "Map");
            try
            {
                var pixels = new byte[width * height * 4];
                int rows = Math.Min(height, Math.Min(content.Height, (int)desc.Height)), columns = Math.Min(width, Math.Min(content.Width, (int)desc.Width));
                fixed (byte* target = pixels)
                    for (var y = 0; y < rows; y++)
                        Buffer.MemoryCopy(mapped.Data + (long)y * mapped.RowPitch, target + (long)y * width * 4, (long)columns * 4, (long)columns * 4);
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return pixels;
            }
            finally { ((delegate* unmanaged<nint, nint, uint, void>)WinRt.Slot(context, 15))(context, staging, 0); }
        }
        finally
        {
            WinRt.Release(staging); WinRt.Release(texture); WinRt.Release(access); WinRt.Release(surface);
            WinRt.Close(frame); WinRt.Close(session); WinRt.Close(pool);
        }
    }

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

    // Bench: WGC capture of an existing window (read only), deviation and duration returned.
    public static int Probe(nint hwnd)
    {
        if (Capture.Area(hwnd) is not { } area) { Console.WriteLine("fail no area"); return 1; }
        var timer = Stopwatch.StartNew();
        var pixels = Window(hwnd, area.Width, area.Height);
        var (deviation, _) = Capture.Statistics(pixels, area.Width, area.Height);
        Console.WriteLine($"{(deviation >= 1.5 ? "ok" : "fail")} {area.Width}x{area.Height} deviation {deviation:0.0} in {timer.ElapsedMilliseconds} ms");
        return deviation >= 1.5 ? 0 : 1;
    }

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
