using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// Presse-papiers Windows (31/10, idee reprise d'agent-desktop), memes droits pour les trois profils : lecture du texte et
// des fichiers copies, ecriture de texte relue. L'ecriture est un EMPRUNT (axe « invisible ») : le contenu de Julien est
// mis de cote, format par format, et rendu seul apres 61,8 s ou sur restore, jamais s'il a recopie entre-temps ; refusee
// tant que son presse-papiers tient un contenu prive. Un contenu marque sensible par son auteur (gestionnaires de mots de passe : formats
// ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory = 0) n'est jamais lu ; les codes de
// securite sont masques comme dans les notifications. Ce que l'agent ecrit est une copie ordinaire : la marquer hors
// historique la ferait passer pour un secret, illisible a la relecture suivante (essai reel du 31/10).
static class Clipboard
{
    const uint CfUnicodeText = 13, CfHdrop = 15, CfDib = 8, GmemMoveable = 0x2;
    const int MaxText = 32768;
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(nint drop, uint index, char[]? file, uint length);
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);
    // GDI or owner-drawn formats are no memory blocks; Windows synthesizes CF_BITMAP and metafiles from what is kept.
    static readonly HashSet<uint> NotMemory = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];
    public const int LendMs = 61_800;
    static readonly object Gate = new();
    static List<(uint Format, byte[] Bytes)>? lent; static uint lentSequence; static Timer? giveBack;

    static readonly uint Exclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint History = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    static void Open()
    {
        // Another application may hold the clipboard for a few milliseconds.
        for (var attempt = 0; attempt < 20; attempt++) { if (OpenClipboard(0)) return; Thread.Sleep(25); }
        throw new InvalidOperationException("The clipboard is held by another application; nothing was read or written");
    }
    static bool Denied(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        var data = GetClipboardData(format); var locked = data == 0 ? 0 : GlobalLock(data);
        try { return locked == 0 || Marshal.ReadInt32(locked) == 0; } finally { if (locked != 0) GlobalUnlock(data); }
    }
    static string? Text()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
        var data = GetClipboardData(CfUnicodeText); var locked = data == 0 ? 0 : GlobalLock(data);
        if (locked == 0) return null;
        try { return Marshal.PtrToStringUni(locked); } finally { GlobalUnlock(data); }
    }

    public static JsonObject Read()
    {
        Open();
        try
        {
            if (IsClipboardFormatAvailable(Exclude) || Denied(History))
                return new JsonObject { ["sensitive"] = true, ["note"] = "The application that copied this content marked it private (password manager); it is not read." };
            var result = new JsonObject();
            if (Text() is { } text)
            {
                result["truncated"] = text.Length > MaxText;
                result["text"] = Notifications.Mask(text.Length > MaxText ? text[..MaxText] : text, "");
            }
            if (IsClipboardFormatAvailable(CfHdrop) && GetClipboardData(CfHdrop) is var drop and not 0)
            {
                var files = new JsonArray();
                for (uint i = 0, count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0); i < count; i++)
                {
                    var buffer = new char[DragQueryFileW(drop, i, null, 0) + 1];
                    DragQueryFileW(drop, i, buffer, (uint)buffer.Length); files.Add(new string(buffer).TrimEnd('\0'));
                }
                result["files"] = files;
            }
            if (IsClipboardFormatAvailable(CfDib)) result["image"] = true;
            if (result.Count == 0) result["empty"] = true;
            return result;
        }
        finally { CloseClipboard(); }
    }

    static void Put(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (memory == 0) throw new InvalidOperationException("Clipboard memory unavailable");
        var locked = GlobalLock(memory);
        if (locked == 0) { GlobalFree(memory); throw new InvalidOperationException("Clipboard memory unavailable"); }
        Marshal.Copy(bytes, 0, locked, bytes.Length); GlobalUnlock(memory);
        // After a successful SetClipboardData, the system owns the memory.
        if (SetClipboardData(format, memory) == 0) { GlobalFree(memory); throw new InvalidOperationException("The clipboard refused the content"); }
    }

    public static JsonObject Write(string text)
    {
        if (text.Length > MaxText) throw new ActionRefusedException($"Text longer than {MaxText} characters; nothing was written");
        EmergencyStop.ThrowIfStopped();
        lock (Gate)
        {
            // A lend still running keeps the user's original content, never the task's own earlier text.
            if (lent != null && GetClipboardSequenceNumber() != lentSequence) { lent = null; giveBack?.Dispose(); giveBack = null; }
            var verified = false;
            Open();
            try
            {
                if (lent == null)
                {
                    if (IsClipboardFormatAvailable(Exclude) || Denied(History))
                        throw new ActionRefusedException("The user's clipboard holds private content (password manager); nothing was written");
                    lent = Keep();
                }
                if (!EmptyClipboard()) throw new InvalidOperationException("The clipboard could not be emptied; nothing was written");
                Put(CfUnicodeText, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
                verified = Text() == text;
            }
            finally { CloseClipboard(); }
            // Read after closing: closing the clipboard advances its sequence (live test of 31/10 took the task's own
            // write for a newer copy and kept the user's content aside).
            lentSequence = GetClipboardSequenceNumber();
            giveBack?.Dispose(); giveBack = new Timer(_ => { try { Restore(); } catch { /* The next call or the exit tries again. */ } }, null, LendMs, Timeout.Infinite);
            return new JsonObject { ["status"] = verified ? "verified" : "unknown", ["verified"] = verified, ["length"] = text.Length, ["givenBackInMs"] = LendMs };
        }
    }

    static List<(uint Format, byte[] Bytes)> Keep()
    {
        var kept = new List<(uint, byte[])>();
        for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            if (NotMemory.Contains(format)) continue;
            var data = GetClipboardData(format); var size = data == 0 ? 0 : (long)GlobalSize(data);
            if (size <= 0) continue;
            var locked = GlobalLock(data); if (locked == 0) continue;
            try { var bytes = new byte[size]; Marshal.Copy(locked, bytes, 0, (int)size); kept.Add((format, bytes)); } finally { GlobalUnlock(data); }
        }
        return kept;
    }

    // Gives the user's content back, unless someone copied after the task's text (their newer copy wins).
    public static JsonObject Restore()
    {
        lock (Gate)
        {
            giveBack?.Dispose(); giveBack = null;
            if (lent == null) return new JsonObject { ["status"] = "nothing-lent" };
            var kept = lent; lent = null;
            if (GetClipboardSequenceNumber() != lentSequence)
                return new JsonObject { ["status"] = "kept-newer-copy", ["note"] = "The clipboard changed after the task's text; that newer content stays." };
            Open();
            try
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("The clipboard could not be emptied; the user's content was not given back");
                foreach (var (format, bytes) in kept) Put(format, bytes);
                return new JsonObject { ["status"] = "given-back", ["formats"] = kept.Count };
            }
            finally { CloseClipboard(); }
        }
    }

    static Clipboard() => AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Restore(); } catch { /* Best effort at exit. */ } };
}
