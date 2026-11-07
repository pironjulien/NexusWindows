using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Anti-repeat guard of act, the same for the three profiles (restored 02/11). The Antigravity engine had it since
// 16/10 (agy/client/agent_guard.py: Gemini resent the same batch six times, each answer "unverified"); it went with the
// Python engines on 30/10, and since 3.13.0 an act without a prior look observes first, so a blind resend went through.
// Rule (Antigravity's, decided by Julien on 02/11): a batch strictly identical to one already sent to the same window is
// refused, nothing sent, when there was no explicit look of that window in between AND its state has not changed since
// that batch was sent. A changed window (next page, new value, another dialog) lets the same batch through: it is a new
// situation. repeat:true is the deliberate repeat it always was (16/10: "to force it knowingly"), but only of a batch
// whose previous run was verified: since 30/10 it never authorizes replaying an uncertain (~) or failed (✗) input.
static partial class McpServer
{
    internal const string RepeatRefusal = "Same batch already sent to this window, and the window has not changed since: refused, nothing was sent. " +
        "Look at the window (look) to see what the previous batch did, then send only the steps still needed, or adapt the batch (another target, value or technique).";
    internal const string RepeatUncertainRefusal = "repeat:true repeats only a batch whose previous run was verified (✓); this one ended with an effect not seen (~) or a failure (✗), " +
        "and the window has not changed since: refused, nothing was sent. Look at the window (look) first, then send only the steps still needed.";

    internal sealed class RepeatGuard
    {
        sealed record Sent(string Signature, string StateBefore, bool Verified);
        readonly Dictionary<long, List<Sent>> sent = new();
        readonly object sync = new();

        // An explicit look of this window (or of the whole screen: window 0) releases every batch sent to it.
        public void Observed(long window)
        {
            lock (sync) { if (window == 0) sent.Clear(); else sent.Remove(window); }
        }

        // null when the batch may go; otherwise the refusal. currentState is read only for a batch already sent (one
        // inspection), and the state it returns becomes the "before" state of this new send.
        public string? Check(long window, string signature, bool repeat, Func<string> currentState, out string? stateNow)
        {
            stateNow = null;
            Sent? previous;
            lock (sync) previous = sent.TryGetValue(window, out var list) ? list.LastOrDefault(s => s.Signature == signature) : null;
            if (previous == null) return null;
            stateNow = currentState();
            if (stateNow != previous.StateBefore) return null; // the window changed since that batch: a new situation
            if (repeat && previous.Verified) return null;      // deliberate repeat of a verified batch
            return repeat ? RepeatUncertainRefusal : RepeatRefusal;
        }

        public void Record(long window, string signature, string stateBefore, bool verified)
        {
            lock (sync)
            {
                if (!sent.TryGetValue(window, out var list)) sent[window] = list = [];
                list.RemoveAll(s => s.Signature == signature);
                list.Add(new Sent(signature, stateBefore, verified));
                if (list.Count > 32) list.RemoveAt(0); // only the batches not yet followed by a look are kept
            }
        }
    }

    // The batch itself: every argument except the window designation and presentation options, keys sorted.
    static readonly HashSet<string> RepeatIgnored = new(StringComparer.Ordinal) { "window", "hwnd", "observation", "repeat", "then", "settle_ms" };
    internal static string RepeatSignature(JsonObject args)
    {
        var batch = new JsonObject();
        foreach (var (key, value) in args.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (!RepeatIgnored.Contains(key)) batch[key] = CanonicalJson(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batch.ToJsonString())));
    }
    static JsonNode? CanonicalJson(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, CanonicalJson(p.Value)))),
        JsonArray a => new JsonArray(a.Select(CanonicalJson).ToArray()),
        _ => node?.DeepClone(),
    };

    // What the window shows: size, then each element's role, name, value and states with its place in the window (a
    // window moved on the desktop is not a change; a scrolled list is). Focus alone is not a change. A window whose tree
    // is too poor to tell (fewer than 6 elements: canvas, game) adds the hash of its pixels.
    internal static string RepeatStateDigest(JsonObject snapshot, Func<string>? pixels = null)
    {
        var frame = snapshot["frame"] as JsonObject;
        double left = Number(frame, "left"), top = Number(frame, "top");
        var text = new StringBuilder().Append(Number(frame, "width")).Append('x').Append(Number(frame, "height")).Append('\n');
        var elements = snapshot["elements"] as JsonArray ?? [];
        foreach (var e in elements.OfType<JsonObject>())
        {
            var line = new JsonArray();
            foreach (var key in new[] { "role", "name", "automationId", "value", "checked", "selected", "expanded", "disabled", "toggled" }) line.Add(e[key]?.DeepClone());
            if (e["bounds"] is JsonArray { Count: 4 } b) line.Add(EngineJson.Node(new JsonArray(Coordinate(b[0]) - left, Coordinate(b[1]) - top, Coordinate(b[2]), Coordinate(b[3]))));
            text.Append(line.ToJsonString()).Append('\n');
        }
        if (elements.Count < 6 && pixels != null) text.Append("pixels:").Append(pixels());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static double Coordinate(JsonNode? value) =>
        double.TryParse(value?.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    static string RepeatPixels(SessionState state, JsonObject snapshot)
    {
        try
        {
            var capture = ReadNativeCapture(state, snapshot);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Str(capture, "path"))));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return ""; }
    }

    // The guarded act: a run of steps or of a routine on a named window (not a continuation, routine management or a
    // Windows panel, which only exist in the foreground and are opened again each time).
    static JsonObject GuardedAct(SessionState state, JsonObject args, long window, Func<JsonObject> run)
    {
        var signature = RepeatSignature(args);
        var refusal = state.Repeats.Check(window, signature, Bool(args, "repeat"), () => { var now = InspectNative(state); return RepeatStateDigest(now, () => RepeatPixels(state, now)); }, out var stateNow);
        if (refusal != null) throw new ActionRefusedException(refusal);
        var before = stateNow ?? (state.CurrentObservation is { } observed ? RepeatStateDigest(ObjectOf(observed), () => RepeatPixels(state, ObjectOf(observed))) : "");
        Program.InputStarted = false;
        JsonObject result;
        try { result = run(); }
        catch
        {
            // Refused before any input: nothing was sent, nothing to remember.
            if (Program.InputStarted) state.Repeats.Record(window, signature, before, verified: false);
            throw;
        }
        state.Repeats.Record(window, signature, before, Str(result["structuredContent"], "status") == "success");
        return result;
    }
}
