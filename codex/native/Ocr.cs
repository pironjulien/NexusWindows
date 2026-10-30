using System.Diagnostics;
using System.Runtime.InteropServices;

// OCR Windows natif (Windows.Media.Ocr) en secours d'une fenetre sans arbre UI Automation utile (Tk, canevas, jeu) :
// lignes de texte de l'image avec leur rectangle, que la session rend ciblables au point de cette image (session.mjs).
// Reference de comportement : NexusWindows Claude (ocr.py, 10 a 21 ms par fenetre au labo du 20/10). Lecture seule et
// locale : rien ne sort du poste. Aucune langue OCR installee ou WinRT indisponible : l'OCR est indisponible, jamais
// une erreur d'outil.
static unsafe class Ocr
{
    public const int MaxLines = 120; // une page de texte entiere n'a rien a faire dans une liste d'elements
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
    // Vrai SEULEMENT quand l'absence de l'OCR est prouvee : aucune langue OCR (TryCreateFromUserProfileLanguages rend
    // null), classe WinRT non enregistree, combase sans WinRT. Toute autre panne (allocation, activation de
    // SoftwareBitmap, moteur bloque) reste une panne : missing faux, session.mjs refuse le geste qui a besoin de la
    // lecture (relecture de Codex du 27/10), et la creation est retentee a l'image suivante.
    public static bool Missing { get; private set; }
    const int ClassNotRegistered = unchecked((int)0x80040154); // REGDB_E_CLASSNOTREG

    static string Short(string text) => text.Length <= 160 ? text : text[..160];

    [StructLayout(LayoutKind.Sequential)] struct PlaneDescription { public int StartIndex, Width, Height, Stride; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float X, Y, Width, Height; }

    // Moteur cree une fois (langues du profil de l'utilisateur). Absence prouvee ou moteur bloque : retenus pour la vie
    // du processus (unavailable) ; autre echec : rendu pour cette image seulement (failure), retente a la suivante.
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

    // Lignes lues dans l'image BGRA (positions en pixels de cette image) ; Unavailable : raison de l'absence d'OCR.
    public static (List<Line> Lines, string? Unavailable, long Ms) Read(byte[] bgra, int width, int height)
    {
        var timer = Stopwatch.StartNew();
        lock (Gate)
        {
            if (!Ready()) return ([], unavailable ?? failure, timer.ElapsedMilliseconds);
            // Limite du moteur : image reduite, positions remises a l'echelle.
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
                // Moteur bloque : l'operation vit encore et lit peut-etre le bitmap, garde vivant ; OCR coupe pour ce
                // processus (le helper se termine apres 30 s sans requete et repart neuf).
                keep = true;
                unavailable = Short("OCR engine stuck: " + error.Message);
                return ([], unavailable, timer.ElapsedMilliseconds);
            }
            catch (Exception error) { return ([], Short("OCR failed: " + error.Message), timer.ElapsedMilliseconds); }
            finally { if (!keep) WinRt.Close(bitmap); }
        }
    }

    // SoftwareBitmap BGRA8 rempli par son tampon (LockBuffer en ecriture, pas de ligne : Stride du plan).
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

    // Rectangle d'une ligne : union des rectangles de ses mots (le moteur n'en donne pas pour la ligne).
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

    // Autotest hors ligne : texte dessine par GDI dans une image en memoire, relu par le vrai moteur ; rien a l'ecran.
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint precision, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(nint dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] static extern bool PatBlt(nint dc, int x, int y, int w, int h, uint rop);
    [DllImport("gdi32.dll")] static extern int SetBkMode(nint dc, int mode);
    public static int SelfTest()
    {
        const int width = 900, height = 200;
        const string expected = "Nexus Windows OCR 2026";
        var screen = Win32.GetDC(0);
        var dc = Win32.CreateCompatibleDC(screen);
        Win32.ReleaseDC(0, screen);
        var header = new Win32.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dib = Win32.CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
        var old = Win32.SelectObject(dc, dib);
        var font = CreateFontW(-48, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
        var oldFont = Win32.SelectObject(dc, font);
        PatBlt(dc, 0, 0, width, height, 0x00FF0062 /* WHITENESS */);
        SetBkMode(dc, 1);
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
        // Classement des pannes d'initialisation : seule une absence prouvee vaut « missing » (relecture de Codex du 27/10).
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
        // Image plus grande que la limite du moteur : positions remises a l'echelle de l'image donnee.
        var wide = new byte[(int)(maxSide + 600) * 40 * 4];
        Array.Fill(wide, (byte)255);
        var (_, wideReason, _) = Read(wide, (int)maxSide + 600, 40);
        if (wideReason != null) { Console.WriteLine("fail wide " + wideReason); failures++; } else Console.WriteLine("ok wide image reduced to the engine limit");
        return failures == 0 ? 0 : 1;
    }
}
