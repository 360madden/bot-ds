using System.Collections.ObjectModel;
using BotDs.App.Services;
using BotDs.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace BotDs.Tests;

public sealed class ArmingReadinessServiceTests : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(500);
    private readonly string _tempDir;

    public ArmingReadinessServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"botds-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task No_active_profile_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var profiles = await CreateProfileService(null);
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("No active profile"));
    }

    [Fact]
    public async Task No_active_profile_still_reports_live_telemetry_frame()
    {
        // Dashboard readiness should surface provider/player diagnostics even when
        // no profile is selected (operator is mid-setup).
        var publisher = new SnapshotPublisher();
        publisher.Publish(HealthyFrame());
        var profiles = await CreateProfileService(null);
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("No active profile"));
        Assert.NotNull(result.Frame);
        Assert.Equal(ProviderHealth.Healthy, result.Frame!.Provider.Health);
        Assert.NotNull(result.Frame.Player);
    }

    [Fact]
    public async Task Disabled_profile_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        publisher.Publish(HealthyFrame());
        WriteProfileFile("test-profile", CreateValidProfileJson(enabled: false));
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("disabled"));
    }

    [Fact]
    public async Task Stale_telemetry_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var stale = HealthyFrame() with
        {
            Provider = HealthyFrame().Provider with
            {
                ReceivedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                Age = TimeSpan.FromSeconds(10),
            },
        };
        publisher.Publish(stale);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("healthy") || b.Contains("fresh"));
    }

    [Fact]
    public async Task Dead_player_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var dead = HealthyFrame() with
        {
            Player = HealthyFrame().Player! with
            {
                Health = new HealthState(0, 1000),
            },
        };
        publisher.Publish(dead);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("dead"));
    }

    [Fact]
    public async Task No_target_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var noTarget = HealthyFrame() with
        {
            Target = null,
            TargetKnownness = TargetKnownness.KnownNoTarget,
        };
        publisher.Publish(noTarget);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("No live target"));
    }

    [Fact]
    public async Task Friendly_target_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var friendly = HealthyFrame() with
        {
            Target = new UnitState(
                Id: "target-1", Name: "Friend", Level: 50, Calling: null,
                IsPlayer: false, Relation: "friendly",
                Health: new HealthState(3000, 3000),
                Resource: null, InCombat: false, Cast: null),
        };
        publisher.Publish(friendly);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("hostile"));
    }

    [Fact]
    public async Task Level_mismatch_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var lowLevel = HealthyFrame() with
        {
            Player = HealthyFrame().Player! with { Level = 30 },
        };
        publisher.Publish(lowLevel);
        WriteProfileFile("test-profile", CreateValidProfileJson(minLevel: 40, maxLevel: 60));
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("level"));
    }

    [Fact]
    public async Task Calling_mismatch_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var mage = HealthyFrame() with
        {
            Player = HealthyFrame().Player! with { Calling = "Mage" },
        };
        publisher.Publish(mage);
        WriteProfileFile("test-profile", CreateValidProfileJson(calling: "Warrior"));
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("calling"));
    }

    [Fact]
    public async Task Truncated_frame_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        var truncated = HealthyFrame() with
        {
            Provider = HealthyFrame().Provider with { IsTruncated = true },
        };
        publisher.Publish(truncated);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_required_ability_blocks_arming()
    {
        var publisher = new SnapshotPublisher();
        // Frame has ability "1001" but profile requires "9999" (missing) plus "1001"
        publisher.Publish(HealthyFrame());
        WriteProfileFile("test-profile", JsonSerializer.Serialize(new
        {
            id = "test-profile",
            profileVersion = 1,
            enabled = true,
            character = new { calling = "Warrior", minimumLevel = 40, maximumLevel = 60 },
            abilities = new Dictionary<string, object>
            {
                ["slice"] = new { abilityId = "1001", key = "1", enabled = true, required = true },
                ["bash"] = new { abilityId = "9999", key = "2", enabled = true, required = true },
            },
            rules = new[]
            {
                new { id = "r1", ability = "slice", enabled = true },
            },
        }));
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.False(result.CanArm);
        Assert.Contains(result.Blockers, b => b.Contains("9999"));
    }

    [Fact]
    public async Task Healthy_state_passes_all_checks()
    {
        var publisher = new SnapshotPublisher();
        publisher.Publish(HealthyFrame());
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.True(result.CanArm);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task Unknown_ability_inventory_produces_warning_not_blocker()
    {
        var publisher = new SnapshotPublisher();
        var unknown = HealthyFrame() with { IsAbilitiesKnown = false };
        publisher.Publish(unknown);
        WriteProfileFile("test-profile", CreateValidProfileJson());
        var profiles = await CreateProfileService("test-profile");
        var svc = new ArmingReadinessService(publisher, profiles);

        var result = svc.Evaluate(MaxAge);
        Assert.True(result.CanArm);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Message.Contains("Ability"));
    }

    // ---- Helpers ----

    private async Task<ProfileService> CreateProfileService(string? activeProfileId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Profiles:Directory"] = _tempDir,
            })
            .Build();
        var log = new NullLogger<ProfileService>();
        var svc = new ProfileService(config, new TestHostEnvironment(_tempDir), log);
        await svc.ReloadAsync();
        if (activeProfileId is not null)
            svc.SetActiveProfile(activeProfileId);
        return svc;
    }

    private void WriteProfileFile(string id, string json)
    {
        File.WriteAllText(Path.Combine(_tempDir, $"{id}.json"), json);
    }

    private static string CreateValidProfileJson(
        bool enabled = true,
        string calling = "Warrior",
        int minLevel = 40,
        int maxLevel = 60)
    {
        return JsonSerializer.Serialize(new
        {
            id = "test-profile",
            profileVersion = 1,
            enabled,
            character = new
            {
                calling,
                minimumLevel = minLevel,
                maximumLevel = maxLevel,
            },
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

    private static TelemetryFrame HealthyFrame()
    {
        var now = DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: Guid.NewGuid().ToString("D"),
                Sequence: 100,
                ProducerFrameMilliseconds: 16,
                ReceivedAtUtc: now,
                Age: TimeSpan.FromMilliseconds(10),
                ClientVersion: "4.0"),
            Player: new UnitState(
                Id: "player-1",
                Name: "TestWarrior",
                Level: 50,
                Calling: "Warrior",
                IsPlayer: true,
                Relation: "friendly",
                Health: new HealthState(5000, 5000),
                Resource: new ResourceState("Power", 100, 100),
                InCombat: true,
                Cast: null),
            Target: new UnitState(
                Id: "target-1",
                Name: "TestMob",
                Level: 50,
                Calling: null,
                IsPlayer: false,
                Relation: "hostile",
                Health: new HealthState(3000, 3000),
                Resource: null,
                InCombat: true,
                Cast: null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(
                new Dictionary<string, AbilityState>
                {
                    ["1001"] = new AbilityState(
                        Id: "1001",
                        Name: "Slice",
                        Available: true,
                        Usable: true,
                        InRange: true,
                        CooldownRemainingMilliseconds: 0,
                        CooldownDurationMilliseconds: 1500,
                        TargetId: "target-1",
                        Costs: new ReadOnlyDictionary<string, int>(
                            new Dictionary<string, int> { ["Power"] = 10 }),
                        CastTimeMilliseconds: 0,
                        IsChannel: false,
                        IsPassive: false),
                }),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string root) => ContentRootPath = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
