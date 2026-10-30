// Offline self-checks of the unified engine that report one verdict (formerly --garde-fous-selftest, --journal-selftest,
// --routines-selftest, --mcp-selftest, --notifications-selftest, --migration-selftest, --mcp-logs-selftest,
// --key-sequence-selftest, --routine-compat-selftest, --shell-compat-selftest, --store-parity-selftest,
// --virtualization-selftest, and the reflection-only seams of the native-*.test.mjs files). No window is shown and no
// input is sent.
namespace NexusWindows.Tests;

[Collection("Engine")]
public class EngineSelfCheckTests
{
    [Fact]
    public void Garde_fous_journal_routines_and_mcp_selftests()
    {
        var gf = SelfTest.Json(GardeFous.SelfTest);
        Assert.True(gf["ok"].B());
        Assert.Equal(["claude", "antigravity", "codex"], gf["agents"].Strings());
        Assert.True(gf["lookalikes"].L() >= 60);
        Assert.True(SelfTest.Json(Journal.SelfTest)["ok"].B());
        Assert.True(SelfTest.Json(Routines.SelfTest)["ok"].B());
        var mcp = SelfTest.Json(McpServer.SelfTest);
        Assert.True(mcp["ok"].B());
        Assert.Equal(["claude", "antigravity", "codex"], mcp["agents"].Strings());
    }

    [Fact]
    public void Observed_identities_french_contracts_image_points_and_stable_deltas()
    {
        var report = SelfTest.Json(McpServer.SelfTest);
        Assert.True(report["ok"].B());
        Assert.True(report["legacyObservationAssertions"].L() >= 21, "run the compiled regression scenarios");
        Assert.True(report["fallbackTraceAssertions"].L() >= 11, "trace the actual fallback seam");
    }

    [Theory]
    [InlineData("notifications")]
    [InlineData("migration")]
    public void Isolated_persistent_fixtures(string mode)
    {
        Func<int> run = mode == "notifications" ? Notifications.SelfTest : McpServer.MigrationSelfTest;
        Assert.True(SelfTest.Json(run)["ok"].B());
    }

    [Fact]
    public void Claude_logs_preserve_legacy_history_and_native_techniques_without_typed_data()
    {
        var result = SelfTest.Json(McpServer.LogsSelfTest);
        foreach (var field in new[] { "ok", "history", "readonly", "typedTextOmitted", "techniques" }) Assert.True(result[field].B(), field);
    }

    [Fact]
    public void Key_sequence_transport_fresh_guards_and_global_proof_without_gui()
    {
        var result = SelfTest.Json(McpServer.KeySequenceSelfTest);
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.True(result["assertions"].L() >= 226);
    }

    [Fact]
    public void Routine_compatibility_permissions_refusals_and_private_mirror_isolation()
    {
        var result = SelfTest.Json(McpServer.RoutineCompatibilitySelfTest);
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.True(result["assertions"].L() >= 97);
    }

    [Fact]
    public void Claude_shell_scope_preserves_background_refusals_and_exact_foreground_identities()
    {
        var result = SelfTest.Json(McpServer.ShellCompatibilitySelfTest);
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.True(result["assertions"].L() >= 40);
    }

    [Fact]
    public void Store_support_state_readback_identity_gate_no_retry_and_hidden_application_classification()
    {
        var result = SelfTest.Json(StorePackages.SelfTest);
        Assert.True(result["ok"].B());
        Assert.True(result["checks"].L() >= 17);
    }

    [Fact]
    public void Virtualized_materialization_realize_precedes_scrolling_and_every_pattern_is_released()
    {
        var result = SelfTest.Json(ComWorker.VirtualizationSelfTest);
        Assert.True(result["ok"].B());
        Assert.True(result["checks"].L() >= 7);
    }

    [Fact]
    public void Inspection_brackets_a_fresh_tree_and_rejects_window_or_content_replacement()
    {
        var result = SelfTest.Node(Program.InspectionFrameSelfTest());
        Assert.True(result["ok"].B());
        Assert.True(result["checks"].L() >= 10);
    }

    [Fact]
    public void Pointer_messages_restore_the_recipient_dpi_context()
    {
        var result = SelfTest.Node(Pointer.RecipientContextSelfTest());
        Assert.True(result["ok"].B());
        Assert.True(result["checks"].L() >= 4);
        Assert.False(result["gui"].B());
        Assert.False(result["inputSent"].B());
    }

    [Fact]
    public void Claude_uia_append_reads_the_same_live_field_and_never_replays()
    {
        var result = SelfTest.Node(ComWorker.AppendValueSelfTest());
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.True(result["checks"].L() >= 14);
    }

    [Fact]
    public void Claude_chromium_focus_publishes_under_exact_identity_and_never_replays_accSelect()
    {
        var result = SelfTest.Node(ComWorker.ChromiumFocusSelfTest());
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.False(result["inputSent"].B());
        Assert.True(result["checks"].L() >= 11);
    }

    [Fact]
    public void Claude_chromium_text_guards_250_bounded_packets_and_2000_runes()
    {
        var result = SelfTest.Node(ChromiumInput.SimulatedPacketGuardSelfTest());
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.False(result["inputSent"].B());
        Assert.True(result["checks"].L() >= 6);
        Assert.Equal(250, result["packets"].L());
        Assert.Equal(250, result["packetProofs"].L());
        Assert.Equal(2000, result["presenceChecks"].L());
        Assert.Equal(0, result["proofDuringRune"].L());
        Assert.Equal(2000, result["utf16Posted"].L());
    }

    [Fact]
    public void Chromium_first_read_warmup_is_bounded_and_respects_stop()
    {
        var result = SelfTest.Node(ComUia.ChromiumWarmupSelfTest());
        Assert.True(result["ok"].B());
        Assert.False(result["gui"].B());
        Assert.True(result["checks"].L() >= 24);
        Assert.Equal(1500, result["waitBudgetMs"].L());
        Assert.Equal(250, result["pollMs"].L());
        Assert.Equal(60000, result["failureCooldownMs"].L());
    }

    [Fact]
    public void Process_identity_reports_a_real_utc_creation_timestamp_and_never_invents_an_unknown_one()
    {
        var started = Program.ProcessStartedUtc((uint)Environment.ProcessId);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", started);
        var when = DateTime.Parse(started!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.True(when <= DateTime.UtcNow);
        Assert.True(when >= System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().AddSeconds(-5));
        Assert.Null(Program.ProcessStartedUtc(0));
    }
}
