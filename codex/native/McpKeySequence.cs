using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

static partial class McpServer
{
    [ThreadStatic] static NativeTextSubmitScope? textSubmitEntry;
    sealed class NativeTextSubmitScope : IDisposable
    {
        readonly SessionState state;
        readonly JsonObject step, initial, identity, selector;
        readonly long window, pid;
        readonly string agent;
        readonly Func<JsonObject> inspect;
        readonly Func<string> title;
        readonly HashSet<string> approved = new(StringComparer.Ordinal);
        readonly NativeTextSubmitScope? previous;
        public NativeTextSubmitScope(SessionState state, JsonObject step, JsonObject snapshot, JsonObject target, Func<JsonObject>? inspect = null, Func<string>? title = null)
        {
            if (state.BoundWindow is not { } bound || textSubmitEntry != null) throw new ActionRefusedException("A text submission needs one bound agent session");
            this.state = state; this.step = step.DeepClone().AsObject(); initial = snapshot.DeepClone().AsObject();
            agent = state.Agent;
            identity = ElementIdentity(snapshot, target); selector = SelectorOf(target);
            if (identity["runtimeId"] is not JsonArray { Count: > 0 }) throw new ActionRefusedException("The original text field needs a real runtime identity");
            (window, pid) = (bound.Id, bound.Pid);
            this.inspect = inspect ?? (() => InspectNative(state)); this.title = title ?? (() => Win32.Title((nint)window));
            previous = textSubmitEntry; textSubmitEntry = this;
        }
        public string Authorize(GardeFous.OutwardAction action)
        {
            var fingerprint = GardeFous.Fingerprint(action);
            if (approved.Contains(fingerprint)) return "";
            var refusal = state.Accords.Check(action, Bool(step, "confirm"), Str(step, "accord"));
            if (refusal.Length == 0) approved.Add(fingerprint);
            return refusal;
        }
        public void BeforeEnter()
        {
            if (!ReferenceEquals(textSubmitEntry, this) || state.Agent != agent || GardeFous.CurrentAgent != agent || state.BoundWindow is not { } bound || bound.Id != window || bound.Pid != pid)
                throw new ActionRefusedException("The text submission session/window changed; Enter not sent");
            var current = inspect(); ValidateKeyContext(initial, current);
            var target = StepTarget(current, new JsonObject { ["observedIdentity"] = identity.DeepClone() }, selector);
            if (Bool(target, "password") || Bool(target, "disabled") || Bool(target, "valueTruncated")) throw new ActionRefusedException("The original text field became secret, disabled or unreadable; Enter not sent");
            if (!Bool(target, "focused")) throw new ActionRefusedException("The original text field lost keyboard focus; Enter not sent");
            if (target["value"] == null)
            {
                // Chromium rich editors may publish TextPattern rather than ValuePattern. Inspect's full document
                // text is usable only when its actual focused element is this same runtime-bound field.
                var publication = current["accessibility"] as JsonObject;
                var focused = $"{Number(target, "index")} {Str(target, "role")} {Str(target, "name")}";
                if (publication == null || Str(publication, "focused_element") != focused || Bool(publication, "document_truncated") || publication["document_text"] is not JsonValue document || !document.TryGetValue<string>(out var contents))
                    throw new ActionRefusedException("The complete submission field cannot be read in its current context; Enter not sent");
                target["value"] = contents;
            }
            var context = title();
            CheckGesture(state, step, current, target, this, context, textUsesEnter: true);
            if (context != title()) throw new ActionRefusedException("The text submission context changed while authorizing; Enter not sent");
        }
        public void CheckLease() { EmergencyStop.ThrowIfStopped(); state.Interaction?.Check(); }
        public void Dispose() => textSubmitEntry = previous;
    }
    public static void BeforeClaudeTextEnter() => BeforeTextEnter();
    public static void BeforeTextEnter(bool requireScope = true)
    {
        var scope = textSubmitEntry;
        // Non-MCP low-level clients retain their own guards. MCP multiline fallbacks always bind a scope.
        if (scope == null && !requireScope) return;
        if (scope == null) throw new ActionRefusedException("Simulated Enter requires a bound MCP text submission scope; no Enter sent");
        var entered = Program.InputStarted;
        try { scope.CheckLease(); scope.BeforeEnter(); }
        finally { Program.InputStarted |= entered; }
    }

