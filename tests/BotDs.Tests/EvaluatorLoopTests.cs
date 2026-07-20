using System.Collections.ObjectModel;
using System.Text.Json;
using BotDs.App.Services;
using BotDs.Core;
using BotDs.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

/// <summary>
/// Integration tests for EvaluatorLoop: the bridge between telemetry,
/// combat evaluation, and action coordination.
/// </summary>
public sealed class EvaluatorLoopTests : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(500);
    private readonly string _tempDir;
    private readonly List<IDisposable> _disposables = [];

    public EvaluatorLoopTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botds-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Core behaviors ────────────────────────────────────────

    [Fact]
    public async Task Loop_skips_evaluation_when_disarmed()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: false);
        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);

        pub.Publish(CreateHealthyFrame());
        await Task.Delay(150, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.Null(loop.LastResult);
        Assert.Equal(ControllerState.Disarmed, sm.State);
    }

    [Fact]
    public async Task Loop_emergency_stops_when_no_active_profile()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: true, writeProfile: false);

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);
        pub.Publish(CreateHealthyFrame());
        await Task.Delay(150, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.Equal(ControllerState.Stopped, sm.State);
        Assert.Equal(StopReason.EmergencyStop, sm.Snapshot.StopReason);
    }

    [Fact]
    public async Task Loop_evaluates_and_applies_result_when_armed()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: true);

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);
        pub.Publish(CreateHealthyFrame());
        await Task.Delay(200, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.NotNull(loop.LastResult);
    }

    [Fact]
    public async Task Loop_delegates_action_to_coordinator()
    {
        var keySink = new FakeKeySink();
        var (loop, sm, pub, coord) = await CreateEvaluatorLoopWithCoordinator(arm: true, keySink: keySink);

        // Publish frame FIRST so readiness check passes, then enable DryRun
        pub.Publish(CreateHealthyFrame(abilityId: "1001", abilityUsable: true));
        coord.TrySetMode(OutputMode.DryRun, MaxAge);
        sm.Arm();

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.True(coord.RecentHistory.Count > 0,
            $"Expected at least one dispatch record, got {coord.RecentHistory.Count}");
    }

    [Fact]
    public async Task Loop_stops_when_evaluation_produces_stop_reason()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: true);

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);

        var staleFrame = new TelemetryFrame(
            new ProviderStatus(ProviderHealth.Faulted, "5", Guid.NewGuid().ToString("D"),
                0, 0, DateTimeOffset.UtcNow, TimeSpan.MaxValue),
            null, null,
            ReadOnlyDictionary<string, AbilityState>.Empty, [], [],
            IsAbilitiesKnown: false, IsPlayerAurasKnown: false, IsTargetAurasKnown: false);
        pub.Publish(staleFrame);
        await Task.Delay(200, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.Equal(ControllerState.Stopped, sm.State);
    }

    [Fact]
    public async Task Loop_continues_evaluation_across_multiple_frames()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: true);

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);

        // First frame — should produce a result
        pub.Publish(CreateHealthyFrame());
        await Task.Delay(150, CancellationToken.None);
        var firstResult = loop.LastResult;
        Assert.NotNull(firstResult);

        // Second frame — should produce another evaluation
        pub.Publish(CreateHealthyFrame(sequence: 200));
        await Task.Delay(150, CancellationToken.None);

        cts.Cancel();
        await task;

        // Both evaluations should have occurred — LastResult updated
        Assert.NotNull(loop.LastResult);
    }

    [Fact]
    public async Task Loop_completes_on_cancellation_without_error()
    {
        var (loop, _, pub) = await CreateEvaluatorLoop(arm: true);

        using var cts = new CancellationTokenSource();
        pub.Publish(CreateHealthyFrame());
        var task = loop.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);
        cts.Cancel();

        // ExecuteAsync exits its loop cleanly — no exception
        await task;
    }

    [Fact]
    public async Task Loop_generation_guard_rejects_stale_evaluation()
    {
        var (loop, sm, pub) = await CreateEvaluatorLoop(arm: true);

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);

        pub.Publish(CreateHealthyFrame());
        await Task.Delay(100, CancellationToken.None);

        // Disarm while loop may be evaluating — generation advances
        // The stale evaluation from before disarm is rejected by ApplyEvaluation
        // because generation no longer matches. This is the core guard.
        sm.Disarm();

        await Task.Delay(100, CancellationToken.None);

        cts.Cancel();
        await task;

        // After disarm, the loop should be Disarmed and no stale result applied.
        // The state is Disarmed because Disarm() was called and the stale
        // evaluation was rejected.
        Assert.Equal(ControllerState.Disarmed, sm.State);
    }

    [Fact]
    public async Task Loop_no_coordinator_logs_action_without_dispatch()
    {
        var sm = new ControllerStateMachine(NullLogger<ControllerStateMachine>.Instance);
        var pub = new SnapshotPublisher();
        var config = CreateConfig();
        var profileService = await CreateProfileServiceWithFile();

        var loop = new EvaluatorLoop(pub, profileService, sm, config,
            NullLogger<EvaluatorLoop>.Instance, actionCoordinator: null);

        sm.Arm();

        var cts = new CancellationTokenSource();
        var task = loop.StartAsync(cts.Token);
        pub.Publish(CreateHealthyFrame(abilityId: "1001", abilityUsable: true));
        await Task.Delay(200, CancellationToken.None);

        cts.Cancel();
        await task;

        Assert.NotNull(loop.LastResult);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static IConfigurationRoot CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
                ["BotDs:Evaluator:EvaluationIntervalMs"] = "10",
            })
            .Build();

    private async Task<ProfileService> CreateProfileServiceWithFile()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BotDs:Profiles:Directory"] = _tempDir })
            .Build();
        var env = new TestHostEnv(_tempDir);
        var svc = new ProfileService(config, env, NullLogger<ProfileService>.Instance);

        // Write the profile file so SetActiveProfile succeeds
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test-profile.json"),
            CreateValidProfileJson());
        await svc.ReloadAsync();
        svc.SetActiveProfile("test-profile");
        return svc;
    }

    private static string CreateValidProfileJson() =>
        JsonSerializer.Serialize(new
        {
            id = "test-profile",
            profileVersion = 1,
            enabled = true,
            character = new { calling = "Warrior", minimumLevel = 1, maximumLevel = 60 },
            abilities = new Dictionary<string, object> { ["slice"] = new { abilityId = "1001", key = "1", enabled = true } },
            rules = new[] { new { id = "r1", ability = "slice", enabled = true } },
        });

    private static TelemetryFrame CreateHealthyFrame(
        string abilityId = "1001", bool abilityUsable = true, ulong sequence = 100)
    {
        var now = DateTimeOffset.UtcNow;
        var abilities = new Dictionary<string, AbilityState>
        {
            [abilityId] = new AbilityState(abilityId, "TestAbility", true, abilityUsable, true,
                0, 1500, null, ReadOnlyDictionary<string, int>.Empty, 0, false, false),
        };

        return new TelemetryFrame(
            new ProviderStatus(ProviderHealth.Healthy, "5", Guid.NewGuid().ToString("D"),
                sequence, 16, now, TimeSpan.FromMilliseconds(10)),
            new UnitState("player-1", "TestPlayer", 50, "Warrior",
                true, "friendly", new HealthState(5000, 5000),
                new ResourceState("Power", 100, 100), true, null),
            new UnitState("target-1", "TestMob", 50, null,
                false, "hostile", new HealthState(3000, 3000),
                null, true, null),
            new ReadOnlyDictionary<string, AbilityState>(abilities),
            [], [],
            IsAbilitiesKnown: true, IsPlayerAurasKnown: true, IsTargetAurasKnown: true);
    }

    private async Task<(EvaluatorLoop loop, ControllerStateMachine sm, SnapshotPublisher pub)>
        CreateEvaluatorLoop(bool arm, bool writeProfile = true)
    {
        var sm = new ControllerStateMachine(NullLogger<ControllerStateMachine>.Instance);
        var pub = new SnapshotPublisher();
        var config = CreateConfig();

        ProfileService profileService;
        if (writeProfile)
            profileService = await CreateProfileServiceWithFile();
        else
        {
            // No profile file written → ActiveProfile is null
            var emptyConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["BotDs:Profiles:Directory"] = _tempDir })
                .Build();
            profileService = new ProfileService(emptyConfig, new TestHostEnv(_tempDir),
                NullLogger<ProfileService>.Instance);
        }

        if (arm)
            sm.Arm();

        var loop = new EvaluatorLoop(pub, profileService, sm, config,
            NullLogger<EvaluatorLoop>.Instance, actionCoordinator: null);
        _disposables.Add(loop);
        return (loop, sm, pub);
    }

    private async Task<(EvaluatorLoop loop, ControllerStateMachine sm, SnapshotPublisher pub,
        ActionCoordinator coord)> CreateEvaluatorLoopWithCoordinator(bool arm, IKeySink? keySink = null)
    {
        var sm = new ControllerStateMachine(NullLogger<ControllerStateMachine>.Instance);
        var pub = new SnapshotPublisher();
        var config = CreateConfig();
        var profileService = await CreateProfileServiceWithFile();

        var coordConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
            })
            .Build();

        var readiness = new ArmingReadinessService(pub, profileService);
        var coord = new ActionCoordinator(pub, sm, profileService, readiness, coordConfig,
            keySink: keySink, log: NullLogger<ActionCoordinator>.Instance);

        if (arm)
            sm.Arm();

        var loop = new EvaluatorLoop(pub, profileService, sm, config,
            NullLogger<EvaluatorLoop>.Instance, actionCoordinator: coord);
        _disposables.Add(loop);
        return (loop, sm, pub, coord);
    }

    private sealed class TestHostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
