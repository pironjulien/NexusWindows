using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

static partial class McpServer
{
    [ThreadStatic] static NativeTextSubmitScope? textSubmitEntry;
    internal sealed class NativeTextSubmitScope : IDisposable
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
    internal sealed class NativeKeyEntryScope : IDisposable
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
    internal sealed class NativeKeyGrant
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
    [LibraryImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KeyGetGuiThreadInfo(uint thread, ref KeyGuiThreadInfo info);
    [LibraryImport("user32.dll", EntryPoint = "GetKeyboardLayout")] private static partial nint KeyGetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll", EntryPoint = "IsChild")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KeyIsChild(nint parent, nint child);

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
    // The three profiles: French and English key names and modifiers become canonical before the guards read them; any
    // other syntax (a virtual key, a Codex chord) is left for the key parser, which refuses what it does not know.
    internal static string NormalizeKeyChord(string spec) { try { return NormalizeClaudeKeyChord(spec); } catch (ActionRefusedException) { return spec.Trim(); } }
    internal static string NormalizeKeySequence(string spec) => string.Join(' ', KeySequencePlan(spec, _ => { }, NormalizeKeyChord));
    internal static string[] KeySequencePlan(string spec, Action<string> validate, Func<string, string>? normalize = null)
    {
        var keys = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keys.Length == 0) throw new ActionRefusedException("Empty key sequence; nothing sent");
        if (normalize != null) keys = keys.Select(normalize).ToArray();
        foreach (var key in keys) validate(key); // Reject the whole malformed plan before its prefix can run.
        return keys;
    }
    static bool KeyDeliveryConfirmed(JsonObject outcome) => Bool(outcome, "ok") && Str(outcome, "effect") is "sent" or "applied";
    // The shortened path of a brief foreground key: only with the real keyboard allowed for that lease and never through
    // DevTools (whose DOM focus transport needs its own observation).
    internal static bool BriefKeyPath(bool briefKey, bool devtools, bool physicalAllowed) => briefKey && !devtools && physicalAllowed;
    internal static void ValidateKeyContext(JsonObject initial, JsonObject fresh)
    {
        if (Str(initial, "processStartedUtc").Length == 0 || Str(initial, "processStartedUtc") != Str(fresh, "processStartedUtc") || Str(initial, "revision") != Str(fresh, "revision"))
            throw new ActionRefusedException("Keyboard window/process identity changed; current combination not sent");
    }
    internal static void ValidateKeyFocus(JsonObject initial, JsonObject fresh)
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
        var threadRead = thread != 0 && KeyGetGuiThreadInfo(thread, ref info);
        var nativeReady = threadRead && info.Focus != 0 && Win32.IsWindow(info.Focus) &&
            (info.Focus == window || KeyIsChild(window, info.Focus) || NativeInteraction.AllowsForeground(window, info.Focus));
        // Nothing focused anywhere in the window's thread (a window never activated): Windows hands keys to the window
        // itself, so they reach no field. The keys go to the named window, read back like a focus (01/11: Ctrl+K refused
        // on the Win32 and WinForms labs, "focus cannot be verified").
        var windowItself = threadRead && info.Focus == 0 && selector == null && !Elements(snapshot).OfType<JsonObject>().Any(e => Bool(e, "focused"));
        if (windowItself) { nativeReady = true; info.Focus = window; }
        JsonObject? focused;
        if (selector != null)
        {
            focused = StepTarget(snapshot, step, selector);
            if (requireReady && !Bool(focused, "focused") && (!nativeReady || Number(focused, "nativeWindow") == 0 || Number(focused, "nativeWindow") != (long)info.Focus))
                throw new ActionRefusedException("The named keyboard target is not focused; no key sent");
        }
        else focused = FocusedKeyLeaf(snapshot, state.RetainedFocus);
        if (requireReady && !nativeReady && !Bool(focused, "focused"))
            throw new ActionRefusedException("The keyboard focus cannot be verified in the named window; no key sent");
        if (Bool(focused, "password")) throw new GuardRailException("Password controls are refused; no key sent");
        return new JsonObject { ["nativeWindow"] = nativeReady ? JsonValue.Create((long)info.Focus) : null, ["element"] = focused != null ? ElementIdentity(snapshot, focused) : null };
    }
    // The focused element a key without target goes to. Several focused leaves (Chrome: the address bar and the page
    // both report keyboard focus) are resolved by the field the batch just aimed at (click, set or type), when it is one
    // of them (04/11: Enter after a set of the address bar was refused, "Keyboard focus is ambiguous"); otherwise refused.
    internal static JsonObject? FocusedKeyLeaf(JsonObject snapshot, JsonObject? retained)
    {
        var candidates = Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr") && Bool(e, "focused")).ToList();
        var leaves = candidates.Where(e => !candidates.Any(other => !ReferenceEquals(e, other) && (other["ancestors"] as JsonArray)?.Any(i => Number(e, "index", -1) == i?.GetValue<int>()) == true)).ToList();
        if (leaves.Count <= 1) return leaves.SingleOrDefault();
        if (retained != null)
        {
            var aimed = FindNative(snapshot, retained);
            if (aimed.Count == 1 && leaves.Any(leaf => ReferenceEquals(leaf, aimed[0]))) return aimed[0];
        }
        throw new ActionRefusedException("Keyboard focus is ambiguous; no key sent");
    }
    internal static string KeyPermissionStamp(JsonObject snapshot, JsonObject focus, string title)
    {
        var controls = new JsonArray();
        foreach (var element in Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr")))
        {
            var control = IdentityPart(element);
            foreach (var field in new[] { "value", "valueTruncated", "password", "focused", "isDefault", "accessKey", "disabled", "checked", "selected", "expanded" }) control[field] = element[field]?.DeepClone();
            controls.Add(EngineJson.Node(control));
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
            else if (ChromiumInput.IsChromium(window)) Keys.ParseForChromium(window, key);
            else Keys.ParseForWindow(window, key);
        }, NormalizeKeyChord);
        if (step["expect"] is JsonObject expect && ExpectationHolds(state, snapshot, expect, ocr: true))
        {
            if (ExactExpectation(expect) || LegacyStateCheck(expect)) return StepResult("already-satisfied", "=", false, true, "Exact state already satisfied; no input sent");
            throw new ActionRefusedException("Vague postcondition is already true before input; choose a condition that this sequence changes");
        }
        // A key aimed at a Chromium field that does not hold the focus (05/11: Enter on the address bar after a set, « The named
        // keyboard target is not focused »): the field takes it first in the background, as a click on it does, read back.
        if (!devtools && ChromiumInput.IsChromium(window) && (step["selector"] as JsonObject ?? step["focus"] as JsonObject) is { } named &&
            StepTarget(snapshot, step, named) is { } field && !Bool(field, "focused") && Str(field, "role") is "edit" or "document")
        {
            var focus = NativeRequest(state, "click"); focus["selector"] = SelectorOf(field); AddIdentity(focus, field, snapshot);
            Program.InputStarted = false;
            try { ObjectOf(ExecuteInternal(focus)); }
            catch (BackgroundNotApplicableException) when (!Program.InputStarted) { } // not focusable in the background: the key keeps its own refusal
            snapshot = InspectNative(state);
        }
        JsonObject? last = null;
        // Brief foreground key (SessionState.BriefKey): the decision does not depend on the focus, so the focus is read
        // once, at the entry guard just before input (password and gesture guards included), not also beforehand.
        var brief = BriefKeyPath(state.BriefKey, devtools, physicalAllowed);
        return ExecuteKeySequence(keys, key =>
        {
            Program.InputStarted = false;
            EmergencyStop.ThrowIfStopped(); state.Interaction?.Check(); GuardClaudeDevtools(state);
            var single = step.DeepClone().AsObject(); single["key"] = key; single.Remove("expect");
            var before = brief ? snapshot : InspectNative(state); ValidateKeyContext(snapshot, before);
            var focus = brief ? null : ReadKeyFocus(state, before, single, requireReady: false, devtools);
            var grant = new NativeKeyGrant();
            using var entry = new NativeKeyEntryScope(() =>
            {
                EmergencyStop.ThrowIfStopped(); state.Interaction?.Check(); GuardClaudeDevtools(state);
                var current = InspectNative(state); ValidateKeyContext(snapshot, current);
                var currentFocus = ReadKeyFocus(state, current, single, requireReady: true, devtools); if (focus != null) ValidateKeyFocus(focus, currentFocus);
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
            if (brief) { request["operation"] = "physical"; request["action"] = "key"; outcome = NativeFallbackAttempt(() => PhysicalNative(state, request), () => Program.InputStarted); }
            else try { outcome = ObjectOf(ExecuteInternal(request)); }
            catch (BackgroundNotApplicableException) when (!devtools && !Program.InputStarted && physicalAllowed)
            { request["operation"] = "physical"; request["action"] = "key"; outcome = NativeFallbackAttempt(() => PhysicalNative(state, request), () => Program.InputStarted); }
            catch (BackgroundNotApplicableException e) when (!devtools && !Program.InputStarted && !physicalAllowed) { throw new ForegroundNeededException(e.Message); }
            if (!entry.Checked && KeyDeliveryConfirmed(outcome)) throw new AfterInputException("Keyboard transport returned without its entry guard; inspect before deciding anything else", new InvalidOperationException("Missing key entry guard"));
            last = outcome; return outcome;
        }, () => Program.InputStarted, () => ProveNative(state, step, snapshot, last, inputEverSent: true));
    }
    internal static JsonObject ExecuteKeySequence(string[] keys, Func<string, JsonObject> deliver, Func<bool> attempted, Func<JsonObject> prove)
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
                    var copy = attempt.DeepClone().AsObject(); copy["index"] = delivery["index"]?.DeepClone(); attempts.Add(EngineJson.Node(copy));
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
                    deliveries.Add(EngineJson.Node(new JsonObject { ["index"] = completed, ["delivery"] = "uncertain", ["technique"] = outcome["technique"]?.DeepClone(), ["attempts"] = outcome["attempts"]?.DeepClone() }));
                    return Report(StepResult("unknown", "~", null, false, "Current key delivery is uncertain; no following combination sent", outcome));
                }
                deliveries.Add(EngineJson.Node(new JsonObject { ["index"] = completed, ["delivery"] = "confirmed", ["technique"] = outcome["technique"]?.DeepClone(), ["attempts"] = outcome["attempts"]?.DeepClone() })); completed++;
            }
            catch (Exception error)
            {
                var input = attempted() || error is AfterInputException;
                var result = RoutineFailure(error, completed > 0 || input);
                result["message"] = $"{completed} earlier combination(s) delivered. Current combination {(input ? "may have been sent" : "not sent")}: " + error.Message;
                if (error is NativeConfirmationException) result["authorizationRequired"] = true;
                if (error is ForegroundNeededException) result["needsForeground"] = true;
                deliveries.Add(EngineJson.Node(new JsonObject { ["index"] = completed, ["delivery"] = input ? "uncertain" : "not-sent", ["attempts"] = result["attempts"]?.DeepClone() }));
                return Report(result);
            }
        }
        try { return Report(prove()); }
        catch (Exception error) { return Report(RoutineFailure(error, true)); }
    }
}
