using System.Runtime.InteropServices;

// Touches et raccourcis en arriere-plan (banc commun des trois NexusWindows, 21/10/2026 ; raccourcis portes de
// NexusClaude et NexusAGY le 25/10, banc de parite : Ctrl+K 0/3 ici, 3/3 chez eux) : WM_KEYDOWN/WM_KEYUP postes au focus
// du fil de la cible ; pour Ctrl/Maj, l'etat clavier de CE fil est simule le temps du traitement (AttachThreadInput au
// seul fil de la cible, jamais a celui du premier plan), tenu WaitForInputIdle (2 s au plus) puis au moins HoldMs, puis
// seules les touches simulees reprennent leur valeur. WaitForInputIdle n'est pas un accuse : l'effet reste relu par la
// session, jamais annonce (« sent »). Tk : focus simule (WM_SETFOCUS envoye, WM_KILLFOCUS poste apres les touches).
// Chromium : activation simulee (ChromiumInput.Keys). Alt et Windows : refus (les raccourcis systeme attendent
// WM_SYSKEYDOWN). WPF, UWP, Firefox : non applicable (ignorent les touches postees, mesure).
static class Keys
{
    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Win32.Rect CaretRect; }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyExW(uint code, uint type, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanExW(char ch, nint layout);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint WaitForInputIdle(nint process, uint ms);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, char[] name, int max);

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeout, out nint result);

    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
    // Modificateurs simules tenus au moins 0,5 s apres l'envoi : a 0,15 s, Ctrl+K poste a une fenetre Win32 recouverte
    // arrivait parfois apres la restauration (mesure de NexusClaude du 23/10, bench/miss_probes.py).
    public const int HoldMs = 500;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = 0x0D, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["BackSpace"] = 0x08,
        ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Page_Up"] = 0x21, ["Prior"] = 0x21, ["Page_Down"] = 0x22, ["Next"] = 0x22, ["space"] = 0x20, ["Insert"] = 0x2D,
        ["apps"] = 0x5D, ["menu"] = 0x5D, ["printscreen"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14,
        ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
    };
    static readonly HashSet<int> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D];

    static string ClassOf(nint hwnd) { var b = new char[256]; var n = GetClassNameW(hwnd, b, b.Length); return new string(b, 0, Math.Max(0, n)); }

    // « Control_L+s », « ctrl+k », « Return » : (modificateurs, touche). Alt/Windows : refus, rien n'est envoye.
    // Disposition du fil CIBLE (relecture de Codex, 21/10) : celle du helper traduisait « 1 » d'un clavier QWERTY en
    // « & » dans une cible AZERTY, et sa Maj implicite pouvait etre fausse.
    static int Modifier(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "control_l" or "control_r" => VK_CONTROL,
        "shift" or "shift_l" or "shift_r" => VK_SHIFT,
        "alt" or "alt_l" or "alt_r" => 0x12,
        "win" or "super" or "meta" or "windows" => 0x5B,
        _ => 0,
    };
    static (List<int> Mods, int Vk) ParseCore(string spec, bool physical, Func<char, short> scanCharacter)
    {
        var text = spec.Trim().Replace(" ", "");
        string[] parts = text == "+" ? ["+"] : text.Length > 2 && text.EndsWith("++", StringComparison.Ordinal)
            ? [.. text[..^2].Split('+'), "+"] : text.Split('+');
        if (parts.Any(part => part.Length == 0)) throw new BackgroundNotApplicableException("Incomplete key combination (missing key)");
        var mods = new List<int>();
        void RequireAllowed(int vk)
        {
            if (!physical && vk is 0x12 or 0x5B or 0x5C) throw new BackgroundNotApplicableException("Alt/Windows keys have no safe background technique (system keys)");
        }
        foreach (var m in parts[..^1])
        {
            // Historical physical alias; the Claude adapter canonicalizes the named menu key to apps.
            var modifier = physical && m.Equals("menu", StringComparison.OrdinalIgnoreCase) ? 0x12 : Modifier(m);
            if (modifier == 0) throw new BackgroundNotApplicableException($"Unknown modifier « {m} »");
            RequireAllowed(modifier);
            if (!mods.Contains(modifier)) mods.Add(modifier);
        }
        var key = parts[^1];
        int vk;
        if (parts.Length == 1 && Modifier(key) is var solo && solo != 0) { RequireAllowed(solo); vk = solo; }
        else if (Named.TryGetValue(key, out var named)) vk = named;
        else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = 0x6F + f;
        else if (key.Length == 1)
        {
            var scan = scanCharacter(key[0]);
            if (scan == -1 || ((scan >> 8) & 0x6) != 0) throw new BackgroundNotApplicableException("Key needs AltGr/Ctrl to type: no background technique");
            vk = scan & 0xFF;
            if (((scan >> 8) & 1) != 0 && !mods.Contains(VK_SHIFT)) mods.Add(VK_SHIFT);
        }
        else throw new BackgroundNotApplicableException($"Unknown key « {key} »");
        return (mods, vk);
    }
    static (List<int> Mods, int Vk) Parse(string spec, nint layout) => ParseCore(spec, false, ch => VkKeyScanExW(ch, layout));

    // Real input supports Alt/Windows too; the background parser still refuses system modifiers before input.
    public static (List<int> Mods, int Vk) ParseFor(string spec, nint layout) => ParseCore(spec, true, ch => VkKeyScanExW(ch, layout));
    public static bool IsExtended(int vk) => Extended.Contains(vk);
    internal static void CheckPhysicalGrammar()
    {
        var layout = GetKeyboardLayout(0);
        if (!ParseFor("alt+f4", layout).Mods.Contains(0x12) || !ParseFor("win+r", layout).Mods.Contains(0x5B)) throw new InvalidOperationException("Physical system modifier parsing failed");
        try { Parse("alt+f4", layout); throw new InvalidOperationException("Background Alt unexpectedly accepted"); }
        catch (BackgroundNotApplicableException) { }
    }
    internal static int KeyGrammarAssertions()
    {
        var count = 0;
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException("Key grammar: " + why); count++; }
        short Scan(char ch) => ch switch { '+' => 0x1BB, 'a' => 0x41, '1' => 0x131, '#' => 0x6DC, _ => -1 };
        (List<int> Mods, int Vk) Read(string spec, bool physical = false) => ParseCore(spec, physical, Scan);
        void Refused(string spec, bool physical = false)
        {
            try { Read(spec, physical); } catch (BackgroundNotApplicableException) { count++; return; }
            throw new InvalidOperationException("Key grammar accepted " + spec);
        }
        foreach (var physical in new[] { false, true })
        {
            for (var f = 1; f <= 24; f++) Check(Read("f" + f, physical).Vk == 0x6F + f, "F1-F24");
            foreach (var pair in new Dictionary<string, int> { ["apps"] = 0x5D, ["printscreen"] = 0x2C, ["pause"] = 0x13, ["capslock"] = 0x14,
                ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE })
                Check(Read(pair.Key, physical).Vk == pair.Value, "named canonical key");
            var plus = Read("+", physical); Check(plus.Vk == 0xBB && plus.Mods.SequenceEqual(new[] { VK_SHIFT }), "literal plus uses injected target layout");
            var ctrlPlus = Read("ctrl++", physical); Check(ctrlPlus.Vk == 0xBB && ctrlPlus.Mods.SequenceEqual(new[] { VK_CONTROL, VK_SHIFT }), "final ++ means plus key");
            var dedup = Read("ctrl+Control_L+shift+shift+a", physical); Check(dedup.Mods.SequenceEqual(new[] { VK_CONTROL, VK_SHIFT }), "duplicate modifiers send once");
            Check(Read("ctrl", physical).Vk == VK_CONTROL && Read("ctrl", physical).Mods.Count == 0, "control alone");
            Check(Read("shift", physical).Vk == VK_SHIFT && Read("shift", physical).Mods.Count == 0, "shift alone");
            Check(Read("1", physical).Mods.SequenceEqual(new[] { VK_SHIFT }), "layout-dependent digit keeps implicit shift");
            foreach (var malformed in new[] { "", "ctrl+", "ctrl+shift+", "++", "ctrl+++", "ctrl++a", "+a", "a+enter", "ctrl+shift", "f0", "f25", "ctrl+f25", "#" }) Refused(malformed, physical);
        }
        foreach (var system in new[] { "alt", "alt_l", "alt_r", "win", "super", "meta", "windows", "alt+f4", "win+a", "ctrl+win+a" })
        {
            Refused(system);
            Check(Read(system, true).Vk != 0, "physical system key remains available");
        }
        Check(IsExtended(Read("apps").Vk), "apps carries the extended flag");
        Check(Read("menu").Vk == 0x5D, "named menu aliases apps");
        return count;
    }

    static nint KeyLparam(int vk, bool up, nint layout)
    {
        long lp = 1 | ((long)(MapVirtualKeyExW((uint)vk, 0, layout) & 0xFF) << 16);
        if (Extended.Contains(vk)) lp |= 1L << 24;
        if (up) lp |= (1L << 30) | (1L << 31);
        return (nint)lp;
    }

    static bool InFront(uint thread) { var fg = Win32.GetForegroundWindow(); return fg != 0 && Win32.GetWindowThreadProcessId(fg, out _) == thread; }

    // Etat clavier simule pour le seul fil cible ; en sortie, seules les touches simulees reprennent leur valeur d'avant
    // (leur etat PHYSIQUE si Julien a pris la cible entre-temps) ; lecture impossible : l'etat d'avant, entier.
    sealed class SimulatedModifiers : IDisposable
    {
        readonly uint own = Win32.GetCurrentThreadId(), thread, pid;
        readonly List<int> vks;
        readonly byte[] saved = new byte[256];
        bool attached;
        public SimulatedModifiers(nint window, List<int> vks)
        {
            this.vks = vks;
            thread = Win32.GetWindowThreadProcessId(window, out pid);
            if (thread == 0 || InFront(thread)) throw new BackgroundNotApplicableException("The target shares the foreground thread: simulated keyboard state refused");
            if (!Win32.AttachThreadInput(own, thread, true)) throw new BackgroundNotApplicableException("AttachThreadInput refused: nothing was sent");
            attached = true;
            GetKeyboardState(saved);
            var state = (byte[])saved.Clone();
            foreach (var vk in vks) state[vk] = 0x80;
            SetKeyboardState(state);
        }
        public void Dispose()
        {
            if (!attached) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var process = OpenProcess(0x00100000 | 0x1000 /* SYNCHRONIZE | QUERY_LIMITED */, false, pid);
                if (process != 0) { try { WaitForInputIdle(process, 2000); } finally { CloseHandle(process); } }
                var left = HoldMs - (int)clock.ElapsedMilliseconds;
                if (left > 0) Thread.Sleep(left);
            }
            finally
            {
                try
                {
                    var now = new byte[256];
                    if (!GetKeyboardState(now)) Array.Copy(saved, now, 256);
                    var taken = InFront(thread);
                    foreach (var vk in vks) now[vk] = taken ? (byte)(((GetAsyncKeyState(vk) & 0x8000) != 0 ? 0x80 : 0) | (saved[vk] & 1)) : saved[vk];
                    SetKeyboardState(now);
                }
                finally { Win32.AttachThreadInput(own, thread, false); attached = false; }
            }
        }
    }

    // Touche ou raccourci postes a `target` ; modificateurs simules pour le fil de `owner` (la fenetre visee).
    // started() avant le premier message : au-dela, toute erreur est une issue inconnue (jamais rejouee).
    internal static string PostCombo(nint owner, nint target, List<int> mods, int vk, nint layout, Action started)
    {
        var sent = 0;
        void Post(uint msg, int key, bool up)
        {
            if (sent == 0) { McpServer.BeforeKeySequenceInput(); McpServer.NativeInteraction.CheckInputLease(); started(); }
            try { Win32.Post(target, msg, key, KeyLparam(key, up, layout)); }
            catch (Exception error) when (sent > 0) { throw new InvalidOperationException($"Partial key input ({sent} messages posted); outcome unknown", error); }
            sent++;
        }
        using (mods.Count > 0 ? new SimulatedModifiers(owner, mods) : null)
        {
            foreach (var m in mods) Post(WM_KEYDOWN, m, false);
            Post(WM_KEYDOWN, vk, false);
            Post(WM_KEYUP, vk, true);
            for (var i = mods.Count - 1; i >= 0; i--) Post(WM_KEYUP, mods[i], true);
        }
        return mods.Count > 0 ? "WM_KEYDOWN/WM_KEYUP, simulated keyboard state" : "WM_KEYDOWN/WM_KEYUP";
    }

    public static (List<int> Mods, int Vk, nint Layout) ParseForWindow(nint window, string spec)
    {
        var layout = GetKeyboardLayout(Win32.GetWindowThreadProcessId(window, out _));
        var (mods, vk) = Parse(spec, layout);
        return (mods, vk, layout);
    }

    public static Outcome Send(nint window, string spec, Action started)
    {
        var cls = ClassOf(window);
        var tk = cls == "TkTopLevel";
        var classic = !cls.StartsWith("Chrome_WidgetWin_") && !cls.StartsWith("HwndWrapper") && cls != "ApplicationFrameWindow" &&
            !cls.StartsWith("Windows.UI.") && !tk && !cls.StartsWith("Mozilla");
        if (!classic && !tk) throw new BackgroundNotApplicableException("This interface ignores posted keys (measured): no background technique");
        var (mods, vk, layout) = ParseForWindow(window, spec);
        var thread = Win32.GetWindowThreadProcessId(window, out _);
        if (thread == 0) throw new BackgroundNotApplicableException("Window thread unknown");
        if (InFront(thread)) throw new BackgroundNotApplicableException("The target shares the foreground thread: posted keys refused");
        EmergencyStop.ThrowIfStopped();
        nint Focused()
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && info.Focus != 0 && (info.Focus == window || IsChild(window, info.Focus)) ? info.Focus : window;
        }
        if (!tk) return new("background:messages", PostCombo(window, Focused(), mods, vk, layout, started), "sent");
        // Tk ne route le clavier qu'a sa fenetre focalisee, tenue de WM_SETFOCUS (FocusIn) : focus simule ENVOYE a sa seule
        // fenetre (jamais WM_ACTIVATE), touches postees, puis WM_KILLFOCUS POSTE (traite apres elles). Mesure de NexusClaude
        // du 23/10 : Ctrl+K recu, fenetre recouverte, premier plan et curseur intacts.
        if (SendMessageTimeoutW(window, WM_SETFOCUS, 0, 0, 0x0002 /* ABORTIFHUNG */, 1000, out _) == 0)
            throw new BackgroundNotApplicableException("Tk did not answer WM_SETFOCUS within 1 s: no key was sent");
        try
        {
            if (InFront(thread)) throw new BackgroundNotApplicableException("The Tk window came to the foreground: no key was sent");
            return new("background:messages", PostCombo(window, Focused(), mods, vk, layout, started) + ", simulated focus", "sent");
        }
        finally
        {
            // Reprise entre-temps par Julien : son vrai focus n'est pas defait.
            if (!InFront(thread)) try { Win32.Post(window, WM_KILLFOCUS, 0, 0); } catch (InvalidOperationException) { }
        }
    }
}
