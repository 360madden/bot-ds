using System.Collections.ObjectModel;
using BotDs.App.Services;
using BotDs.Core;
using BotDs.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

public sealed class ExternalActionConflictTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"botds-ext-{Guid.NewGuid():N}");

    public ExternalActionConflictTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Detector_flags_unexplained_cooldown_transition()
    {
        CombatProfile profile = Profile();
        TelemetryFrame prev = Frame(seq: 1, cooldownMs: 0);
        TelemetryFrame curr = Frame(seq: 2, session: prev.Provider.SessionId, cooldownMs: 1500);

        Assert.True(ExternalActionConflictDetector.TryDetect(
            profile, prev, curr, pendingAction: null, out string detail));
        Assert.Contains("External action conflict", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detector_ignores_cooldown_for_pending_botds_action()
    {
        CombatProfile profile = Profile();
        TelemetryFrame prev = Frame(seq: 1, cooldownMs: 0);
        TelemetryFrame curr = Frame(seq: 2, session: prev.Provider.SessionId, cooldownMs: 1500);
        var pending = new ActionDecision("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 1);

        Assert.False(ExternalActionConflictDetector.TryDetect(
            profile, prev, curr, pending, out _));
    }

    [Fact]
    public async Task Coordinator_stops_on_external_action_while_armed()
    {
        var time = new FakeTimeProvider();
        var pub = new SnapshotPublisher(time);
        var profiles = await CreateProfiles();
        pub.Publish(Frame(seq: 10, cooldownMs: 0, now: time.GetUtcNow()));

        var readiness = new ArmingReadinessService(pub, profiles, time);
        var sm = new ControllerStateMachine(new NullLogger<ControllerStateMachine>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Action:AcknowledgementTimeoutMs"] = "2000",
                ["BotDs:Action:MaxGlobalPerSecond"] = "4",
                ["BotDs:Action:MaxPerKeyPerSecond"] = "2",
                ["BotDs:Action:DetectExternalActionConflicts"] = "true",
                ["BotDs:Action:RequireEmergencyHotkeyForLive"] = "false",
                ["BotDs:Action:RequireBindingVerificationForLive"] = "false",
            })
            .Build();

        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        Assert.True(hotkey.TryRegister(static () => { }));
        var coord = new ActionCoordinator(
            pub, sm, profiles, readiness, config,
            keySink: new TestLiveKeySink(),
            timeProvider: time,
            emergencyHotkey: hotkey);

        Assert.True(coord.TrySetMode(OutputMode.Live, TimeSpan.FromMilliseconds(500)));
        sm.Arm();

        // Seed previous frame via an empty observe
        TelemetryFrame ready = Frame(seq: 10, cooldownMs: 0, now: time.GetUtcNow());
        pub.Publish(ready);
        _ = coord.ObservePending(ready, sm.Generation);

        // Unexplained cooldown while no pending action
        TelemetryFrame external = Frame(
            seq: 11,
            session: ready.Provider.SessionId,
            cooldownMs: 1200,
            now: time.GetUtcNow());
        pub.Publish(external);
        DispatchRecord? record = coord.ObservePending(external, sm.Generation);

        Assert.NotNull(record);
        Assert.Equal(DispatchOutcome.Cancelled, record!.Outcome);
        Assert.Equal(ControllerState.Stopped, sm.State);
        Assert.Equal(StopReason.ExternalActionConflict, sm.Snapshot.StopReason);
        Assert.Equal(OutputMode.Disabled, coord.Mode);
    }

    [Fact]
    public void Fake_emergency_hotkey_triggers_callback()
    {
        var hotkey = new FakeEmergencyHotkey("Ctrl+Shift+F12");
        int hits = 0;
        Assert.True(hotkey.TryRegister(() => hits++));
        hotkey.SimulateTrigger();
        Assert.Equal(1, hits);
        Assert.Equal(1, hotkey.TriggerCount);
    }

    [Fact]
    public void VirtualKeyMap_parses_emergency_hotkey_and_detects_collision()
    {
        Assert.True(VirtualKeyMap.TryParseHotkey("Ctrl+Shift+F12", out uint mods, out ushort vk, out _));
        Assert.True((mods & VirtualKeyMap.ModControl) != 0);
        Assert.True((mods & VirtualKeyMap.ModShift) != 0);
        Assert.Equal(0x7B, vk); // F12

        Assert.True(VirtualKeyMap.Collides("Ctrl+Shift+F12", "Ctrl+Shift+F12"));
        Assert.False(VirtualKeyMap.Collides("1", "Ctrl+Shift+F12"));
        Assert.False(VirtualKeyMap.TryParseHotkey("F12", out _, out _, out string? err));
        Assert.Contains("modifier", err, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProfileService> CreateProfiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "test-profile.json"), """
            {
              "id": "test-profile",
              "profileVersion": 1,
              "enabled": true,
              "character": { "calling": "Warrior", "minimumLevel": 1, "maximumLevel": 60 },
              "abilities": {
                "slice": { "abilityId": "1001", "key": "1", "enabled": true, "required": true }
              },
              "rules": [
                { "id": "r1", "ability": "slice", "enabled": true }
              ]
            }
            """);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = _tempDir,
            })
            .Build();
        var svc = new ProfileService(config, new HostEnv(_tempDir), new NullLogger<ProfileService>());
        await svc.ReloadAsync();
        svc.SetActiveProfile("test-profile");
        return svc;
    }

    private static CombatProfile Profile() => new()
    {
        Id = "p1",
        Enabled = true,
        Character = new CharacterRequirements { Calling = "Warrior" },
        Abilities = new Dictionary<string, AbilityBinding>
        {
            ["slice"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true },
        },
        Rules = [new CombatRule { Id = "r1", Ability = "slice", Enabled = true }],
    };

    private static TelemetryFrame Frame(
        ulong seq,
        int? cooldownMs,
        string? session = null,
        DateTimeOffset? now = null)
    {
        DateTimeOffset t = now ?? DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                ProviderHealth.Healthy, "5", session ?? "s1", seq, 16, t,
                TimeSpan.FromMilliseconds(5), SourceGeneration: 1, AttachmentProcessId: 4242),
            Player: new UnitState(
                "p1", "Test", 45, "Warrior", true, "friendly",
                new HealthState(100, 100), new ResourceState("Power", 50, 100), true, null),
            Target: new UnitState(
                "t1", "Mob", 45, null, false, "hostile",
                new HealthState(100, 100), null, true, null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(
                new Dictionary<string, AbilityState>
                {
                    ["1001"] = new AbilityState(
                        "1001", "Slice", true, true, true, cooldownMs, 1500, null,
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

    private sealed class HostEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
