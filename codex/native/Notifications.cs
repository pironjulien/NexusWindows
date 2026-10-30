using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Port de claude/src/nexus_windows/notifications.py. SQLite Windows, strictement en lecture seule.
static class Notifications
{
    const string Sql = "SELECT h.PrimaryId,n.ArrivalTime,n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId=n.HandlerId WHERE n.Type='toast' AND (n.ExpiryTime IS NULL OR n.ExpiryTime=0 OR n.ExpiryTime>=?) ORDER BY n.ArrivalTime DESC LIMIT 1000";
    static readonly Regex Text = new(@"<text\b[^>]*>(.*?)</text>", RegexOptions.Singleline);
    static readonly Regex Tags = new("<[^>]+>");
    static readonly Regex Space = new(@"\s+");
    static readonly Regex Context = new(@"code|c[óo]digo|codice|otp|v[ée]rif|passcode|mot de passe|password|contrase|s[ée]curit|security|2fa|connexion|sign.?in|log.?in|authent|\bpin\b|confirm|apple id|identifiant|one.?time|is your|acc[eè]s|access|do not share|ne (?:le )?partagez|connect|entrez|saisissez|\benter\b|valid", RegexOptions.IgnoreCase);
    static readonly Regex Code = new(@"(?<!\w)(?:[A-Z]-)?\d(?:[ -]?\d){3,11}(?!\w)");
    static readonly Regex Alnum = new(@"(?<![\w.-])(?=[\w-]*\d)(?=[\w-]*[A-Za-z])[A-Za-z0-9](?:[A-Za-z0-9-]{3,10})[A-Za-z0-9](?![\w-])");
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(nint db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(nint db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, nint tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_bind_int64(nint statement, int index, long value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern nint sqlite3_column_text(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern nint sqlite3_column_blob(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, nint error);

    public static string Mask(string text, string context) => Context.IsMatch(context.Length > 0 ? context : text) ? Alnum.Replace(Code.Replace(text, "•••"), "•••") : text;
    static string AppName(string id)
    {
        var name = id.Split('!')[0];
        if (name.StartsWith("electron.app.", StringComparison.OrdinalIgnoreCase)) return name[13..];
        if (name.Contains('\\')) return System.IO.Path.GetFileNameWithoutExtension(name);
        return name.Split('_')[0].Split('.').Last();
    }
    public static JsonArray Read(int limit = 20, string? app = null, string? database = null)
    {
        database ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        if (!System.IO.File.Exists(database)) throw new InvalidOperationException("Windows notification database not found on this PC.");
        nint db = 0, statement = 0;
        try
        {
            // SQLITE_OPEN_READONLY (no CREATE, no fallback to writable).
            if (sqlite3_open_v2(database, out db, 1, 0) != 0) throw new InvalidOperationException("Notification database unreadable.");
            sqlite3_busy_timeout(db, 2000);
            if (sqlite3_prepare_v2(db, Sql, -1, out statement, 0) != 0 || sqlite3_bind_int64(statement, 1, DateTime.UtcNow.ToFileTimeUtc()) != 0) throw new InvalidOperationException("Notification database unreadable (schema).");
            var output = new JsonArray(); int status;
            while ((status = sqlite3_step(statement)) == 100)
            {
                var id = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0)) ?? "";
                var name = AppName(id);
                if (!string.IsNullOrEmpty(app) && !name.Contains(app, StringComparison.OrdinalIgnoreCase) && !id.Contains(app, StringComparison.OrdinalIgnoreCase)) continue;
                var payload = new byte[sqlite3_column_bytes(statement, 2)];
                if (payload.Length > 0) Marshal.Copy(sqlite3_column_blob(statement, 2), payload, 0, payload.Length);
                var texts = Text.Matches(Encoding.UTF8.GetString(payload)).Select(m => Space.Replace(WebUtility.HtmlDecode(Tags.Replace(m.Groups[1].Value, "")), " ").Trim()).Where(s => s.Length > 0).ToList();
                if (texts.Count == 0) continue;
                var context = string.Join(' ', texts);
                output.Add(new JsonObject { ["app"] = name, ["arrived"] = DateTime.FromFileTimeUtc(sqlite3_column_int64(statement, 1)).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz"), ["texts"] = new JsonArray(texts.Select(t => (JsonNode)JsonValue.Create(Mask(t, context))!).ToArray()) });
                if (output.Count >= Math.Clamp(limit, 1, 200)) return output;
            }
            if (status != 101) throw new InvalidOperationException("Notification database unreadable (query).");
            return output;
        }
        finally { if (statement != 0) sqlite3_finalize(statement); if (db != 0) sqlite3_close(db); }
    }

    public static int SelfTest()
    {
        void Assert(bool value) { if (!value) throw new InvalidOperationException("Notifications selftest failed"); }
        Assert(Mask("123 456. K7F2QX ab12-cd34 G-123456", "Code de connexion") == "•••. ••• ••• •••");
        Assert(Mask("2026 1500", "Solde du compte") == "2026 1500");
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-notifications-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Assert(sqlite3_open_v2(path, out var db, 6, 0) == 0);
            try
            {
                Assert(sqlite3_exec(db, "CREATE TABLE NotificationHandler(RecordId INTEGER,PrimaryId TEXT); CREATE TABLE Notification(Type TEXT,HandlerId INTEGER,ArrivalTime INTEGER,Payload BLOB,ExpiryTime INTEGER); INSERT INTO NotificationHandler VALUES(1,'electron.app.Fixture');", 0, 0, 0) == 0);
                var now = DateTime.UtcNow.ToFileTimeUtc();
                Assert(sqlite3_exec(db, $"WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<1001) INSERT INTO Notification SELECT 'toast',1,{now}+x,'<text>expired</text>',1 FROM seq; INSERT INTO Notification VALUES('toast',1,{now},'<text>Code de connexion</text><text>&#49;23456. K7F2QX</text>',0);", 0, 0, 0) == 0);
            }
            finally { sqlite3_close(db); }
            var hash = System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path));
            var rows = Read(20, "fixture", path);
            Assert(rows.Count == 1 && rows[0]!["texts"]![1]!.GetValue<string>() == "•••. •••");
            Assert(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path))));
            Console.WriteLine("{\"ok\":true,\"expiredBeforeLimit\":1001,\"readonly\":true}"); return 0;
        }
        finally { System.IO.File.Delete(path); }
    }
}
