using System.Diagnostics;
using System.Runtime.InteropServices;

unsafe sealed partial class ComUia
{
    readonly ChromiumContentWarmup chromiumWarmup = new();
    readonly Stopwatch chromiumWarmupClock = Stopwatch.StartNew();

    static ChromiumContentWarmup.Identity? ChromiumIdentity(nint hwnd)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd)) return null;
        var pid = Win32.ProcessOf(hwnd);
        var birth = Program.ProcessStartedUtc(pid);
        if (pid == 0 || string.IsNullOrEmpty(birth)) return null;
        var identity = new ChromiumContentWarmup.Identity((long)hwnd, pid, birth, Win32.ClassName(hwnd));
        // Process metadata and native window queries are independent reads. Recheck the endpoints so a
        // closed/replaced HWND cannot create a mixed identity that later retries would treat as current.
        return Win32.IsWindow(hwnd) && Win32.ProcessOf(hwnd) == pid && Program.ProcessStartedUtc(pid) == birth
            && Win32.ClassName(hwnd) == identity.WindowClass ? identity : null;
    }

    internal static object ChromiumWarmupSelfTest() => ChromiumContentWarmup.SelfTest();
}

// Chromium initializes accessibility on demand. A first real tree may contain only its native toolbar.
// Preserve that observation, but allow the same short read-only warmup as the former Python snapshot.
// This policy owns every tree handed to it: discarded trees are disposed, returned trees belong to caller.
// No cached tree, UIA pattern, simulated activation, foreground call or input is used by the retries.
sealed class ChromiumContentWarmup
{
    internal const int BudgetMs = 1500, PollMs = 250, MissingCooldownMs = 60000;
    internal readonly record struct Identity(long Hwnd, uint Pid, string Birth, string WindowClass);
    readonly record struct Miss(Identity Identity, double At);
    readonly Dictionary<long, Miss> missing = new();

    internal static bool HasWebContent(UiTree tree)
    {
        var documents = tree.Nodes.Where(node => node.Role == "document").Select(node => node.Index).ToHashSet();
        return documents.Count != 0 && tree.Nodes.Any(node => node.Ancestors.Length != 0 && documents.Contains(node.Ancestors[^1]));
    }

    internal UiTree Read(UiTree initial, bool chromium, Identity? expected, Func<Identity?> currentIdentity,
        Func<UiTree> readFresh, Func<bool> stopped, Func<double> nowMs, Action<int> wait)
    {
        var tree = initial;
        try
        {
            // Missing birth permits the ordinary initial observation, never an extra delayed observation.
            if (!chromium || expected is not { Hwnd: not 0, Pid: not 0, Birth.Length: > 0 } identity) return tree;
            void Revalidate()
            {
                if (currentIdentity() != identity)
                    throw new ActionRefusedException("Chromium window identity changed while waiting for accessibility; inspect again");
            }
            Revalidate();
            var now = nowMs();
            foreach (var stale in missing.Where(pair => now - pair.Value.At >= MissingCooldownMs || now < pair.Value.At).Select(pair => pair.Key).ToArray())
                missing.Remove(stale);
            if (HasWebContent(tree)) { missing.Remove(identity.Hwnd); return tree; }
            if (stopped()) return tree; // STOP keeps the native toolbar readable and ends only the optional warmup.
            if (missing.TryGetValue(identity.Hwnd, out var miss))
            {
                if (miss.Identity == identity && now - miss.At < MissingCooldownMs) return tree;
                missing.Remove(identity.Hwnd); // HWND reuse, PID replacement or process rebirth resets the miss.
            }
            var deadline = now + BudgetMs;
            while (true)
            {
                if (stopped()) return tree;
                Revalidate();
                var remaining = deadline - nowMs();
                if (remaining <= 0) break;
                wait(Math.Min(PollMs, (int)Math.Ceiling(remaining)));
                if (stopped()) return tree;
                Revalidate();
                // Never begin a provider call after the deadline. One synchronous UIA call already in
                // progress still has its existing connection/transaction timeout; it cannot be preempted.
                if (nowMs() > deadline) break;
                UiTree? next = null;
                try { next = readFresh(); Revalidate(); }
                catch { next?.Dispose(); throw; }
                tree.Dispose(); tree = next;
                if (HasWebContent(tree)) { missing.Remove(identity.Hwnd); return tree; }
            }
            missing[identity.Hwnd] = new(identity, nowMs());
            return tree; // Last independently read toolbar remains usable; no fabricated document or status.
        }
        catch { tree.Dispose(); throw; }
    }

