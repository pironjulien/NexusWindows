// Offline self-checks of commun/engine/McpKeySequence.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static McpServer;

static partial class McpServerSelfTests
{
    internal static string NormalizeClaudeKeySequence(string spec) => string.Join(' ', KeySequencePlan(spec, _ => { }, NormalizeClaudeKeyChord));

    public static int KeySequenceSelfTest()
    {
        var assertions = 0;
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException("Key sequence selftest: " + why); assertions++; }
        JsonObject Sent(string effect = "sent") => new() { ["ok"] = true, ["effect"] = effect, ["technique"] = "fixture-transport" };
        JsonObject Snap(string birth = "start", string revision = "revision") => new() { ["processStartedUtc"] = birth, ["revision"] = revision };
        void Refused(Action action) { try { action(); } catch (ActionRefusedException) { assertions++; return; } throw new InvalidOperationException("Expected refusal"); }
        var parsed = new List<string>(); var plan = KeySequencePlan("ctrl+a\tdelete", parsed.Add);
        Check(plan.SequenceEqual(new[] { "ctrl+a", "delete" }) && parsed.SequenceEqual(plan), "all combinations planned before input");
        Refused(() => KeySequencePlan("  ", _ => { }));
        foreach (var (legacy, canonical) in new (string, string)[] {
            ("Ctrl+Maj+Entrée", "ctrl+shift+enter"), ("ctl+retour", "ctrl+backspace"), ("cmd+E", "win+e"), ("entrée", "enter"), ("échap", "escape"),
            ("espace", "space"), ("suppr", "delete"), ("del", "delete"), ("ins", "insert"), ("début", "home"), ("fin", "end"), ("pgup", "page_up"), ("pgdn", "page_down"),
            ("haut", "up"), ("bas", "down"), ("gauche", "left"), ("droite", "right"), ("menu", "apps"), ("apps", "apps"), ("printscreen", "printscreen"), ("pause", "pause"),
            ("capslock", "capslock"), ("numlock", "numlock"), ("scrolllock", "scrolllock"), ("plus", "plus"), ("minus", "minus"), ("comma", "comma"), ("period", "period"),
            ("+", "+"), ("ctrl++", "ctrl++"), ("ctrl+ctrl+A", "ctrl+a"), ("control", "ctrl"), ("maj", "shift"), ("alt", "alt"), ("cmd", "win"),
            ("Control_L+Return", "ctrl+enter"), ("Shift_R+Tab", "shift+tab"), ("Windows", "win") })
            Check(NormalizeClaudeKeyChord(legacy) == canonical, "legacy chord canonisation");
        for (var function = 1; function <= 24; function++) Check(NormalizeClaudeKeyChord("F" + function) == "f" + function, "all legacy function keys are retained");
        foreach (var invalid in new[] { "", "ctrl+", "ctrl+shift+", "ctrl+++", "ctrl+shift", "hyper+x", "f25", "unknown" }) Refused(() => NormalizeClaudeKeyChord(invalid));
        Check(NormalizeClaudeKeySequence("ctl+a suppr") == "ctrl+a delete", "the whole legacy sequence is canonical before policy evaluation");
        Check(NormalizeKeySequence("ctl+a suppr") == "ctrl+a delete" && NormalizeKeyChord(" VK_F5 ") == "VK_F5", "aliases for the three profiles, other syntaxes left to the key parser");
        Check(GardeFous.KeyKinds(NormalizeClaudeKeySequence("ctl+entrée")).SequenceEqual(new[] { "send" }) && GardeFous.KeyKinds(NormalizeClaudeKeySequence("entrée")).SequenceEqual(new[] { "submit" }), "translated names retain outward-action policy");
        Check(GardeFous.CheckBlindPaste(KeySequencePlan(NormalizeClaudeKeySequence("ctl+v entrée"), _ => { }).Select(key => ("key", key, false))).Length > 0, "legacy aliases cannot hide a paste-then-submit batch");
        var sends = new List<string>(); var proofs = 0;
        var complete = ExecuteKeySequence(plan, key => { sends.Add(key); return Sent(); }, () => false, () => { proofs++; return StepResult("verified", "✓", true, true, null); });
        Check(sends.SequenceEqual(plan) && proofs == 1 && Bool(complete, "success"), "the second key is sent after a delivery acknowledgement even when its prefix has no UI proof");
        Check(Number(complete["keySequence"], "delivered") == 2 && Number(complete["keySequence"], "remainingCount") == 0 && !complete["keySequence"]!.ToJsonString().Contains("ctrl+a"), "full delivered progress without retaining literal keys");
        sends.Clear(); proofs = 0;
        var uncertain = ExecuteKeySequence(plan, key => { sends.Add(key); return Sent("none"); }, () => false, () => { proofs++; return StepResult("verified", "✓", true, true, null); });
        Check(sends.Count == 1 && proofs == 0 && Str(uncertain, "mark") == "~" && !Bool(uncertain, "success"), "an uncertain transport prevents every following key and cannot claim UI success");
        Check(uncertain["keySequence"]!["remainingCount"] == null && !Bool(uncertain["keySequence"], "automaticReplay"), "an uncertain key cannot expose a replayable suffix");
        sends.Clear(); var attempted = false;
        var partial = ExecuteKeySequence(new[] { "ctrl+a", "delete", "enter" }, key => { sends.Add(key); if (key == "delete") { attempted = true; throw new InvalidOperationException("partial transport"); } return Sent(); }, () => attempted, () => throw new Exception("must not prove"));
        Check(sends.SequenceEqual(new[] { "ctrl+a", "delete" }) && Number(partial["keySequence"], "delivered") == 1 && Str(partial, "mark") == "~", "partial current delivery preserves prefix and stops without retry");
        var denied = ExecuteKeySequence(new[] { "ctrl+a", "enter" }, key => key == "enter" ? throw new NativeConfirmationException("fresh accord required") : Sent(), () => false, () => throw new Exception("must not prove"));
        Check(Bool(denied, "authorizationRequired") && Number(denied["keySequence"], "delivered") == 1 && Str(denied, "mark") == "~" && Str(denied, "message").Contains("Current combination not sent"), "outward refusal reports the already delivered prefix");
        var unproved = ExecuteKeySequence(plan, _ => Sent(), () => false, () => StepResult("unmet", "~", false, false, "global state absent"));
        Check(!Bool(unproved, "success") && Str(unproved, "mark") == "~" && Number(unproved["keySequence"], "delivered") == 2, "delivery never substitutes for global UI proof");
        var failedRead = ExecuteKeySequence(plan, _ => Sent(), () => false, () => throw new InvalidOperationException("readback"));
        Check(Str(failedRead, "mark") == "~" && Number(failedRead["keySequence"], "delivered") == 2, "readback failure cannot erase sent input");
        var prefixTrace = ExecuteKeySequence(plan, key => key == "ctrl+a" ? NativeFallbackAttempt(() => Sent(), () => true) : Sent(), () => false, () => StepResult("verified", "✓", true, true, null));
        Check(prefixTrace["keySequence"]!["deliveries"]![0]!["attempts"] is JsonArray { Count: 2 } && prefixTrace["keySequence"]!["deliveries"]![1]!["attempts"] == null && prefixTrace["attempts"] is JsonArray { Count: 2 } && Number(prefixTrace["attempts"]![0], "index", -1) == 0, "a later background combination does not erase the prefix fallback trace in the reply or call logger");
        var failedTrace = ExecuteKeySequence(plan, key => key == "ctrl+a" ? Sent() : NativeFallbackAttempt(() => throw new IOException("private-key private-field"), () => false), () => false, () => throw new Exception("must not prove"));
        Check(Str(failedTrace, "status") == "unknown" && failedTrace["keySequence"]!["deliveries"]![1]!["attempts"] is JsonArray { Count: 2 } && !failedTrace["keySequence"]!.ToJsonString().Contains("private-"), "a refused fallback keeps its sanitized trace and the already-sent prefix remains uncertain as a whole");
        ValidateKeyContext(Snap(), Snap()); assertions++;
        Refused(() => ValidateKeyContext(Snap(), Snap("new-process"))); Refused(() => ValidateKeyContext(Snap(), Snap(revision: "new-revision")));
        var focus = new JsonObject { ["nativeWindow"] = 10, ["element"] = new JsonObject { ["runtimeId"] = new JsonArray(1, 2) } };
        ValidateKeyFocus(focus, focus.DeepClone().AsObject()); assertions++;
        var moved = focus.DeepClone().AsObject(); moved["nativeWindow"] = 11; Refused(() => ValidateKeyFocus(focus, moved));
        moved = focus.DeepClone().AsObject(); moved["element"]!["runtimeId"] = new JsonArray(1, 3); Refused(() => ValidateKeyFocus(focus, moved));
        var checks = 0; var grant = new NativeKeyGrant(); grant.Check("same", () => checks++); grant.Check("same", () => checks++);
        Check(checks == 1, "unchanged before-input fallback does not consume the same authorization twice");
        Refused(() => grant.Check("changed-fields", () => throw new ActionRefusedException("fresh accord required")));
        var newCombination = new NativeKeyGrant(); newCombination.Check("same", () => checks++); Check(checks == 2, "a new combination receives its own authorization");
        var fields = new JsonObject { ["elements"] = new JsonArray(new JsonObject { ["role"] = "edit", ["name"] = "Body", ["runtimeId"] = new JsonArray(1), ["value"] = "prepared A", ["focused"] = true }) };
        var stampA = KeyPermissionStamp(fields, focus, "Fixture"); fields["elements"]![0]!["value"] = "prepared B";
        var stampB = KeyPermissionStamp(fields, focus, "Fixture"); Check(stampA != stampB, "fresh prepared-field changes invalidate the cached grant");
        var registry = new GardeFous.AccordsRegistry(clock: () => 1000);
        GardeFous.OutwardAction Outward(string content) => GardeFous.BuildAction("claude-windows", "key enter", "Send", [], content, [], "Fixture");
        var approved = Outward("prepared A"); var token = registry.Propose(approved); var exactGrant = new NativeKeyGrant(); var consumptions = 0;
        void Authorize(GardeFous.OutwardAction action) { consumptions++; if (registry.Check(action, true, token).Length > 0) throw new ActionRefusedException("fresh accord required"); }
        exactGrant.Check(stampA, () => Authorize(approved)); exactGrant.Check(stampA, () => Authorize(approved)); Check(consumptions == 1, "the real single-use registry is consumed only at the exact entry boundary");
        Refused(() => exactGrant.Check(stampB, () => Authorize(Outward("prepared B"))));
        var stopped = ExecuteKeySequence(new[] { "ctrl+a", "delete" }, key => key == "delete" ? throw new EmergencyStopException("fixture stop") : Sent(), () => false, () => throw new Exception("must not prove"));
        Check(Str(stopped, "status") == "interrupted" && Number(stopped["keySequence"], "delivered") == 1, "STOP preserves a delivered prefix and sends no suffix");
        var entryCalls = 0; Program.InputStarted = true;
        using (var entry = new NativeKeyEntryScope(() => { entryCalls++; Program.InputStarted = false; }))
        { BeforeKeySequenceInput(); Check(entry.Checked && Program.InputStarted && entryCalls == 1, "entry-boundary read preserves earlier input"); }
        BeforeKeySequenceInput(); Check(entryCalls == 1, "entry hook is a no-op outside the keyboard scope"); Program.InputStarted = false;
        Refused(BeforeClaudeTextEnter);
        var profile = GardeFous.CurrentAgent;
        try
        {
            GardeFous.CurrentAgent = "claude";
            var textState = new SessionState("claude") { BoundWindow = (1, "fixture", "Fixture editor", 2) };
            var field = new JsonObject { ["index"] = 0, ["role"] = "edit", ["name"] = "Body", ["automationId"] = "body", ["runtimeId"] = new JsonArray(1), ["ancestors"] = new JsonArray(), ["focused"] = true, ["value"] = "" };
            var textSnapshot = Snap(); textSnapshot["elements"] = new JsonArray(field);
            var live = textSnapshot.DeepClone().AsObject();
            var write = new JsonObject { ["kind"] = "typeText", ["text"] = "A\nB", ["selector"] = new JsonObject { ["name"] = "Body" } };
            using (var submission = new NativeTextSubmitScope(textState, write, textSnapshot, field, () => live, () => "Fixture editor"))
            {
                submission.BeforeEnter(); Check(true, "an editor newline with no outward trigger remains permitted");
                live["elements"]![0]!["value"] = "A";
                live["elements"]!.AsArray().Add(EngineTestJson.Node(new JsonObject { ["index"] = 1, ["role"] = "button", ["name"] = "Send", ["isDefault"] = true }));
                var refused = false;
                try { submission.BeforeEnter(); } catch (NativeConfirmationException) { refused = true; }
                Check(refused && Str(live["elements"]![0], "value") == "A", "a send control appearing after the prefix is refused before Enter without altering or replaying that prefix");
            }
            var approvedText = GardeFous.BuildAction("claude-windows", "typeText", "Send", [], "Body=A", [], "Fixture editor");
            var consent = write.DeepClone().AsObject(); consent["confirm"] = true; consent["accord"] = textState.Accords.Propose(approvedText);
            using (var submission = new NativeTextSubmitScope(textState, consent, textSnapshot, field, () => textSnapshot, () => "Fixture editor"))
            {
                Check(submission.Authorize(approvedText).Length == 0 && submission.Authorize(approvedText).Length == 0, "the same exact text submission consumes one real registry accord only once");
                Check(submission.Authorize(approvedText with { Content = "recipient or field changed" }).Length > 0, "changed recipient/content never inherits a text submission approval");
            }
        }
        finally { GardeFous.CurrentAgent = profile; }
        assertions += KeysSelfTests.KeyGrammarAssertions();
        Console.WriteLine(new JsonObject { ["ok"] = true, ["assertions"] = assertions, ["gui"] = false }.ToJsonString()); return 0;
    }
}
