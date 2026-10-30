using System.Diagnostics;
using System.Runtime.InteropServices;

// Operation asynchrone annulee qui n'a pas atteint d'etat terminal : jamais fermee (contrat IAsyncInfo.Close), ses
// objets sont laisses vivants ; l'appelant cesse d'utiliser le service jusqu'a la fin du processus.
sealed class WinRtStuckException(string message) : TimeoutException(message);

sealed class WinRtException(string what, int hr) : InvalidOperationException($"{what} failed (0x{hr:X8})")
{
    public int HResult32 { get; } = hr;
}

// WinRT appele en COM brut (combase + vtables) : OCR (Ocr.cs) et Windows.Graphics.Capture (Wgc.cs) sans la projection
// CsWinRT (Microsoft.Windows.SDK.NET.dll, ~25 Mo) ni aucun paquet. IID et ordre des methodes (emplacement 6 = premiere
// methode apres IInspectable) relus dans les metadonnees du systeme (System32\WinMetadata\*.winmd) le 27/10.
static unsafe class WinRt
{
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string text, int length, nint* hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);

    public static readonly Guid IAsyncInfo = new("00000036-0000-0000-c000-000000000046");
    public static readonly Guid IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");

    public static void Check(int hr, string what) { if (hr < 0) throw new WinRtException(what, hr); }
    public static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];

    public static nint Factory(string runtimeClass, Guid iid)
    {
        nint name, factory;
        Check(WindowsCreateString(runtimeClass, runtimeClass.Length, &name), "WindowsCreateString");
        try { Check(RoGetActivationFactory(name, &iid, &factory), "RoGetActivationFactory " + runtimeClass); }
        finally { WindowsDeleteString(name); }
        return factory;
    }

    // 0 si l'objet n'offre pas l'interface (Windows plus ancien : reglage facultatif ignore).
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

    // Close (IClosable) puis Release : ressources natives rendues tout de suite, jamais au gre du ramasse-miettes.
    public static void Close(nint obj)
    {
        if (obj == 0) return;
        var closable = Query(obj, IClosable);
        if (closable != 0) { ((delegate* unmanaged<nint, int>)Slot(closable, 6))(closable); Release(closable); }
        Release(obj);
    }

    // HSTRING rendue par une methode : lue puis liberee.
    public static string Take(nint hstring)
    {
        if (hstring == 0) return "";
        try { uint length; var raw = WindowsGetStringRawBuffer(hstring, &length); return new string(raw, 0, (int)length); }
        finally { WindowsDeleteString(hstring); }
    }

    // Attente bornee d'une IAsyncOperation<T> par IAsyncInfo.Status (aucun delegue COM a implementer), puis GetResults
    // (emplacement 8). Delai depasse : annulee, puis attente bornee de son etat terminal ; Close seulement une fois
    // terminee (IAsyncInfo.Close est interdit avant, relecture de Codex du 27/10). Jamais terminee : WinRtStuckException,
    // operation laissee vivante (aucun Close, aucun Release).
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
