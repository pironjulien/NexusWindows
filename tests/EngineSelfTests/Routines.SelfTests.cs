// Offline self-checks of commun/engine/Routines.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Routines;

static partial class RoutinesSelfTests
{
    public static int SelfTest()
    {
        void Assert(bool cond, string msg)
        {
            if (!cond) throw new InvalidOperationException("Routines SelfTest failed: " + msg);
        }

        Assert(ApplicationKey(@"process:C:\Windows\System32\notepad.exe") == "process:notepad.exe", "ApplicationKey process");
        Assert(ValidateRoutineName("remplir_fiche-1.0") == "remplir_fiche-1.0", "ValidateRoutineName");

        var rawSteps = JsonNode.Parse("""
        [
          { "kind": "setValue", "selector": { "role": "edit", "automationId": "nom" }, "value": "{{nom}}", "confirm": true },
          { "kind": "select", "selector": { "role": "combobox", "automationId": "choix" }, "option": "Alpha" }
        ]
        """)!.AsArray();

        var prepared = PrepareRoutineSteps(rawSteps);
        Assert(prepared.Count == 2, "prepared count");
        Assert(prepared[0]!.AsObject()["confirm"] == null, "confirm stripped from saved routine");
        Assert(prepared[0]!.AsObject()["expect"] != null, "inferred expect on setValue");

        var expanded = ExpandRoutine(prepared, new Dictionary<string, string> { ["nom"] = "Julien" });
        Assert(expanded[0]!.AsObject()["value"]!.GetValue<string>() == "Julien", "parameter expanded in value");
        Assert(expanded[0]!.AsObject()["expect"]!.AsObject()["equals"]!.GetValue<string>() == "Julien", "parameter expanded in expect");

        var caught = false;
        try { ExpandRoutine(prepared, new Dictionary<string, string>()); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("missing nom", StringComparison.Ordinal)) { caught = true; }
        Assert(caught, "missing parameter rejected");

        Console.WriteLine(EngineTestJson.Serialize(new JournalSelfTestJson(ok: true)));
        return 0;
    }
}
