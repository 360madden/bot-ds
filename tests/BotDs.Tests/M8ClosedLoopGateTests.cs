using System.Collections.ObjectModel;
using BotDs.App.Services;
using BotDs.Core;
using BotDs.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

/// <summary>
/// M8 closed-loop acceptance criteria 1–3: drive real ActionCoordinator /
/// ControllerStateMachine / EmergencyHotkeyHostedService entry points.
/// </summary>
public sealed class M8ClosedLoopGateTests : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(500);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"botds-m8-{Guid.NewGuid():N}");

    public M8ClosedLoopGateTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Startup_output_mode_is_Disabled()
    {
        var (coord, _, _, _, _) = await CreatePipeline();
        Assert.Equal(OutputMode.Disabled, coord.Mode);
    }

    [Fact]
    public async Task Live_blocked_without_registered_emergency_hotkey()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        // Intentionally NOT registered
        var (coord, _, _, _, _) = await CreatePipeline(hotkey: hotkey, requireHotkey: true);
        coord.Bindings.MarkVerified("slice");

        Assert.False(hotkey.IsRegistered);
        Assert.False(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Equal(OutputMode.Disabled, coord.Mode);
    }

    [Fact]
    public async Task Live_allowed_when_hotkey_registered_and_bindings_verified()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        Assert.True(hotkey.TryRegister(static () => { }));
        var (coord, _, _, _, _) = await CreatePipeline(hotkey: hotkey, requireHotkey: true);
        coord.Bindings.MarkVerified("slice");

        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Equal(OutputMode.Live, coord.Mode);
    }

    [Fact]
    public async Task Profile_key_colliding_with_emergency_hotkey_blocks_DryRun_and_Live()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        Assert.True(hotkey.TryRegister(static () => { }));
        var (coord, _, _, _, _) = await CreatePipeline(
            hotkey: hotkey,
            abilityKey: "Ctrl+Shift+F12");

        Assert.False(coord.TrySetMode(OutputMode.DryRun, MaxAge));
        Assert.False(coord.TrySetMode(OutputMode.Live, MaxAge));
        Assert.Equal(OutputMode.Disabled, coord.Mode);
    }

    [Fact]
    public async Task Unrelated_ability_cooldown_does_not_acknowledge_pending_action()
    {
        var (coord, _, pub, _, sm) = await CreateReadyDryRun();
        sm.Arm();

        TelemetryFrame pre = Frame(seq: 20, cooldownMs: 0, otherCooldownMs: 0);
        pub.Publish(pre);
        var dispatch = coord.Consume(
            new EvaluationResult(
                ControllerState.Armed,
                new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 20),
                []),
            sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, dispatch!.Outcome);
        Assert.NotNull(coord.PendingAction);

        // Different ability enters cooldown — must NOT ack slice.
        TelemetryFrame unrelated = Frame(
            seq: 21,
            session: pre.Provider.SessionId,
            cooldownMs: 0,
            otherCooldownMs: 2000);
        pub.Publish(unrelated);
        DispatchRecord? obs = coord.ObservePending(unrelated, sm.Generation);
        Assert.True(obs is null || obs.Outcome != DispatchOutcome.Acknowledged);
        Assert.NotNull(coord.PendingAction);
    }

    [Fact]
    public async Task Matching_cooldown_acknowledges_and_clears_pending()
    {
        var (coord, _, pub, _, sm) = await CreateReadyDryRun();
        sm.Arm();

        TelemetryFrame pre = Frame(seq: 30, cooldownMs: 0);
        pub.Publish(pre);
        Assert.Equal(
            DispatchOutcome.Dispatched,
            coord.Consume(
                new EvaluationResult(
                    ControllerState.Armed,
                    new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 30),
                    []),
                sm.Generation)!.Outcome);

        TelemetryFrame post = Frame(seq: 31, session: pre.Provider.SessionId, cooldownMs: 1500);
        pub.Publish(post);
        DispatchRecord? ack = coord.ObservePending(post, sm.Generation);
        Assert.NotNull(ack);
        Assert.Equal(DispatchOutcome.Acknowledged, ack!.Outcome);
        Assert.Null(coord.PendingAction);
        Assert.Equal(BindingVerificationState.Verified, coord.Bindings.GetState("slice"));
    }

    [Fact]
    public async Task Target_identity_change_invalidates_pending_action()
    {
        var (coord, _, pub, _, sm) = await CreateReadyDryRun();
        sm.Arm();

        TelemetryFrame pre = Frame(seq: 40, cooldownMs: 0, targetId: "mob-a");
        pub.Publish(pre);
        Assert.Equal(
            DispatchOutcome.Dispatched,
            coord.Consume(
                new EvaluationResult(
                    ControllerState.Armed,
                    new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 40),
                    []),
                sm.Generation)!.Outcome);

        TelemetryFrame switched = Frame(
            seq: 41,
            session: pre.Provider.SessionId,
            cooldownMs: 1500,
            targetId: "mob-b");
        pub.Publish(switched);
        DispatchRecord? inv = coord.ObservePending(switched, sm.Generation);
        Assert.NotNull(inv);
        Assert.Equal(DispatchOutcome.PendingInvalidated, inv!.Outcome);
        Assert.Null(coord.PendingAction);
    }

    [Fact]
    public async Task Provider_session_change_invalidates_pending_action()
    {
        var (coord, _, pub, _, sm) = await CreateReadyDryRun();
        sm.Arm();

        TelemetryFrame pre = Frame(seq: 50, cooldownMs: 0, session: "session-a");
        pub.Publish(pre);
        Assert.Equal(
            DispatchOutcome.Dispatched,
            coord.Consume(
                new EvaluationResult(
                    ControllerState.Armed,
                    new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 50),
                    []),
                sm.Generation)!.Outcome);

        TelemetryFrame reloaded = Frame(seq: 1, cooldownMs: 1500, session: "session-b");
        pub.Publish(reloaded);
        DispatchRecord? inv = coord.ObservePending(reloaded, sm.Generation);
        Assert.NotNull(inv);
        Assert.Equal(DispatchOutcome.PendingInvalidated, inv!.Outcome);
        Assert.Null(coord.PendingAction);
    }

    [Fact]
    public async Task Pending_action_blocks_second_combat_key_dispatch()
    {
        var (coord, _, pub, _, sm) = await CreateReadyDryRun();
        sm.Arm();
        pub.Publish(Frame(seq: 60, cooldownMs: 0));

        var first = coord.Consume(
            new EvaluationResult(
                ControllerState.Armed,
                new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 60),
                []),
            sm.Generation);
        Assert.Equal(DispatchOutcome.Dispatched, first!.Outcome);

        var second = coord.Consume(
            new EvaluationResult(
                ControllerState.Armed,
                new ActionDecision("r1", "slice", "1001", "2", AcknowledgementKind.Cooldown, 61),
                []),
            sm.Generation);
        Assert.Equal(DispatchOutcome.PendingActionBlocked, second!.Outcome);
    }

    [Fact]
    public async Task Emergency_hotkey_host_latches_Stopped_and_disables_output()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        var (coord, _, pub, readiness, sm) = await CreatePipeline(hotkey: hotkey, requireHotkey: true);
        Assert.True(hotkey.TryRegister(static () => { })); // will be re-registered by host
        coord.Bindings.MarkVerified("slice");
        Assert.True(coord.TrySetMode(OutputMode.Live, MaxAge));
        sm.Arm();
        pub.Publish(Frame(seq: 70, cooldownMs: 0));
        Assert.Equal(
            DispatchOutcome.Dispatched,
            coord.Consume(
                new EvaluationResult(
                    ControllerState.Armed,
                    new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 70),
                    []),
                sm.Generation)!.Outcome);
        Assert.NotNull(coord.PendingAction);

        var services = new ServiceCollection();
        services.AddSingleton<IEmergencyHotkey>(hotkey);
        services.AddSingleton(sm);
        services.AddSingleton(coord);
        services.AddSingleton<ILogger<EmergencyHotkeyHostedService>>(
            NullLogger<EmergencyHotkeyHostedService>.Instance);
        services.AddSingleton<EmergencyHotkeyHostedService>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        var host = sp.GetRequiredService<EmergencyHotkeyHostedService>();
        await host.StartAsync(CancellationToken.None);

        hotkey.SimulateTrigger();

        Assert.Equal(ControllerState.Stopped, sm.State);
        Assert.Equal(StopReason.EmergencyStop, sm.Snapshot.StopReason);
        Assert.Equal(OutputMode.Disabled, coord.Mode);
        Assert.Null(coord.PendingAction);

        await host.StopAsync(CancellationToken.None);
    }

    // ── Pipeline helpers ─────────────────────────────────────

    private async Task<(ActionCoordinator, ProfileService, SnapshotPublisher, ArmingReadinessService, ControllerStateMachine)>
        CreateReadyDryRun()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        Assert.True(hotkey.TryRegister(static () => { }));
        var pipeline = await CreatePipeline(hotkey: hotkey, requireHotkey: false);
        Assert.True(pipeline.Item1.TrySetMode(OutputMode.DryRun, MaxAge));
        return pipeline;
    }

    private async Task<(ActionCoordinator, ProfileService, SnapshotPublisher, ArmingReadinessService, ControllerStateMachine)>
        CreatePipeline(
            IEmergencyHotkey? hotkey = null,
            bool requireHotkey = true,
            string abilityKey = "1")
    {
        var time = new FakeTimeProvider();
        var pub = new SnapshotPublisher(time);
        WriteProfile(abilityKey);
        var profiles = await LoadProfiles();
        pub.Publish(Frame(seq: 1, cooldownMs: 0, now: time.GetUtcNow()));

        var readiness = new ArmingReadinessService(pub, profiles, time);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Action:RequireBindingVerificationForLive"] = "true",
                ["BotDs:Action:RequireEmergencyHotkeyForLive"] = requireHotkey ? "true" : "false",
                ["BotDs:Action:DetectExternalActionConflicts"] = "true",
                ["BotDs:Action:EmergencyHotkey"] = "Ctrl+Shift+F12",
            })
            .Build();

        IEmergencyHotkey key = hotkey ?? CreateRegisteredFake();
        var coord = new ActionCoordinator(
            pub, sm, profiles, readiness, config,
            keySink: new FakeKeySink(),
            timeProvider: time,
            emergencyHotkey: key);
        return (coord, profiles, pub, readiness, sm);
    }

    private static FakeEmergencyHotkey CreateRegisteredFake()
    {
        var fake = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        _ = fake.TryRegister(static () => { });
        return fake;
    }

    private void WriteProfile(string abilityKey)
    {
        string json = $$"""
            {
              "id": "test-profile",
              "profileVersion": 1,
              "enabled": true,
              "character": { "calling": "Warrior", "minimumLevel": 1, "maximumLevel": 60 },
              "abilities": {
                "slice": { "abilityId": "1001", "key": "{{abilityKey}}", "enabled": true, "required": true }
              },
              "rules": [
                { "id": "r1", "ability": "slice", "enabled": true, "acknowledgement": "Cooldown" }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(_tempDir, "test-profile.json"), json);
    }

    private async Task<ProfileService> LoadProfiles()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = _tempDir,
            })
            .Build();
        var svc = new ProfileService(config, new HostEnv(_tempDir), new NullLogger<ProfileService>());
        ProfileReloadResult result = await svc.ReloadAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(svc.SetActiveProfile("test-profile"));
        return svc;
    }

    private static TelemetryFrame Frame(
        ulong seq,
        int? cooldownMs,
        int? otherCooldownMs = null,
        string? session = null,
        string? targetId = "target-1",
        DateTimeOffset? now = null)
    {
        DateTimeOffset t = now ?? DateTimeOffset.UtcNow;
        var abilities = new Dictionary<string, AbilityState>
        {
            ["1001"] = new AbilityState(
                "1001", "Slice", true, true, true, cooldownMs, 1500, null,
                new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()),
                0, false, false),
            ["2002"] = new AbilityState(
                "2002", "Other", true, true, true, otherCooldownMs ?? 0, 1500, null,
                new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()),
                0, false, false),
        };

        return new TelemetryFrame(
            Provider: new ProviderStatus(
                ProviderHealth.Healthy, "5", session ?? "session-1", seq, 16, t,
                TimeSpan.FromMilliseconds(5), SourceGeneration: 1),
            Player: new UnitState(
                "player-1", "Test", 45, "Warrior", true, "friendly",
                new HealthState(5000, 5000), new ResourceState("Power", 100, 100), true, null),
            Target: new UnitState(
                targetId, "Mob", 45, null, false, "hostile",
                new HealthState(3000, 3000), null, true, null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(abilities),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }

    private sealed class HostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
