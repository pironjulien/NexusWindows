using System.Runtime.InteropServices;

// Entrees PHYSIQUES de Julien (clavier, boutons, molette et deplacements de souris), distinguees des entrees
// injectees : Raw Input donne le peripherique d'origine, une entree injectee (SendInput de Sky, UIA SetFocus...)
// n'en a pas (hDevice nul). Fenetre de messages invisible en RIDEV_INPUTSINK : aucun crochet, aucune latence dans la
// chaine d'entree, ni contenu de touche lu ou garde ; seulement l'instant. Evenements limites a un par 100 ms.
// Repli : GetLastInputInfo (entrees injectees comprises), interroge toutes les 100 ms ; la session ecarte alors les
// evenements qui tombent pendant ses propres injections.
static unsafe partial class Presence
{
    // WNDCLASSW, blittable: a static UnmanagedCallersOnly window procedure and character pointers, so the registration
    // is source-generated (trimming, Native AOT) and no delegate has to be kept alive.
    [StructLayout(LayoutKind.Sequential)]
    struct WndClass
    {
        public uint Style; public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public char* MenuName; public char* ClassName;
    }
    const string ClassName = "NexusCodexPresence";
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static nint WindowProcedure(nint hwnd, uint msg, nint wparam, nint lparam) => Handle(hwnd, msg, wparam, lparam);
    [StructLayout(LayoutKind.Sequential)] struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; }

    [LibraryImport("user32.dll", SetLastError = true)] private static partial ushort RegisterClassW(in WndClass wc);
    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wparam, nint lparam);
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [LibraryImport("user32.dll")] private static partial uint GetRawInputData(nint input, uint command, byte[] data, ref uint size, uint headerSize);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(ref Msg msg);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint GetModuleHandleW(string? name);

    const uint WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100;
    const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    static readonly nint HWND_MESSAGE = -3;
    static Action<object>? emit;
    static long lastEmit = -1;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public static void Start(Action<object> output)
    {
        emit = output;
        var thread = new Thread(Loop) { IsBackground = true, Name = "nexus-presence" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void Signal(string device)
    {
        var now = Clock.ElapsedMilliseconds;
        if (lastEmit >= 0 && now - lastEmit < 100) return;
        lastEmit = now;
        emit?.Invoke(new PresenceSignalJson(@event: "input", device: device));
    }

    static void Loop()
    {
        var registered = false;
        try
        {
            var instance = GetModuleHandleW(null);
            fixed (char* name = ClassName)
            {
                var wc = new WndClass { Proc = &WindowProcedure, Instance = instance, ClassName = name };
                RegisterClassW(in wc);
            }
            var hwnd = CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, instance, 0);
            var devices = new[]
            {
                new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, Target = hwnd }, // clavier
                new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = hwnd }, // souris
            };
            registered = hwnd != 0 && RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        catch (Exception) { registered = false; }
        if (!registered) { Poll(); return; }
        emit?.Invoke(new PresenceLoopJson(@event: "presence", source: "raw-input"));
        while (GetMessageW(out var msg, 0, 0, 0) > 0) DispatchMessageW(ref msg);
    }

    static nint Handle(nint hwnd, uint msg, nint wparam, nint lparam)
    {
        if (msg == WM_INPUT)
        {
            var header = (uint)(8 + 2 * IntPtr.Size); // dwType, dwSize, hDevice, wParam
            var buffer = new byte[64];
            var size = (uint)buffer.Length;
            var got = GetRawInputData(lparam, RID_INPUT, buffer, ref size, header);
            if (got != uint.MaxValue && got >= header)
            {
                var type = BitConverter.ToUInt32(buffer, 0);
                var device = IntPtr.Size == 8 ? (nint)BitConverter.ToInt64(buffer, 8) : BitConverter.ToInt32(buffer, 8);
                if (device != 0) // entree injectee : aucun peripherique d'origine
                {
                    if (type == RIM_TYPEKEYBOARD) Signal("keyboard");
                    else if (type == RIM_TYPEMOUSE)
                    {
                        var buttons = BitConverter.ToUInt16(buffer, (int)header + 4);
                        var dx = BitConverter.ToInt32(buffer, (int)header + 12);
                        var dy = BitConverter.ToInt32(buffer, (int)header + 16);
                        if (buttons != 0 || dx != 0 || dy != 0) Signal("mouse");
                    }
                }
            }
        }
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }

    static void Poll()
    {
        emit?.Invoke(new PresenceLoopJson(@event: "presence", source: "last-input-info"));
        uint seen = 0;
        while (true)
        {
            var info = new Win32.LastInputInfo { Size = (uint)Marshal.SizeOf<Win32.LastInputInfo>() };
            if (Win32.GetLastInputInfo(ref info) && info.Time != seen)
            {
                if (seen != 0) emit?.Invoke(new PresencePollJson(@event: "input", device: "any", injectedPossible: true));
                seen = info.Time;
            }
            Thread.Sleep(100);
        }
    }
}
