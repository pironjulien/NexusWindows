using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

public class NativeFreshnessTests
{
    internal static JsonObject Snapshot()
    {
        var snapshot = JsonNode.Parse("""
        {"processId":1618,"processStartedUtc":"2026-10-30T00:00:00Z","contentProcessId":2618,
        "contentProcessStartedUtc":"2026-10-30T00:00:01Z","app":"fixture.exe","revision":"fixture-revision",
        "frame":{"left":0,"top":0,"width":640,"height":480},"accessibility":{"document_truncated":false},"elements":[
        {"index":0,"depth":0,"role":"window","name":"Fixture","runtimeId":[42,1],"ancestors":[],"bounds":[0,0,640,480],"disabled":false},
        {"index":1,"depth":2,"role":"group","name":"Controls","runtimeId":[42,2],"ancestors":[0],"bounds":[0,0,200,480],"disabled":false},
        {"index":2,"depth":4,"role":"link","name":"Open control","automationId":"stable-control","runtimeId":[42,3],"ancestors":[0,1],"bounds":[10,20,100,30],
          "value":null,"description":"Open the fixture control","visible":true,"disabled":false,"selected":null,"checked":null,"expanded":null,"focused":false,"password":false,"invoke":true,"actions":[],"nativeWindow":0,"flags":[]},
        {"index":3,"depth":4,"role":"text","name":"Context label","runtimeId":[42,4],"ancestors":[0,1],"bounds":[10,70,100,20]},
        {"index":4,"depth":2,"role":"group","name":"Streaming output","runtimeId":[42,5],"ancestors":[0],"bounds":[300,0,340,480]},
        {"index":5,"depth":4,"role":"text","name":"First token","runtimeId":[42,6],"ancestors":[0,4],"bounds":[310,20,100,20]}]}
        """)!.AsObject();
        foreach (var node in snapshot["elements"]!.AsArray().OfType<JsonObject>().Where(n => n["role"].S() == "text")) CompletePassiveText(node);
        return snapshot;
    }

    static void CompletePassiveText(JsonObject node)
    {
        var evidence = JsonNode.Parse("""
            {"actions":[],"invoke":false,"focused":false,"isDefault":false,"password":false,"accessKey":"","value":null,
            "capabilities":{"focusable":false,"focused":false,"invoke":false,"toggle":false,"selectionItem":false,"selection":false,
            "expandCollapse":false,"value":false,"rangeValue":false,"scroll":false,"legacy":true,"legacyActionPresent":false,
            "legacyState":0,"accessKeyPresent":false,"ariaRolePresent":false,"ariaPropertiesPresent":false,"ariaModal":false,"dialog":false,"modal":false}}
            """)!.AsObject();
        foreach (var (key, value) in evidence) node[key] = value?.DeepClone();
    }

    internal static JsonObject Request() => JsonNode.Parse("""
        {"steps":[{"kind":"click","selector":{"role":"link","automationId":"stable-control"},"fallback":false,
        "expect":{"selector":{"role":"text","name":"Changed"},"exists":true}}]}
        """)!.AsObject();

    static JsonObject Streaming(JsonObject before)
    {
        var after = before.DeepClone().AsObject();
        var added = JsonNode.Parse("""
            {"index":6,"depth":4,"role":"text","name":"Next token","runtimeId":[42,7],"ancestors":[0,4],"bounds":[310,50,100,20]}
            """)!.AsObject();
        CompletePassiveText(added); after["elements"]!.AsArray().Add(added);
        return after;
    }

