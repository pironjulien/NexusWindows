// Offline self-checks of commun/engine/ChromiumSimulatedText.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using static ChromiumInput;

static partial class ChromiumInputSelfTests
{
    internal static object SimulatedPacketGuardSelfTest()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Simulated packet guard selftest: " + message); }
        var packetProofs = 0; var presenceChecks = 0; var posted = 0; var proofDuringRune = 0; var packets = 0;
        foreach (var unit in SimulatedWriteUnits(new string('n', 2000)))
        {
            var insidePacketRunes = false; packets++;
            PostObservedCharacters(unit, () => { packetProofs++; if (insidePacketRunes) proofDuringRune++; },
                () => { insidePacketRunes = true; presenceChecks++; }, _ => posted++);
        }
        Check(packets == 32 && packetProofs == 32, "2000 characters have exactly 32 fresh packet proofs");
        Check(presenceChecks == 2000 && posted == 2000, "presence is checked before every real rune");
        Check(proofDuringRune == 0, "no UIA packet proof is requested from inside the rune loop");
        var unicode = new List<char>(); var unicodeProofs = 0; var unicodePresence = 0;
        PostObservedCharacters("😀", () => unicodeProofs++, () => unicodePresence++, character => unicode.Add(character));
        Check(new string(unicode.ToArray()) == "😀" && unicodeProofs == 1 && unicodePresence == 1 && unicode.Count == 2, "one guarded Unicode rune preserves both UTF-16 halves");
        var sent = 0; var quickChecks = 0;
        try { PostObservedCharacters("refused", () => throw new ActionRefusedException("focus changed"), () => quickChecks++, _ => sent++); }
        catch (ActionRefusedException) { Check(sent == 0 && quickChecks == 0, "a lost focus proof sends no packet"); }
        try { PostObservedCharacters("abcd", () => { }, () => { if (++quickChecks == 3) throw new JulienActiveException("user resumed"); }, _ => sent++); }
        catch (JulienActiveException) { Check(sent == 2 && quickChecks == 3, "user presence interrupts the packet before the next rune without replay"); }
        return new ChromiumInputSimulatedPacketGuardSelfTestJson(ok: true, checks: checks, packets: packets, packetProofs: packetProofs, presenceChecks: presenceChecks, proofDuringRune: proofDuringRune, utf16Posted: posted, gui: false, inputSent: false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "Also returns anonymous result objects; only the units branch is a List<string>.")]
    internal static object SimulatedTextSelfTest(JsonElement test)
    {
        static string S(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
        if (test.TryGetProperty("prefix", out var prefix)) return new ChromiumInputSimulatedTextSelfTestJson(confirmed: ConfirmedTextPrefix(S(prefix, "value"), S(prefix, "text"), S(prefix, "before"), prefix.TryGetProperty("append", out var append) && append.GetBoolean()));
        if (test.TryGetProperty("units", out var units)) return SimulatedWriteUnits(units.GetString()!);
        var value = S(test, "before"); var text = S(test, "text"); var replace = test.TryGetProperty("replace", out var r) && r.GetBoolean();
        var events = new List<string>(); var checks = 0; var waits = 0; var inputPackets = 0;
        var loseAfter = test.TryGetProperty("loseAfter", out var lost) ? lost.GetInt32() : int.MaxValue;
        var noAck = test.TryGetProperty("noAck", out var no) && no.GetBoolean();
        var changeAfterEnter = test.TryGetProperty("suppressEnter", out var suppress) && suppress.GetBoolean();
        var revealSend = test.TryGetProperty("revealSendAfter", out var reveal) ? reveal.GetString() : null;
        // suggest: an inline autocompletion shown after the typed characters (Chrome address bar).
        var suggestion = test.TryGetProperty("suggest", out var suggest) ? suggest.GetString() : null; var typedSoFar = "";
        // lateAck: the field shows the LAST characters only once the wait has run out (a budget expiring just as the
        // text lands): the outcome must then come from the field's value, not from the acknowledged count.
        var lateAck = test.TryGetProperty("lateAck", out var late) && late.GetBoolean(); string? pending = null;
        void Check() { checks++; if (inputPackets >= loseAfter) throw new ActionRefusedException("Fixture focus changed"); }
        var io = new SimulatedTextIo(Check, () => value, () => events.Add(replace ? "select-all" : "caret-end"),
            () => { events.Add("delete"); inputPackets++; if (!noAck) value = ""; },
            unit =>
            {
                events.Add("chars:" + unit); inputPackets++;
                if (noAck) return;
                var shown = suggestion == null ? value + unit : suggestion.StartsWith(typedSoFar += unit, StringComparison.Ordinal) ? suggestion : typedSoFar;
                if (lateAck && shown.EndsWith(text, StringComparison.Ordinal)) pending = shown; else value = shown;
            },
            () =>
            {
                if (revealSend != null && value.Contains(revealSend, StringComparison.Ordinal))
                {
                    events.Add("enter-refused:outward-context-changed");
                    throw new ActionRefusedException("A send control appeared after the text prefix; no exact action accord, no Enter sent");
                }
                events.Add("enter"); inputPackets++; if (!noAck && !changeAfterEnter) value += "\n";
            },
            () => { if (waits < 3) return true; if (pending != null) value = pending; return false; }, () => waits++);
        try { var result = RunObservedText(value, text, replace, io); return new ChromiumInputSimulatedTextSelfTestJson2(ok: true, result: result, value: value, events: events, checks: checks, waits: waits, inputPackets: inputPackets); }
        catch (Exception error) { return new ChromiumInputSimulatedTextSelfTestJson3(ok: false, error: error.GetType().Name, message: error.Message, value: value, events: events, checks: checks, waits: waits, inputPackets: inputPackets); }
    }
}
