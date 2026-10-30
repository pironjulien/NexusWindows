using System.Runtime.InteropServices;

// Clic, double clic, clic droit et molette en arriere-plan a un point d'une capture NATIVE (banc commun des trois
// NexusWindows, 21/10/2026, plan de Codex etape 5) : messages postes a l'enfant le plus profond sous le point, premier
// plan et curseur intacts. Le cadre DWM releve a la capture est relu juste avant l'entree : fenetre deplacee ou
// redimensionnee = refus (observer de nouveau). Win32 et WinForms ; Chromium : clic poste, molette par DevTools si la
// cible a un port de debogage (Cdp.cs ; molette postee ignoree, mesure) ; WPF : molette par defilement semantique UI
// Automation (ComWorker scrollAt) ; Tk : clic envoye avec le bouton simule pour son fil, liste defilee par sa barre
// native (TkInput.cs). UWP et le reste de WPF : non applicable (souris postee ignoree). Survol (hover, 26/10, parite
// avec Windows-MCP) : un seul WM_MOUSEMOVE poste au controle sous le point (Win32, WinForms ; envoye a Tk), curseur
// intact ; ailleurs non applicable (vraie souris du helper en repli). Point d'un selecteur : sans cadre de capture, le
// point (centre lu dans un arbre frais) doit seulement etre dans la fenetre.
static class Pointer
{
    [DllImport("user32.dll")] static extern nint ChildWindowFromPointEx(nint parent, Win32.Point point, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint hwnd, ref Win32.Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, char[] name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Win32.Rect rect, int size);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Win32.Point point);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Win32.Rect CaretRect;
    }

    // Trace du dernier clic poste (banc commun : double clic WinForms sans effet, plan de Codex du 22/10) : fenetre visee
    // (HWND, classe, fil), point client, messages postes, fenetre reellement sous le point (WinForms ne declenche Click et
    // DoubleClick que si WindowFromPoint du point du relachement est le controle lui-meme : Control.WmMouseUp) et etat du
    // fil apres l'envoi (capture, focus). La reception des messages postes n'est pas observable sans crochet dans le
    // processus cible : elle se deduit de cet etat, jamais affirmee.
    [ThreadStatic] public static object? LastTrace;

    internal static void InRecipientContext(Func<nint> enter, Action send, Action<nint> restore)
    {
        var previous = enter();
        if (previous == 0) throw new ActionRefusedException("Recipient DPI context unavailable; no pointer message sent");
        try { send(); }
        finally { restore(previous); }
    }

    internal static object RecipientContextSelfTest()
    {
        var checks = 0;
        void Check(bool value) { checks++; if (!value) throw new InvalidOperationException("Pointer DPI context selftest failed"); }
        var sent = 0; var restored = new List<nint>();
        InRecipientContext(() => 42, () => sent++, previous => restored.Add(previous));
        Check(sent == 1 && restored.SequenceEqual(new nint[] { 42 }));
        try { InRecipientContext(() => 43, () => { sent++; throw new IOException("partial message"); }, previous => restored.Add(previous)); }
        catch (IOException) { Check(sent == 2 && restored.Last() == 43); }
        var before = sent;
        try { InRecipientContext(() => 0, () => sent++, previous => restored.Add(previous)); }
        catch (ActionRefusedException) { Check(sent == before && restored.Count == 2); }
        var contexts = new Stack<nint>();
        InRecipientContext(() => 1, () => InRecipientContext(() => 2, () => contexts.Push(3), previous => contexts.Push(previous)), previous => contexts.Push(previous));
        Check(contexts.SequenceEqual(new nint[] { 1, 2, 3 }));
        return new { ok = true, checks, gui = false, inputSent = false };
    }

    const uint WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    static readonly Dictionary<string, (uint Down, uint Up, int Mk, uint Dbl)> Buttons = new()
    {
        ["left"] = (0x0201, 0x0202, 0x0001, 0x0203),
        ["right"] = (0x0204, 0x0205, 0x0002, 0x0206),
        ["middle"] = (0x0207, 0x0208, 0x0010, 0x0209),
    };

    static string ClassOf(nint hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return new string(b, 0, Math.Max(0, n)); }
    static nint Lparam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    static nint DeepestChild(nint top, int x, int y)
    {
        var current = top;
        for (var i = 0; i < 32; i++)
        {
            var p = new Win32.Point { X = x, Y = y };
            ScreenToClient(current, ref p);
            var child = ChildWindowFromPointEx(current, p, 0x0001 | 0x0002 | 0x0004); // CWP_SKIPINVISIBLE | SKIPDISABLED | SKIPTRANSPARENT
            if (child == 0 || child == current) break;
            current = child;
        }
        return current;
    }

    static object? ThreadState(uint thread)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? new { capture = (long)info.Capture, focus = (long)info.Focus, active = (long)info.Active } : null;
    }

    public static Outcome Act(nint window, string action, int x, int y, int[] frame, string button, int count, int deltaY, int deltaX, Action started)
    {
        // Pixels reels de chaque ecran, comme la capture (Capture.cs) : point, cadre et repere client dans le meme espace.
        var previous = Win32.SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        try { return ActPhysicalPixels(window, action, x, y, frame, button, count, deltaY, deltaX, started); }
        finally { if (previous != 0) Win32.SetThreadDpiAwarenessContext(previous); }
    }

    static Outcome ActPhysicalPixels(nint window, string action, int x, int y, int[] frame, string button, int count, int deltaY, int deltaX, Action started)
    {
        var cls = ClassOf(window);
        var chromium = cls.StartsWith("Chrome_WidgetWin_");
        var wpf = cls.StartsWith("HwndWrapper");
        var tk = cls == "TkTopLevel";
        var classic = !chromium && !wpf && cls != "ApplicationFrameWindow" && !cls.StartsWith("Windows.UI.") &&
            cls != "TkTopLevel" && !cls.StartsWith("Mozilla");
        if (action is not ("click" or "wheel" or "hover")) throw new InvalidOperationException("Unknown pointer action");
        if (!classic && !(chromium && action is "click" or "wheel") && !(wpf && action == "wheel") && !(tk && action is "click" or "wheel" or "hover"))
            throw new BackgroundNotApplicableException("This interface ignores posted mouse input (measured): no background technique");
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        if (Win32.IsIconic(window)) throw new ActionRefusedException("Window is minimized: observe again");
        // Geometrie relue : le point de l'image ne vise le meme pixel que si le cadre n'a pas bouge depuis la capture.
        if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var now, 16) != 0 ||
            (frame.Length != 4 && !(action == "hover" && frame.Length == 0)) ||
            (frame.Length == 4 && (now.Left != frame[0] || now.Top != frame[1] || now.Width != frame[2] || now.Height != frame[3])))
            throw new ActionRefusedException("Window moved or resized since the native capture: observe again");
        if (x < now.Left || y < now.Top || x >= now.Right || y >= now.Bottom) throw new ActionRefusedException("Point is outside the window");
        // Une seule technique par geste : celle de l'interface, jamais une seconde apres un envoi (banc commun du 21/10).
        if (action == "wheel" && wpf)
        {
            EmergencyStop.ThrowIfStopped();
            // Entree potentielle des cet appel : un echec ensuite est une issue inconnue (jamais « rien n'a ete envoye »), et
            // la surveillance du premier plan sous jeton s'applique (relecture de Codex, 21/10). Le processus COM ne leve
            // « non applicable » qu'avant le premier pas.
            started();
            return ComBridge.Call(new { op = "scrollAt", windowId = (long)window, runtimeId = Array.Empty<int>(), x, y, deltaY, deltaX });
        }
        // CEF heberge (Steam SDL) : meme preuve CDP stricte que Chromium. Sans port/PID/page/surface attribuables,
        // Cdp refuse AVANT envoi ; jamais de molette postee silencieusement a la place. Clic/hover gardent leur voie.
        if (action == "wheel" && (chromium || ChromiumInput.IsCefHost(window))) return Cdp.Wheel(window, x, y, deltaY, deltaX, started);
        var target = DeepestChild(window, x, y);
        // Tk : barre native collee au widget d'abord ; sans elle, fenetre ENTIEREMENT recouverte deplacee hors ecran le
        // temps de la molette (TkWheel.cs, recette de NexusWindows Claude 3.7.0 du 27/10).
        if (tk && action == "wheel")
            return TkInput.HasBar(target, deltaY != 0) ? TkInput.Scroll(target, deltaY, deltaX, started) : TkWheel.Wheel(window, x, y, deltaY, deltaX, DeepestChild, started);
        // Bouton Tk (Button, Checkbutton, Radiobutton) : fenetre de classe BUTTON mais menee par ButtonProc de Tk
        // (tkWinButton.c, DefWindowProc) ; la souris simulee n'y prend pas (verification reelle du 27/10 : clic au point
        // OCR de « Valider » sans effet) et un WM_COMMAND poste au parent revient au bouton sans l'invoquer (tkWinX.c,
        // reflexion inchangee ; relecture de Codex du 27/10). Tk n'invoque que sur BN_CLICKED (= 0) recu PAR le bouton
        // avec un parametre non nul : c'est son propre invoke (commande, variable de la case), poste, en fond.
        if (tk && action == "click" && button == "left" && count == 1 && Win32.BaseClass(target) == "button")
            return TkInput.Invoke(target, started);
        if (tk)
        {
            var at = new Win32.Point { X = x, Y = y };
            ScreenToClient(target, ref at);
            var ratio = Capture.Scale(target);
            if (ratio != 1.0) at = new Win32.Point { X = (int)Math.Round(at.X / ratio), Y = (int)Math.Round(at.Y / ratio) };
            return action == "hover" ? TkInput.Hover(target, Lparam(at.X, at.Y), started) : TkInput.Click(target, Lparam(at.X, at.Y), button, count, started);
        }
        // WinForms ne leve DoubleClick que si la fenetre au point (hit-test) est le controle lui-meme : point recouvert,
        // double clic poste sans effet (banc commun du 22/10, les trois outils) ; rien n'est envoye, l'entree physique
        // (premier plan bref) s'en charge si elle est permise.
        if (count >= 2 && action == "click" && ClassOf(target).StartsWith("WindowsForms10.", StringComparison.Ordinal) &&
            WindowFromPoint(new Win32.Point { X = x, Y = y }) != target)
            throw new BackgroundNotApplicableException("WinForms double click at a covered point has no effect by messages: nothing was sent");
        EmergencyStop.ThrowIfStopped();
        var sent = 0;
        void Post(uint msg, nint wparam, nint lparam)
        {
            if (sent == 0) started(); // au-dela, toute erreur suit une entree : resultat inconnu, jamais rejoue
            try { Win32.Post(target, msg, wparam, lparam); }
            catch (Exception error) when (sent > 0) { throw new InvalidOperationException($"Partial pointer input ({sent} messages posted); outcome unknown", error); }
            sent++;
        }
        if (action == "wheel")
        {
            if (deltaY != 0) Post(WM_MOUSEWHEEL, (nint)((deltaY & 0xFFFF) << 16), Lparam(x, y));
            if (deltaX != 0) Post(WM_MOUSEHWHEEL, (nint)((deltaX & 0xFFFF) << 16), Lparam(x, y));
            return new("background:messages", "WM_MOUSEWHEEL (screen point)", "sent");
        }
        if (!Buttons.TryGetValue(button, out var b)) throw new InvalidOperationException("Invalid mouse button");
        var client = new Win32.Point { X = x, Y = y };
        var moveOnly = action == "hover";
        ScreenToClient(target, ref client);
        // Repere ou la cible LIT le message : pixels logiques pour une fenetre non adaptee au DPI (ou DPI systeme) ;
        // Le point est deja converti : poster dans le contexte exact du destinataire evite une seconde
        // conversion a la reception (preuve canvas OCR : 544,200 devenait 435,160 a 125 %).
        var scale = Capture.Scale(target);
        if (scale != 1.0) client = new Win32.Point { X = (int)Math.Round(client.X / scale), Y = (int)Math.Round(client.Y / scale) };
        var lp = Lparam(client.X, client.Y);
        var hit = WindowFromPoint(new Win32.Point { X = x, Y = y });
        var thread = Win32.GetWindowThreadProcessId(target, out _);
        var posted = new List<string>();
        void Trace(string state) => LastTrace = new
        {
            target = (long)target, className = ClassOf(target), thread, client = new[] { client.X, client.Y }, screen = new[] { x, y },
            posted = posted.ToArray(), state, hitWindow = (long)hit, hitClass = hit == 0 ? "" : ClassOf(hit), hitIsTarget = hit == target,
            threadAfter = ThreadState(thread),
        };
        try
        {
            var recipientContext = Win32.GetWindowDpiAwarenessContext(target);
            if (recipientContext == 0) throw new ActionRefusedException("Recipient DPI identity unavailable; no pointer message sent");
            InRecipientContext(() => Win32.SetThreadDpiAwarenessContext(recipientContext), () =>
            {
                Post(WM_MOUSEMOVE, 0, lp); posted.Add("WM_MOUSEMOVE");
                for (var i = 0; !moveOnly && i < Math.Clamp(count, 1, 2); i++)
                {
                    Post(i == 1 ? b.Dbl : b.Down, b.Mk, lp);
                    posted.Add($"0x{(i == 1 ? b.Dbl : b.Down):X4}");
                    Post(b.Up, 0, lp); posted.Add($"0x{b.Up:X4}");
                }
            }, previous =>
            {
                if (Win32.SetThreadDpiAwarenessContext(previous) == 0) throw new InvalidOperationException("Pointer DPI context could not be restored");
            });
        }
        catch { Trace("partial"); throw; }
        Trace("posted");
        if (moveOnly) return new("background:messages", "posted WM_MOUSEMOVE (hover)", "sent");
        return new("background:messages", count == 2 ? "posted double click" : $"posted {button} click", "sent");
    }
}