    [ThreadStatic] static Action? keySequenceEntry;
    // Called at the real entry boundary, after activation and before the first key event.
    // Read-only requests in the guard reset Program.InputStarted: preserve any earlier activation/input.
    public static void BeforeKeySequenceInput()
    {
        if (keySequenceEntry is not { } guard) return;
        var entered = Program.InputStarted;
        try { guard(); }
        finally { Program.InputStarted |= entered; }
    }
    sealed class NativeKeyEntryScope : IDisposable
    {
        readonly Action? previous;
        public bool Checked { get; private set; }
        public NativeKeyEntryScope(Action guard)
        {
            previous = keySequenceEntry;
            if (previous != null) throw new ActionRefusedException("Nested keyboard sequence refused; nothing sent");
            keySequenceEntry = () => { guard(); Checked = true; };
        }
        public void Dispose() => keySequenceEntry = previous;
    }
    sealed class NativeKeyGrant
    {
        string? granted;
        public void Check(string current, Action check)
        {
            if (current == granted) return; // Same combination, same exact context, before-input fallback only.
            check(); granted = current;
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct KeyGuiThreadInfo
    { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Win32.Rect CaretRect; }
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")] static extern bool KeyGetGuiThreadInfo(uint thread, ref KeyGuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetKeyboardLayout")] static extern nint KeyGetKeyboardLayout(uint thread);
    [DllImport("user32.dll", EntryPoint = "IsChild")] static extern bool KeyIsChild(nint parent, nint child);