    // Deterministic policy/lifetime tests only, without COM providers, sleeping, native windows or input.
    internal static object SelfTest()
    {
        var checks = 0;
        void Require(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        static UiTree Tree(string name, bool document = false, bool child = false)
        {
            var tree = new UiTree();
            Node N(string role, int[] ancestors) => new() { Role = role, Name = name, AutomationId = "", Description = "",
                RuntimeId = [], Ancestors = ancestors, Bounds = [], Depth = ancestors.Length };
            tree.Add(N("window", [])); tree.Add(N("button", [0])); // Native toolbar is independently present.
            if (document) { var doc = tree.Add(N("document", [0])); if (child) tree.Add(N("text", [0, doc.Index])); }
            return tree;
        }
        var identity = new Identity(42, 8, "birth-a", "Chrome_WidgetWin_1");
        var policy = new ChromiumContentWarmup();
        double now = 0; var reads = 0; var waits = new List<int>(); var stopped = false;
        Identity? actual = identity;
        Action<int> advance = ms => { waits.Add(ms); now += ms; };
        Func<UiTree> read = () => { reads++; return Tree("fresh"); };
        UiTree Run(UiTree tree, bool chromium = true, Identity? bound = null, Func<UiTree>? fresh = null, Action<int>? delay = null) =>
            policy.Read(tree, chromium, bound ?? identity, () => actual, fresh ?? read, () => stopped, () => now, delay ?? advance);

        using (var tree = Run(Tree("native"), chromium: false)) Require(reads == 0 && waits.Count == 0, "Non-Chromium must never warm up");
        using (var tree = policy.Read(Tree("unknown birth"), true, null, () => throw new Exception("identity must not be read"),
            () => throw new Exception("provider must not be read"), () => false, () => 0, _ => throw new Exception("must not wait")))
            Require(tree.Nodes.Count == 2, "Unknown identity keeps the initial native tree");
        using (var tree = Run(Tree("ready", true, true))) Require(reads == 0 && waits.Count == 0, "Document with a child is ready immediately");
        var initial = Tree("initial", true); UiTree? intermediate = null;
        using (var ready = Run(initial, fresh: () => { reads++; return reads == 1 ? intermediate = Tree("empty document", true) : Tree("ready", true, true); }))
        {
            Require(reads == 2 && waits.SequenceEqual(new[] { 250, 250 }), "Empty Document must still wait, then stop immediately on actual content");
            Require(initial.Nodes.Count == 0 && intermediate!.Nodes.Count == 0, "Discarded initial and intermediate trees must be disposed");
            Require(HasWebContent(ready), "Return the independent fresh content tree");
        }
        reads = 0; waits.Clear(); now = 0;
        using (var timeout = Run(Tree("initial toolbar")))
        {
            Require(reads == 6 && waits.Count == 6 && now == BudgetMs, "Missing web content is bounded to six 250 ms polls");
            Require(timeout.Nodes.Count == 2 && timeout.Nodes[0].Name == "fresh", "Timeout must retain the newest real native toolbar");
        }
        reads = 0; waits.Clear();
        using (var cooldown = Run(Tree("new initial toolbar")))
            Require(reads == 0 && waits.Count == 0 && cooldown.Nodes[0].Name == "new initial toolbar", "Cooldown suppresses delay, never caches an old observation");
        now += MissingCooldownMs;
        using (var expired = Run(Tree("after cooldown"), fresh: () => { reads++; return Tree("ready", true, true); }))
            Require(reads == 1 && HasWebContent(expired), "Elapsed cooldown permits warmup again");
        reads = 0; waits.Clear();
        using (var timeout = Run(Tree("miss again"))) { }
        using (var published = Run(Tree("document appeared", true, true))) Require(HasWebContent(published), "An initial success clears a previous miss during cooldown");
        reads = 0;
        using (var afterSuccess = Run(Tree("new navigation"), fresh: () => { reads++; return Tree("ready", true, true); }))
            Require(reads == 1, "Success must reset the negative cache");

        foreach (var replacement in new[] { identity with { Pid = 9 }, identity with { Birth = "birth-b" }, identity with { Hwnd = 43 }, identity with { WindowClass = "Chrome_WidgetWin_2" } })
        {
            actual = identity; using (var miss = Run(Tree("old identity miss"))) { }
            actual = replacement; reads = 0;
            using (var freshIdentity = Run(Tree("new identity"), bound: replacement, fresh: () => { reads++; return Tree("ready", true, true); }))
                Require(reads == 1, "Negative cache must not survive window/process identity replacement");
        }
        actual = identity; policy = new ChromiumContentWarmup(); reads = 0; waits.Clear();
        initial = Tree("identity changes during wait");
        try { Run(initial, delay: ms => { now += ms; actual = identity with { Birth = "replaced" }; }); throw new Exception("identity change accepted"); }
        catch (ActionRefusedException) { Require(reads == 0 && initial.Nodes.Count == 0, "Identity change before a fresh read must refuse and release its initial tree"); }
        actual = identity; UiTree? replaced = null; initial = Tree("identity changes in provider");
        try { Run(initial, fresh: () => { reads++; actual = identity with { Pid = 99 }; return replaced = Tree("wrong process"); }); throw new Exception("provider identity change accepted"); }
        catch (ActionRefusedException) { Require(initial.Nodes.Count == 0 && replaced!.Nodes.Count == 0, "Identity change during fresh read must release both trees"); }
        actual = identity; stopped = true; reads = 0; waits.Clear();
        using (var stoppedTree = Run(Tree("stopped toolbar"))) Require(reads == 0 && waits.Count == 0, "STOP retains toolbar without polling");
        stopped = false;
        using (var afterStop = Run(Tree("after STOP"), fresh: () => { reads++; return Tree("ready", true, true); })) Require(reads == 1, "STOP must not create a 60-second failure cache");
        reads = 0; waits.Clear();
        using (var stopDuringWait = Run(Tree("toolbar"), delay: ms => { now += ms; stopped = true; }))
            Require(reads == 0 && stopDuringWait.Nodes.Count == 2, "STOP during warmup ends it before the next provider call");
        stopped = false; policy = new ChromiumContentWarmup(); reads = 0;
        using (var overshot = Run(Tree("overshot"), delay: _ => now += BudgetMs + 1)) Require(reads == 0, "Overshot deadline must not start a late provider read");
        policy = new ChromiumContentWarmup(); reads = 0;
        using (var slowProvider = Run(Tree("slow provider"), fresh: () => { reads++; now += BudgetMs; return Tree("slow fresh toolbar"); }))
            Require(reads == 1 && slowProvider.Nodes[0].Name == "slow fresh toolbar", "Provider latency must not extend the budget with another read");
        policy = new ChromiumContentWarmup(); initial = Tree("provider failure");
        try { Run(initial, fresh: () => throw new COMException("provider failed")); throw new Exception("provider failure accepted"); }
        catch (COMException) { Require(initial.Nodes.Count == 0, "Provider errors dispose the owned tree and remain real errors"); }
        return new { ok = true, checks, gui = false, waitBudgetMs = BudgetMs, pollMs = PollMs, failureCooldownMs = MissingCooldownMs };
    }
}
