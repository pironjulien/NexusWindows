using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

[Collection("Engine")]
public class TextGestureGuardTests
{
    const string Context = "Chrome Web Store - Listing";
    static JsonObject Field(string value = "Existing description") => new()
    {
        ["index"] = 0, ["role"] = "edit", ["name"] = "Description*", ["automationId"] = "description",
        ["runtimeId"] = new JsonArray(1, 2), ["ancestors"] = new JsonArray(), ["focused"] = true, ["value"] = value,
    };
    static JsonObject Snapshot(bool publish = true) => new()
    {
        ["processStartedUtc"] = "fixture-process", ["revision"] = "fixture-revision",
        ["elements"] = publish ? new JsonArray(Field(), new JsonObject
        {
            ["index"] = 1, ["role"] = "button", ["name"] = "Envoyer pour examen", ["runtimeId"] = new JsonArray(3),
        }) : new JsonArray(Field()),
    };
    static McpServer.SessionState Session(string agent = "codex") => new(agent) { BoundWindow = (1, "chrome.exe", Context, 2) };
    static void Invoke(MethodInfo method, object? instance, params object?[] arguments)
    {
        try { method.Invoke(instance, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    static void Check(McpServer.SessionState state, JsonObject step, JsonObject snapshot, JsonObject? target = null)
    {
        var method = typeof(McpServer).GetMethod("CheckGesture", BindingFlags.NonPublic | BindingFlags.Static)!;
        Invoke(method, null, state, step, snapshot, target, null, Context, false);
    }
    static string Token(Exception refusal)
    {
        const string marker = "accord=\"";
        return refusal.Message[(refusal.Message.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('"')[0];
    }
    static Exception Refusal(Action action) => Assert.ThrowsAny<InvalidOperationException>(action);

    [Theory]
    [InlineData("codex")]
    [InlineData("antigravity")]
    public void Replacing_a_multiline_description_does_not_click_the_pages_publish_button(string agent)
    {
        var snapshot = Snapshot();
        var step = new JsonObject { ["kind"] = "setValue", ["value"] = "First paragraph\n\nSecond paragraph" };
        Check(Session(agent), step, snapshot, snapshot["elements"]![0]!.AsObject());
    }

    [Theory]
    [InlineData("click", "")]
    [InlineData("key", "enter")]
    [InlineData("key", "ctrl+enter")]
    [InlineData("key", "space")]
    [InlineData("typeText", "")]
    public void Real_submit_gestures_still_require_the_exact_single_use_accord(string kind, string key)
    {
        var state = Session(); var snapshot = Snapshot();
        var step = new JsonObject { ["kind"] = kind, ["key"] = key, ["text"] = "A\nB" };
        var trigger = snapshot["elements"]![1]!.AsObject();
        if (key == "space") trigger["focused"] = true;
        var target = kind == "click" ? trigger : snapshot["elements"]![0]!.AsObject();
        var refusal = Refusal(() => Check(state, step, snapshot, target));
        Assert.Contains("Envoyer pour examen", refusal.Message);
        step["confirm"] = true; step["accord"] = Token(refusal);
        Check(state, step, snapshot, target);
        Assert.Contains("already used", Refusal(() => Check(state, step, snapshot, target)).Message);
    }

    [Fact]
    public void A_submit_accord_does_not_authorize_changed_field_content()
    {
        var state = Session(); var snapshot = Snapshot();
        var step = new JsonObject { ["kind"] = "click" }; var target = snapshot["elements"]![1]!.AsObject();
        var refusal = Refusal(() => Check(state, step, snapshot, target));
        step["confirm"] = true; step["accord"] = Token(refusal);
        snapshot["elements"]![0]!["value"] = "A different description";
        Assert.Contains("action changed", Refusal(() => Check(state, step, snapshot, target)).Message);
    }

    [Theory]
    [InlineData("codex", "submit")]
    [InlineData("antigravity", "submit")]
    [InlineData("claude", "submit")]
    [InlineData("codex", "replace")]
    [InlineData("codex", "focus")]
    [InlineData("antigravity", "focus")]
    [InlineData("claude", "focus")]
    public void A_real_text_enter_rechecks_new_submit_controls_field_identity_and_keyboard_focus(string agent, string change)
    {
        var previous = GardeFous.CurrentAgent;
        try
        {
            GardeFous.CurrentAgent = agent;
            var initial = Snapshot(publish: false); var live = initial.DeepClone().AsObject();
            var field = initial["elements"]![0]!.AsObject();
            var step = new JsonObject { ["kind"] = "setValue", ["value"] = "A\nB" };
            var scopeType = typeof(McpServer).GetNestedType("NativeTextSubmitScope", BindingFlags.NonPublic)!;
            using var scope = (IDisposable)Activator.CreateInstance(scopeType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [Session(agent), step, initial, field, (Func<JsonObject>)(() => live), (Func<string>)(() => Context)], null)!;
            var beforeEnter = scopeType.GetMethod("BeforeEnter")!;
            Invoke(beforeEnter, scope);
            if (change == "replace") live["elements"]![0]!["runtimeId"] = new JsonArray(9, 9);
            else if (change == "focus") live["elements"]![0]!["focused"] = false;
            else live["elements"]!.AsArray().Add(new JsonObject { ["index"] = 1, ["role"] = "button", ["name"] = "Envoyer pour examen" });
            var refusal = Refusal(() => Invoke(beforeEnter, scope));
            Assert.Contains(change == "replace" ? "replaced" : change == "focus" ? "lost keyboard focus" : "Envoyer pour examen", refusal.Message);
        }
        finally { GardeFous.CurrentAgent = previous; }
    }
}
