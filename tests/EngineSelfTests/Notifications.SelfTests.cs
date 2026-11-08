// Offline self-checks of commun/engine/Notifications.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Notifications;

static partial class NotificationsSelfTests
{
    [LibraryImport("winsqlite3.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, nint error);

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
            finally { _ = sqlite3_close(db); }
            var hash = System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path));
            var rows = Read(20, "fixture", path);
            Assert(rows.Count == 1 && rows[0]!["texts"]![1]!.GetValue<string>() == "•••. •••");
            Assert(hash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path))));
            Console.WriteLine("{\"ok\":true,\"expiredBeforeLimit\":1001,\"readonly\":true}"); return 0;
        }
        finally { System.IO.File.Delete(path); }
    }
}
