using System.Runtime.InteropServices;
using System.Text;

// Declarations Win32 partagees par les techniques d'arriere-plan, la capture, les fenetres liees et la presence.
static partial class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Empty => Width <= 0 || Height <= 0;
        public readonly Rect Intersect(Rect o) => new() { Left = Math.Max(Left, o.Left), Top = Math.Max(Top, o.Top), Right = Math.Min(Right, o.Right), Bottom = Math.Min(Bottom, o.Bottom) };
    }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct LastInputInfo { public uint Size; public uint Time; }

    public const uint WM_COMMAND = 0x0111, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_INITMENU = 0x0116, WM_INITMENUPOPUP = 0x0117, WM_UNINITMENUPOPUP = 0x0125;
    public const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
    public const uint EM_SETSEL = 0x00B1, EM_REPLACESEL = 0x00C2;
    public const uint CB_GETCOUNT = 0x0146, CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_GETLBTEXTLEN = 0x0149, CB_SETCURSEL = 0x014E,
        CB_SHOWDROPDOWN = 0x014F, CB_GETDROPPEDSTATE = 0x0157;
    public const uint LB_SETCURSEL = 0x0186, LB_GETCURSEL = 0x0188, LB_GETTEXT = 0x0189, LB_GETTEXTLEN = 0x018A, LB_GETCOUNT = 0x018B;
    public const uint TCM_GETITEMCOUNT = 0x1304, TCM_GETCURSEL = 0x130B, TCM_SETCURFOCUS = 0x1330;
    public const int GWL_STYLE = -16;
    public const uint GW_OWNER = 4, GW_ENABLEDPOPUP = 6, GA_ROOT = 2;
    public const int ES_MULTILINE = 0x4, ES_PASSWORD = 0x20, ES_READONLY = 0x800;
    public const int LBS_MULTIPLESEL = 0x8, LBS_EXTENDEDSEL = 0x800;
    public const int WS_DISABLED = 0x08000000;

    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref Point point);
    [LibraryImport("user32.dll")] private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wparam, ref Rect lparam, uint flags, uint timeout, out nint result);
    // Rectangle (client) de l'element `index` d'une LISTBOX (LB_GETITEMRECT), sans attente infinie (SMTO_ABORTIFHUNG).
    public static bool ItemRect(nint list, int index, out Rect rect)
    {
        rect = default;
        return SendMessageTimeoutW(list, 0x0198, index, ref rect, 0x0002, 1000, out var ok) != 0 && ok != -1;
    }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LastInputInfo info);
    [LibraryImport("kernel32.dll")] public static partial uint GetTickCount();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static partial int DwmGetWindowAttributeInt(nint hwnd, int attribute, out int value, int size);
    // Fenetre masquee par le gestionnaire de fenetres (bureau virtuel, application suspendue) : DWMWA_CLOAKED.
    public static bool IsCloaked(nint hwnd) => DwmGetWindowAttributeInt(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint hwnd, uint command);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetParent(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetDlgCtrlID(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetWindowLongW(nint hwnd, int index);
    [LibraryImport("user32.dll")] public static partial nint GetNextDlgGroupItem(nint dialog, nint control, [MarshalAs(UnmanagedType.Bool)] bool previous);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetClassNameW(nint hwnd, [Out] char[] name, int max);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetWindowTextW(nint hwnd, [Out] char[] text, int max);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowExW(nint parent, nint after, string? cls, string? title);
    public delegate bool EnumProc(nint hwnd, nint data);
    // Window enumeration without a marshalled delegate (trimming and Native AOT): the native callback is a static
    // UnmanagedCallersOnly function that reaches the managed callback through a GCHandle. An exception thrown by the
    // callback never crosses the native frames: it stops the enumeration and is rethrown here, as before.
    public static unsafe bool EnumWindows(EnumProc callback, nint data) => Enumerate(callback, data, 0, children: false);
    public static unsafe bool EnumChildWindows(nint parent, EnumProc callback, nint data) => Enumerate(callback, data, parent, children: true);
    sealed class Enumeration(EnumProc callback, nint data)
    {
        public readonly EnumProc Callback = callback;
        public readonly nint Data = data;
        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure;
    }
    static unsafe bool Enumerate(EnumProc callback, nint data, nint parent, bool children)
    {
        var state = new Enumeration(callback, data);
        var handle = GCHandle.Alloc(state);
        bool result;
        try
        {
            result = children ? NativeEnumChildWindows(parent, &EnumerationStep, GCHandle.ToIntPtr(handle)) : NativeEnumWindows(&EnumerationStep, GCHandle.ToIntPtr(handle));
        }
        finally { handle.Free(); }
        state.Failure?.Throw();
        return result;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static int EnumerationStep(nint hwnd, nint state)
    {
        var enumeration = (Enumeration)GCHandle.FromIntPtr(state).Target!;
        try { return enumeration.Callback(hwnd, enumeration.Data) ? 1 : 0; }
        catch (Exception error)
        {
            enumeration.Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }
    [LibraryImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool NativeEnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint data);
    [LibraryImport("user32.dll", EntryPoint = "EnumChildWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool NativeEnumChildWindows(nint parent, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint data);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint msg, nint wparam, nint lparam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(nint hwnd, uint msg, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutText(nint hwnd, uint msg, nint wparam, string lparam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutBuffer(nint hwnd, uint msg, nint wparam, [Out] char[] lparam, uint flags, uint timeout, out nint result);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("dwmapi.dll")] public static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);

    // Menus Win32 (HMENU) : objets utilisateur globaux, lisibles depuis un autre processus.
    [LibraryImport("user32.dll")] public static partial nint GetMenu(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetMenuItemCount(nint menu);
    [LibraryImport("user32.dll")] public static partial nint GetSubMenu(nint menu, int position);
    [LibraryImport("user32.dll")] public static partial uint GetMenuItemID(nint menu, int position);
    [LibraryImport("user32.dll")] public static partial uint GetMenuState(nint menu, uint id, uint flags);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GetMenuStringW(nint menu, uint id, [Out] char[] text, int max, uint flags);

    // DPI
    [LibraryImport("user32.dll")] public static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] public static partial nint GetWindowDpiAwarenessContext(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int GetAwarenessFromDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint hwnd);

    // GDI
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint dc, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    const uint SMTO_ABORTIFHUNG = 0x2;
    const uint MessageTimeoutMs = 2000;

    // SendMessage borne : une application figee ne bloque pas le helper ; null = pas de reponse.
    public static nint? Send(nint hwnd, uint msg, nint wparam = 0, nint lparam = 0) =>
        SendMessageTimeout(hwnd, msg, wparam, lparam, SMTO_ABORTIFHUNG, MessageTimeoutMs, out var result) == 0 ? null : result;
    public static nint? SendText(nint hwnd, uint msg, nint wparam, string text) =>
        SendMessageTimeoutText(hwnd, msg, wparam, text, SMTO_ABORTIFHUNG, MessageTimeoutMs, out var result) == 0 ? null : result;
    public static string? SendGetText(nint hwnd, uint lengthMsg, uint textMsg, nint index, bool indexed)
    {
        var length = indexed ? Send(hwnd, lengthMsg, index) : Send(hwnd, lengthMsg);
        if (length is null || length < 0 || length > 0x100000) return null;
        var buffer = new char[(int)length + 2];
        var copied = indexed
            ? SendMessageTimeoutBuffer(hwnd, textMsg, index, buffer, SMTO_ABORTIFHUNG, MessageTimeoutMs, out var r1) == 0 ? -1 : r1
            : SendMessageTimeoutBuffer(hwnd, textMsg, buffer.Length, buffer, SMTO_ABORTIFHUNG, MessageTimeoutMs, out var r2) == 0 ? -1 : r2;
        if (copied < 0) return null;
        return new string(buffer, 0, (int)Math.Min(copied, (nint)(buffer.Length - 1)));
    }
    public static string WindowText(nint hwnd) => SendGetText(hwnd, WM_GETTEXTLENGTH, WM_GETTEXT, 0, false) ?? throw new BackgroundNotApplicableException("Control does not answer text requests");

    public static string ClassName(nint hwnd)
    {
        var name = new char[256];
        var length = GetClassNameW(hwnd, name, name.Length);
        return length > 0 ? new string(name, 0, length) : "";
    }
    // Text of a NUL-terminated buffer filled by a Win32 call (what a StringBuilder argument used to give).
    internal static string Terminated(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }
    // Classe Win32 sous-jacente, en minuscules : « WindowsForms10.BUTTON.app.0.x » -> « button ».
    public static string BaseClass(nint hwnd)
    {
        var cls = ClassName(hwnd).ToLowerInvariant();
        if (cls.StartsWith("windowsforms", StringComparison.Ordinal))
        {
            var parts = cls.Split('.');
            if (parts.Length > 1) cls = parts[1];
        }
        return cls;
    }
    public static string Title(nint hwnd)
    {
        var text = new char[512];
        var length = GetWindowTextW(hwnd, text, text.Length);
        return length > 0 ? new string(text, 0, length) : "";
    }
    public static string MenuLabel(nint menu, int position)
    {
        var text = new char[256];
        var length = GetMenuStringW(menu, (uint)position, text, text.Length, 0x400 /* MF_BYPOSITION */);
        return length > 0 ? new string(text, 0, length) : "";
    }
    public static int Style(nint hwnd) => GetWindowLongW(hwnd, GWL_STYLE);
    public static int ExStyle(nint hwnd) => GetWindowLongW(hwnd, -20 /* GWL_EXSTYLE */);
    // Surgissante transitoire : WS_POPUP sans barre de titre qui ne s'active pas (WS_EX_NOACTIVATE : liste deroulante,
    // infobulle, surgissante WPF), ou liste ouverte d'une liste deroulante Win32 (ComboLBox).
    // `bound` : fenetre suivie. Menu WinForms : role UIA « menu » de la surgissante et lien a la fenetre suivie (meme fil
    // d'interface, ou possedee par elle) exiges, lus seulement pour une fenetre outil WinForms sans barre de titre.
    public static bool IsTransientPopup(nint hwnd, string cls, nint bound)
    {
        var (style, ex) = (unchecked((uint)Style(hwnd)), unchecked((uint)ExStyle(hwnd)));
        var winformsTool = (style & 0x80000000) != 0 && (style & 0x00C00000) != 0x00C00000 && (ex & 0x88) != 0 && cls.StartsWith("WindowsForms10.", StringComparison.Ordinal);
        var menu = winformsTool && MenuRole(hwnd);
        var linked = winformsTool && (GetWindowThreadProcessId(hwnd, out _) == GetWindowThreadProcessId(bound, out _) || OwnedBy(hwnd, bound));
        return IsTransientPopupStyle(style, ex, cls, menu, linked);
    }
    // Menus ouverts lies a la fenetre suivie : fenetres de premier niveau visibles du meme processus, dans l'ordre Z.
    // WinForms (ContextMenuStrip, ToolStripDropDown) : surgissante transitoire prouvee (role menu ET lien). WPF
    // (ContextMenu, mesure du 22/10) : surgissante HwndWrapper sans activation POSSEDEE par la fenetre suivie, du meme fil,
    // contenant un menu UIA (sa racine est une « fenetre » : role lu dans son arbre).
    public static List<nint> LinkedMenuPopups(nint bound)
    {
        var pid = ProcessOf(bound);
        var found = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (h != bound && IsWindowVisible(h) && ProcessOf(h) == pid && ClassName(h) is var cls &&
                (cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) && IsTransientPopup(h, cls, bound) ||
                 cls.StartsWith("HwndWrapper", StringComparison.Ordinal) && IsWpfMenuPopup(h, bound))) found.Add(h);
            return true;
        }, 0);
        return found;
    }
    static bool IsWpfMenuPopup(nint hwnd, nint bound)
    {
        var (style, ex) = (unchecked((uint)Style(hwnd)), unchecked((uint)ExStyle(hwnd)));
        if ((style & 0x80000000) == 0 || (style & 0x00C00000) == 0x00C00000 || (ex & 0x08000000) == 0) return false; // WS_POPUP, sans titre, NOACTIVATE
        if (GetWindow(hwnd, GW_OWNER) != bound || GetWindowThreadProcessId(hwnd, out _) != GetWindowThreadProcessId(bound, out _)) return false;
        try
        {
            using var tree = ComUia.Shared.ReadTree(hwnd, withPopups: false);
            return tree.Nodes.Any(n => n.Role == "menu");
        }
        catch (Exception) { return false; }
    }
    static bool MenuRole(nint hwnd)
    {
        try { return ComUia.Shared.ControlTypeOfWindow(hwnd) == 50009 /* Menu */; }
        catch (Exception) { return false; }
    }
    static bool OwnedBy(nint hwnd, nint bound)
    {
        for (var (owner, n) = (GetWindow(hwnd, GW_OWNER), 0); owner != 0 && n < 16; owner = GetWindow(owner, GW_OWNER), n++) if (owner == bound) return true;
        return false;
    }

    // Surgissante transitoire d'apres styles et classe (fonction pure, testee hors ligne : tests/CdpTests.cs).
    public static bool IsTransientPopupStyle(uint style, uint ex, string cls, bool menuRole = false, bool linked = false)
    {
        if (string.Equals(cls, "ComboLBox", StringComparison.OrdinalIgnoreCase)) return true;
        const uint WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
        if ((style & WS_POPUP) == 0 || (style & WS_CAPTION) == WS_CAPTION) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0) return true;
        // Menu deroulant ou contextuel WinForms (ToolStripDropDown) : fenetre outil sans barre de titre, sans
        // WS_EX_NOACTIVATE ; jamais suivie comme une nouvelle fenetre, signalee dans obs.popups (banc commun du 21/10 :
        // le menu contextuel ouvert n'etait ni suivi ni signale, « found 0 »). Le style seul ne prouve rien (une palette
        // persistante a le meme, relecture de Codex du 22/10) : role UIA « menu » ET lien a la fenetre suivie exiges.
        // ContextMenuStrip mesure le 22/10 (labo WinForms) : WS_EX_TOPMOST | WS_EX_CONTROLPARENT, SANS WS_EX_TOOLWINDOW ; le
        // clic droit l'ouvrait mais le helper ne le reconnaissait pas (« contextMenu: null »).
        return (ex & (WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) != 0 && cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) && menuRole && linked;
    }

    public static int PopupSelfTest()
    {
        using var input = System.Text.Json.JsonDocument.Parse(Console.In.ReadToEnd());
        var results = input.RootElement.EnumerateArray()
            .Select(c => IsTransientPopupStyle(c.GetProperty("style").GetUInt32(), c.GetProperty("ex").GetUInt32(), c.GetProperty("cls").GetString() ?? "",
                c.TryGetProperty("menu", out var m) && m.GetBoolean(), c.TryGetProperty("linked", out var l) && l.GetBoolean())).ToList();
        Console.WriteLine(EngineJson.Serialize(results));
        return 0;
    }
    public static uint ProcessOf(nint hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid; }

    // PostMessage verifie : un refus (fenetre disparue, application elevee bloquee par UIPI) n'est jamais un succes.
    public static void Post(nint hwnd, uint msg, nint wparam, nint lparam)
    {
        if (!PostMessageW(hwnd, msg, wparam, lparam))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(error == 5 ? "Windows blocks messages to this application (elevated, UIPI)" : $"PostMessage refused (error {error})");
        }
    }

    public static (nint Foreground, Point Cursor, uint LastInput) Probe()
    {
        GetCursorPos(out var cursor);
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        GetLastInputInfo(ref info);
        return (GetForegroundWindow(), cursor, info.Time);
    }
}
