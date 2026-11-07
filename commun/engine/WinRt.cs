using System.Diagnostics;
using System.Runtime.InteropServices;

// A cancelled asynchronous operation that did not reach a terminal state: never closed (IAsyncInfo.Close contract), its
// objects are left alive; the caller stops using the service until the process ends.
sealed class WinRtStuckException(string message) : TimeoutException(message);

sealed class WinRtException(string what, int hr) : InvalidOperationException($"{what} failed (0x{hr:X8})")
{
    public int HResult32 { get; } = hr;
}

// WinRT called as raw COM (combase + vtables): OCR (Ocr.cs) and Windows.Graphics.Capture (Wgc.cs) without the CsWinRT
// projection (Microsoft.Windows.SDK.NET.dll, ~25 MB) or any package. IIDs and method order (slot 6 = first method after
// IInspectable) read from the system metadata (System32\WinMetadata\*.winmd) on 27/10.
static unsafe partial class WinRt
{
    [LibraryImport("combase.dll")] private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int WindowsCreateString(string text, int length, nint* hstring);
    [LibraryImport("combase.dll")] private static partial int WindowsDeleteString(nint hstring);
    [LibraryImport("combase.dll")] private static partial char* WindowsGetStringRawBuffer(nint hstring, uint* length);

    public static readonly Guid IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    public static readonly Guid IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

    public static void Check(int hr, string what) { if (hr < 0) throw new WinRtException(what, hr); }
    public static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    public static nint Factory(string runtimeClass, Guid iid)
    {
        nint name, factory;
        Check(WindowsCreateString(runtimeClass, runtimeClass.Length, &name), "WindowsCreateString");
        try { Check(RoGetActivationFactory(name, &iid, &factory), "RoGetActivationFactory " + runtimeClass); }
        finally { _ = WindowsDeleteString(name); }
        return factory;
    }

    // 0 when the object does not offer the interface (older Windows: optional setting ignored).
    public static nint Query(nint obj, Guid iid)
    {
        nint result;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result) < 0 ? 0 : result;
    }
    public static nint QueryRequired(nint obj, Guid iid, string what)
    {
        var result = Query(obj, iid);
        if (result == 0) throw new WinRtException(what + " interface", unchecked((int)0x80004002));
        return result;
    }

    public static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged<nint, uint>)Slot(obj, 2))(obj); }

    // Close (IClosable) then Release: native resources given back at once, never at the garbage collector's leisure.
    public static void Close(nint obj)
    {
        if (obj == 0) return;
        var closable = Query(obj, IClosable);
        if (closable != 0) { ((delegate* unmanaged<nint, int>)Slot(closable, 6))(closable); Release(closable); }
        Release(obj);
    }

    // HSTRING returned by a method: read, then freed.
    public static string Take(nint hstring)
    {
        if (hstring == 0) return "";
        try { uint length; var raw = WindowsGetStringRawBuffer(hstring, &length); return new string(raw, 0, (int)length); }
        finally { _ = WindowsDeleteString(hstring); }
    }

    // Bounded wait on an IAsyncOperation<T> through IAsyncInfo.Status (no COM delegate to implement), then GetResults
    // (slot 8). Timeout: cancelled, then a bounded wait for its terminal state; Close only once it has ended
    // (IAsyncInfo.Close is forbidden before, Codex's review of 27/10). Never ended: WinRtStuckException, operation left
    // alive (no Close, no Release).
    const int CancelWaitMs = 2000;
    public static nint Await(nint operation, int timeoutMs, string what)
    {
        var info = QueryRequired(operation, IAsyncInfo, what);
        var closable = true;
        try
        {
            var timer = Stopwatch.StartNew();
            var canceled = false;
            int status;
            while (true)
            {
                if (((delegate* unmanaged<nint, int*, int>)Slot(info, 7))(info, &status) < 0) { closable = false; throw new WinRtStuckException(what + ": status unreadable"); }
                if (status != 0) break; // 0 Started, 1 Completed, 2 Canceled, 3 Error
                if (!canceled && timer.ElapsedMilliseconds > timeoutMs)
                {
                    ((delegate* unmanaged<nint, int>)Slot(info, 9))(info);
                    canceled = true;
                    timer.Restart();
                }
                else if (canceled && timer.ElapsedMilliseconds > CancelWaitMs)
                {
                    closable = false;
                    throw new WinRtStuckException(what + " did not finish after " + timeoutMs + " ms and its cancellation");
                }
                Thread.Sleep(1);
            }
            if (canceled) throw new TimeoutException(what + " took more than " + timeoutMs + " ms (canceled)");
            if (status != 1)
            {
                int code = 0;
                ((delegate* unmanaged<nint, int*, int>)Slot(info, 8))(info, &code);
                throw new WinRtException(what + (status == 2 ? " canceled" : ""), code);
            }
            nint result;
            Check(((delegate* unmanaged<nint, nint*, int>)Slot(operation, 8))(operation, &result), what + " results");
            return result;
        }
        finally
        {
            if (closable)
            {
                ((delegate* unmanaged<nint, int>)Slot(info, 10))(info);
                Release(info);
                Release(operation);
            }
        }
    }
}
