// Offline self-checks of commun/engine/ComWorker.cs, kept out of the shipped engine: they reach it through
// its internal members (InternalsVisibleTo NexusWindows.Tests) and run in-process from the xUnit tests.
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static ComWorker;

static partial class ComWorkerSelfTests
{
    // Offline check (tests): stdin = ["closed" | "open" | "throws"]; output one result per case.
    public static int CollapseSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<string>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            var kind = c.GetString();
            try
            {
                CollapseVerified(() => { if (kind == "throws") throw new COMException("element gone"); }, () => kind == "open" ? 1 : 0);
                results.Add("closed");
            }
            catch (InvalidOperationException error) { results.Add(error.Message); }
        }
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }

    internal static object ClickSelfTest(JsonElement test)
    {
        Acted = false; var calls = new List<string>(); var sent = 0;
        var order = Program.ClickOrder(test.GetProperty("role").GetString() ?? "", true, true, test.GetProperty("cef").GetBoolean());
        try
        {
            var outcome = ChooseClick(order, name =>
            {
                calls.Add(name);
                var state = test.GetProperty("patterns").TryGetProperty(name, out var p) ? p.GetString() : null;
                if (state is null or "missing") return null;
                if (state == "read-error") throw Before("Pattern unavailable before input");
                Acted = true; sent++;
                if (state == "lost") throw new COMException("Provider response lost after input");
                return new Outcome(Uia, name, state);
            });
            return new ComWorkerClickSelfTestJson(order: order, calls: calls, sent: sent, effect: outcome.Effect, beforeInput: !Acted);
        }
        catch (Exception e) { var failure = Failure(e); return new ComWorkerClickSelfTestJson2(order: order, calls: calls, sent: sent, error: failure.error, beforeInput: failure.beforeInput); }
    }

    public static int ExpandSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var results = new List<object>();
        foreach (var c in input.RootElement.EnumerateArray())
        {
            if (c.TryGetProperty("phase", out var phase))
            {
                Acted = phase.GetString() == "after";
                Exception error = c.GetProperty("error").GetString() switch
                {
                    "stop" => new EmergencyStopException("stop"),
                    "refused" => new ActionRefusedException("refused"),
                    "missing" => new BackgroundNotApplicableException("missing"),
                    _ => new COMException("provider read failed"),
                };
                // A wrong mark, or one kept from a sub-step, cannot contradict the worker's real phase.
                error.Data["beforeInput"] = c.TryGetProperty("hint", out var hint) && hint.GetBoolean();
                var reply = Failure(error);
                Exception mapped;
                if (phase.GetString() == "startup")
                {
                    try { ComBridge.BeforeRequest<object>(() => throw error); throw new InvalidOperationException("Preparation unexpectedly succeeded"); }
                    catch (Exception preparation) { mapped = preparation; }
                }
                else mapped = ComBridge.ReplyError(reply.error, reply.message, reply.beforeInput);
                results.Add(new ComWorkerExpandSelfTestJson(workerBeforeInput: reply.beforeInput, error: mapped.GetType().Name,
                    beforeInput: mapped.Data["beforeInput"] is true));
                continue;
            }
            if (c.TryGetProperty("hresult", out var hr)) { results.Add(IsRefusal(unchecked((int)hr.GetUInt32())) ? "refusal" : "unknown"); continue; }
            if (c.TryGetProperty("route", out _))
            {
                Acted = false;
                var expanded = 0;
                var invoked = 0;
                var action = c.TryGetProperty("action", out var a) ? a.GetString() ?? "Expand" : "Expand";
                var failure = c.TryGetProperty("failure", out var f) ? f.GetString() : null;
                try
                {
                    var outcome = ChooseExpandCollapse(action, !c.TryGetProperty("allowInvoke", out var allow) || allow.GetBoolean(),
                        () => !c.GetProperty("pattern").GetBoolean() ? null : ExpandCollapseOnce(action, () =>
                        {
                            if (failure == "before") throw Before("No input available");
                            if (failure == "stopped")
                            {
                                var stopped = new EmergencyStopException("Emergency stop before the provider call");
                                stopped.Data["beforeInput"] = true;
                                throw stopped;
                            }
                            Acted = true; expanded++;
                            if (failure == "refusal") throw new COMException("provider refused after call", unchecked((int)0x80131509));
                            if (failure == "disabled") throw new COMException("provider disabled after call", unchecked((int)0x80040200));
                            if (failure == "unknown") throw new COMException("provider failed after call", unchecked((int)0x80004005));
                        }, () =>
                        {
                            if (failure == "read") throw new COMException("read failed after call");
                            if (failure == "read-stopped") throw new EmergencyStopException("Emergency stop after the provider call");
                            return c.TryGetProperty("reached", out var r) && r.GetBoolean();
                        }),
                        () =>
                        {
                            if (!c.GetProperty("invoke").GetBoolean()) return null;
                            Acted = true; invoked++;
                            return new Outcome(Uia, "Invoke", "sent");
                        });
                    results.Add(new ComWorkerExpandSelfTestJson2(effect: outcome.Effect, expandCalls: expanded, invokeCalls: invoked));
                }
                catch (Exception error)
                {
                    results.Add(new ComWorkerExpandSelfTestJson3(error: error.GetType().Name, beforeInput: error.Data["beforeInput"] is true,
                        expandCalls: expanded, invokeCalls: invoked));
                }
                continue;
            }
            results.Add(EffectAfterCall(c.GetProperty("reached").GetBoolean()));
        }
        Console.WriteLine(EngineTestJson.Serialize(results));
        return 0;
    }
}