    static readonly Dictionary<string, string> ClaudeKeyModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "ctrl", ["control"] = "ctrl", ["ctl"] = "ctrl", ["control_l"] = "ctrl", ["control_r"] = "ctrl",
        ["alt"] = "alt", ["alt_l"] = "alt", ["alt_r"] = "alt", ["shift"] = "shift", ["maj"] = "shift", ["shift_l"] = "shift", ["shift_r"] = "shift",
        ["win"] = "win", ["cmd"] = "win", ["super"] = "win", ["meta"] = "win", ["windows"] = "win",
    };
    static readonly Dictionary<string, string> ClaudeKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = "enter", ["entrée"] = "enter", ["return"] = "enter", ["tab"] = "tab", ["space"] = "space", ["espace"] = "space",
        ["backspace"] = "backspace", ["retour"] = "backspace", ["escape"] = "escape", ["esc"] = "escape", ["échap"] = "escape",
        ["delete"] = "delete", ["del"] = "delete", ["suppr"] = "delete", ["insert"] = "insert", ["ins"] = "insert", ["home"] = "home", ["début"] = "home",
        ["end"] = "end", ["fin"] = "end", ["pageup"] = "page_up", ["pgup"] = "page_up", ["page_up"] = "page_up", ["prior"] = "page_up",
        ["pagedown"] = "page_down", ["pgdn"] = "page_down", ["page_down"] = "page_down", ["next"] = "page_down",
        ["up"] = "up", ["haut"] = "up", ["down"] = "down", ["bas"] = "down", ["left"] = "left", ["gauche"] = "left", ["right"] = "right", ["droite"] = "right",
        ["apps"] = "apps", ["menu"] = "apps", ["printscreen"] = "printscreen", ["pause"] = "pause", ["capslock"] = "capslock", ["numlock"] = "numlock", ["scrolllock"] = "scrolllock",
        ["plus"] = "plus", ["minus"] = "minus", ["comma"] = "comma", ["period"] = "period",
    };
    internal static string NormalizeClaudeKeyChord(string spec)
    {
        var text = spec.Trim().Replace(" ", "");
        string[] parts = text == "+" ? ["+"] : text.EndsWith("++", StringComparison.Ordinal) && text.Length > 2 ? [.. text[..^2].Split('+'), "+"] : text.Split('+');
        if (parts.Any(p => p.Length == 0)) throw new ActionRefusedException("Incomplete key combination; nothing sent");
        var modifiers = new List<string>();
        foreach (var part in parts[..^1])
        {
            if (!ClaudeKeyModifiers.TryGetValue(part, out var modifier)) throw new ActionRefusedException("Unknown key modifier; nothing sent");
            if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
        }
        var last = parts[^1]; string key;
        if (modifiers.Count == 0 && ClaudeKeyModifiers.TryGetValue(last, out var solo)) key = solo;
        else if (ClaudeKeyNames.TryGetValue(last, out var named)) key = named;
        else if (last.Length >= 2 && (last[0] == 'f' || last[0] == 'F') && int.TryParse(last[1..], out var function) && function is >= 1 and <= 24) key = "f" + function;
        else if (last.Length == 1) key = last[0] is >= 'A' and <= 'Z' ? last.ToLowerInvariant() : last;
        else throw new ActionRefusedException("Unknown key name; nothing sent");
        return string.Join('+', modifiers.Append(key));
    }
    internal static string NormalizeClaudeKeySequence(string spec) => string.Join(' ', KeySequencePlan(spec, _ => { }, NormalizeClaudeKeyChord));
    static string[] KeySequencePlan(string spec, Action<string> validate, Func<string, string>? normalize = null)
    {
        var keys = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) throw new ActionRefusedException("Empty key sequence; nothing sent");
        if (normalize != null) keys = keys.Select(normalize).ToArray();
        foreach (var key in keys) validate(key); // Reject the whole malformed plan before its prefix can run.
        return keys;
    }
    static bool KeyDeliveryConfirmed(JsonObject outcome) => Bool(outcome, "ok") && Str(outcome, "effect") is "sent" or "applied";
    static void ValidateKeyContext(JsonObject initial, JsonObject fresh)
    {
        if (Str(initial, "processStartedUtc").Length == 0 || Str(initial, "processStartedUtc") != Str(fresh, "processStartedUtc") || Str(initial, "revision") != Str(fresh, "revision"))
            throw new ActionRefusedException("Keyboard window/process identity changed; current combination not sent");
    }
    static void ValidateKeyFocus(JsonObject initial, JsonObject fresh)
    {
        if (initial["nativeWindow"] != null && !JsonNode.DeepEquals(initial["nativeWindow"], fresh["nativeWindow"]) || initial["element"] != null && !JsonNode.DeepEquals(initial["element"], fresh["element"]))
            throw new ActionRefusedException("Keyboard focus changed before input; current combination not sent");
    }
    static JsonObject ReadKeyFocus(SessionState state, JsonObject snapshot, JsonObject step, bool requireReady, bool devtools)
    {
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        if (devtools)
        {
            // The transport attests the actual DOM focus after binding the page and backendNodeId.
            // UIA identity remains real and is checked when the caller named a target; no DOM anchor is fabricated here.
            if (requireReady && !ClaudeDomKeyEntryReady(state)) throw new ActionRefusedException("The DevTools keyboard entry is not verified; no key sent");
            return new JsonObject { ["transport"] = "devtools", ["element"] = selector != null ? ElementIdentity(snapshot, StepTarget(snapshot, step, selector)) : null };
        }
        var window = (nint)state.BoundWindow!.Value.Id;
        var thread = Win32.GetWindowThreadProcessId(window, out _);
        var info = new KeyGuiThreadInfo { Size = Marshal.SizeOf<KeyGuiThreadInfo>() };
        var nativeReady = thread != 0 && KeyGetGuiThreadInfo(thread, ref info) && info.Focus != 0 && Win32.IsWindow(info.Focus) &&
            (info.Focus == window || KeyIsChild(window, info.Focus) || NativeInteraction.AllowsForeground(window, info.Focus));
        JsonObject? focused;
        if (selector != null)
        {
            focused = StepTarget(snapshot, step, selector);
            if (requireReady && !Bool(focused, "focused") && (!nativeReady || Number(focused, "nativeWindow") == 0 || Number(focused, "nativeWindow") != (long)info.Focus))
                throw new ActionRefusedException("The named keyboard target is not focused; no key sent");
        }
        else
        {
            var candidates = Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr") && Bool(e, "focused")).ToList();
            var leaves = candidates.Where(e => !candidates.Any(other => !ReferenceEquals(e, other) && (other["ancestors"] as JsonArray)?.Any(i => Number(e, "index", -1) == i?.GetValue<int>()) == true)).ToList();
            if (leaves.Count > 1) throw new ActionRefusedException("Keyboard focus is ambiguous; no key sent");
            focused = leaves.SingleOrDefault();
        }
        if (requireReady && !nativeReady && !Bool(focused, "focused"))
            throw new ActionRefusedException("The keyboard focus cannot be verified in the named window; no key sent");
        if (state.Agent == "codex" && Bool(focused, "password")) throw new ActionRefusedException("Password controls are refused for Codex; no key sent");
        return new JsonObject { ["nativeWindow"] = nativeReady ? JsonValue.Create((long)info.Focus) : null, ["element"] = focused != null ? ElementIdentity(snapshot, focused) : null };
    }
    static string KeyPermissionStamp(JsonObject snapshot, JsonObject focus, string title)
    {
        var controls = new JsonArray();
        foreach (var element in Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr")))
        {
            var control = IdentityPart(element);
            foreach (var field in new[] { "value", "valueTruncated", "password", "focused", "isDefault", "accessKey", "disabled", "checked", "selected", "expanded" }) control[field] = element[field]?.DeepClone();
            controls.Add(control);
        }
        return Routines.Digest(new JsonArray(JsonValue.Create(title), focus.DeepClone(), controls).ToJsonString(JsonOut));
    }

    static JsonObject? TryPerformKeySequence(SessionState state, JsonObject step, JsonObject snapshot, bool physicalAllowed)
    {
        if (Str(step, "kind") != "key") return null;
        var window = (nint)state.BoundWindow!.Value.Id;
        var devtools = HasClaudeDevtools(state);
        var layout = KeyGetKeyboardLayout(Win32.GetWindowThreadProcessId(window, out _));
        var keys = KeySequencePlan(Str(step, "key"), key =>
        {
            if (devtools) Cdp.ValidateDomKey(key);
            else if (physicalAllowed) Keys.ParseFor(key, layout);
            else Keys.ParseForWindow(window, key);
        }, key => state.Agent == "claude" ? NormalizeClaudeKeyChord(key) : key);
        if (step["expect"] is JsonObject expect && ExpectationHolds(state, snapshot, expect, ocr: true))
        {
            if (ExactExpectation(expect) || state.Agent == "claude" && LegacyStateCheck(expect)) return StepResult("already-satisfied", "=", false, true, "Exact state already satisfied; no input sent");
            throw new ActionRefusedException("Vague postcondition is already true before input; choose a condition that this sequence changes");
        }
        JsonObject? last = null;
        return ExecuteKeySequence(keys, key =>
        {
            Program.InputStarted = false;
            EmergencyStop.ThrowIfStopped(); state.Interaction?.Check(); GuardClaudeDevtools(state);
            var single = step.DeepClone().AsObject(); single["key"] = key; single.Remove("expect");
            var before = InspectNative(state); ValidateKeyContext(snapshot, before);
            var focus = ReadKeyFocus(state, before, single, requireReady: false, devtools);
            var grant = new NativeKeyGrant();
            using var entry = new NativeKeyEntryScope(() =>
            {
                EmergencyStop.ThrowIfStopped(); state.Interaction?.Check(); GuardClaudeDevtools(state);
                var current = InspectNative(state); ValidateKeyContext(snapshot, current);
                var currentFocus = ReadKeyFocus(state, current, single, requireReady: true, devtools); ValidateKeyFocus(focus, currentFocus);
                var title = Win32.Title(window); var stamp = KeyPermissionStamp(current, currentFocus, title);
                grant.Check(stamp, () => CheckGesture(state, single, current, null));
                if (title != Win32.Title(window)) throw new ActionRefusedException("Keyboard context changed while authorizing; current combination not sent");
            });
            var request = NativeRequest(state, "key"); request["key"] = key;
            if (devtools && (single["selector"] as JsonObject ?? single["focus"] as JsonObject) is { } selector)
            {
                var target = StepTarget(before, single, selector); request["selector"] = SelectorOf(target); AddIdentity(request, target, before);
            }
            using var cdpScope = devtools ? ClaudeDevtoolsScope(state, single, before) : null;
            Program.InputStarted = false;
            JsonObject outcome;
            try { outcome = ObjectOf(ExecuteInternal(request)); }
            catch (BackgroundNotApplicableException) when (!devtools && !Program.InputStarted && physicalAllowed)
            { request["operation"] = "physical"; request["action"] = "key"; outcome = NativeFallbackAttempt(() => PhysicalNative(state, request), () => Program.InputStarted); }
            if (!entry.Checked && KeyDeliveryConfirmed(outcome)) throw new AfterInputException("Keyboard transport returned without its entry guard; inspect before deciding anything else", new InvalidOperationException("Missing key entry guard"));
            last = outcome; return outcome;
        }, () => Program.InputStarted, () => ProveNative(state, step, snapshot, last, inputEverSent: true));
    }
    static JsonObject ExecuteKeySequence(string[] keys, Func<string, JsonObject> deliver, Func<bool> attempted, Func<JsonObject> prove)
    {
        var deliveries = new JsonArray(); var completed = 0;
        JsonObject Report(JsonObject result)
        {
            var uncertain = deliveries.LastOrDefault() is JsonObject last && Str(last, "delivery") == "uncertain";
            var trace = new JsonObject { ["delivered"] = completed, ["total"] = keys.Length, ["nextIndex"] = completed, ["deliveries"] = deliveries.DeepClone(), ["automaticReplay"] = false,
                ["note"] = uncertain ? "Current delivery is uncertain. Observe again; never retry it or replay the prefix automatically." : "Delivery is a transport acknowledgement, not proof of the UI effect. Never replay the delivered prefix." };
            if (!uncertain) trace["remainingCount"] = keys.Length - completed;
            result["keySequence"] = trace;
            // The call logger reads the step's attempts field. Retain every combination's safe trace there too,
            // rather than only the final outcome's fallback (or none when the last key stayed in the background).
            var attempts = new JsonArray();
            foreach (var delivery in deliveries.OfType<JsonObject>())
                foreach (var attempt in (delivery["attempts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var copy = attempt.DeepClone().AsObject(); copy["index"] = delivery["index"]?.DeepClone(); attempts.Add(copy);
                }
            if (attempts.Count > 0) result["attempts"] = attempts;
            return result;
        }
        foreach (var key in keys)
        {
            try
            {
                var outcome = deliver(key);
                if (!KeyDeliveryConfirmed(outcome))
                {
                    deliveries.Add(new JsonObject { ["index"] = completed, ["delivery"] = "uncertain", ["technique"] = outcome["technique"]?.DeepClone(), ["attempts"] = outcome["attempts"]?.DeepClone() });
                    return Report(StepResult("unknown", "~", null, false, "Current key delivery is uncertain; no following combination sent", outcome));
                }
                deliveries.Add(new JsonObject { ["index"] = completed, ["delivery"] = "confirmed", ["technique"] = outcome["technique"]?.DeepClone(), ["attempts"] = outcome["attempts"]?.DeepClone() }); completed++;
            }
            catch (Exception error)
            {
                var input = attempted() || error is AfterInputException;
                var result = RoutineFailure(error, completed > 0 || input);
                result["message"] = $"{completed} earlier combination(s) delivered. Current combination {(input ? "may have been sent" : "not sent")}: " + error.Message;
                if (error is NativeConfirmationException) result["authorizationRequired"] = true;
                deliveries.Add(new JsonObject { ["index"] = completed, ["delivery"] = input ? "uncertain" : "not-sent", ["attempts"] = result["attempts"]?.DeepClone() });
                return Report(result);
            }
        }
        try { return Report(prove()); }
        catch (Exception error) { return Report(RoutineFailure(error, true)); }
    }

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
                live["elements"]!.AsArray().Add(new JsonObject { ["index"] = 1, ["role"] = "button", ["name"] = "Send", ["isDefault"] = true });
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
        assertions += Keys.KeyGrammarAssertions();
        Console.WriteLine(new JsonObject { ["ok"] = true, ["assertions"] = assertions, ["gui"] = false }.ToJsonString()); return 0;
    }
}
