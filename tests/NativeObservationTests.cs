using System.Text.Json.Nodes;

namespace NexusWindows.Tests;

// A live chat can update beside a stable sidebar command. The direct semantic action must stay bound to what
// the agent observed, while changes inside that command's context, a new modal or unanchored input still refuse.
public class NativeObservationTests
{
    static JsonObject Element(int index, string role, string name, string id, int parent, params int[] ancestors) => new()
    {
        ["index"] = index, ["depth"] = ancestors.Length, ["role"] = role, ["name"] = name,
        ["automationId"] = id, ["runtimeId"] = new JsonArray(7, index + 100), ["nativeWindow"] = 42,
        ["parent"] = parent, ["ancestors"] = new JsonArray(ancestors.Select(i => JsonValue.Create(i)).ToArray()),
        ["visible"] = true, ["disabled"] = false, ["password"] = false,
        ["bounds"] = new JsonArray(index * 10, index * 10, 80, 20),
    };

    static JsonObject Snapshot() => new()
    {
        ["processId"] = 123, ["processStartedUtc"] = "2026-11-02T20:51:05.3937633Z", ["revision"] = "app-version-A",
        ["frame"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["width"] = 800, ["height"] = 600 },
        ["elements"] = new JsonArray(
            Element(0, "window", "ChatGPT", "", -1),
            Element(1, "document", "App", "RootWebArea", 0, 0),
            Element(2, "group", "Sidebar", "sidebar", 1, 0, 1),
            Element(3, "button", "Ajouter un projet", "add-project", 2, 0, 1, 2),
            Element(4, "text", "Sidebar status", "sidebar-status", 2, 0, 1, 2),
            Element(5, "group", "Chat", "chat", 1, 0, 1),
            Element(6, "button", "Lire le message", "message-action", 5, 0, 1, 5))
    };

    static JsonObject Node(JsonObject snapshot, int index) => snapshot["elements"]!.AsArray().OfType<JsonObject>()
        .Single(e => e["index"]!.GetValue<int>() == index);
    static JsonObject ChangedChat(JsonObject observed)
    {
        var current = observed.DeepClone().AsObject();
        Node(current, 6)["name"] = "Copier le message";
        current["elements"]!.AsArray().Add(Element(7, "button", "Nouvelle commande du chat", "new-chat-action", 5, 0, 1, 5));
        return current;
    }
    static JsonObject Click(string kind = "click") => new()
    {
        ["kind"] = kind,
        ["selector"] = new JsonObject { ["role"] = "button", ["name"] = "Ajouter un projet" },
    };
    static JsonArray Steps(JsonObject? step = null) => new(step ?? Click());
    static JsonObject Args() => new();
    static bool Validate(JsonObject observed, JsonObject current, JsonArray? steps = null, JsonObject? args = null) =>
        McpServer.ValidateNativeObservation(observed, current, args ?? Args(), steps ?? Steps());
    static void Refused(JsonObject observed, JsonObject current, JsonArray? steps = null, JsonObject? args = null) =>
        Assert.Throws<ActionRefusedException>(() => Validate(observed, current, steps, args));

    [Fact]
    public void A_live_chat_update_does_not_invalidate_a_stable_sidebar_command()
    {
        var observed = Snapshot(); var steps = Steps();
        Assert.True(Validate(observed, ChangedChat(observed), steps));
        var identity = Assert.IsType<JsonObject>(steps[0]!["observedIdentity"]);
        Assert.True(JsonNode.DeepEquals(Node(observed, 3)["runtimeId"], identity["runtimeId"]));
        Assert.Equal(3, identity["ancestors"]!.AsArray().Count);
    }

    [Fact]
    public void An_unchanged_tree_also_pins_the_observed_runtime_identity()
    {
        var observed = Snapshot(); var steps = Steps();
        Validate(observed, observed.DeepClone().AsObject(), steps);
        Assert.True(JsonNode.DeepEquals(Node(observed, 3)["runtimeId"], steps[0]!["observedIdentity"]!["runtimeId"]));
    }

