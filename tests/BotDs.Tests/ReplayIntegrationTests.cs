using System.Collections.ObjectModel;
using System.Text.Json;
using BotDs.App.Services;
using BotDs.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

/// <summary>
/// Replay-based integration tests that load a ReplayEnvelope fixture,
/// feed each frame through the evaluator and coordinator, and assert
/// deterministic dispatch outcomes.
/// </summary>
public sealed class ReplayIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ReplayIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botds-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Replay_combat_cycle_produces_expected_dispatch_outcomes()
    {
        // Build a replay envelope with a simple combat cycle:
        // Tick 0: Player has target, ability ready → should dispatch
        // Tick 1: Cooldown active → no dispatch
        // Tick 2: Cooldown expired → should dispatch
        var envelope = BuildCombatCycleEnvelope();

        // Set up the evaluator + coordinator pipeline
        var (coordinator, sm, pub, time) = await CreateReplayPipeline();

        // Publish initial frame so readiness check passes
        pub.Publish(envelope.Frames[0].Snapshot.ToFrame());

        // Arm the coordinator
        Assert.True(coordinator.TrySetMode(OutputMode.DryRun, TimeSpan.FromMilliseconds(500)));
        sm.Arm();

        var results = new List<(long tick, DispatchRecord? record)>();
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var profile = CreateWarriorProfile();

        long lastElapsedMs = 0;

        // Feed each frame through the pipeline
        foreach (var replayFrame in envelope.Frames)
        {
            long delta = replayFrame.ElapsedMs - lastElapsedMs;
            if (delta > 0) time.Advance(TimeSpan.FromMilliseconds(delta));
            lastElapsedMs = replayFrame.ElapsedMs;
            // Apply commands at this tick
            foreach (var cmd in replayFrame.Commands)
            {
                ApplyCommand(cmd, sm, coordinator);
            }

            // Convert replay frame to TelemetryFrame and publish
            var telemetryFrame = replayFrame.Snapshot.ToFrame();
            pub.Publish(telemetryFrame);

            // Evaluate
            var evalResult = evaluator.Evaluate(profile, telemetryFrame);
            sm.ApplyEvaluation(evalResult, sm.Generation);

            // Advance time past the dispatch window
            if (replayFrame.Tick == 0) time.Advance(TimeSpan.FromMilliseconds(50));

            // Dispatch through coordinator
            var dispatch = coordinator.Consume(evalResult, sm.Generation);
            results.Add((replayFrame.Tick, dispatch));

            // Verify expected dispatch
            if (replayFrame.ExpectedDispatch is not null)
            {
                Assert.NotNull(dispatch);
                Assert.Equal(DispatchOutcome.Dispatched, dispatch!.Outcome);
                Assert.Equal(replayFrame.ExpectedDispatch.RuleId, dispatch.RuleId);
                Assert.Equal(replayFrame.ExpectedDispatch.AbilityId, dispatch.AbilityId);
                Assert.Equal(replayFrame.ExpectedDispatch.Key, dispatch.Key);
            }
            else
            {
                // No dispatch expected — should be null or blocked
                Assert.True(dispatch is null || dispatch.Outcome != DispatchOutcome.Dispatched,
                    $"Tick {replayFrame.Tick}: expected no dispatch, got {dispatch?.Outcome}");
            }
        }

        // Verify all frames were processed
        Assert.Equal(4, results.Count);
        Assert.Equal(DispatchOutcome.Dispatched, results[0].record!.Outcome); // First fireball
        Assert.True(results[1].record is null || results[1].record!.Outcome == DispatchOutcome.PendingActionBlocked); // Cooldown = no new dispatch
        Assert.True(results[2].record is null); // Still in cooldown, evaluator produces no action
        Assert.Equal(DispatchOutcome.Dispatched, results[3].record!.Outcome); // Cooldown expired → second fireball
    }

    [Fact]
    public async Task Replay_determinism_produces_identical_results_on_rerun()
    {
        var envelope = BuildCombatCycleEnvelope();

        async Task<List<string>> RunReplay()
        {
            var (coordinator, sm, pub, time) = await CreateReplayPipeline();
            pub.Publish(envelope.Frames[0].Snapshot.ToFrame());
            Assert.True(coordinator.TrySetMode(OutputMode.DryRun, TimeSpan.FromMilliseconds(500)));
            sm.Arm();

            var outcomes = new List<string>();
            var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
            var profile = CreateWarriorProfile();

            long lastElapsedMs = 0;

            foreach (var frame in envelope.Frames)
            {
                long delta = frame.ElapsedMs - lastElapsedMs;
                if (delta > 0) time.Advance(TimeSpan.FromMilliseconds(delta));
                lastElapsedMs = frame.ElapsedMs;

                var telemetryFrame = frame.Snapshot.ToFrame();
                pub.Publish(telemetryFrame);
                var evalResult = evaluator.Evaluate(profile, telemetryFrame);
                sm.ApplyEvaluation(evalResult, sm.Generation);

                if (frame.Tick == 0) time.Advance(TimeSpan.FromMilliseconds(50));

                var dispatch = coordinator.Consume(evalResult, sm.Generation);
                outcomes.Add($"{frame.Tick}:{dispatch?.Outcome ?? DispatchOutcome.NotArmed}:{dispatch?.RuleId ?? "none"}");
            }
            return outcomes;
        }

        var run1 = await RunReplay();
        var run2 = await RunReplay();

        Assert.Equal(run1, run2);
    }

    [Fact]
    public async Task Replay_handles_profile_mismatch_gracefully()
    {
        var envelope = BuildCombatCycleEnvelope();
        var (coordinator, sm, pub, time) = await CreateReplayPipeline();

        pub.Publish(envelope.Frames[0].Snapshot.ToFrame());

        Assert.True(coordinator.TrySetMode(OutputMode.DryRun, TimeSpan.FromMilliseconds(500)));
        sm.Arm();

        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        // Use a Mage profile against Warrior frame → should stop
        var mageProfile = CreateMageProfile();
        var frame = envelope.Frames[0].Snapshot.ToFrame();
        var result = evaluator.Evaluate(mageProfile, frame);

        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    // ---- Fixture builders ----

    private static ReplayEnvelope BuildCombatCycleEnvelope()
    {
        var frames = new List<ReplayFrame>();

        // Tick 0: Target acquired, ability ready
        frames.Add(new ReplayFrame
        {
            Tick = 0,
            ElapsedMs = 0,
            Snapshot = BuildSnapshot(100, 0, 100),
            ExpectedDispatch = new ReplayDispatch
            {
                RuleId = "fireball-main", AbilityAlias = "fireball",
                AbilityId = "1001", Key = "1",
            },
        });

        // Tick 1: Cooldown active (ability just used)
        frames.Add(new ReplayFrame
        {
            Tick = 1,
            ElapsedMs = 100,
            Snapshot = BuildSnapshot(101, 1500, 90),
            ExpectedDispatch = null, // No dispatch — cooldown active
        });

        // Tick 2: Cooldown still active, tick passes
        frames.Add(new ReplayFrame
        {
            Tick = 2,
            ElapsedMs = 200,
            Snapshot = BuildSnapshot(102, 800, 80),
            ExpectedDispatch = null, // No dispatch — cooldown not ready
        });

        // Tick 3: Cooldown expired
        frames.Add(new ReplayFrame
        {
            Tick = 3,
            ElapsedMs = 2500,
            Snapshot = BuildSnapshot(103, 0, 70),
            ExpectedDispatch = new ReplayDispatch
            {
                RuleId = "fireball-main", AbilityAlias = "fireball",
                AbilityId = "1001", Key = "1",
            },
        });

        return ReplayEnvelope.Create(frames);
    }

    private static ReplaySnapshot BuildSnapshot(ulong seq, int cooldownMs, double healthPct)
    {
        int maxHp = 5000;
        int curHp = (int)(maxHp * healthPct / 100.0);

        return new ReplaySnapshot
        {
            Provider = new ReplayProviderStatus
            {
                Health = ProviderHealth.Healthy,
                Sequence = seq,
                ProducerFrameMilliseconds = 16,
                AgeMs = 10,
            },
            Player = new ReplayUnitState
            {
                Id = "player-1", Name = "TestWarrior", Level = 50,
                Calling = "Warrior", IsPlayer = true, Relation = "friendly",
                CurrentHealth = 5000, MaxHealth = 5000,
                ResourceKind = "Power", ResourceCurrent = 100, ResourceMax = 100,
                InCombat = true,
            },
            Target = new ReplayUnitState
            {
                Id = "target-1", Name = "TestMob", Level = 50,
                Calling = null, IsPlayer = false, Relation = "hostile",
                CurrentHealth = curHp, MaxHealth = maxHp,
                InCombat = true,
            },
            Abilities = new Dictionary<string, ReplayAbilityState>
            {
                ["1001"] = new ReplayAbilityState
                {
                    Id = "1001", Available = true, Usable = true,
                    InRange = true, CooldownRemainingMs = cooldownMs,
                },
            },
        };
    }

    private static CombatProfile CreateWarriorProfile()
    {
        return new CombatProfile
        {
            Id = "test-profile",
            Enabled = true,
            Character = new CharacterRequirements
            {
                Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 60,
            },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["fireball"] = new AbilityBinding
                {
                    AbilityId = "1001", Key = "1", Enabled = true, Required = true,
                },
            },
            Rules = new List<CombatRule>
            {
                new()
                {
                    Id = "fireball-main", Ability = "fireball", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, CooldownReady = true, ResourceAtLeast = 20,
                    },
                },
            },
        };
    }

    private static CombatProfile CreateMageProfile()
    {
        return new CombatProfile
        {
            Id = "mage-profile",
            Enabled = true,
            Character = new CharacterRequirements
            {
                Calling = "Mage", MinimumLevel = 1, MaximumLevel = 60,
            },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["fireball"] = new AbilityBinding
                {
                    AbilityId = "1001", Key = "1", Enabled = true,
                },
            },
            Rules = new List<CombatRule>
            {
                new()
                {
                    Id = "fireball-main", Ability = "fireball", Enabled = true,
                    When = new RuleConditions { TargetHostile = true },
                },
            },
        };
    }

    private async Task<(ActionCoordinator coordinator, ControllerStateMachine sm, SnapshotPublisher pub, FakeTimeProvider time)> CreateReplayPipeline()
    {
        var time = new FakeTimeProvider();
        var pub = new SnapshotPublisher();
        var profiles = await CreateProfileService("test-profile");

        // Write the profile JSON so it loads
        WriteProfileFile();
        await profiles.ReloadAsync();
        profiles.SetActiveProfile("test-profile");

        var readiness = new ArmingReadinessService(pub, profiles);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
            })
            .Build();

        var coordinator = new ActionCoordinator(pub, sm, profiles, readiness, config, time);
        return (coordinator, sm, pub, time);
    }

    private void WriteProfileFile()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "test-profile",
            profileVersion = 1,
            enabled = true,
            character = new { calling = "Warrior", minimumLevel = 1, maximumLevel = 60 },
            abilities = new Dictionary<string, object>
            {
                ["fireball"] = new { abilityId = "1001", key = "1", enabled = true, required = true },
            },
            rules = new[]
            {
                new
                {
                    id = "fireball-main", ability = "fireball", enabled = true,
                    when = new { targetHostile = true, cooldownReady = true, resourceAtLeast = 20 },
                },
            },
        });

        File.WriteAllText(Path.Combine(_tempDir, "test-profile.json"), json);
    }

    private async Task<ProfileService> CreateProfileService(string activeId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = _tempDir,
            })
            .Build();
        var svc = new ProfileService(config, new TestHostEnv(_tempDir), new NullLogger<ProfileService>());
        await svc.ReloadAsync();
        if (activeId is not null)
            svc.SetActiveProfile(activeId);
        return svc;
    }

    private static void ApplyCommand(ReplayCommand cmd, ControllerStateMachine sm, ActionCoordinator coord)
    {
        switch (cmd.Kind)
        {
            case ReplayCommandKind.Arm:
                sm.Arm();
                break;
            case ReplayCommandKind.Disarm:
                sm.Disarm();
                coord.CancelPending("Replay command: Disarm");
                break;
            case ReplayCommandKind.EmergencyStop:
                sm.EmergencyStop(cmd.Detail);
                coord.CancelPending("Replay command: EStop");
                break;
            case ReplayCommandKind.ClearStop:
                sm.ClearStop();
                break;
        }
    }

    private sealed class TestHostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
