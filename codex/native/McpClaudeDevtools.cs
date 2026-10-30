using System.Text.Json;
using System.Text.Json.Nodes;

// No wire flag can grant this route. Only the in-process Claude session's registered launch lease can.
static partial class McpServer
{
    [ThreadStatic] static ClaudeDomScope? claudeDomScope;
    sealed class ClaudeDomScope : IDisposable
    {
        internal readonly SessionState State;
        internal readonly ClaudeDevtoolsLease Lease;
        internal readonly long Window;
        internal readonly string Birth;
        internal readonly JsonObject Step;
        internal readonly int[]? SourceRuntimeId;
        internal bool KeyEntryReady;
        readonly ClaudeDomScope? previous;
        internal ClaudeDomScope(SessionState state, ClaudeDevtoolsLease lease, JsonObject step, JsonObject snapshot)
        {
            State = state; Lease = lease; Window = state.BoundWindow!.Value.Id;
            Birth = Str(snapshot, "processStartedUtc"); Step = step.DeepClone().AsObject();
            if ((step["selector"] as JsonObject ?? step["focus"] as JsonObject) is { } selector && StepTarget(snapshot, step, selector)["runtimeId"] is JsonArray runtime)
                SourceRuntimeId = runtime.Select(v => v!.GetValue<int>()).ToArray();
            previous = claudeDomScope; claudeDomScope = this;
        }
        internal void Check()
        {
            EmergencyStop.ThrowIfStopped(); State.Interaction?.Check(); NativeInteraction.CheckInputLease();
            if (State.Agent != "claude" || GardeFous.CurrentAgent != "claude" || State.BoundWindow?.Id != Window ||
                !Win32.IsWindow((nint)Window) || Program.ProcessStartedUtc(Win32.ProcessOf((nint)Window)) != Birth || !Lease.Includes(Window))
                throw new ActionRefusedException("Claude DevTools window/process binding changed; nothing else sent");
            Lease.Check();
            if (!ClaudePortOwner(Lease.Process.Id, Lease.Port)) throw new ActionRefusedException("Registered DevTools port ownership changed; nothing else sent");
        }
        public void Dispose() { claudeDomScope = previous; }
    }
    static ClaudeDevtoolsLease? ClaudeLease(SessionState state)
    {
        if (state.Agent != "claude" || GardeFous.CurrentAgent != "claude" || state.BoundWindow == null || !ClaudeOwnedLaunches.TryGetValue(state, out var bag)) return null;
        lock (bag.Sync)
        {
            var matches = bag.Items.Where(l => l.Includes(state.BoundWindow.Value.Id)).ToList();
            if (matches.Count > 1) throw new ActionRefusedException("Ambiguous registered DevTools launch; nothing sent");
            return matches.SingleOrDefault();
        }
    }
    // A native file chooser can share the browser's process and launch lease. It is not a webContents.
    // Account checks still run through GuardClaudeDevtools, while its controls retain their native routes.
    static bool ClaudeRenderedWindow(SessionState state) => state.BoundWindow is { } bound && ChromiumInput.HasChromiumContent((nint)bound.Id);
    static bool HasClaudeDevtools(SessionState state) => ClaudeRenderedWindow(state) && ClaudeLease(state) != null;
    static IDisposable? ClaudeDevtoolsScope(SessionState state, JsonObject step, JsonObject snapshot)
    {
        if (!ClaudeRenderedWindow(state)) return null;
        var lease = ClaudeLease(state); if (lease == null) return null;
        var scope = new ClaudeDomScope(state, lease, step, snapshot);
        try { scope.Check(); return scope; } catch { scope.Dispose(); throw; }
    }
    static bool ClaudeDomKeyEntryReady(SessionState state) => claudeDomScope is { KeyEntryReady: true } scope && ReferenceEquals(scope.State, state);
    public static bool ClaudeDevtoolsInScope(nint window) => claudeDomScope is { } scope && scope.Window == (long)window;
    internal static bool ClaudeFixtureDiagnosticsEnabled()
    {
        if (claudeDomScope is not { } scope || scope.State.Agent != "claude" || scope.Lease.OwnSession ||
            !Guid.TryParse(Environment.GetEnvironmentVariable("NEXUS_CDP_FIXTURE_DIAGNOSTICS"), out var run)) return false;
        var custom = Environment.GetEnvironmentVariable("NEXUS_WINDOWS_HOME");
        if (string.IsNullOrEmpty(custom)) return false;
        try
        {
            var root = Path.GetFullPath(custom);
            var markerPath = Path.Combine(root, ".nexus-cdp-fixture.json");
            if (!Path.GetFileName(root).StartsWith("nexus-claude-cdp-live-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0) return false;
            using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
            return marker.RootElement.GetProperty("owner").GetString() == "nexus-cdp-local-acceptance" &&
                Guid.TryParse(marker.RootElement.GetProperty("run").GetString(), out var ownerRun) && ownerRun == run;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }

    public static bool TryClaudeDevtoolsInput(nint window, string op, Node? target, JsonElement request, Action started, out Outcome outcome)
    {
        outcome = default;
        if (claudeDomScope is not { } scope || scope.Window != (long)window) return false;
        // CDP never replaces native browser chrome/dialog controls. A semantic page target must come from Chrome UIA.
        if (target != null && target.FrameworkId != "Chrome") return false;
        if (op is not ("click" or "focus" or "setValue" or "typeText" or "select" or "key" or "point")) return false;
        scope.Check();
        void Boundary()
        {
            scope.Check();
            if (target != null) Program.RevalidateClaudeDomTarget(window, target, request);
            if (op == "key")
            {
                scope.KeyEntryReady = true;
                try { BeforeKeySequenceInput(); }
                finally { scope.KeyEntryReady = false; }
                scope.Check();
            }
            started();
        }
        try
        {
            outcome = Cdp.ClaudeInput(window, scope.Lease.Port, scope.Lease.Process.Id, op, target, scope.SourceRuntimeId, request,
                scope.Check, Boundary, () => scope.KeyEntryReady = false);
        }
        catch (BackgroundNotApplicableException e)
        { throw new ActionRefusedException("Registered DevTools route could not be proved; no alternate transport was sent: " + e.Message); }
        return true;
    }
}

static partial class Program
{
    // Same target and ancestor contract as Execute.Resolve, reread after CDP page preparation and before input.
    internal static void RevalidateClaudeDomTarget(nint window, Node original, JsonElement request)
    {
        using var tree = ComUia.Shared.ReadIdentity(window, original.RuntimeId, original.NativeWindow);
        var target = tree.Nodes.FirstOrDefault(n => n.RuntimeId.SequenceEqual(original.RuntimeId));
        if (target == null || target.Disabled || target.Password || target.FrameworkId != "Chrome" || !target.Bounds.SequenceEqual(original.Bounds))
            throw new ActionRefusedException("The observed DOM target changed while preparing input; no text/key/click sent");
        if (request.TryGetProperty("selector", out var selector) && !Matches(target, selector))
            throw new ActionRefusedException("DOM target selector changed while preparing input");
        if (request.TryGetProperty("ancestry", out var ancestors) && ancestors.ValueKind == JsonValueKind.Array &&
            !SameAncestry(tree.Nodes, target, ancestors.EnumerateArray().ToList()))
        {
            // Same exact guard as Execute.Resolve: UIA GetParent and the complete ControlView can expose the same
            // runtime element through different parents. Only a fresh complete chain equal to the observation wins.
            var expected = ancestors.EnumerateArray().ToList();
            using var full = ComUia.Shared.ReadTree(window);
            var fresh = full.Nodes.FirstOrDefault(n => n.RuntimeId.SequenceEqual(original.RuntimeId));
            ClaudeAncestryFixtureDiagnostic(tree.Nodes, target, expected, full.Nodes, fresh);
            if (fresh == null || fresh.Disabled || fresh.Password || fresh.FrameworkId != "Chrome" || !fresh.Bounds.SequenceEqual(original.Bounds) ||
                !SameAncestry(full.Nodes, fresh, expected) || request.TryGetProperty("selector", out var fullSelector) &&
                (!Matches(fresh, fullSelector) || fullSelector.TryGetProperty("ancestor", out var required) && !fresh.Ancestors.Any(i => Matches(full.Nodes[i], required))))
                throw new ActionRefusedException("DOM target ancestry changed while preparing input");
        }
    }
    static void ClaudeAncestryFixtureDiagnostic(List<Node> limited, Node target, List<JsonElement> expected, List<Node> fullNodes, Node? fullTarget)
    {
        if (!McpServer.ClaudeFixtureDiagnosticsEnabled()) return;
        string Category(string name) => name.Length == 0 ? "empty" : name.StartsWith("nexus-", StringComparison.Ordinal) ? "proof-marker" :
            name.Contains("Nexus Claude CDP fixture", StringComparison.Ordinal) ? "fixture-title" : "other";
        object Differences(List<Node> nodes, Node actual)
        {
            var changes = new List<object>();
            for (var i = 0; i < Math.Max(actual.Ancestors.Length, expected.Count); i++)
            {
                var observed = i < expected.Count ? expected[i] : (JsonElement?)null;
                var fresh = i < actual.Ancestors.Length ? nodes[actual.Ancestors[i]] : null;
                var name = observed is { } e ? Text(e, "name") : "";
                var runtime = observed is { } r ? RuntimeIdOf(r) : [];
                var role = observed is { } roleElement ? Text(roleElement, "role") : "";
                var automationId = observed is { } aid ? Text(aid, "automationId") : "";
                if (observed != null && fresh != null && role == fresh.Role && name == fresh.Name &&
                    automationId == fresh.AutomationId && runtime.SequenceEqual(fresh.RuntimeId)) continue;
                changes.Add(new {
                    position = i, expectedPresent = observed != null, actualPresent = fresh != null,
                    expectedIndex = observed is { } index && index.TryGetProperty("index", out var n) && n.TryGetInt32(out var value) ? value : (int?)null,
                    actualIndex = fresh?.Index, expectedRole = role, actualRole = fresh?.Role,
                    expectedNameCategory = Category(name), actualNameCategory = fresh != null ? Category(fresh.Name) : null,
                    expectedNameLength = name.Length, actualNameLength = fresh?.Name.Length, nameEqual = fresh?.Name == name,
                    expectedRuntimeLength = runtime.Length, actualRuntimeLength = fresh?.RuntimeId.Length,
                    runtimeEqual = fresh != null && runtime.SequenceEqual(fresh.RuntimeId), automationIdEqual = fresh?.AutomationId == automationId,
                });
            }
            return new { expectedCount = expected.Count, actualCount = actual.Ancestors.Length, differences = changes };
        }
        object full = fullTarget == null ? new { targetFound = false } : (object)new { targetFound = true,
            match = SameAncestry(fullNodes, fullTarget, expected), comparison = Differences(fullNodes, fullTarget) };
        // Metadata only. No name, title, value, profile or complete runtime identity is printed.
        Console.Error.WriteLine(JsonSerializer.Serialize(new { fixture = "claude-cdp-ancestry-diff", limited = Differences(limited, target), full }));
    }
}
