using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class McpServer
{
    // Routine stamps are ISO 8601 (an offset or the local time), read the same whatever the user's culture.
    static DateTimeOffset ParseSaved(string text) => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    static string NativeStateRoot(string agent)
    {
        var custom = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME");
        if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
        return Path.Combine(CommonHome, "state", agent);
    }
    internal static string StructureNative(JsonObject snapshot)
    {
        var a = new JsonArray();
        foreach (var e in Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr")))
        {
            var role = Str(e, "role");
            var name = Str(e, "name");
            if (role == "tab" && !string.IsNullOrEmpty(name))
            {
                // Chromium appends this localized AX metric, including decimal values below 100 units.
                // Match its complete suffix only: page-title text and other controls retain their strict hash.
                name = Regex.Replace(name, @"\s+-\s+(?:Utilisation (?:élevée )?de la mémoire|(?:High )?memory usage)\s+-\s+[0-9]+(?:[.,][0-9]+)?\s*(?:[KMGTPE](?:o|B))\z", "",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            a.Add(EngineJson.Node(new JsonArray(e["depth"]?.DeepClone(), e["role"]?.DeepClone(), e["automationId"]?.DeepClone(),
                JsonValue.Create(role is "button" or "tab" or "menuitem" or "checkbox" or "radio" or "combobox" ? name : ""))));
        }
        return Routines.Digest(a.ToJsonString(JsonOut));
    }
    internal static string BoundaryNative(JsonObject snapshot)
    {
        var states = new JsonArray();
        foreach (var e in Elements(snapshot).OfType<JsonObject>().Where(e => !Bool(e, "ocr")))
        {
            var state = new JsonObject();
            foreach (var key in new[] { "index", "role", "automationId", "value", "valueTruncated", "checked", "selected", "expanded", "focused", "disabled" }) state[key] = e[key]?.DeepClone();
            states.Add(EngineJson.Node(state));
        }
        return Routines.Digest(new JsonArray(JsonValue.Create(StructureNative(snapshot)), states).ToJsonString(JsonOut));
    }
    static string RoutineStamp(JsonObject entry)
    {
        var definition = new JsonObject();
        foreach (var key in new[] { "steps", "anchors", "revision", "structureHash", "saved", "coverage", "verifiedTransitions", "migration" }) definition[key] = entry[key]?.DeepClone();
        return Routines.Digest(definition.ToJsonString(JsonOut));
    }
    static string AnchorNative(JsonObject snapshot, JsonObject step)
    {
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        var e = selector != null ? StepTarget(snapshot, step, selector) : Elements(snapshot).OfType<JsonObject>().FirstOrDefault(e => Bool(e, "focused")) ?? Elements(snapshot).OfType<JsonObject>().First();
        JsonArray Shape(JsonObject node) => new(JsonValue.Create(Str(node, "role")), JsonValue.Create(Str(node, "automationId")),
            JsonValue.Create(Str(node, "role") is "button" or "tab" or "menuitem" or "checkbox" or "radio" or "combobox" or "link" or "treeitem" or "listitem" ? Str(node, "name") : ""));
        var chain = new JsonArray();
        foreach (var i in e["ancestors"] as JsonArray ?? new JsonArray()) chain.Add(EngineJson.Node(Shape(Elements(snapshot).OfType<JsonObject>().First(n => Number(n, "index", -1) == i!.GetValue<int>()))));
        return Routines.Digest(new JsonArray(Shape(e), chain).ToJsonString(JsonOut));
    }
    internal static JsonObject RoutineStepSnapshot(JsonObject start, JsonObject step, string operation, string? routine, string? remember,
        int index, int next, bool interactionOpened, Func<JsonObject> inspect)
    {
        // Only the first direct step follows start through pure plan/permission validation. A routine read,
        // continuation, new foreground lease or target preparation must retain its own fresh inspection.
        if (operation != "run" || routine != null || remember != null || index != 0 || next != 0 || interactionOpened || step["ocrTarget"] != null || Str(step, "kind") == "wait") return inspect();
        var selector = step["selector"] as JsonObject ?? step["focus"] as JsonObject;
        if (selector != null)
        {
            JsonObject target;
            try { target = StepTarget(start, step, selector); }
            catch (ActionRefusedException) { return inspect(); }
            if (!Bool(target, "visible")) return inspect();
        }
        return start;
    }
    static void CheckStructureSinceLook(SessionState state, JsonObject args)
    {
        if (state.CurrentObservation == null) throw new ActionRefusedException("Look at the named window before acting");
        args = RoutineCompatibilityArgs(args);
        var observed = ObjectOf(state.CurrentObservation.Value);
        var steps = new JsonArray();
        if (args["routine"] == null && args["steps"] is JsonArray raw)
            foreach (var step in raw) steps.Add(EngineJson.Node(NormalizeStep(state, observed, step!.AsObject(), allowFutureTarget: true)));
        ValidateNativeObservation(observed, InspectNative(state), args, steps);
    }
    static JsonObject ActNative(SessionState state, JsonObject args, bool modern, bool structureCheckedBeforeLease = false)
    {
        args = RoutineCompatibilityArgs(args);
        var operation = Str(args, "operation", "run");
        ValidateRoutineControls(args, operation);
        var oldBound = state.BoundWindow;
        var named = ResolveWindow(state, args, modern);
        if (oldBound is { } old && old.Id != named.Id && operation is "run" or "continue" or "inspect")
        {
            state.CurrentObservation = null; state.CurrentObservationId = null; state.Pending = null;
            throw new ActionRefusedException("The named window differs from the last look; observe it before acting");
        }
        var memory = new NativeRoutineMemory(state.Agent, named.App);
        memory.Sync();
        if (operation is "list" or "sync" or "forget" or "inspect")
        {
            JsonObject data;
            if (operation == "list") data = new JsonObject { ["operation"] = operation, ["routines"] = memory.List() };
            else if (operation == "sync") data = memory.Sync();
            else if (operation == "forget") data = new JsonObject { ["forgotten"] = memory.Forget(Str(args, "routine")) };
            else
            {
                if (state.CurrentObservation == null || DateTimeOffset.UtcNow - state.ObservedAt > TimeSpan.FromSeconds(60)) throw new ActionRefusedException("Inspect needs a fresh look");
                var entry = memory.Read(Str(args, "routine")) ?? throw new InvalidOperationException("Routine not found");
                var snapshot = InspectNative(state);
                state.ReviewedRoutines[ReviewKey(named.App, Str(args, "routine"))] = RoutineStamp(entry);
                data = new JsonObject { ["inspected"] = true, ["routine"] = Str(args, "routine"), ["definition"] = entry.DeepClone(),
                    ["applicationChanged"] = Str(entry, "revision") != Str(snapshot, "revision"), ["structureChanged"] = Str(entry, "structureHash") != StructureNative(snapshot),
                    ["needsReview"] = NeedsReview(entry), ["note"] = "Inspection grants no authorization; each target and current permission is revalidated at execution." };
            }
            return NativeResponse(data);
        }
        if (operation is not ("run" or "continue")) throw new InvalidOperationException("Unsupported routine operation");
        // One agent at a time on this window for the whole batch (WindowLock.cs); another one waits, then is refused.
        using var windowLock = WindowLock.Acquire(named.Id, state.Agent);
        if (state.CurrentObservation == null || state.CurrentObservationId == null) throw new ActionRefusedException("Look at the named window before acting");
        if (DateTimeOffset.UtcNow - state.ObservedAt > TimeSpan.FromSeconds(60)) throw new ActionRefusedException("Observation expired after 60 seconds; look again");
        var obsArg = Str(args, "observation");
        if (obsArg.Length > 0 && obsArg != state.CurrentObservationId) throw new ActionRefusedException("Observation already consumed or replaced; look again");
        var observed = ObjectOf(state.CurrentObservation.Value);
        var start = InspectNative(state);
        CheckNativeApplication(observed, start);
        JsonArray steps, results, anchors; JsonArray? definition = null; string? remember = null, routine = null;
        var next = 0; var transitions = 0; JsonObject? entryForRun = null;
        if (operation == "continue")
        {
            var pending = state.Pending; state.Pending = null;
            if (pending == null || Str(args, "continuation") != pending.Token || DateTimeOffset.UtcNow - pending.Created > TimeSpan.FromSeconds(60) || pending.Window != named.Id || pending.Pid != named.Pid ||
                pending.Started != Str(start, "processStartedUtc") || pending.Revision != Str(start, "revision") || pending.Boundary != BoundaryNative(start)) throw new ActionRefusedException("Continuation expired or its window/application/interface changed; no input sent");
            if (args["steps"] != null || args["params"] != null || args["routine"] != null || args["remember"] != null) throw new ActionRefusedException("A continuation accepts only its token and current authorizations");
            (steps, next, results, definition, remember, routine, anchors, transitions) = (pending.Steps, pending.Next, pending.Results, pending.Definition, pending.Remember, pending.Routine, pending.Anchors, pending.Transitions);
            if (routine != null)
            {
                entryForRun = memory.Read(routine) ?? throw new ActionRefusedException("Routine was removed during pause; continuation refused");
                if (pending.RoutineStamp != RoutineStamp(entryForRun)) throw new ActionRefusedException("Routine definition changed during pause; continuation refused");
            }
        }
        else
        {
            state.Pending = null;
            results = new JsonArray(); anchors = new JsonArray();
            routine = args["routine"] is JsonValue ? Str(args, "routine") : null;
            remember = args["remember"] is JsonValue ? Routines.ValidateRoutineName(Str(args, "remember")) : null;
            if (routine != null)
            {
                if (args["steps"] != null || remember != null) throw new InvalidOperationException("Routine replay cannot also supply steps or remember");
                entryForRun = memory.Read(routine) ?? throw new InvalidOperationException("Routine not found");
                if (NeedsReview(entryForRun) || state.FailedRoutines.Contains(ReviewKey(named.App, routine)))
                {
                    if (!state.ReviewedRoutines.TryGetValue(ReviewKey(named.App, routine), out var reviewed) || reviewed != RoutineStamp(entryForRun)) throw new ActionRefusedException("Routine needs review: fresh look, inspect, then explicit run");
                }
                state.ReviewedRoutines.Remove(ReviewKey(named.App, routine));
                definition = entryForRun["steps"]!.DeepClone().AsArray();
                steps = Routines.ExpandRoutine(definition, Bindings(args));
            }
            else
            {
                if (args["steps"] is not JsonArray raw || raw.Count == 0) throw new InvalidOperationException("act needs a nonempty steps array");
                // Normalize/validate the entire definition before any input.
                steps = new JsonArray();
                foreach (var n in raw) steps.Add(EngineJson.Node(NormalizeStep(state, observed, n as JsonObject ?? throw new InvalidOperationException("Invalid step"), allowFutureTarget: true)));
                if (remember != null)
                {
                    if (!RoutineLearningAllowed(args)) throw new ActionRefusedException("Saving a routine requires staticUI:true");
                    definition = Routines.PrepareRoutineSteps(steps);
                    steps = Routines.ExpandRoutine(definition, Bindings(args));
                }
                else if (args["params"] != null) throw new InvalidOperationException("params applies only to routine learning or replay");
            }
        }
        var localTargetContext = !structureCheckedBeforeLease
            ? ValidateNativeObservation(observed, start, args, steps)
            : ValidateNativeTargetContext(observed, start, args, steps);
        ValidateRoutineAuthorizations(args, steps.Count, next);
        state.CurrentObservation = null; state.CurrentObservationId = null;
        var paste = GardeFous.CheckBlindPaste(steps.OfType<JsonObject>().SelectMany(s => (Str(s, "kind") == "key" ? Str(s, "key").Split(' ', StringSplitOptions.RemoveEmptyEntries) : new[] { Str(s, "key") }).Select(key => (Str(s, "kind"), key, Bool(s, "confirm")))));
        if (paste.Length > 0) throw new ActionRefusedException(paste);
        var timer = Stopwatch.StartNew();
        // A batch that types text keeps going for as long as its foreground lease does (30 s + the text's typing time),
        // so a type that took longer than 30 s is not followed by a pause before its Enter.
        var ceiling = (double)NativeInteraction.BudgetFor(steps);
        var budget = Math.Clamp(args["time_budget"] != null ? Number(args, "time_budget") * 1000 : Number(args, "timeBudgetMs", ceiling), 0, ceiling);
        var physicalAllowed = RoutinePhysicalAllowed(state.Agent, args);
        if (RoutineBoolean(args, "interaction", false) && !physicalAllowed) throw new ActionRefusedException("interaction:true requires permission for foreground input; nothing sent");
        using var interaction = RoutineBoolean(args, "interaction", false) && state.Interaction == null ? NativeInteraction.Begin(state, steps) : null;
        var completedThisCall = 0;
        for (var i = next; i < steps.Count; i++)
        {
            if (completedThisCall > 0 && timer.ElapsedMilliseconds >= budget)
            {
                try
                {
                    var boundary = InspectNative(state);
                    if (Str(boundary, "processStartedUtc") != Str(start, "processStartedUtc") || Str(boundary, "revision") != Str(start, "revision")) throw new ActionRefusedException("Application identity changed before the pause boundary");
                    var token = Guid.NewGuid().ToString("N");
                    state.Pending = new PendingNativeBatch(token, DateTimeOffset.UtcNow, named.Id, named.Pid, Str(boundary, "processStartedUtc"), Str(boundary, "revision"), BoundaryNative(boundary),
                        StripPermissions(steps), i, results, definition, remember, routine, entryForRun != null ? RoutineStamp(entryForRun) : null, anchors, transitions);
                    return BatchResponse(state, results, steps.Count, i, "paused", token);
                }
                catch (Exception error)
                {
                    var pauseResponse = RoutinePauseFailure(state, results, steps.Count, i, error);
                    if (routine != null)
                    {
                        var key = ReviewKey(named.App, routine); state.ReviewedRoutines.Remove(key); state.FailedRoutines.Add(key);
                        try { memory.RecordRun(routine, false); }
                        catch (Exception persistence) { pauseResponse["structuredContent"]!["persistenceError"] = "Failure could not be written: " + persistence.GetType().Name; }
                    }
                    return pauseResponse;
                }
            }
            JsonObject stepResult; var actionTimer = Stopwatch.StartNew();
            var step = steps[i]!.DeepClone().AsObject();
            // Never retain an authorization inside a saved definition or continuation.
            step.Remove("confirm"); step.Remove("accord");
            var authorization = (args["authorizations"] as JsonObject)?[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            if (authorization != null) { step["confirm"] = Bool(authorization, "confirm"); step["accord"] = Str(authorization, "accord"); }
            else if (operation == "run" && routine == null) { step["confirm"] = Bool(steps[i], "confirm", Bool(args, "confirm")); step["accord"] = Str(steps[i], "accord", Str(args, "accord")); }
            var preparationInputAttempted = false;
            Program.InputStarted = false;
            try
            {
                EmergencyStop.ThrowIfStopped(); state.Interaction?.Check();
                var before = RoutineStepSnapshot(start, step, operation, routine, remember, i, next, interaction != null, () => InspectNative(state));
                if (Str(before, "processStartedUtc") != Str(start, "processStartedUtc") || Str(before, "revision") != Str(start, "revision")) throw new ActionRefusedException("Application identity changed during batch");
                before = PrepareNativeTarget(state, step, before, () => preparationInputAttempted = true);
                if (Str(before, "processStartedUtc") != Str(start, "processStartedUtc") || Str(before, "revision") != Str(start, "revision")) throw new ActionRefusedException("Application identity changed while preparing the target; no primary action sent");
                if (localTargetContext)
                {
                    // Recheck the local action context after activation/materialization and immediately before the
                    // primary gesture. A live sibling may change; a local selection, field or modal may not.
                    before = InspectNative(state);
                    ValidateNativeTargetContext(observed, before, args, steps);
                }
                var anchor = AnchorNative(before, step);
                if (entryForRun != null && !Bool(entryForRun["migration"], "requiresAnchors") && (entryForRun["anchors"] as JsonArray)?[i]?.GetValue<string>() != anchor) throw new ActionRefusedException("Routine target/context changed; look and inspect before an explicit new run");
                if (anchors.Count <= i) anchors.Add(EngineJson.Node(anchor));
                // foreground on a step allows its physical fallback like foreground on the batch (05/11: it was ignored
                // without a word); fallback:false still forbids it.
                var stepPhysical = physicalAllowed || RoutineBoolean(step, "foreground", false) && RoutineBoolean(args, "fallback", true);
                stepResult = PerformNative(state, step, before, stepPhysical && Bool(step, "fallback", true));
            }
            catch (Exception e) { stepResult = RoutineFailure(e, preparationInputAttempted || Program.InputStarted); }
            stepResult = RoutinePreparedResult(stepResult, preparationInputAttempted);
            // A shortcut the app ignores when posted (Codex, measured) goes by the real keyboard in a brief foreground
            // lease, without asking (Julien, 05/11: « un vol de focus ultra rapide puis remettre derrière »): only when he
            // is not typing or moving the mouse (the lease waits for that pause), his window given back at once, and only
            // for a key refused before any input.
            if (BriefForegroundKey(state, step, stepResult, physicalAllowed, routine))
            {
                try
                {
                    using var brief = NativeInteraction.BeginLegacy(state, new JsonArray(step.DeepClone()));
                    var briefBefore = InspectNative(state);
                    stepResult = PerformNative(state, step, briefBefore, true);
                    stepResult["foregroundReturned"] = brief.ReturnForeground();
                    stepResult["briefForeground"] = true;
                }
                catch (Exception e) { stepResult = RoutineFailure(e, Program.InputStarted); }
            }
            stepResult["index"] = i; stepResult["action"] = Str(step, "kind"); results.Add(EngineJson.Node(stepResult));
            Journal.WriteEntry(state.Agent, Version, state.SessionId, named.App, Str(step, "kind"), Str(stepResult, "status"), Str(stepResult, "technique"), actionTimer.ElapsedMilliseconds, Bool(stepResult, "foregroundTaken"), Str(stepResult, "message"));
            if (Str(stepResult, "status") == "verified") transitions++;
            // A step delivered without a postcondition (sent-unconfirmed) no longer ends a one-off batch when the next step
            // names its own target: that step resolves and guards its target again on a fresh observation before any input,
            // so nothing goes blind (05/11: paste then key took one call per step). Routines keep stopping.
            // Keys in a row (alt+n, ctrl+a, ctrl+v: 05/11, the batch stopped after alt+n) go on like one key sequence: each key
            // delivered is acknowledged, the next one reads its focus again before input, a key not delivered stops the batch.
            if (Str(stepResult, "status") == "sent-unconfirmed" && routine == null && remember == null && i + 1 < steps.Count &&
                (NamesOwnTarget(steps[i + 1]) || KeyFollowsDeliveredKey(step, stepResult, steps[i + 1])))
            {
                completedThisCall++;
                continue;
            }
            if (!Bool(stepResult, "success"))
            {
                state.Pending = null;
                if (routine != null)
                {
                    var reviewKey = ReviewKey(named.App, routine); state.ReviewedRoutines.Remove(reviewKey); state.FailedRoutines.Add(reviewKey);
                    try { memory.RecordRun(routine, false); }
                    catch (Exception error) { stepResult["persistenceError"] = "Routine remains needs-review in this session; failure could not be written: " + error.GetType().Name; }
                }
                return BatchResponse(state, results, steps.Count, i, Str(stepResult, "mark") == "~" ? "unverified" : "failed", null);
            }
            completedThisCall++;
        }
        string? persistenceError = null;
        try
        {
            if (remember != null && definition != null)
                memory.Save(remember, new JsonObject { ["steps"] = definition.DeepClone(), ["anchors"] = anchors.DeepClone(), ["revision"] = Str(start, "revision"), ["structureHash"] = StructureNative(start),
                    ["coverage"] = transitions > 0 ? "verified" : "not-exercised", ["verifiedTransitions"] = transitions });
            if (routine != null)
            {
                if (entryForRun != null && Bool(entryForRun["migration"], "requiresAnchors")) memory.EstablishAnchors(routine, anchors, Str(start, "revision"), StructureNative(start));
                memory.RecordRun(routine, true); memory.Sync(); state.FailedRoutines.Remove(ReviewKey(named.App, routine));
            }
        }
        catch (Exception error)
        {
            persistenceError = "Steps completed; routine persistence failed: " + error.GetType().Name;
            if (routine != null) state.FailedRoutines.Add(ReviewKey(named.App, routine));
        }
        var unconfirmed = results.OfType<JsonObject>().Any(r => Str(r, "status") == "sent-unconfirmed");
        var response = BatchResponse(state, results, steps.Count, steps.Count, unconfirmed ? "unverified" : "success", null);
        if (persistenceError != null) { response["structuredContent"]!["persistenceError"] = persistenceError; response["content"]!.AsArray().Add(EngineJson.Node(TextBlock(persistenceError))); }
        return response;
    }
    static string ReviewKey(string app, string routine) => Routines.ApplicationKey(app) + "|" + routine;
    internal static bool BriefForegroundKey(SessionState state, JsonObject step, JsonObject result, bool physicalAllowed, string? routine) =>
        !physicalAllowed && routine == null && state.Interaction == null && Str(step, "kind") == "key"
        && Bool(step, "fallback", true) && Bool(result, "needsForeground") && Str(result, "status") == "refused";
    internal static bool KeyFollowsDeliveredKey(JsonObject step, JsonObject result, JsonNode? next) =>
        // A click that gave a field the focus (05/11: « Code » in a page, then three keys): the keys that follow read the focus
        // again before any input and go to that field.
        Str(step, "kind") == "click" && (step["selector"] as JsonObject)?["role"]?.ToString() is "edit" or "document" && Str(result, "technique").Length > 0 &&
        next is JsonObject afterClick && Str(afterClick, "kind", Str(afterClick, "do", Str(afterClick, "action", ""))) == "key" && !NamesOwnTarget(afterClick) ||
        Str(step, "kind") == "key" && next is JsonObject following && Str(following, "kind", Str(following, "do", Str(following, "action", ""))) == "key" &&
        result["keySequence"] is JsonObject sequence && Number(sequence, "delivered") == Number(sequence, "total") && Number(sequence, "total") > 0 &&
        (sequence["deliveries"] as JsonArray ?? new()).OfType<JsonObject>().All(d => Str(d, "delivery") == "confirmed");
    internal static bool NamesOwnTarget(JsonNode? step) => step is JsonObject s && (s["target"] != null || s["selector"] is JsonObject || s["focus"] is JsonObject);

    internal static JsonObject RoutinePauseFailure(SessionState state, JsonArray results, int total, int next, Exception error)
    {
        state.Pending = null;
        var response = BatchResponse(state, results, total, next, "unverified", null);
        var message = "Pause boundary could not be verified (" + error.GetType().Name + "); completed steps are retained, no continuation issued";
        response["structuredContent"]!["continuationError"] = message;
        response["content"]!.AsArray().Add(EngineJson.Node(TextBlock(message)));
        return response;
    }

    static bool RoutineBoolean(JsonObject args, string key, bool fallback)
    {
        if (args[key] == null) return fallback;
        if (args[key] is JsonValue value && value.TryGetValue<bool>(out var enabled)) return enabled;
        throw new ActionRefusedException(key + " must be boolean; nothing sent");
    }
    internal static bool RoutinePhysicalAllowed(string agent, JsonObject args)
    {
        var foreground = RoutineBoolean(args, "foreground", false);
        var fallback = RoutineBoolean(args, "fallback", true);
        return foreground && fallback;
    }
    // remember saves a routine unless staticUI:false says the interface changes from one run to the next (three profiles).
    internal static bool RoutineLearningAllowed(JsonObject args)
        => RoutineBoolean(args, "staticUI", true);
    // remember with no steps (or steps:[]) forgets the named routine, for the three profiles.
    internal static JsonObject RoutineCompatibilityArgs(JsonObject args)
    {
        if (Str(args, "operation", "run") != "run" || args["remember"] == null || args["steps"] != null && args["steps"] is not JsonArray { Count: 0 }) return args;
        if (args["routine"] != null || args["continuation"] != null || args["params"] != null && args["params"] is not JsonObject { Count: 0 } || args["authorizations"] != null && args["authorizations"] is not JsonObject { Count: 0 })
            throw new ActionRefusedException("Forgetting a routine accepts no replay, continuation, bindings or authorizations");
        var copy = args.DeepClone().AsObject();
        copy["routine"] = Routines.ValidateRoutineName(Str(args, "remember")); copy["operation"] = "forget";
        foreach (var key in new[] { "remember", "steps", "params", "authorizations" }) copy.Remove(key);
        return copy;
    }
    internal static void ValidateRoutineControls(JsonObject args, string operation)
    {
        foreach (var key in new[] { "foreground", "fallback", "interaction", "staticUI", "confirm" }) RoutineBoolean(args, key, false);
        if (operation is not ("run" or "continue" or "list" or "sync" or "inspect" or "forget")) throw new ActionRefusedException("Unsupported routine operation");
        if (operation != "continue" && args["continuation"] != null) throw new ActionRefusedException("continuation requires operation='continue'");
        if (operation == "continue" && new[] { "steps", "params", "routine", "remember" }.Any(key => args[key] != null)) throw new ActionRefusedException("A continuation accepts only its token and current authorizations");
        if (operation is "list" or "sync" or "inspect" or "forget" && new[] { "steps", "params", "remember", "authorizations", "accord" }.Any(key => args[key] != null))
            throw new ActionRefusedException("Routine management accepts no input, bindings or authorizations");
    }
    internal static void ValidateRoutineAuthorizations(JsonObject args, int total, int next)
    {
        if (args["authorizations"] == null) return;
        if (args["authorizations"] is not JsonObject authorizations) throw new ActionRefusedException("authorizations must be an object indexed from 0; nothing sent");
        foreach (var (key, value) in authorizations)
        {
            if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index.ToString(System.Globalization.CultureInfo.InvariantCulture) != key || index < next || index >= total || value is not JsonObject permission)
                throw new ActionRefusedException("Authorization indices must name a remaining step, starting at 0; nothing sent");
            if (permission.Any(kv => kv.Key is not ("confirm" or "accord"))) throw new ActionRefusedException("An authorization accepts only confirm and accord; nothing sent");
            RoutineBoolean(permission, "confirm", false);
            if (permission["accord"] != null && (permission["accord"] is not JsonValue accord || !accord.TryGetValue<string>(out _))) throw new ActionRefusedException("accord must be text; nothing sent");
        }
    }
    internal static JsonObject RoutinePreparedResult(JsonObject result, bool prepared)
    {
        if (!prepared) return result;
        result["preparation"] = new JsonObject { ["inputAttempted"] = true, ["kind"] = "materialize-target" };
        if (Str(result, "status") == "already-satisfied") result["message"] = "Exact state satisfied after target preparation; no primary action sent";
        else if (!Bool(result, "success") && Str(result, "mark") == "✗") { result["status"] = "unknown"; result["mark"] = "~"; result["verified"] = null; }
        return result;
    }
    internal static JsonObject RoutineFailure(Exception error, bool inputAttempted)
    {
        JsonObject result;
        if (inputAttempted || error is AfterInputException)
            result = StepResult(error is EmergencyStopException or JulienActiveException ? "interrupted" : "unknown", "~", null, false, error.Message);
        else result = error switch
        {
            NativeConfirmationException => StepResult("confirmation-required", "✗", false, false, error.Message),
            EmergencyStopException => StepResult("stopped", "✗", false, false, error.Message),
            JulienActiveException => StepResult("deferred", "✗", false, false, error.Message),
            _ => StepResult("refused", "✗", null, false, error.Message),
        };
        if (!inputAttempted && error is ForegroundNeededException) result["needsForeground"] = true;
        return NativeFailureAttempts(result, error);
    }

    static JsonArray StripPermissions(JsonArray steps)
    {
        var copy = steps.DeepClone().AsArray(); foreach (var step in copy.OfType<JsonObject>()) { step.Remove("confirm"); step.Remove("accord"); } return copy;
    }
    internal static Dictionary<string, string> Bindings(JsonObject args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args["params"] != null && args["params"] is not JsonObject) throw new ActionRefusedException("Routine params must be an object of text bindings; nothing sent");
        if (args["params"] is JsonObject p) foreach (var kv in p) result[kv.Key] = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new InvalidOperationException("Routine parameter must be text");
        return result;
    }
    static JsonObject BatchResponse(SessionState state, JsonArray results, int total, int next, string status, string? continuation)
    {
        var completed = results.OfType<JsonObject>().Count(r => Bool(r, "success"));
        var data = new JsonObject { ["status"] = status, ["success"] = status == "success", ["paused"] = status == "paused", ["steps"] = results.DeepClone(),
            ["progress"] = new JsonObject { ["completed"] = completed, ["total"] = total, ["nextStep"] = next },
            ["next"] = status == "success" ? "✓ done (move on)" : status == "paused" ? "Fresh look, then continue with this token and current authorizations" : "Look again; no step will be replayed automatically" };
        if (continuation != null) data["continuation"] = continuation;
        return NativeResponse(data);
    }
    static JsonObject NativeResponse(JsonObject data) => new() { ["content"] = new JsonArray(TextBlock(data)), ["structuredContent"] = data.DeepClone() };

    // mirrorReview: a definition received from another PC is inspected once before its first replay here.
    internal static bool NeedsReview(JsonObject entry) => !Bool(entry, "lastOk", true) || Bool(entry, "mirrorReview") || Bool(entry["migration"], "requiresAnchors");

    static bool FixedMirrorText(JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return false;
        const string parameter = @"\{\{\s*([\p{L}_][\p{L}\p{N}_]*)\s*\}\}";
        return !Regex.IsMatch(text, parameter) || !string.IsNullOrWhiteSpace(Regex.Replace(text, parameter, ""));
    }
    static bool PrivateMirrorSelector(JsonNode? value)
    {
        if (value == null) return false;
        JsonObject selector;
        if (value is JsonObject obj) selector = obj;
        else
        {
            if (Regex.Split(value.ToString(), @"\s*>+\s*").Any(part => Regex.IsMatch(part, "^\\s*(?:window|fenêtre|fenetre|document)\\s*(?::|[«\"'])", RegexOptions.IgnoreCase))) return true;
            try { selector = LegacySelector(value); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { return true; }
        }
        var role = Str(selector, "role").ToLowerInvariant(); var name = role.StartsWith("legacy/", StringComparison.Ordinal) ? role[7..] : role;
        var titleRole = name is "window" or "document" or "fenêtre" or "fenetre" || role.StartsWith("legacy/", StringComparison.Ordinal) && LegacyRoles(name) is { Length: > 0 } roles && roles.All(r => r is "window" or "document");
        return selector["window"] != null || titleRole && selector["name"] != null || PrivateMirrorSelector(selector["ancestor"]);
    }
    static bool TransferableLegacyRoutine(JsonObject legacy)
    {
        if (Bool(legacy, "localOnly") || legacy["steps"] is not JsonArray steps) return false;
        if ((legacy["anchors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().SelectMany(anchor => anchor.Select(kv => kv.Value).OfType<JsonObject>())
            .Any(node => Str(node, "type") is "Window" or "Document" && Str(node, "name_hash").Length > 0)) return false;
        foreach (var node in steps)
        {
            if (node is not JsonObject step) return false;
            var check = step["check"] as JsonObject; var expect = step["expect"] as JsonObject;
            // Preserve Claude plain_text(), including an old fixed expect such as "Registered". AGY's
            // legacy object conditions are checked too; conversion must never make a private source transferable.
            foreach (var text in new[] { step["text"], step["expect"] is JsonValue ? step["expect"] : null, check?["value"], check?["contains"], expect?["value"], expect?["contains"], expect?["title"], expect?["title_contains"] })
                if (FixedMirrorText(text)) return false;
            if (Str(step, "kind", Str(step, "action")) is "setValue" or "typeText" or "set" or "type" && FixedMirrorText(step["value"])) return false;
            foreach (var selector in new[] { step["target"], step["to"], check?["target"], step["selector"], step["focus"] }) if (PrivateMirrorSelector(selector)) return false;
        }
        return true;
    }
    internal static bool TransferableNativeRoutine(string agent, JsonObject entry)
    {
        if (Bool(entry, "localOnly") || entry["steps"] is not JsonArray steps) return false;
        if (entry["migration"]?["legacyDefinition"] is JsonObject legacy && !TransferableLegacyRoutine(legacy)) return false;
        bool Expectation(JsonObject expect, JsonObject step)
        {
            if (expect["all"] is JsonArray all) return all.OfType<JsonObject>().Count() == all.Count && all.OfType<JsonObject>().All(e => Expectation(e, step));
            if (PrivateMirrorSelector(expect["selector"])) return false;
            if (FixedMirrorText(expect["text"])) return false; // every profile: a fixed expected text can be private (old AGY excluded it too)
            if (Str(expect, "field") == "value")
                foreach (var field in new[] { "equals", "includes" })
                    if (FixedMirrorText(expect[field]) && !(Str(step, "kind") == "select" && JsonNode.DeepEquals(expect[field], step["option"]))) return false;
            return true;
        }
        foreach (var node in steps)
        {
            if (node is not JsonObject step || PrivateMirrorSelector(step["selector"]) || PrivateMirrorSelector(step["focus"])) return false;
            if (Str(step, "kind") is "setValue" or "typeText" && (FixedMirrorText(step["value"]) || FixedMirrorText(step["text"]))) return false;
            if (step["expect"] is JsonObject expect && !Expectation(expect, step)) return false;
        }
        return true;
    }
    internal static JsonObject NativeMirrorDefinition(JsonObject entry, string agent)
    {
        var result = new JsonObject();
        foreach (var field in new[] { "steps", "anchors", "revision", "structureHash", "saved", "coverage", "verifiedTransitions" }) if (entry.ContainsKey(field)) result[field] = entry[field]?.DeepClone();
        if (entry["migration"] is JsonObject migration)
        {
            var archive = new JsonObject();
            foreach (var field in new[] { "sourceAgent", "requiresAnchors" }) if (migration.ContainsKey(field)) archive[field] = migration[field]?.DeepClone();
            if (migration["legacyDefinition"] is JsonObject legacy)
            {
                var definition = new JsonObject();
                foreach (var field in new[] { "steps", "saved", "coverage", "verifiedTransitions" }) if (legacy.ContainsKey(field)) definition[field] = legacy[field]?.DeepClone();
                if (legacy["fingerprint"] is JsonObject fingerprint)
                {
                    var portable = new JsonObject();
                    var provenance = Str(migration, "sourceAgent", agent);
                    var fields = agent != "antigravity" && provenance is ("claude" or "codex") ? new[] { "path", "app", "size", "mtime" } : new[] { "app", "size", "mtime" };
                    foreach (var field in fields) if (fingerprint.ContainsKey(field)) portable[field] = fingerprint[field]?.DeepClone();
                    definition["fingerprint"] = portable;
                }
                archive["legacyDefinition"] = definition;
            }
            result["migration"] = archive;
        }
        return result;
    }

    internal sealed class NativeRoutineMemory
    {
        readonly string agent, appKey, file, root; readonly string? mirror;
        public NativeRoutineMemory(string agent, string app)
        {
            this.agent = agent;
            appKey = Routines.ApplicationKey(app);
            var explicitRoot = agent == "antigravity" ? Environment.GetEnvironmentVariable("NEXUS_AGY_ROUTINES_HOME") : null;
            root = !string.IsNullOrWhiteSpace(explicitRoot) ? Path.GetFullPath(explicitRoot) : Path.Combine(NativeStateRoot(agent), "routines");
            file = Path.Combine(root, Routines.Digest(appKey) + ".json");
            var explicitMirror = (agent == "antigravity" ? Environment.GetEnvironmentVariable("NEXUS_AGY_ROUTINES_MIRROR") : null) ?? Environment.GetEnvironmentVariable("NEXUS_ROUTINES_MIRROR");
            if (explicitMirror != null) mirror = string.IsNullOrWhiteSpace(explicitMirror) ? null : Path.GetFullPath(explicitMirror);
            else if (Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME") == null)
            {
                var source = SourceRoot();
                var project = agent == "antigravity" ? "agy" : agent;
                if (source != null && File.Exists(Path.Combine(source, project, ".nexus-publish.toml"))) mirror = Path.Combine(source, project, "routines", "native");
            }
        }
        JsonObject Empty() => new() { ["schema"] = 1, ["appKey"] = appKey, ["routines"] = new JsonObject(), ["forgotten"] = new JsonObject() };
        void FilterMirror(JsonObject data)
        {
            if (data["routines"] is JsonObject definitions)
                foreach (var pair in definitions.ToList())
                    if (pair.Value is not JsonObject entry || !TransferableNativeRoutine(agent, entry)) definitions.Remove(pair.Key);
        }
        JsonObject Load(string path, bool fromMirror = false)
        {
            if (!File.Exists(path)) return Empty();
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Routine file too large");
            var data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (fromMirror) FilterMirror(data);
            return Validate(data);
        }
        JsonObject Validate(JsonObject data)
        {
            if (Number(data, "schema") != 1 || Str(data, "appKey") != appKey || data["routines"] is not JsonObject || data["forgotten"] is not JsonObject) throw new InvalidDataException("Invalid routine storage; existing data left intact");
            foreach (var kv in data["routines"]!.AsObject())
            {
                Routines.ValidateRoutineName(kv.Key); var e = kv.Value!.AsObject(); e["steps"] = Routines.PrepareRoutineSteps(e["steps"] as JsonArray);
                if ((!Bool(e["migration"], "requiresAnchors") && (e["anchors"] is not JsonArray a || a.Count != e["steps"]!.AsArray().Count || a.Any(n => n is not JsonValue v || !v.TryGetValue<string>(out var s) || !System.Text.RegularExpressions.Regex.IsMatch(s, "^[a-f0-9]{64}$")))) || !DateTimeOffset.TryParse(Str(e, "saved"), out _)) throw new InvalidDataException("Invalid routine metadata");
            }
            foreach (var kv in data["forgotten"]!.AsObject())
            {
                Routines.ValidateRoutineName(kv.Key);
                if (kv.Value is not JsonValue date || !date.TryGetValue<string>(out var text) || !DateTimeOffset.TryParse(text, out _)) throw new InvalidDataException("Invalid routine tombstone");
            }
            return data;
        }
        static void Write(string path, JsonObject data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var text = data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
                if (File.Exists(path) && File.ReadAllText(path) == text) return;
                File.WriteAllText(tmp, text, new UTF8Encoding(false)); File.Move(tmp, path, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        T Locked<T>(Func<T> work)
        {
            Directory.CreateDirectory(root); using var stream = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return work();
        }
        public JsonObject? Read(string name) => Locked(() => (Load(file)["routines"]![Routines.ValidateRoutineName(name)] as JsonObject)?.DeepClone().AsObject());
        public JsonArray List() => Locked(() => new JsonArray(Load(file)["routines"]!.AsObject().Select(kv => (JsonNode)new JsonObject { ["name"] = kv.Key, ["steps"] = kv.Value!["steps"]!.AsArray().Count,
            ["parameters"] = new JsonArray(Routines.ParametersIn(kv.Value!["steps"]).Distinct().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()), ["coverage"] = kv.Value!["coverage"]?.DeepClone(),
            ["verifiedTransitions"] = kv.Value!["verifiedTransitions"]?.DeepClone(), ["needsReview"] = NeedsReview(kv.Value!.AsObject()), ["runs"] = Number(kv.Value, "runs") }).ToArray()));
        public int Import(JsonObject imported, bool fromMirror = false)
        {
            imported = imported.DeepClone().AsObject();
            if (fromMirror) FilterMirror(imported);
            imported = Validate(imported);
            return Locked(() =>
            {
                var local = Load(file); var changed = 0;
                foreach (var kv in imported["routines"]!.AsObject())
                {
                    Routines.ValidateRoutineName(kv.Key); var e = kv.Value!.AsObject(); e["steps"] = Routines.PrepareRoutineSteps(e["steps"] as JsonArray);
                    var date = ParseSaved(Str(e, "saved"));
                    if (local["forgotten"]![kv.Key] is JsonValue tomb && ParseSaved(tomb.GetValue<string>()) >= date) continue;
                    if (local["routines"]![kv.Key] is JsonObject existing && ParseSaved(Str(existing, "saved")) >= date) continue;
                    var incoming = e.DeepClone().AsObject(); if (fromMirror) incoming["mirrorReview"] = true;
                    local["routines"]![kv.Key] = incoming; local["forgotten"]!.AsObject().Remove(kv.Key); changed++;
                }
                foreach (var kv in imported["forgotten"]!.AsObject())
                {
                    var date = ParseSaved(kv.Value!.GetValue<string>());
                    if (local["routines"]![kv.Key] is JsonObject e && ParseSaved(Str(e, "saved")) > date) continue;
                    if (local["forgotten"]![kv.Key] is JsonValue t && ParseSaved(t.GetValue<string>()) >= date) continue;
                    local["routines"]!.AsObject().Remove(kv.Key); local["forgotten"]![kv.Key] = kv.Value.DeepClone(); changed++;
                }
                if (changed > 0) Write(file, local);
                // Validate the resulting file using the same reader as replay.
                Load(file); return changed;
            });
        }
        public void EstablishAnchors(string name, JsonArray anchors, string revision, string structure) => Locked(() =>
        {
            var data = Load(file); var e = data["routines"]![name]!.AsObject();
            e["anchors"] = anchors.DeepClone(); e["revision"] = revision; e["structureHash"] = structure;
            e["migration"]!["requiresAnchors"] = false; Write(file, data); return true;
        });
        public void Save(string name, JsonObject entry)
        {
            Locked(() => { var data = Load(file); entry["steps"] = Routines.PrepareRoutineSteps(entry["steps"]!.AsArray()); entry["saved"] = DateTimeOffset.UtcNow.ToString("O"); entry["runs"] = 0; entry["lastOk"] = true;
                data["routines"]![Routines.ValidateRoutineName(name)] = entry.DeepClone(); data["forgotten"]!.AsObject().Remove(name); Write(file, data); return true; }); Sync();
        }
        public bool Forget(string name)
        {
            var found = Locked(() => { var data = Load(file); var removed = data["routines"]!.AsObject().Remove(Routines.ValidateRoutineName(name)); data["forgotten"]![name] = DateTimeOffset.UtcNow.ToString("O"); Write(file, data); return removed; }); Sync(); return found;
        }
        public void RecordRun(string name, bool ok) => Locked(() => { var data = Load(file); if (data["routines"]![name] is JsonObject e) { e["runs"] = (int)Number(e, "runs") + 1; e["lastOk"] = ok; e["lastRun"] = DateTimeOffset.UtcNow.ToString("O"); if (ok) e.Remove("mirrorReview"); Write(file, data); } return true; });
        public JsonObject Sync()
        {
            if (mirror == null) return new JsonObject { ["mirror"] = "disabled" };
            if (!File.Exists(file) && !File.Exists(Path.Combine(mirror, Path.GetFileName(file)))) return new JsonObject { ["mirror"] = "empty", ["routines"] = 0 };
            return Locked(() =>
            {
                Directory.CreateDirectory(mirror); var mirrorFile = Path.Combine(mirror, Path.GetFileName(file));
                using var held = new FileStream(Path.Combine(root, "mirror-" + Routines.Digest(mirrorFile) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var local = Load(file); var remote = Load(mirrorFile, fromMirror: true);
                // A shared definition is not permission to import private text. Retraction affects the mirror
                // copy only: it is not a user's Forget and must never create a tombstone for the local routine.
                var names = new[] { local["routines"]!.AsObject(), local["forgotten"]!.AsObject(), remote["routines"]!.AsObject(), remote["forgotten"]!.AsObject() }.SelectMany(d => d.Select(kv => kv.Key)).Distinct().ToList();
                foreach (var name in names)
                {
                    var choices = new List<(DateTimeOffset Date, int Priority, JsonObject? Entry)>();
                    foreach (var (data, basePriority) in new[] { (local, 1), (remote, 0) })
                    {
                        if (data["routines"]![name] is JsonObject e) choices.Add((ParseSaved(Str(e, "saved")), basePriority, e));
                        if (data["forgotten"]![name] is JsonValue d) choices.Add((ParseSaved(d.GetValue<string>()), basePriority + 2, null));
                    }
                    var winner = choices.OrderByDescending(c => c.Date).ThenByDescending(c => c.Priority).First();
                    if (winner.Entry != null && !TransferableNativeRoutine(agent, winner.Entry))
                    {
                        // Only a local definition can win this branch: private incoming definitions were excluded.
                        // Keep it verbatim locally, without replacing a different transferable mirror definition.
                        continue;
                    }
                    foreach (var data in new[] { local, remote })
                    {
                        if (winner.Entry == null) { data["routines"]!.AsObject().Remove(name); data["forgotten"]![name] = winner.Date.ToString("O"); }
                        else
                        {
                            var copy = winner.Entry.DeepClone().AsObject();
                            if (ReferenceEquals(data, local) && local["routines"]![name] is JsonObject previous && Str(previous, "saved") == Str(copy, "saved"))
                                foreach (var k in new[] { "runs", "lastOk", "lastRun", "mirrorReview" }) { if (previous[k] != null) copy[k] = previous[k]!.DeepClone(); }
                            else if (ReferenceEquals(data, local) && !ReferenceEquals(winner.Entry, local["routines"]![name])) copy["mirrorReview"] = true;
                            if (ReferenceEquals(data, remote))
                            {
                                copy = NativeMirrorDefinition(copy, agent);
                            }
                            data["routines"]![name] = copy; data["forgotten"]!.AsObject().Remove(name);
                        }
                    }
                }
                Write(file, local); Write(mirrorFile, remote); return new JsonObject { ["mirror"] = "synchronized", ["routines"] = local["routines"]!.AsObject().Count };
            });
        }
    }
}