    static void Refused(JsonObject before, JsonObject after, JsonObject? request = null, string operation = "run")
    {
        var input = 0;
        Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            McpServer.ValidateNativeFreshness(before, after, request ?? Request(), operation);
            input++;
        });
        Assert.Equal(0, input);
    }

    [Fact]
    public void Text_streaming_outside_a_proved_parent_allows_one_background_semantic_action()
    {
        var before = Snapshot(); var after = Streaming(before);
        McpServer.ValidateNativeFreshness(before, after, Request(), "run");
        // Existing text may also advance; every changed rectangle remains outside the protected parent.
        after["elements"]![5]!["name"] = "More generated text";
        McpServer.ValidateNativeFreshness(before, after, Request(), "run");
    }

    [Theory]
    [InlineData("name", "\"A different link\"")]
    [InlineData("role", "\"button\"")]
    [InlineData("automationId", "\"replacement\"")]
    [InlineData("runtimeId", "[42,99]")]
    [InlineData("nativeWindow", "99")]
    [InlineData("description", "\"Different purpose\"")]
    [InlineData("value", "\"Different value\"")]
    [InlineData("disabled", "true")]
    [InlineData("visible", "false")]
    [InlineData("focused", "true")]
    [InlineData("checked", "true")]
    [InlineData("selected", "true")]
    [InlineData("expanded", "true")]
    [InlineData("password", "true")]
    [InlineData("invoke", "false")]
    [InlineData("actions", "[\"Expand\"]")]
    [InlineData("bounds", "[11,20,100,30]")]
    [InlineData("flags", "[\"offscreen\"]")]
    public void Reused_runtime_identity_never_hides_changed_target_properties(string property, string json)
    {
        var before = Snapshot(); var after = Streaming(before);
        after["elements"]![2]![property] = JsonNode.Parse(json);
        Refused(before, after);
    }

    [Fact]
    public void Parent_ancestor_document_and_sibling_context_changes_are_refused()
    {
        var before = Snapshot();
        foreach (var index in new[] { 0, 1, 3 })
        {
            var after = Streaming(before); after["elements"]![index]!["name"] = "Changed context";
            Refused(before, after);
        }
        var moved = Streaming(before); moved["elements"]![2]!["ancestors"] = new JsonArray(0, 4);
        Refused(before, moved);
        var overlap = Streaming(before); overlap["elements"]![6]!["bounds"] = new JsonArray(10, 30, 100, 20);
        Refused(before, overlap);
        var ambiguous = Streaming(before); var duplicate = ambiguous["elements"]![2]!.DeepClone();
        duplicate["runtimeId"] = new JsonArray(42, 98); duplicate["index"] = 7;
        ambiguous["elements"]!.AsArray().Add(duplicate);
        Refused(before, ambiguous);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("dialog")]
    [InlineData("menu")]
    [InlineData("pane")]
    [InlineData("group")]
    [InlineData("button")]
    public void New_sibling_dialog_or_untyped_aria_overlay_refuses_before_input(string role)
    {
        var before = Snapshot(); var after = Streaming(before);
        var barrier = JsonNode.Parse("""{"index":7,"depth":2,"role":"group","name":"","runtimeId":[42,8],"ancestors":[0],"bounds":[300,80,100,100]}""")!;
        barrier["role"] = role; after["elements"]!.AsArray().Add(barrier);
        Refused(before, after);
        var changed = Streaming(before); changed["elements"]![4]!["ariaRole"] = "dialog";
        Refused(before, changed);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("treeTruncated")]
    [InlineData("incomplete")]
    [InlineData("document_truncated")]
    [InlineData("valueTruncated")]
    [InlineData("runtimeId")]
    [InlineData("ancestors")]
    public void Missing_or_truncated_native_evidence_refuses_before_input(string field)
    {
        var before = Snapshot(); var after = Streaming(before);
        if (field == "document_truncated") after["accessibility"]![field] = true;
        else if (field == "valueTruncated") after["elements"]![5]![field] = true;
        else if (field is "runtimeId" or "ancestors") after["elements"]![4]!.AsObject().Remove(field);
        else after[field] = true;
        Refused(before, after);
    }

    [Fact]
    public void Coordinates_routines_continuations_and_implicit_physical_fallback_keep_the_global_guard()
    {
        var before = Snapshot(); var after = Streaming(before);
        foreach (var mutation in new Action<JsonObject>[] {
            r => r["steps"]![0]!.AsObject().Remove("fallback"), r => r["steps"]![0]!["fallback"] = true,
            r => r["steps"]![0]!["point"] = new JsonObject { ["x"] = 10, ["y"] = 20 },
            r => r["steps"]![0]!["kind"] = "key", r => r["routine"] = "existing",
            r => r["remember"] = "new", r => r["interaction"] = true,
            r => r["steps"]!.AsArray().Add(r["steps"]![0]!.DeepClone()) })
        {
            var request = Request(); mutation(request); Refused(before, after, request);
        }
        Refused(before, after, operation: "continue");
        foreach (var field in new[] { "processId", "processStartedUtc", "contentProcessId", "contentProcessStartedUtc", "app", "revision", "frame" })
        {
            var changed = after.DeepClone().AsObject(); changed.Remove(field); Refused(before, changed);
        }
    }

    [Fact]
    public void Traversal_indices_can_shift_without_changing_control_or_ancestry_identity()
    {
        var before = Snapshot(); var after = Streaming(before);
        var nodes = after["elements"]!.AsArray().Select(n => n!.AsObject()).ToArray();
        foreach (var node in nodes)
        {
            node["index"] = node["index"]!.GetValue<int>() + 100;
            node["ancestors"] = new JsonArray(node["ancestors"]!.AsArray().Select(n => (JsonNode)JsonValue.Create(n!.GetValue<int>() + 100)!).ToArray());
        }
        McpServer.ValidateNativeFreshness(before, after, Request(), "run");
    }

    [Theory]
    [InlineData("legacy-process-metadata")]
    [InlineData("unchanged-ocr")]
    [InlineData("unchanged-truncation")]
    [InlineData("unchanged-missing-runtime")]
    public void An_unchanged_tree_keeps_the_existing_native_compatibility_path(string scenario)
    {
        var before = Snapshot();
        if (scenario == "legacy-process-metadata") before.Remove("contentProcessStartedUtc");
        else if (scenario == "unchanged-ocr") before["elements"]![5]!["ocr"] = true;
        else if (scenario == "unchanged-truncation") before["accessibility"]!["document_truncated"] = true;
        else before["elements"]![5]!.AsObject().Remove("runtimeId");
        McpServer.ValidateNativeFreshness(before, before.DeepClone().AsObject(), Request(), "run");
        Refused(before, Streaming(before));
    }

    [Theory]
    [InlineData("invoke", "true")]
    [InlineData("actions", "[\"Expand\"]")]
    [InlineData("focused", "true")]
    [InlineData("isDefault", "true")]
    [InlineData("accessKey", "\"Alt+X\"")]
    [InlineData("expanded", "false")]
    [InlineData("selected", "false")]
    [InlineData("value", "\"Writable or ambiguous value\"")]
    [InlineData("capabilities.focusable", "true")]
    [InlineData("capabilities.value", "true")]
    [InlineData("capabilities.toggle", "true")]
    [InlineData("capabilities.legacyActionPresent", "true")]
    [InlineData("capabilities.legacyState", "1048576")]
    [InlineData("capabilities.ariaRolePresent", "true")]
    [InlineData("capabilities.ariaModal", "true")]
    [InlineData("capabilities.dialog", "true")]
    [InlineData("capabilities.modal", "true")]
    [InlineData("capabilities.invoke", "null")]
    public void Interactive_modal_or_ambiguous_text_is_never_reconciled(string path, string json)
    {
        void Mutate(JsonObject node)
        {
            var parts = path.Split('.');
            (parts.Length == 1 ? node : node[parts[0]]!.AsObject())[parts[^1]] = JsonNode.Parse(json);
        }
        var before = Snapshot(); var added = Streaming(before); Mutate(added["elements"]![6]!.AsObject()); Refused(before, added);
        var changed = Streaming(before); Mutate(changed["elements"]![5]!.AsObject()); Refused(before, changed);
        var interactiveBefore = Snapshot(); Mutate(interactiveBefore["elements"]![5]!.AsObject());
        var removed = Streaming(interactiveBefore);
        var additional = removed["elements"]![6]!.DeepClone(); additional["index"] = 7; additional["runtimeId"] = new JsonArray(42, 8);
        removed["elements"]!.AsArray().Add(additional); removed["elements"]!.AsArray().RemoveAt(5);
        Refused(interactiveBefore, removed);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("automationId")]
    [InlineData("nativeWindow")]
    [InlineData("flags")]
    [InlineData("capabilities")]
    public void Persistent_passive_text_cannot_change_its_identity_or_metadata(string field)
    {
        var before = Snapshot(); var after = Streaming(before);
        after["elements"]![5]![field] = field switch {
            "nativeWindow" => JsonValue.Create(99), "flags" => new JsonArray("offscreen"),
            "capabilities" => null, _ => JsonValue.Create("Changed metadata") };
        Refused(before, after);
    }
}
