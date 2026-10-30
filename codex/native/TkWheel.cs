using System.Runtime.InteropServices;

// La fenetre deplacee pour la molette Tk n'a pas pu revenir a sa place : echec de l'action, jamais une issue simplement
// incertaine (le client la rend terminale, aucun repli).
sealed class WindowNotRestoredException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

// Molette Tk d'une fenetre ENTIEREMENT recouverte (recette de NexusWindows Claude 3.7.0, 27/10, relue cinq fois par
// Codex) : Tk renvoie la molette a la fenetre visible AU POINT du message (tkWinX.c) ; recouverte, elle est perdue. La
// fenetre, entierement cachee, est deplacee au-dela du bureau virtuel (sans activation ni changement d'ordre), la molette
// y est postee au MEME controle (releve avant le deplacement), puis la fenetre revient, relue au rectangle entier.
static class TkWheel
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern int GetWindowRgnBox(nint hwnd, out Win32.Rect rect);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Win32.Point point);

    const uint GW_HWNDPREV = 3, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    const int GWL_EXSTYLE = -20, SM_XVIRTUALSCREEN = 76, SM_CXVIRTUALSCREEN = 78, SettleMs = 150, HoldMs = 300;
    // Styles qui ne prouvent pas l'opacite : translucide (LAYERED), traversee (TRANSPARENT), composition sans bitmap
    // (NOREDIRECTIONBITMAP : client possiblement entierement transparent).
    const int NotOpaque = 0x00000020 | 0x00080000 | 0x00200000;

    static Win32.Rect Frame(nint hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, 16) == 0) return r;
        Win32.GetWindowRect(hwnd, out r);
        return r;
    }

    // Rayon majore des coins arrondis DWM : 8 px a 100 %, lineaire avec l'echelle du MONITEUR (GetDpiForWindow rend 96
    // pour une application non adaptee au DPI), + 2 px ; echelle illisible : 0 (la fenetre ne couvre rien).
    static int Corner(nint hwnd)
    {
        if (Win32.GetDpiForMonitor(Win32.MonitorFromWindow(hwnd, 2), 0, out var dpi, out _) != 0 || dpi == 0) return 0;
        var scale = Math.Max(dpi, Win32.GetDpiForWindow(hwnd));
        return (int)((8 * scale + 95) / 96) + 2;
    }

    // box prive de cut : jusqu'a quatre rectangles.
    internal static IEnumerable<Win32.Rect> Minus(Win32.Rect box, Win32.Rect cut)
    {
        if (cut.Left >= box.Right || cut.Right <= box.Left || cut.Top >= box.Bottom || cut.Bottom <= box.Top) { yield return box; yield break; }
        if (cut.Top > box.Top) yield return new() { Left = box.Left, Top = box.Top, Right = box.Right, Bottom = cut.Top };
        if (cut.Bottom < box.Bottom) yield return new() { Left = box.Left, Top = cut.Bottom, Right = box.Right, Bottom = box.Bottom };
        var top = Math.Max(box.Top, cut.Top);
        var bottom = Math.Min(box.Bottom, cut.Bottom);
        if (cut.Left > box.Left) yield return new() { Left = box.Left, Top = top, Right = cut.Left, Bottom = bottom };
        if (cut.Right < box.Right) yield return new() { Left = cut.Right, Top = top, Right = box.Right, Bottom = bottom };
    }

    // Couverture pure : cadre de la cible prive de la croix de chaque couvrant (son cadre sans ses quatre coins).
    internal static bool Covered(Win32.Rect target, IEnumerable<(Win32.Rect Frame, int Corner)> covers)
    {
        List<Win32.Rect> left = [target];
        foreach (var (c, k) in covers)
        {
            if (left.Count == 0) break;
            Win32.Rect[] cross =
            [
                new() { Left = c.Left + k, Top = c.Top, Right = c.Right - k, Bottom = c.Bottom },
                new() { Left = c.Left, Top = c.Top + k, Right = c.Right, Bottom = c.Bottom - k },
            ];
            foreach (var cut in cross) left = left.SelectMany(box => Minus(box, cut)).ToList();
        }
        return left.Count == 0;
    }

    // Aucun pixel de la fenetre n'est visible : seules comptent les fenetres au-dessus d'elle (ordre z reel) visibles, non
    // reduites, non masquees par DWM, sans style NotOpaque ni region SetWindowRgn, d'echelle lisible.
    static bool FullyCovered(nint top)
    {
        var covers = new List<(Win32.Rect, int)>();
        for (var above = Win32.GetWindow(top, GW_HWNDPREV); above != 0; above = Win32.GetWindow(above, GW_HWNDPREV))
        {
            if (!Win32.IsWindowVisible(above) || Win32.IsIconic(above) || Win32.IsCloaked(above)) continue;
            if ((Win32.GetWindowLongW(above, GWL_EXSTYLE) & NotOpaque) != 0 || GetWindowRgnBox(above, out _) != 0) continue;
            var k = Corner(above);
            if (k > 0) covers.Add((Frame(above), k));
        }
        return Covered(Frame(top), covers);
    }

    static bool SamePlace(nint top, Win32.Rect place) =>
        Win32.GetWindowRect(top, out var now) && now.Left == place.Left && now.Top == place.Top && now.Right == place.Right && now.Bottom == place.Bottom;

    static bool InFront(nint top)
    {
        var fg = Win32.GetForegroundWindow();
        return fg != 0 && Win32.GetWindowThreadProcessId(fg, out _) == Win32.GetWindowThreadProcessId(top, out _);
    }

    // Refus, rien envoye (BackgroundNotApplicableException) : fenetre agrandie ou en partie visible, autre controle au point
    // une fois dehors, fenetre ramenee devant par l'utilisateur. Fenetre non remise : WindowNotRestoredException. Le delai
    // tenu dehors n'est pas un accuse de traitement ; event.x/y de Tk viennent du curseur (GetMessagePos).
    public static Outcome Wheel(nint top, int x, int y, int deltaY, int deltaX, Func<nint, int, int, nint> deepest, Action started)
    {
        if ((deltaY == 0) == (deltaX == 0)) throw new BackgroundNotApplicableException("Tk wheel off screen: one axis at a time");
        if (IsZoomed(top) || Win32.IsIconic(top)) throw new BackgroundNotApplicableException("A maximized or minimized Tk window is never moved");
        if (!FullyCovered(top)) throw new BackgroundNotApplicableException("The Tk window is partly visible: it is never moved off screen");
        if (!Win32.GetWindowRect(top, out var place)) throw new BackgroundNotApplicableException("The Tk window position cannot be read");
        var expected = deepest(top, x, y);
        var dx = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN) + 50 - place.Left;
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
        EmergencyStop.ThrowIfStopped(); // la fenetre n'a pas bouge
        var posted = false;
        Exception? failure = null;
        try
        {
            if (!SetWindowPos(top, 0, place.Left + dx, place.Top, 0, 0, flags)) throw new BackgroundNotApplicableException("The Tk window could not be moved off screen");
            Thread.Sleep(SettleMs);
            if (deepest(top, x + dx, y) != expected || WindowFromPoint(new Win32.Point { X = x + dx, Y = y }) != expected)
                throw new BackgroundNotApplicableException("Off screen, the point does not fall on the same control");
            EmergencyStop.ThrowIfStopped();
            if (InFront(top)) throw new BackgroundNotApplicableException("The Tk window was brought forward meanwhile"); // seul Alt+Tab l'atteint dehors
            started();
            var lp = (nint)((((long)y & 0xFFFF) << 16) | ((long)(x + dx) & 0xFFFF));
            Win32.Post(expected, deltaY != 0 ? WM_MOUSEWHEEL : WM_MOUSEHWHEEL, (nint)(((deltaY != 0 ? deltaY : deltaX) & 0xFFFF) << 16), lp);
            posted = true;
            Thread.Sleep(HoldMs);
        }
        catch (Exception error) { failure = error; }
        // Retour relu quelle que soit l'issue ; refuse ou corrige par l'application (WM_WINDOWPOSCHANGING) : un second essai
        // de placement, jamais une seconde molette.
        var back = SetWindowPos(top, 0, place.Left, place.Top, 0, 0, flags) && SamePlace(top, place);
        if (!back)
        {
            Thread.Sleep(50);
            back = SetWindowPos(top, 0, place.Left, place.Top, place.Width, place.Height, SWP_NOZORDER | SWP_NOACTIVATE) && SamePlace(top, place);
        }
        if (!back)
            throw new WindowNotRestoredException($"The window could not be put back exactly at ({place.Left}, {place.Top}): " +
                (posted ? "the wheel was sent, its effect is unknown" : "nothing was sent") + ". Look at it before anything else.", failure);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return new("background:messages", "Tk wheel off screen (window entirely covered, moved and put back)", "sent");
    }

    // Tests hors ligne de la geometrie de couverture (le meme jeu que NexusWindows Claude).
    public static int SelfTest()
    {
        static Win32.Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
        var target = R(100, 100, 300, 200);
        var checks = new (string Name, bool Ok)[]
        {
            ("one cover", Covered(target, [(R(0, 0, 1000, 1000), 10)])),
            ("one-pixel strip refused", !Covered(target, [(R(0, 0, 110, 1000), 10), (R(111, 0, 1000, 1000), 10)])),
            ("two adjacent covers", Covered(target, [(R(0, 0, 200, 1000), 10), (R(200, 0, 1000, 1000), 10)])),
            ("corner at 250 % refused", !Covered(target, [(R(88, 100, 1000, 1000), 22)])),
            ("no cover", !Covered(target, [])),
        };
        foreach (var (name, ok) in checks) Console.WriteLine($"{(ok ? "ok" : "FAILED")} {name}");
        return checks.All(c => c.Ok) ? 0 : 1;
    }
}
