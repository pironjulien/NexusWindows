using System.Diagnostics;
using System.Text.Json.Nodes;

static partial class McpServer
{
    public sealed class NativeInteraction : IDisposable
    {
        [ThreadStatic] static NativeInteraction? current;
        readonly SessionState state;
        readonly ForegroundToken token;
        readonly IDisposable presence;
        readonly nint noted;
        readonly WindowIdentity? notedIdentity;
        WindowIdentity? targetIdentity;
        readonly bool legacy;
        readonly Dictionary<nint, WindowIdentity> shellWindows = new();
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool disposed;
        internal readonly record struct WindowIdentity(nint Window, uint Pid, string Started, string Class, nint Owner);
        NativeInteraction(SessionState state, ForegroundToken token, IDisposable presence, bool legacy)
        {
            this.state = state; this.token = token; this.presence = presence; this.legacy = legacy;
            noted = Win32.GetForegroundWindow();
            try { if (noted != 0) notedIdentity = ReadIdentity(noted); } catch (ActionRefusedException) { }
        }
        internal static void ValidateSteps(string agent, JsonArray steps, bool legacy)
        {
            if (legacy)
            {
                if (agent != "claude") throw new ActionRefusedException("The historical foreground scope belongs to the Claude profile only");
                return;
            }
            foreach (var step in steps.OfType<JsonObject>())
            {
                if (step["expect"] is not JsonObject || new[] { "point", "from", "to", "x", "y", "box_2d" }.Any(k => step[k] != null))
                    throw new ActionRefusedException("interaction:true needs semantic steps with an explicit postcondition for every step");
            }
        }
        public static NativeInteraction Begin(SessionState state, JsonArray steps)
        {
            ValidateSteps(state.Agent, steps, false);
            return Open(state, legacy: false, activate: true, shellOpening: false);
        }
        public static NativeInteraction BeginLegacy(SessionState state) => Open(state, legacy: true, activate: true, shellOpening: false);
        internal static NativeInteraction BeginLegacyShell(SessionState state) => Open(state, legacy: true, activate: false, shellOpening: true);
        static NativeInteraction Open(SessionState state, bool legacy, bool activate, bool shellOpening)
        {
            if (legacy) ValidateSteps(state.Agent, new(), true);
            if (state.Interaction != null) throw new ActionRefusedException("An interaction scope is already active");
            var bound = shellOpening ? null : state.BoundWindow;
            if (!shellOpening && bound == null) throw new ActionRefusedException("Bind a window before starting an interaction");
            var identity = bound is { } named ? ReadIdentity((nint)named.Id) : (WindowIdentity?)null;
            if (bound is { } b && identity!.Value.Pid != b.Pid) throw new ActionRefusedException("Window process changed before the interaction; nothing sent");
            if (legacy && identity is { } live && state.CurrentObservation is { } observed && observed.TryGetProperty("processStartedUtc", out var born)
                && born.ValueKind == System.Text.Json.JsonValueKind.String && born.GetString() != live.Started)
                throw new ActionRefusedException("Window process birth differs from the last observation; nothing sent");
            NativeMcpGuard.WaitForPause();
            var token = ForegroundToken.Acquire(legacy ? "Claude historical foreground batch" : "Native MCP interaction");
            IDisposable? presence = null; NativeInteraction? lease = null;
            try
            {
                NativeMcpGuard.RequirePause(); EmergencyStop.ThrowIfStopped();
                presence = NativeMcpGuard.PhysicalScope();
                lease = new NativeInteraction(state, token, presence, legacy) { targetIdentity = identity };
                state.Interaction = lease;
                current = lease;
                lease.Check();
                if (activate) Physical.Activate(identity!.Value.Window, () => { lease.Check(); Program.InputStarted = true; });
                return lease;
            }
            catch { if (lease != null) lease.Dispose(); else { presence?.Dispose(); token.Dispose(); } throw; }
        }
        public void Check()
        {
            EmergencyStop.ThrowIfStopped(); NativeMcpGuard.CheckInterruption();
            if (clock.ElapsedMilliseconds >= 30000) throw new ActionRefusedException("Interaction budget expired; look again before any input");
            if (disposed || !ReferenceEquals(state.Interaction, this)) throw new ActionRefusedException("Interaction lease is no longer active");
            if (targetIdentity is { } target)
            {
                if (state.BoundWindow is not { } bound || bound.Id != (long)target.Window || bound.Pid != target.Pid || !StillMatches(target))
                    throw new ActionRefusedException("Interaction window/process identity changed; nothing else sent");
            }
        }
        internal static WindowIdentity ReadIdentity(nint window)
        {
            if (window == 0 || !Win32.IsWindow(window)) throw new ActionRefusedException("Interaction window no longer exists; nothing sent");
            var pid = Win32.ProcessOf(window);
            var started = Program.ProcessStartedUtc(pid);
            if (pid == 0 || string.IsNullOrEmpty(started)) throw new ActionRefusedException("Interaction process birth cannot be verified; nothing sent");
            var result = new WindowIdentity(window, pid, started, Win32.ClassName(window), Win32.GetWindow(window, Win32.GW_OWNER));
            if (!Win32.IsWindow(window) || Win32.ProcessOf(window) != pid) throw new ActionRefusedException("Interaction identity changed while reading; nothing sent");
            return result;
        }
        internal static bool SameIdentity(WindowIdentity expected, WindowIdentity actual) => expected == actual;
        static bool StillMatches(WindowIdentity identity)
        {
            try { return SameIdentity(identity, ReadIdentity(identity.Window)); }
            catch (ActionRefusedException) { return false; }
        }
        internal void BindLegacyShell((long Id, string App, string Title, long Pid) target, IEnumerable<nint> owned)
        {
            if (!legacy || targetIdentity != null) throw new ActionRefusedException("A shell target can only be bound once in its historical scope");
            Check();
            var identity = ReadIdentity((nint)target.Id);
            if (identity.Pid != target.Pid) throw new ActionRefusedException("Shell window identity changed before binding");
            state.BoundWindow = target; targetIdentity = identity;
            RememberShellWindows(owned.Append(identity.Window));
            Check();
        }
        internal void RememberShellWindows(IEnumerable<nint> owned)
        {
            foreach (var hwnd in owned.Where(h => h != 0)) shellWindows[hwnd] = ReadIdentity(hwnd);
        }
        // Only HWNDs actually observed at opening are accepted. Process names never grant this permission.
        internal bool OwnsShellWindow(nint hwnd) => shellWindows.TryGetValue(hwnd, out var identity) && StillMatches(identity);
        internal static void CheckInputLease() => current?.Check();
        internal static bool HasShellLease(nint target) => current is { legacy: true, disposed: false } scope
            && scope.state.Agent == "claude" && scope.targetIdentity?.Window == target && scope.shellWindows.Count > 0;
        internal static bool AllowsForeground(nint target, nint candidate)
        {
            if (current is not { legacy: true, disposed: false } scope || scope.state.Agent != "claude" || scope.targetIdentity?.Window != target) return false;
            scope.Check();
            var root = candidate == 0 ? 0 : Win32.GetAncestor(candidate, Win32.GA_ROOT); if (root != 0) candidate = root;
            try { return ShellIdentityPermitted(scope.state.Agent, scope.legacy, scope.targetIdentity.Value, ReadIdentity(target), ReadIdentity(candidate), scope.shellWindows); }
            catch (ActionRefusedException) { return false; }
        }
        internal static bool ShellIdentityPermitted(string agent, bool legacy, WindowIdentity bound, WindowIdentity target, WindowIdentity candidate, IReadOnlyDictionary<nint, WindowIdentity> observed) =>
            agent == "claude" && legacy && SameIdentity(bound, target)
            && observed.TryGetValue(target.Window, out var shownTarget) && SameIdentity(shownTarget, target)
            && observed.TryGetValue(candidate.Window, out var shownCandidate) && SameIdentity(shownCandidate, candidate);
        internal bool UserResumed => NativeMcpGuard.UserResumed;
        internal string ReturnForeground()
        {
            if (NativeMcpGuard.UserResumed) return "kept-by-julien";
            if (notedIdentity is not { } previous || !StillMatches(previous)) return "none";
            var front = Win32.GetForegroundWindow();
            var root = front == 0 ? 0 : Win32.GetAncestor(front, Win32.GA_ROOT); if (root != 0) front = root;
            if (shellWindows.Count > 0)
            {
                if (!OwnsShellWindow(front)) return front == noted ? "not-taken" : "kept-by-julien";
                return ForegroundReturn.GiveBack(noted, front, sameProcess: true);
            }
            return targetIdentity is { } target && StillMatches(target) ? ForegroundReturn.GiveBack(noted, target.Window) : "none";
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { ReturnForeground(); }
            finally { state.Interaction = null; if (ReferenceEquals(current, this)) current = null; presence.Dispose(); token.Dispose(); }
        }
    }
}
