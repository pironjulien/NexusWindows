// Offline self-checks of commun/engine/ComChromiumWarmup.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.Runtime.InteropServices;
using static ComUia;
using static ChromiumContentWarmup;

static partial class ComUiaSelfTests
{
    internal static object ChromiumWarmupSelfTest() => ChromiumContentWarmupSelfTests.SelfTest();
}

static partial class ChromiumContentWarmupSelfTests
{
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
        // A covered window that loaded its page behind (05/11): one reveal after the first fresh read without a page, then the
        // same budget again; never a second reveal in the same read, never one for a ready page.
        policy = new ChromiumContentWarmup(); reads = 0; waits.Clear(); now = 0; var reveals = 0;
        using (var revealed = policy.Read(Tree("hidden page"), true, identity, () => actual, () => { reads++; return reads >= 3 ? Tree("ready", true, true) : Tree("toolbar"); },
            () => false, () => now, advance, () => { reveals++; now += 600; return true; }))
            Require(reveals == 1 && reads == 3 && HasWebContent(revealed), "A reveal after the first empty read lets the page publish");
        policy = new ChromiumContentWarmup(); reads = 0; now = 0; reveals = 0;
        using (var never = policy.Read(Tree("never"), true, identity, () => actual, () => { reads++; return Tree("toolbar"); },
            () => false, () => now, advance, () => { reveals++; return true; }))
            Require(reveals == 1 && reads == 7 && now == BudgetMs + PollMs, "One reveal per read, then the budget once more, bounded");
        policy = new ChromiumContentWarmup(); reads = 0; now = 0; reveals = 0;
        using (var refused = policy.Read(Tree("refused"), true, identity, () => actual, () => { reads++; return Tree("toolbar"); },
            () => false, () => now, advance, () => { reveals++; return false; }))
            Require(reveals == 1 && reads == 6 && now == BudgetMs, "A refused reveal (window partly visible, minimized, in front) keeps the ordinary budget");
        reveals = 0;
        using (var ready = policy.Read(Tree("ready page", true, true), true, identity, () => actual, read, () => false, () => now, advance, () => { reveals++; return true; }))
            Require(reveals == 0, "A published page is never revealed");
        // Within the miss cooldown: a browser window revealed more than RevealEveryMs ago is revealed again (a page loaded after
        // it); a window whose reveal was refused (an app, a dialog) keeps the whole cooldown, without any wait.
        policy = new ChromiumContentWarmup(); now = 0; reveals = 0; reads = 0;
        using (policy.Read(Tree("first"), true, identity, () => actual, () => { reads++; return Tree("toolbar"); }, () => false, () => now, advance, () => { reveals++; return true; })) { }
        now += RevealEveryMs + 1; var before = reads;
        using (var again = policy.Read(Tree("loaded later"), true, identity, () => actual, () => { reads++; return reads - before >= 2 ? Tree("ready", true, true) : Tree("toolbar"); }, () => false, () => now, advance, () => { reveals++; return true; }))
            Require(reveals == 2 && HasWebContent(again), "A page loaded after the previous reveal is revealed again within the miss cooldown");
        policy = new ChromiumContentWarmup(); now = 0; reveals = 0; reads = 0;
        using (policy.Read(Tree("app"), true, identity, () => actual, () => { reads++; return Tree("toolbar"); }, () => false, () => now, advance, () => { reveals++; return false; })) { }
        now += RevealEveryMs + 1; before = reads;
        using (policy.Read(Tree("app again"), true, identity, () => actual, () => { reads++; return Tree("toolbar"); }, () => false, () => now, advance, () => { reveals++; return false; }))
            Require(reveals == 1 && reads == before, "A window that cannot be revealed keeps the cooldown: no wait, no new reveal");
        checks += ChromiumRevealSelfTests.SelfTest();
        return new ChromiumContentWarmupSelfTestJson(ok: true, checks: checks, gui: false, waitBudgetMs: BudgetMs, pollMs: PollMs, failureCooldownMs: MissingCooldownMs);
    }
}
