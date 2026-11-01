using System.Text.Json.Nodes;

static partial class McpServer
{
    public static int ShellCompatibilitySelfTest()
    {
        var assertions = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Shell compatibility selftest: " + message); assertions++; }
        void Refused(Action action) { try { action(); } catch (ActionRefusedException) { assertions++; return; } throw new InvalidOperationException("Shell compatibility selftest: expected a refusal"); }
        foreach (var alias in new[] { "quick settings", "réglages rapides", "notifications", "notification center", "centre de notifications", "start", "démarrer" })
        {
            var background = new JsonObject { ["window"] = alias, ["foreground"] = false };
            Program.InputStarted = false;
            Refused(() => RunShellAct(new SessionState("claude"), background, true));
            Check(!Program.InputStarted, "background panel rejected before all native input");
            var foreground = background.DeepClone().AsObject(); foreground["foreground"] = true;
            Check(PlanShellAct(foreground) != null, "shell alias recognized for the three profiles");
        }
        Refused(() => PlanShellAct(new JsonObject { ["window"] = "start" }));
        Refused(() => PlanShellAct(new JsonObject { ["window"] = "start", ["foreground"] = "true" }));
        Check(PlanShellAct(new JsonObject { ["window"] = "ordinary application" }) == null, "ordinary windows retain normal resolution");
        foreach (var accepted in new[] { "win", "win+a", "win+n" }) Check(ShellShortcutKeys(accepted).Length > 0, "allowlisted shortcut");
        foreach (var rejected in new[] { "win+r", "win+l", "alt+f4", "win+e", "", "WIN+A" }) Refused(() => ShellShortcutKeys(rejected));
        var coordinates = new JsonArray(new JsonObject { ["kind"] = "click", ["x"] = 10, ["y"] = 20 });
        foreach (var agent in new[] { "claude", "codex", "antigravity" }) { NativeInteraction.ValidateSteps(agent, coordinates, true); assertions++; }
        Refused(() => NativeInteraction.ValidateSteps("codex", coordinates, false));
        Refused(() => NativeInteraction.ValidateSteps("claude", coordinates, false));
        Refused(() => NativeInteraction.ValidateSteps("codex", new JsonArray(new JsonObject { ["kind"] = "click" }), false));
        NativeInteraction.ValidateSteps("codex", new JsonArray(new JsonObject { ["kind"] = "click", ["selector"] = new JsonObject { ["name"] = "Fixture" }, ["expect"] = new JsonObject { ["exists"] = true } }), false); assertions++;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = System.IO.Path.Combine(root, StartPlan.RelativePath);
        var app = StartPlan.Package + "!App";
        Check(OfficialShellIdentity(StartPlan, path, app, root), "official protected path and package accepted");
        Check(OfficialShellIdentity(StartPlan, path, "process:" + path, root), "official protected executable identity accepted");
        Check(!OfficialShellIdentity(StartPlan, System.IO.Path.Combine(root, "Temp", "StartMenuExperienceHost.exe"), app, root), "shell name copied outside protected path refused");
        Check(!OfficialShellIdentity(StartPlan, path, "Unrelated.Package!App", root), "foreign package identity refused");
        Check(!OfficialShellIdentity(StartPlan, path, StartPlan.Package + ".Imitation!App", root), "package prefix imitation refused");
        var notificationsPath = System.IO.Path.Combine(root, NotificationsPlan.RelativePath);
        Check(OfficialShellIdentity(NotificationsPlan, notificationsPath, "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy!App", root), "notifications package family differs from its system directory name");
        Check(!OfficialShellIdentity(NotificationsPlan, notificationsPath, "ShellExperienceHost_cw5n1h2txyewy!App", root), "directory name is not the official notifications package family");

        var target = new NativeInteraction.WindowIdentity(100, 10, "birth-one", "Main", 0);
        var search = new NativeInteraction.WindowIdentity(200, 20, "birth-two", "Search", 0);
        var observed = new Dictionary<nint, NativeInteraction.WindowIdentity> { [100] = target, [200] = search };
        bool Allowed(bool legacy, NativeInteraction.WindowIdentity actualTarget, NativeInteraction.WindowIdentity actualFront) => NativeInteraction.ShellIdentityPermitted(legacy, target, actualTarget, actualFront, observed);
        Check(Allowed(true, target, search), "observed Search sibling admitted under the historical lease");
        Check(!Allowed(false, target, search), "strict scopes stay closed");
        Check(!Allowed(true, target, search with { Window = 201 }), "unobserved sibling refused");
        Check(!Allowed(true, target, search with { Pid = 21 }), "recycled foreground HWND refused");
        Check(!Allowed(true, target, search with { Started = "new-birth" }), "reused PID refused");
        Check(!Allowed(true, target with { Started = "new-birth" }, search), "replaced target process refused");
        Check(!Allowed(true, target, search with { Class = "Imitation" }), "changed foreground identity refused");
        Check(!Allowed(true, target, search with { Owner = 99 }), "changed owner refused");
        // Windows regression trap: transferring focus first cloaks/suspends the source queue, so its later Escape
        // cannot be consumed. The real cleanup coordinator must close and observe the panel before GiveBack.
        var order = new List<string>(); var escaped = false; var closed = false; var suspended = false;
        var released = CloseShellPanelInOrder(() => true, () => order.Add("guard"),
            () => { order.Add("escape"); if (!suspended) escaped = true; },
            () => { order.Add("wait-closed"); closed = escaped; },
            () => { order.Add("return"); suspended = !closed; return closed ? "restored" : "refused"; });
        Check(released == "restored" && closed && !suspended, "Escape is consumed before a foreground transfer can suspend the panel");
        Check(order.SequenceEqual(new[] { "guard", "escape", "wait-closed", "return" }), "guard, one Escape, closure readback, then foreground return");
        foreach (var reason in new[] { "user-resumed", "unowned-foreground", "already-cloaked" })
        {
            var entries = 0;
            Check(CloseShellPanelInOrder(() => false, () => entries++, () => entries++, () => entries++, () => "not-taken") == "not-taken" && entries == 0,
                reason + " never receives Escape or an implicit retry");
        }
        var escapeCount = 0;
        Check(CloseShellPanelInOrder(() => true, () => { }, () => escapeCount++, () => { }, () => "refused") == "refused" && escapeCount == 1,
            "unconfirmed closure never replays Escape");
        var sentAfterStop = false;
        try { CloseShellPanelInOrder(() => true, () => throw new EmergencyStopException("fixture"), () => sentAfterStop = true, () => { }, () => "restored"); }
        catch (EmergencyStopException) { assertions++; }
        Check(!sentAfterStop, "STOP refuses the closure before its only input");
        Console.WriteLine(new JsonObject { ["ok"] = true, ["assertions"] = assertions, ["gui"] = false, ["coverage"] = "pure shell planning, pre-input refusals and frozen-identity authorization; no live panel validation" }.ToJsonString());
        return 0;
    }
}