    [Theory]
    [InlineData("runtimeId")]
    [InlineData("nativeWindow")]
    [InlineData("role")]
    [InlineData("name")]
    [InlineData("automationId")]
    public void A_replacement_button_cannot_inherit_the_old_observation(string field)
    {
        var observed = Snapshot(); var current = observed.DeepClone().AsObject();
        Node(current, 3)[field] = field switch
        {
            "runtimeId" => new JsonArray(7, 999), "nativeWindow" => JsonValue.Create(43),
            _ => JsonValue.Create("replacement"),
        };
        Refused(observed, current);
    }

    [Theory]
    [InlineData(0, "runtimeId")]
    [InlineData(1, "runtimeId")]
    [InlineData(2, "runtimeId")]
    [InlineData(2, "name")]
    [InlineData(2, "automationId")]
    [InlineData(2, "nativeWindow")]
    public void The_complete_observed_ancestry_stays_bound(int index, string field)
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        Node(current, index)[field] = field switch
        {
            "runtimeId" => new JsonArray(7, 999), "nativeWindow" => JsonValue.Create(43),
            _ => JsonValue.Create("replacement"),
        };
        Refused(observed, current);
    }

    [Fact]
    public void Moving_the_same_runtime_button_under_another_parent_is_refused()
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        Node(current, 3)["parent"] = 5; Node(current, 3)["ancestors"] = new JsonArray(0, 1, 5);
        Refused(observed, current);
    }

    [Fact]
    public void A_new_homonymous_command_makes_the_semantic_target_ambiguous()
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        current["elements"]!.AsArray().Add(Element(8, "button", "Ajouter un projet", "another-add-project", 5, 0, 1, 5));
        Refused(observed, current);
    }

    [Fact]
    public void A_command_that_was_not_in_the_observation_cannot_be_substituted()
    {
        var current = Snapshot(); var observed = current.DeepClone().AsObject();
        observed["elements"]!.AsArray().Remove(Node(observed, 3));
        Refused(observed, current);
    }

    [Fact]
    public void An_unchanged_virtualized_list_can_still_materialize_its_named_target()
    {
        var observed = Snapshot(); observed["elements"]!.AsArray().Remove(Node(observed, 3));
        var steps = Steps();
        Assert.False(Validate(observed, observed.DeepClone().AsObject(), steps));
        Assert.Null(steps[0]!["observedIdentity"]);
    }

    [Fact]
    public void A_virtualized_target_does_not_relax_the_full_window_guard()
    {
        var observed = Snapshot(); observed["elements"]!.AsArray().Remove(Node(observed, 3));
        Refused(observed, ChangedChat(observed));
    }

    [Theory]
    [InlineData("value")]
    [InlineData("selected")]
    public void A_local_change_after_binding_still_refuses_before_the_primary_gesture(string field)
    {
        var observed = Snapshot(); var beforeLease = observed.DeepClone().AsObject(); var steps = Steps();
        Validate(observed, beforeLease, steps);
        Assert.IsType<JsonObject>(steps[0]!["observedIdentity"]);
        var afterLease = beforeLease.DeepClone().AsObject();
        Node(afterLease, 4)[field] = field == "selected" ? JsonValue.Create(true) : JsonValue.Create("new local value");
        Refused(observed, afterLease, steps);
    }

    [Fact]
    public void A_modal_opened_after_binding_still_refuses_before_the_primary_gesture()
    {
        var observed = Snapshot(); var beforeLease = observed.DeepClone().AsObject(); var steps = Steps();
        Validate(observed, beforeLease, steps);
        var afterLease = beforeLease.DeepClone().AsObject();
        afterLease["elements"]!.AsArray().Add(Element(8, "dialog", "Confirmation", "confirmation", 0, 0));
        Refused(observed, afterLease, steps);
    }

    [Fact]
    public void Revealing_and_focusing_the_same_bound_control_does_not_change_its_semantic_context()
    {
        var observed = Snapshot(); Node(observed, 3)["visible"] = false;
        var beforeLease = observed.DeepClone().AsObject(); var steps = Steps();
        Validate(observed, beforeLease, steps);
        var afterPreparation = ChangedChat(beforeLease);
        Node(afterPreparation, 3)["visible"] = true; Node(afterPreparation, 3)["focused"] = true;
        Node(afterPreparation, 3)["bounds"] = new JsonArray(500, 400, 120, 25);
        Validate(observed, afterPreparation, steps);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("password")]
    public void Revealing_a_bound_control_never_ignores_disabling_or_protecting_it(string state)
    {
        var observed = Snapshot(); Node(observed, 3)["visible"] = false;
        var beforeLease = observed.DeepClone().AsObject(); var steps = Steps();
        Validate(observed, beforeLease, steps);
        var afterPreparation = beforeLease.DeepClone().AsObject();
        Node(afterPreparation, 3)["visible"] = true; Node(afterPreparation, 3)[state] = true;
        Refused(observed, afterPreparation, steps);
    }

    [Theory]
    [InlineData("processId")]
    [InlineData("processStartedUtc")]
    [InlineData("revision")]
    public void Changing_or_omitting_application_identity_always_refuses(string field)
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        current[field] = field == "processId" ? JsonValue.Create(321) : JsonValue.Create("changed");
        Refused(observed, current);
        current.Remove(field); Refused(observed, current);
        current = ChangedChat(observed); observed.Remove(field); Refused(observed, current);
    }

    [Theory]
    [InlineData("processId")]
    [InlineData("processStartedUtc")]
    [InlineData("revision")]
    public void Missing_identity_in_both_snapshots_does_not_authorize_a_targeted_rebase(string field)
    {
        var observed = Snapshot(); observed.Remove(field);
        Refused(observed, ChangedChat(observed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_process_id_never_authorizes_a_targeted_rebase(int processId)
    {
        var observed = Snapshot(); observed["processId"] = processId;
        Refused(observed, ChangedChat(observed));
    }

    [Theory]
    [InlineData("contentProcessId")]
    [InlineData("contentProcessStartedUtc")]
    public void A_changed_content_process_identity_is_refused_when_the_provider_exposes_it(string field)
    {
        var observed = Snapshot();
        observed[field] = field == "contentProcessId" ? JsonValue.Create(124) : JsonValue.Create("2026-11-02T20:51:06Z");
        var current = ChangedChat(observed);
        Validate(observed, current);
        current[field] = field == "contentProcessId" ? JsonValue.Create(125) : JsonValue.Create("2026-11-02T20:51:07Z");
        Refused(observed, current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_disabled_target_or_ancestor_refuses_before_input(int index)
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        Node(current, index)["disabled"] = true;
        Refused(observed, current);
    }

    [Fact]
    public void A_new_dialog_cannot_be_ignored_as_an_unrelated_chat_update()
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        current["elements"]!.AsArray().Add(Element(8, "dialog", "Confirmation", "confirmation", 0, 0));
        Refused(observed, current);
    }

    [Fact]
    public void A_nested_modal_window_is_a_local_context_independent_of_the_chat_behind_it()
    {
        var observed = Snapshot(); Node(observed, 2)["role"] = "window";
        Assert.True(Validate(observed, ChangedChat(observed)));
    }

    [Fact]
    public void A_field_change_inside_a_nested_modal_window_requires_a_new_look()
    {
        var observed = Snapshot(); Node(observed, 2)["role"] = "window";
        var current = ChangedChat(observed); Node(current, 4)["value"] = "changed modal field";
        Refused(observed, current);
    }

    [Fact]
    public void Replacing_the_nested_modal_window_cannot_reuse_its_observed_command()
    {
        var observed = Snapshot(); Node(observed, 2)["role"] = "window";
        var current = ChangedChat(observed); Node(current, 2)["runtimeId"] = new JsonArray(7, 999);
        Refused(observed, current);
    }

    [Theory]
    [InlineData("role", "button")]
    [InlineData("name", "A different sidebar status")]
    [InlineData("automationId", "another-status")]
    [InlineData("value", "Changed value")]
    [InlineData("checked", true)]
    [InlineData("selected", true)]
    [InlineData("expanded", true)]
    [InlineData("disabled", true)]
    [InlineData("password", true)]
    public void Any_meaningful_change_inside_the_command_context_requires_a_new_look(string field, object value)
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        Node(current, 4)[field] = value is bool flag ? JsonValue.Create(flag) : JsonValue.Create((string)value);
        Refused(observed, current);
    }

    [Fact]
    public void Inserting_or_reordering_commands_inside_the_context_requires_a_new_look()
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        var elements = current["elements"]!.AsArray();
        elements.Insert(4, Element(8, "button", "Autre projet", "other-project", 2, 0, 1, 2));
        Refused(observed, current);
        current = ChangedChat(observed); elements = current["elements"]!.AsArray();
        var target = Node(current, 3); elements.Remove(target); elements.Insert(4, target);
        Refused(observed, current);
    }

    [Fact]
    public void Geometry_and_published_indices_may_change_without_substituting_a_semantic_target()
    {
        var observed = Snapshot(); var current = ChangedChat(observed);
        foreach (var element in current["elements"]!.AsArray().OfType<JsonObject>())
        {
            element["index"] = element["index"]!.GetValue<int>() + 10;
            if (element["parent"]!.GetValue<int>() >= 0) element["parent"] = element["parent"]!.GetValue<int>() + 10;
            element["ancestors"] = new JsonArray(element["ancestors"]!.AsArray().Select(i => JsonValue.Create(i!.GetValue<int>() + 10)).ToArray());
            element["bounds"] = new JsonArray(500, 400, 120, 25);
        }
        Validate(observed, current);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("setValue")]
    [InlineData("typeText")]
    [InlineData("select")]
    [InlineData("secondary")]
    [InlineData("scroll")]
    [InlineData("hover")]
    [InlineData("focus")]
    public void Direct_semantic_gestures_use_the_same_observation_contract(string kind)
    {
        var observed = Snapshot(); var step = Click(kind);
        if (kind == "typeText") { step["focus"] = step["selector"]!.DeepClone(); step.Remove("selector"); }
        Validate(observed, ChangedChat(observed), Steps(step));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("menu")]
    [InlineData("drag")]
    [InlineData("wait")]
    public void Nonsemantic_actions_keep_the_full_window_stability_requirement(string kind)
    {
        var observed = Snapshot();
        Refused(observed, ChangedChat(observed), Steps(new JsonObject { ["kind"] = kind }));
    }

    [Theory]
    [InlineData("point")]
    [InlineData("x")]
    [InlineData("ocrTarget")]
    [InlineData("placeOnly")]
    public void Coordinate_and_ocr_input_never_use_the_semantic_exception(string property)
    {
        var observed = Snapshot(); var step = Click();
        step[property] = property switch
        {
            "point" => new JsonObject { ["x"] = 20, ["y"] = 30 },
            "ocrTarget" => new JsonObject { ["name"] = "Ajouter un projet", ["ocr"] = true },
            "placeOnly" => JsonValue.Create(true), _ => JsonValue.Create(20),
        };
        Refused(observed, ChangedChat(observed), Steps(step));
    }

    [Theory]
    [InlineData("routine", "saved")]
    [InlineData("remember", "saved")]
    [InlineData("operation", "continue")]
    public void Routines_learning_and_continuations_keep_the_full_window_guard(string property, string value)
    {
        var observed = Snapshot();
        Refused(observed, ChangedChat(observed), args: new JsonObject { [property] = value });
    }

    [Fact]
    public void A_multistep_batch_keeps_the_full_window_guard()
    {
        var observed = Snapshot();
        Refused(observed, ChangedChat(observed), new JsonArray(Click(), Click()));
    }

    [Fact]
    public void A_target_without_native_runtime_identity_keeps_the_full_window_guard()
    {
        var observed = Snapshot(); Node(observed, 3).Remove("runtimeId");
        Refused(observed, ChangedChat(observed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void An_ancestor_without_native_runtime_identity_keeps_the_full_window_guard(int index)
    {
        var observed = Snapshot(); Node(observed, index).Remove("runtimeId");
        Refused(observed, ChangedChat(observed));
    }

    [Fact]
    public void The_window_root_alone_is_not_a_local_context_for_a_semantic_exception()
    {
        var observed = Snapshot(); var elements = observed["elements"]!.AsArray();
        elements.Remove(Node(observed, 1)); elements.Remove(Node(observed, 2));
        foreach (var index in new[] { 3, 4, 5 })
        {
            Node(observed, index)["parent"] = 0; Node(observed, index)["ancestors"] = new JsonArray(0);
            Node(observed, index)["depth"] = 1;
        }
        Node(observed, 6)["ancestors"] = new JsonArray(0, 5); Node(observed, 6)["depth"] = 2;
        Refused(observed, ChangedChat(observed));
    }
}
