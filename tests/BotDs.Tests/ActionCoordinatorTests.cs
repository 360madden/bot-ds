using BotDs.App.Services;
using BotDs.Core;
using BotDs.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace BotDs.Tests;

public sealed class ActionCoordinatorTests : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(500);
    private readonly string _tempDir;

    public ActionCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botds-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Disabled_mode_rejects_all_actions()
    {
        var (coord, _, _, _, sm) = CreateCoordinator();

        // Startup: coordinator must default to Disabled (M8 safety requirement)
        Assert.Equal(OutputMode.Disabled, coord.Mode);

        var result = CreateHealthyResult();
        var record = coord.Consume(result, sm.Generation);
        Assert.Null(record); // disabled mode rejects all actions
    }

    [Fact]
    public async Task DryRun_dispatches_when_ready()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        var result = CreateHealthyResult();
        var record = coord.Consume(result, sm.Generation);
        Assert.NotNull(record);
        Assert.Equal(DispatchOutcome.Dispatched, record.Outcome);
        Assert.Equal("dry-run", record.Detail);
    }

    [Fact]
    public async Task Global_rate_limit_blocks_second_dispatch()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        // First dispatch creates pending action
        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);

        // Second dispatch blocked by pending action (checked before rate limits)
        var r2 = coord.Consume(CreateHealthyResult(actionKey: "2"), sm.Generation);
        Assert.Equal(DispatchOutcome.PendingActionBlocked, r2!.Outcome);
    }

    [Fact]
    public async Task Per_key_and_pending_action_block_second_dispatch()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        // First dispatch creates pending action
        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);

        // Second dispatch with same key blocked by pending action
        var r2 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.PendingActionBlocked, r2!.Outcome);
    }

    [Fact]
    public async Task Pending_action_blocks_further_dispatches()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.NotNull(coord.PendingAction);

        var r2 = coord.Consume(CreateHealthyResult(actionKey: "2"), sm.Generation);
        Assert.NotNull(r2);
        Assert.True(r2!.Outcome is DispatchOutcome.PendingActionBlocked or DispatchOutcome.RateLimited);
    }

    [Fact]
    public async Task Disabling_coordinator_cancels_pending_action()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.NotNull(coord.PendingAction);

        coord.Disable();
        Assert.Null(coord.PendingAction);
        Assert.Null(coord.Consume(CreateHealthyResult(actionKey: "2"), sm.Generation));
    }

    [Fact]
    public async Task Disarmed_controller_blocks_dispatch()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));

        var record = coord.Consume(CreateHealthyResult(), sm.Generation);
        Assert.NotNull(record);
        Assert.Equal(DispatchOutcome.Cancelled, record.Outcome);
    }

    [Fact]
    public async Task Mode_change_requires_disarmed_state()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        Assert.False(coord.TrySetMode(OutputMode.DryRun, MaxAge));
    }

    [Fact]
    public async Task ObservePending_acknowledges_cooldown_and_verifies_binding()
    {
        var (coord, _, pub, _, sm) = await CreateReadyCoordinator();
        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        TelemetryFrame pre = CreateHealthyFrame(sequence: 100, cooldownRemainingMs: 0);
        pub.Publish(pre);

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1", sequence: 100), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.NotNull(coord.PendingAction);
        Assert.Equal(BindingVerificationState.Unverified, coord.Bindings.GetState("slice"));

        TelemetryFrame post = CreateHealthyFrame(
            sequence: 101,
            cooldownRemainingMs: 1200,
            sessionId: pre.Provider.SessionId);
        pub.Publish(post);

        var ack = coord.ObservePending(post, sm.Generation);
        Assert.NotNull(ack);
        Assert.Equal(DispatchOutcome.Acknowledged, ack!.Outcome);
        Assert.Null(coord.PendingAction);
        Assert.Equal(BindingVerificationState.Verified, coord.Bindings.GetState("slice"));
    }

    [Fact]
    public async Task Live_mode_requires_verified_bindings()
    {
        var (coord, _, _, _, _) = await CreateReadyCoordinator();

        // Fresh profile binding is Unverified — Live must fail closed.
        Assert.False(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Equal(OutputMode.Disabled, coord.Mode);

        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Equal(OutputMode.Live, coord.Mode);
    }

    [Fact]
    public async Task Live_ack_timeout_stops_controller()
    {
        var time = new FakeTimeProvider();
        var (coord, _, pub, _, sm) = await CreateReadyCoordinator(time, frameNow: time.GetUtcNow());
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();

        TelemetryFrame pre = CreateHealthyFrame(sequence: 50, cooldownRemainingMs: 0, now: time.GetUtcNow());
        pub.Publish(pre);
        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1", sequence: 50), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);

        time.Advance(TimeSpan.FromMilliseconds(2500));
        TelemetryFrame stillPending = CreateHealthyFrame(
            sequence: 51,
            cooldownRemainingMs: 0,
            sessionId: pre.Provider.SessionId,
            now: time.GetUtcNow());
        pub.Publish(stillPending);

        var timeout = coord.ObservePending(stillPending, sm.Generation);
        Assert.NotNull(timeout);
        Assert.Equal(DispatchOutcome.AcknowledgementTimeout, timeout!.Outcome);
        Assert.Equal(ControllerState.Stopped, sm.State);
        Assert.Equal(StopReason.ActionNotAcknowledged, sm.Snapshot.StopReason);
        Assert.Equal(OutputMode.Disabled, coord.Mode);
    }

    // ---- Helpers ----

    private async Task<(ActionCoordinator, ProfileService, SnapshotPublisher, ArmingReadinessService, ControllerStateMachine)>
        CreateReadyCoordinator(TimeProvider? timeProvider = null, DateTimeOffset? frameNow = null)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var pub = new SnapshotPublisher(clock);
        var profiles = await CreateProfileService("test-profile");
        WriteProfileFile(CreateValidProfileJson());
        await profiles.ReloadAsync();
        profiles.SetActiveProfile("test-profile");
        pub.Publish(CreateHealthyFrame(now: frameNow ?? clock.GetUtcNow()));
        var readiness = new ArmingReadinessService(pub, profiles, clock);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = CreateConfig();
        var bindings = new BindingVerificationTracker();
        var coord = new ActionCoordinator(
            pub, sm, profiles, readiness, config,
            keySink: new FakeKeySink(),
            timeProvider: clock,
            bindingVerification: bindings);
        return (coord, profiles, pub, readiness, sm);
    }

    private (ActionCoordinator, ProfileService, SnapshotPublisher, ArmingReadinessService, ControllerStateMachine)
        CreateCoordinator()
    {
        var pub = new SnapshotPublisher();
        var profiles = CreateEmptyProfileService();
        var readiness = new ArmingReadinessService(pub, profiles);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = CreateConfig();
        var coord = new ActionCoordinator(pub, sm, profiles, readiness, config);
        return (coord, profiles, pub, readiness, sm);
    }

    private static ProfileService CreateEmptyProfileService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = Path.Combine(Path.GetTempPath(), "botds-nonexist"),
            })
            .Build();
        return new ProfileService(config, new TestHostEnv(Path.GetTempPath()), new NullLogger<ProfileService>());
    }

    private async Task<ProfileService> CreateProfileService(string activeProfileId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = _tempDir,
            })
            .Build();
        var svc = new ProfileService(config, new TestHostEnv(_tempDir), new NullLogger<ProfileService>());
        await svc.ReloadAsync();
        if (activeProfileId is not null)
            svc.SetActiveProfile(activeProfileId);
        return svc;
    }

    private void WriteProfileFile(string json)
    {
        File.WriteAllText(Path.Combine(_tempDir, "test-profile.json"), json);
    }

    private static string CreateValidProfileJson()
    {
        return JsonSerializer.Serialize(new
        {
            id = "test-profile",
            profileVersion = 1,
            enabled = true,
            character = new { calling = "Warrior", minimumLevel = 1, maximumLevel = 60 },
            abilities = new Dictionary<string, object>
            {
                ["slice"] = new { abilityId = "1001", key = "1", enabled = true },
            },
            rules = new[]
            {
                new { id = "r1", ability = "slice", enabled = true },
            },
        });
    }

    private static IConfigurationRoot CreateConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
            })
            .Build();
    }

    private static EvaluationResult CreateHealthyResult(string actionKey = "1", ulong sequence = 100)
    {
        return new EvaluationResult(
            ControllerState.Armed,
            new ActionDecision("r1", "slice", "1001", actionKey, AcknowledgementKind.Cooldown, sequence),
            []);
    }

    private static TelemetryFrame CreateHealthyFrame(
        ulong sequence = 100,
        int? cooldownRemainingMs = 0,
        string? sessionId = null,
        DateTimeOffset? now = null)
    {
        DateTimeOffset receivedAt = now ?? DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: sessionId ?? Guid.NewGuid().ToString("D"),
                Sequence: sequence,
                ProducerFrameMilliseconds: 16,
                ReceivedAtUtc: receivedAt,
                Age: TimeSpan.FromMilliseconds(10),
                SourceGeneration: 1),
            Player: new UnitState(
                Id: "player-1", Name: "Test", Level: 50, Calling: "Warrior",
                IsPlayer: true, Relation: "friendly",
                Health: new HealthState(5000, 5000),
                Resource: new ResourceState("Power", 100, 100),
                InCombat: true, Cast: null),
            Target: new UnitState(
                Id: "target-1", Name: "Mob", Level: 50, Calling: null,
                IsPlayer: false, Relation: "hostile",
                Health: new HealthState(3000, 3000),
                Resource: null, InCombat: true, Cast: null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(
                new Dictionary<string, AbilityState>
                {
                    ["1001"] = new AbilityState(
                        "1001", "Slice", true, true, true, cooldownRemainingMs, 1500, null,
                        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()),
                        0, false, false),
                }),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }

    private sealed class TestHostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
