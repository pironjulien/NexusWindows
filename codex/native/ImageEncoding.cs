using System.Runtime.InteropServices;

// Shared Nexus image rule (NexusShared/images.md, fallback updated 2026-11-06), the same for the three editions:
// lossless WebP while it fits 500 KB; then lossless WebP with 2, then 3 low bits per colour channel dropped (never more:
// 4 bits lost words in the Windows OCR bench); then lossy. Windows has no lossy WebP encoder (WIC only decodes), so
// the lossy steps are JPEG from quality 82 (GDI+), at the same pixel size. Only when none fits does the caller shrink
// the image and run the chain again.
static class ImageEncoding
{
    internal const int Limit = 500 * 1024;
    internal static readonly int[] DroppedBits = [2, 3];
    internal static readonly int[] JpegQualities = [82, 72, 60];

    internal readonly record struct Encoded(byte[] Bytes, string Format, string How)
    {
        public string MimeType => "image/" + Format;
        public string Extension => Format == "jpeg" ? ".jpg" : ".webp";
    }

    // First step of the chain that fits `limit`, or null (the caller shrinks the image).
    internal static Encoded? Fit(byte[] bgra, int width, int height, int limit = Limit)
    {
        var bytes = WebP.Lossless(bgra, width, height);
        if (bytes.Length <= limit) return new(bytes, "webp", "webp-lossless");
        foreach (var bits in DroppedBits)
        {
            bytes = WebP.Lossless(DropLowBits(bgra, bits), width, height);
            if (bytes.Length <= limit) return new(bytes, "webp", $"webp-near-lossless-{bits}bits");
        }
        foreach (var quality in JpegQualities)
        {
            bytes = Jpeg(bgra, width, height, quality);
            if (bytes.Length <= limit) return new(bytes, "jpeg", $"jpeg-q{quality}");
        }
        return null;
    }

    // Last resort once shrinking is exhausted: the lowest JPEG quality of the chain, whatever its size.
    internal static Encoded Smallest(byte[] bgra, int width, int height) =>
        new(Jpeg(bgra, width, height, JpegQualities[^1]), "jpeg", $"jpeg-q{JpegQualities[^1]}");

    // Low `bits` of each colour channel set to zero (gap <= 2^bits - 1 out of 255); alpha untouched.
    internal static byte[] DropLowBits(byte[] bgra, int bits)
    {
        var result = (byte[])bgra.Clone();
        var mask = (byte)(0xFF << bits);
        for (var i = 0; i < result.Length; i++) if ((i & 3) != 3) result[i] &= mask;
        return result;
    }

    // JPEG by GDI+ (part of every Windows): BGRA read as 32 bpp RGB (alpha ignored: PrintWindow often leaves it at 0).
    internal static unsafe byte[] Jpeg(byte[] bgra, int width, int height, int quality)
    {
        Start();
        nint bitmap = 0, stream = 0;
        try
        {
            fixed (byte* scan = bgra)
                Check(GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppRGB, (nint)scan, out bitmap), "GdipCreateBitmapFromScan0");
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, true, out stream));
            long value = Math.Clamp(quality, 1, 100);
            var parameters = new EncoderParameters { Count = 1, Parameter = new EncoderParameter { Guid = QualityParameter, NumberOfValues = 1, Type = 4 /* long */, Value = (nint)(&value) } };
            var encoder = JpegEncoder;
            Check(GdipSaveImageToStream(bitmap, stream, ref encoder, ref parameters), "GdipSaveImageToStream");
            // Bytes written = stream position (IStream::Seek, vtable slot 5); the HGLOBAL may be larger.
            ulong length;
            var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)stream)[5];
            Marshal.ThrowExceptionForHR(seek(stream, 0, 1 /* STREAM_SEEK_CUR */, &length));
            Marshal.ThrowExceptionForHR(GetHGlobalFromStream(stream, out var global));
            var pointer = GlobalLock(global);
            if (pointer == 0) throw new InvalidOperationException("GlobalLock failed");
            try
            {
                var bytes = new byte[(int)length];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { GlobalUnlock(global); }
        }
        finally
        {
            if (stream != 0) Marshal.Release(stream);
            if (bitmap != 0) GdipDisposeImage(bitmap);
        }
    }

    static readonly Guid JpegEncoder = new("557CF401-1A04-11D3-9A73-0000F81EF32E");
    static readonly Guid QualityParameter = new("1D5BE4B5-FA4A-452D-9CDD-5DB35105E7EB");
    const int PixelFormat32bppRGB = 0x00022009;
    static readonly object StartGate = new();
    static nuint token;

    static void Start()
    {
        lock (StartGate)
        {
            if (token != 0) return;
            var input = new StartupInput { Version = 1 };
            Check(GdiplusStartup(out token, ref input, 0), "GdiplusStartup");
        }
    }

    static void Check(int status, string call)
    {
        if (status != 0) throw new InvalidOperationException($"{call} failed with GDI+ status {status}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInput { public uint Version; public nint Callback; public int SuppressBackgroundThread; public int SuppressExternalCodecs; }

    [StructLayout(LayoutKind.Sequential)]
    struct EncoderParameter { public Guid Guid; public uint NumberOfValues; public uint Type; public nint Value; }

    // Nested as in gdiplusimaging.h: the parameter array is aligned on a pointer, after Count.
    [StructLayout(LayoutKind.Sequential)]
    struct EncoderParameters { public uint Count; public EncoderParameter Parameter; }

    [DllImport("gdiplus.dll")] static extern int GdiplusStartup(out nuint token, ref StartupInput input, nint output);
    [DllImport("gdiplus.dll")] static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);
    [DllImport("gdiplus.dll")] static extern int GdipSaveImageToStream(nint image, nint stream, ref Guid encoder, ref EncoderParameters parameters);
    [DllImport("gdiplus.dll")] static extern int GdipDisposeImage(nint image);
    [DllImport("ole32.dll")] static extern int CreateStreamOnHGlobal(nint global, bool deleteOnRelease, out nint stream);
    [DllImport("ole32.dll")] static extern int GetHGlobalFromStream(nint stream, out nint global);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
}
