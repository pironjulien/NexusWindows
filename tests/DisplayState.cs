// Whether the screens are asleep (GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed). Windows reports the current
// value once, right after registration; tests that prove an effect by the pixels of a window skip while it is 0.
using System.Runtime.InteropServices;

namespace NexusWindows.Tests;

static class DisplayState
{
    delegate uint Callback(nint context, uint type, nint setting);
    [StructLayout(LayoutKind.Sequential)] struct Recipient { public Callback Callback; public nint Context; }
    [DllImport("powrprof.dll")] static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref Recipient recipient, out nint handle);
    [DllImport("powrprof.dll")] static extern uint PowerSettingUnregisterNotification(nint handle);
    const uint DeviceNotifyCallback = 2, PowerSettingChange = 0x8013;
    static Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    public static bool IsOff()
    {
        var state = -1;
        using var reported = new ManualResetEventSlim();
        Callback callback = (_, type, setting) =>
        {
            // POWERBROADCAST_SETTING: Guid (16 bytes), DataLength (4), then the DWORD value.
            if (type == PowerSettingChange) { state = Marshal.ReadInt32(setting, 20); reported.Set(); }
            return 0;
        };
        var recipient = new Recipient { Callback = callback };
        if (PowerSettingRegisterNotification(ref ConsoleDisplayState, DeviceNotifyCallback, ref recipient, out var handle) != 0) return false;
        try { reported.Wait(1000); }
        finally { _ = PowerSettingUnregisterNotification(handle); GC.KeepAlive(callback); }
        return state == 0;
    }
}
