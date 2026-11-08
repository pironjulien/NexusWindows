using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// "Invoked" heard from the clicked element: the application's own UI Automation provider raises Invoke_Invoked when the
// control processed the activation (WPF Button.OnClick, Chromium on the DOM click); the Win32 and WinForms MSAA proxies
// never do. It says the click arrived, not what it produced (expect checks that). The proof existed in the Python engine
// and was lost when 3.9.0 went native only (found 08/11: README promised it, every WPF click came back `~`).
// The listener is a minimal COM object (IUnknown + IUIAutomationEventHandler, agile) in native memory: opened before
// Invoke, removed after; UI Automation calls it on its own threads (the worker is MTA) and it only sets a flag, never an
// UIA call inside the handler.
unsafe sealed partial class ComUia
{
    const int InvokedEventId = 20009; // UIA_Invoke_InvokedEventId
    static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
    static readonly Guid IidEventHandler = new("146c3c17-f12e-4e22-8c27-f894b9b79c69");
    static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    // Events arrive within a few milliseconds (WPF synchronously in OnClick, Chromium after its DOM click).
    public const int InvokedWaitMs = 300;

    [StructLayout(LayoutKind.Sequential)]
    struct Heard { public nint* Vtable; public int References; public int Fired; }

    static readonly nint* HeardVtable = CreateHeardVtable();
    static nint* CreateHeardVtable()
    {
        var table = (nint*)NativeMemory.Alloc(4, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<Heard*, Guid*, nint*, int>)&HeardQueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<Heard*, uint>)&HeardAddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<Heard*, uint>)&HeardRelease;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<Heard*, nint, int, int>)&HeardHandle;
        return table;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int HeardQueryInterface(Heard* self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidEventHandler || *iid == IidAgileObject)
        {
            Interlocked.Increment(ref self->References);
            *result = (nint)self;
            return 0;
        }
        *result = 0;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint HeardAddRef(Heard* self) => (uint)Interlocked.Increment(ref self->References);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint HeardRelease(Heard* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left == 0) NativeMemory.Free(self);
        return (uint)left;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int HeardHandle(Heard* self, nint sender, int eventId)
    {
        if (eventId == InvokedEventId) Volatile.Write(ref self->Fired, 1);
        return 0;
    }

    // Invoke sent ONCE, with the Invoked listener of this element opened before it: true if the element raised Invoked
    // within waitMs. A listener the client cannot open leaves the click unproved (`~`), never refused or sent twice.
    public bool InvokeHeard(nint element, nint invokePattern, Action beforeAct, int waitMs = InvokedWaitMs)
    {
        var heard = (Heard*)NativeMemory.Alloc((nuint)sizeof(Heard));
        heard->Vtable = HeardVtable; heard->References = 1; heard->Fired = 0;
        var listening = false;
        try
        {
            // IUIAutomation::AddAutomationEventHandler(eventId, element, scope, cacheRequest, handler), slot 32.
            listening = ((delegate* unmanaged[Stdcall]<nint, int, nint, int, nint, nint, int>)Vt(automation)[32])(
                automation, InvokedEventId, element, TreeScopeElement, 0, (nint)heard) >= 0;
            beforeAct();
            CallSlot(invokePattern, 3);
            if (!listening) return false;
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref heard->Fired) == 0 && clock.ElapsedMilliseconds < waitMs) Thread.Sleep(5);
            return Volatile.Read(ref heard->Fired) != 0;
        }
        finally
        {
            // IUIAutomation::RemoveAutomationEventHandler(eventId, element, handler), slot 33; UIA releases its own reference.
            if (listening) ((delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)Vt(automation)[33])(automation, InvokedEventId, element, (nint)heard);
            Release((nint)heard);
        }
    }
}
