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

        // DryRun records rate limits but never creates a pending action.
        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);

        // Second dispatch is blocked by the global rate limit.
        var r2 = coord.Consume(CreateHealthyResult(actionKey: "2"), sm.Generation);
        Assert.Equal(DispatchOutcome.RateLimited, r2!.Outcome);
    }

    [Fact]
    public async Task Per_key_and_pending_action_block_second_dispatch()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        // First observational dispatch updates the rate-limit history only.
        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);

        // Second dispatch with the same key is rate limited.
        var r2 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.RateLimited, r2!.Outcome);
    }

    [Fact]
    public async Task DryRun_never_creates_pending_action()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.Null(coord.PendingAction);

        var r2 = coord.Consume(CreateHealthyResult(actionKey: "2"), sm.Generation);
        Assert.NotNull(r2);
        Assert.Equal(DispatchOutcome.RateLimited, r2!.Outcome);
        Assert.Null(coord.PendingAction);
    }

    [Fact]
    public async Task Disabling_coordinator_cancels_pending_action()
    {
        var (coord, _, _, _, sm) = await CreateReadyCoordinator();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1"), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.Null(coord.PendingAction);

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
    public async Task DryRun_observations_do_not_acknowledge_or_verify_binding()
    {
        var (coord, _, pub, _, sm) = await CreateReadyCoordinator();
        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();

        TelemetryFrame pre = CreateHealthyFrame(sequence: 100, cooldownRemainingMs: 0);
        pub.Publish(pre);

        var r1 = coord.Consume(CreateHealthyResult(actionKey: "1", sequence: 100), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, r1!.Outcome);
        Assert.Null(coord.PendingAction);
        Assert.Equal(BindingVerificationState.Unverified, coord.Bindings.GetState("slice"));

        TelemetryFrame post = CreateHealthyFrame(
            sequence: 101,
            cooldownRemainingMs: 1200,
            sessionId: pre.Provider.SessionId);
        pub.Publish(post);

        var ack = coord.ObservePending(post, sm.Generation);
        Assert.Null(ack);
        Assert.Null(coord.PendingAction);
        Assert.Equal(BindingVerificationState.Unverified, coord.Bindings.GetState("slice"));
    }

    [Fact]
    public async Task DryRun_never_calls_sink_or_stops_on_manual_game_action()
    {
        var sink = new TestLiveKeySink { ThrowOnDispatch = true };
        var (coord, _, pub, _, sm) = await CreateReadyCoordinator(keySink: sink);
        coord.Bindings.MarkMismatch("slice");
        BindingVerificationSnapshot before = coord.Bindings.Snapshot();

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        sm.Arm();
        TelemetryFrame pre = CreateHealthyFrame(sequence: 200);
        pub.Publish(pre);

        DispatchRecord? dispatched = coord.Consume(
            CreateHealthyResult(sequence: 200), sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, dispatched!.Outcome);
        Assert.Equal("dry-run", dispatched.Detail);
        Assert.Equal(0, sink.DispatchCount);
        Assert.Null(coord.PendingAction);

        TelemetryFrame manualAction = CreateHealthyFrame(
            sequence: 201,
            cooldownRemainingMs: 1500,
            sessionId: pre.Provider.SessionId);
        pub.Publish(manualAction);
        Assert.Null(coord.ObservePending(manualAction, sm.Generation));
        Assert.NotEqual(ControllerState.Stopped, sm.State);
        Assert.Equal(OutputMode.DryRun, coord.Mode);
        BindingVerificationSnapshot after = coord.Bindings.Snapshot();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.ProfileId, after.ProfileId);
        Assert.Equal(before.ProviderSessionId, after.ProviderSessionId);
        Assert.Equal(BindingVerificationState.Mismatch, coord.Bindings.GetState("slice"));
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

    [Fact]
    public async Task Live_fence_dispatches_once_for_same_target_newer_valid_frame()
    {
        var sink = new TestLiveKeySink();
        var (coord, profiles, pub, _, sm) = await CreateReadyCoordinator(
            keySink: sink,
            detectExternalActionConflicts: false);
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();

        TelemetryFrame decisionFrame = CreateHealthyFrame(sequence: 100);
        pub.Publish(decisionFrame);
        ActionDecision decision = new CombatEvaluator(MaxAge).Evaluate(
            profiles.ActiveProfile!, decisionFrame).Action!;
        pub.Publish(NextSequence(decisionFrame));

        DispatchRecord? record = coord.Consume(
            new EvaluationResult(ControllerState.Armed, decision, []), sm.Generation);

        Assert.Equal(DispatchOutcome.Dispatched, record!.Outcome);
        Assert.Equal(1, sink.DispatchCount);
        Assert.NotNull(coord.PendingAction);
    }

    [Fact]
    public async Task Live_fence_accepts_uint_sequence_wrap()
    {
        var sink = new TestLiveKeySink();
        var (coord, profiles, pub, _, sm) = await CreateReadyCoordinator(
            keySink: sink,
            detectExternalActionConflicts: false);
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();

        TelemetryFrame decisionFrame = CreateHealthyFrame(sequence: uint.MaxValue);
        pub.Publish(decisionFrame);
        ActionDecision decision = new CombatEvaluator(MaxAge).Evaluate(
            profiles.ActiveProfile!, decisionFrame).Action!;
        pub.Publish(decisionFrame with
        {
            Provider = decisionFrame.Provider with { Sequence = 0 },
        });

        DispatchRecord? record = coord.Consume(
            new EvaluationResult(ControllerState.Armed, decision, []), sm.Generation);

        Assert.Equal(DispatchOutcome.Dispatched, record!.Outcome);
        Assert.Equal(1, sink.DispatchCount);
    }

    [Fact]
    public async Task Live_fence_rejects_transient_game_state_changes_without_input_or_pending()
    {
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with { Sequence = frame.Provider.Sequence + 1 },
            Target = frame.Target! with { Id = "target-2" },
        }, fatal: false);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with { Sequence = frame.Provider.Sequence + 1 },
            Target = null,
            TargetKnownness = TargetKnownness.KnownNoTarget,
        }, fatal: false);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with { Sequence = frame.Provider.Sequence + 1 },
            Target = frame.Target! with { Health = new HealthState(0, 3000) },
        }, fatal: false);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with { Sequence = frame.Provider.Sequence + 1 },
            Player = frame.Player! with { Health = new HealthState(0, 5000) },
        }, fatal: false);
        await AssertLiveFenceRejects(frame => NextSequence(frame) with { GameInputReady = false }, fatal: false);
        await AssertLiveFenceRejects(frame => NextSequence(frame) with { GameInputReady = null }, fatal: false);
        await AssertLiveFenceRejects(frame => WithAbility(
            NextSequence(frame), ability => ability with { CooldownRemainingMilliseconds = 1000 }), fatal: false);
    }

    [Fact]
    public async Task Live_fence_stops_and_disables_on_provider_identity_or_integrity_change()
    {
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with
            {
                Sequence = frame.Provider.Sequence + 1,
                SessionId = "session-2",
            },
        }, fatal: true);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with
            {
                Sequence = frame.Provider.Sequence + 1,
                SourceGeneration = 2,
            },
        }, fatal: true);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with
            {
                Sequence = frame.Provider.Sequence + 1,
                AttachmentProcessId = 9999,
            },
        }, fatal: true);
        await AssertLiveFenceRejects(frame => frame with
        {
            Provider = frame.Provider with
            {
                Sequence = frame.Provider.Sequence + 1,
                Health = ProviderHealth.Stale,
            },
        }, fatal: true);
    }

    [Fact]
    public async Task Live_fence_rejects_changed_winning_rule_without_input()
    {
        var sink = new TestLiveKeySink();
        var (coord, profiles, pub, _, sm) = await CreateReadyCoordinator(
            keySink: sink,
            detectExternalActionConflicts: false,
            profileJson: CreateTwoRuleProfileJson());
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();

        TelemetryFrame decisionFrame = CreateHealthyFrame(sequence: 300) with
        {
            Player = CreateHealthyFrame(sequence: 300).Player! with
            {
                Health = new HealthState(2000, 5000),
            },
        };
        pub.Publish(decisionFrame);
        ActionDecision decision = new CombatEvaluator(MaxAge).Evaluate(
            profiles.ActiveProfile!, decisionFrame).Action!;
        Assert.Equal("low-health", decision.RuleId);

        TelemetryFrame latest = NextSequence(decisionFrame) with
        {
            Player = decisionFrame.Player! with { Health = new HealthState(5000, 5000) },
        };
        pub.Publish(latest);
        DispatchRecord? record = coord.Consume(
            new EvaluationResult(ControllerState.Armed, decision, []), sm.Generation);

        Assert.Equal(DispatchOutcome.RevalidationFailed, record!.Outcome);
        Assert.Equal(0, sink.DispatchCount);
        Assert.Null(coord.PendingAction);
        Assert.Equal(OutputMode.Live, coord.Mode);
    }

    [Fact]
    public async Task Live_mode_rejects_fake_missing_mismatched_and_unready_sinks()
    {
        var (fakeCoord, _, _, _, _) = await CreateReadyCoordinator(keySink: new FakeKeySink());
        fakeCoord.Bindings.MarkVerified("slice");
        Assert.False(fakeCoord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Contains(fakeCoord.GetLiveBlockers(), b => b.Contains("does not support", StringComparison.OrdinalIgnoreCase));

        // A zero telemetry PID is a distinct blocker from the sink's bound PID.
        // Publish after fixture setup so readiness sees the missing identity.
        // Reusing the fixture publisher is intentionally avoided below.
        var missing = await CreateReadyCoordinator(keySink: new TestLiveKeySink(4242));
        missing.Item3.Publish(CreateHealthyFrame() with
        {
            Provider = CreateHealthyFrame().Provider with { AttachmentProcessId = null },
        });
        missing.Item1.Bindings.MarkVerified("slice");
        Assert.False(missing.Item1.TrySetMode(OutputMode.Live, MaxAge));

        var (mismatchCoord, _, _, _, _) = await CreateReadyCoordinator(keySink: new TestLiveKeySink(9999));
        mismatchCoord.Bindings.MarkVerified("slice");
        Assert.False(mismatchCoord.TrySetMode(OutputMode.Live, MaxAge));

        var unreadySink = new TestLiveKeySink { Ready = false };
        var (unreadyCoord, _, _, _, _) = await CreateReadyCoordinator(keySink: unreadySink);
        unreadyCoord.Bindings.MarkVerified("slice");
        Assert.False(unreadyCoord.TrySetMode(OutputMode.Live, MaxAge));
    }

    [Fact]
    public async Task Live_mode_rejects_unsupported_acknowledgement_but_DryRun_allows_it()
    {
        string profileJson = CreateValidProfileJson().Replace(
            "\"id\":\"r1\",\"ability\":\"slice\",\"enabled\":true}",
            "\"id\":\"r1\",\"ability\":\"slice\",\"enabled\":true,\"acknowledgement\":\"resource\"}",
            StringComparison.Ordinal);
        var (coord, _, _, _, _) = await CreateReadyCoordinator(profileJson: profileJson);

        Assert.True(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        coord.Disable();
        coord.Bindings.MarkVerified("slice");
        Assert.False(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Contains(coord.GetLiveBlockers(), b => b.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Helpers ----

    private async Task<(ActionCoordinator, ProfileService, SnapshotPublisher, ArmingReadinessService, ControllerStateMachine)>
        CreateReadyCoordinator(
            TimeProvider? timeProvider = null,
            DateTimeOffset? frameNow = null,
            IKeySink? keySink = null,
            bool detectExternalActionConflicts = true,
            string? profileJson = null)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var pub = new SnapshotPublisher(clock);
        var profiles = await CreateProfileService("test-profile");
        WriteProfileFile(profileJson ?? CreateValidProfileJson());
        await profiles.ReloadAsync();
        profiles.SetActiveProfile("test-profile");
        pub.Publish(CreateHealthyFrame(now: frameNow ?? clock.GetUtcNow()));
        var readiness = new ArmingReadinessService(pub, profiles, clock);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = CreateConfig(detectExternalActionConflicts);
        var bindings = new BindingVerificationTracker();
        var coord = new ActionCoordinator(
            pub, sm, profiles, readiness, config,
            keySink: keySink ?? new TestLiveKeySink(),
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

    private static IConfigurationRoot CreateConfig(bool detectExternalActionConflicts = true)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
                ["BotDs:Action:DetectExternalActionConflicts"] = detectExternalActionConflicts ? "true" : "false",
            })
            .Build();
    }

    private static EvaluationResult CreateHealthyResult(string actionKey = "1", ulong sequence = 100)
    {
        return new EvaluationResult(
            ControllerState.Armed,
            new ActionDecision(
                "r1", "slice", "1001", actionKey, AcknowledgementKind.Cooldown, sequence,
                ProviderSessionId: "session-1",
                SourceGeneration: 1,
                TargetId: "target-1",
                AttachmentProcessId: 4242),
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
                SessionId: sessionId ?? "session-1",
                Sequence: sequence,
                ProducerFrameMilliseconds: 16,
                ReceivedAtUtc: receivedAt,
                Age: TimeSpan.FromMilliseconds(10),
                SourceGeneration: 1,
                AttachmentProcessId: 4242),
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
            IsTargetAurasKnown: true,
            TargetKnownness: TargetKnownness.KnownTarget,
            GameInputReady: true);
    }

    private async Task AssertLiveFenceRejects(
        Func<TelemetryFrame, TelemetryFrame> mutate,
        bool fatal)
    {
        var sink = new TestLiveKeySink();
        var (coord, profiles, pub, _, sm) = await CreateReadyCoordinator(
            keySink: sink,
            detectExternalActionConflicts: false);
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();

        TelemetryFrame decisionFrame = CreateHealthyFrame(sequence: 100);
        pub.Publish(decisionFrame);
        ActionDecision decision = new CombatEvaluator(MaxAge).Evaluate(
            profiles.ActiveProfile!, decisionFrame).Action!;
        pub.Publish(mutate(decisionFrame));

        DispatchRecord? record = coord.Consume(
            new EvaluationResult(ControllerState.Armed, decision, []), sm.Generation);

        Assert.NotNull(record);
        Assert.Equal(DispatchOutcome.RevalidationFailed, record!.Outcome);
        Assert.Equal(0, sink.DispatchCount);
        Assert.Null(coord.PendingAction);
        if (fatal)
        {
            Assert.Equal(ControllerState.Stopped, sm.State);
            Assert.Equal(OutputMode.Disabled, coord.Mode);
        }
        else
        {
            Assert.NotEqual(ControllerState.Stopped, sm.State);
            Assert.Equal(OutputMode.Live, coord.Mode);
        }
    }

    private static TelemetryFrame NextSequence(TelemetryFrame frame) => frame with
    {
        Provider = frame.Provider with { Sequence = frame.Provider.Sequence + 1 },
    };

    private static TelemetryFrame WithAbility(
        TelemetryFrame frame,
        Func<AbilityState, AbilityState> mutate)
    {
        var abilities = frame.Abilities.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == "1001" ? mutate(pair.Value) : pair.Value,
            StringComparer.OrdinalIgnoreCase);
        return frame with
        {
            Abilities = new ReadOnlyDictionary<string, AbilityState>(abilities),
        };
    }

    private static string CreateTwoRuleProfileJson()
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
            rules = new object[]
            {
                new
                {
                    id = "low-health",
                    ability = "slice",
                    enabled = true,
                    when = new { playerHealthBelowPercent = 50 },
                },
                new { id = "fallback", ability = "slice", enabled = true },
            },
        });
    }

    private sealed class TestHostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
