using BotDs.Core;

namespace BotDs.Tests;

/// <summary>
/// Proves the evaluator engine is calling-agnostic by running a synthetic
/// Mage profile against a constructed Mage telemetry frame.
/// </summary>
public sealed class CallingAgnosticTests
{
    [Fact]
    public void Mage_profile_evaluates_correctly_against_mage_frame()
    {
        // Load the synthetic Mage profile
        var profile = new CombatProfile
        {
            Id = "synthetic-mage-50",
            Enabled = true,
            Character = new CharacterRequirements
            {
                Calling = "Mage",
                MinimumLevel = 45,
                MaximumLevel = 55,
            },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["fireball"] = new AbilityBinding
                {
                    AbilityId = "mage-fireball-1", Key = "1", Enabled = true, Required = true,
                },
                ["frostbolt"] = new AbilityBinding
                {
                    AbilityId = "mage-frostbolt-1", Key = "2", Enabled = true, Required = true,
                },
                ["arcane-blast"] = new AbilityBinding
                {
                    AbilityId = "mage-arcane-blast-1", Key = "3", Enabled = true, Required = false,
                    MinimumLevel = 55, MaximumLevel = 60,
                },
            },
            Rules = new List<CombatRule>
            {
                new()
                {
                    Id = "frostbolt-opening", Ability = "frostbolt", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, PlayerInCombat = false,
                        AbilityUsable = true, CooldownReady = true,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
                new()
                {
                    Id = "fireball-execute", Ability = "fireball", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, TargetHealthBelowPercent = 25,
                        CooldownReady = true, ResourceAtLeast = 30,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
                new()
                {
                    Id = "fireball-main", Ability = "fireball", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, CooldownReady = true, ResourceAtLeast = 20,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
            },
        };

        // Construct a Mage player frame
        var frame = CreateMageFrame(50, true, 100, 100);
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(profile, frame);
        Assert.NotNull(result);
        Assert.True(result.HasAction, "Evaluator should produce an action for a healthy Mage frame.");
        // fireball-main should fire since inCombat=true and resource >= 20
        Assert.Equal("fireball-main", result.Action!.RuleId);
        Assert.Equal("mage-fireball-1", result.Action.AbilityId);
        Assert.Equal("1", result.Action.Key);
    }

    [Fact]
    public void Mage_profile_uses_frostbolt_opening_when_out_of_combat()
    {
        var profile = CreateMageProfile();
        var frame = CreateMageFrame(50, false, 100, 100);
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(profile, frame);
        Assert.True(result.HasAction);
        Assert.Equal("frostbolt-opening", result.Action!.RuleId);
    }

    [Fact]
    public void Mage_profile_uses_fireball_execute_when_target_low_health()
    {
        var profile = CreateMageProfile();
        var frame = CreateMageFrame(50, true, 100, 10);
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(profile, frame);
        Assert.True(result.HasAction);
        Assert.Equal("fireball-execute", result.Action!.RuleId);
    }

    [Fact]
    public void Mage_profile_rejects_when_calling_mismatches()
    {
        var profile = CreateMageProfile();
        var frame = CreateMageFrame(50, true, 100, 100);
        frame = frame with
        {
            Player = frame.Player! with { Calling = "Warrior" },
        };
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    [Fact]
    public void Mage_profile_blocks_level_out_of_range()
    {
        var profile = CreateMageProfile();
        var frame = CreateMageFrame(30, true, 100, 100);
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    [Fact]
    public void Warrior_profile_rejects_mage_frame()
    {
        // Create a Warrior profile
        var warriorProfile = new CombatProfile
        {
            Id = "warrior-test",
            Enabled = true,
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 60,
            },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding
                {
                    AbilityId = "warrior-slash-1", Key = "1", Enabled = true,
                },
            },
            Rules = new List<CombatRule>
            {
                new()
                {
                    Id = "slash-rule", Ability = "slash", Enabled = true,
                    When = new RuleConditions { TargetHostile = true },
                },
            },
        };

        // Feed it a Mage frame
        var mageFrame = CreateMageFrame(50, true, 100, 100);
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        var result = evaluator.Evaluate(warriorProfile, mageFrame);
        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    // ---- Helpers ----

    private static CombatProfile CreateMageProfile()
    {
        return new CombatProfile
        {
            Id = "synthetic-mage-50",
            Enabled = true,
            Character = new CharacterRequirements
            {
                Calling = "Mage",
                MinimumLevel = 45,
                MaximumLevel = 55,
            },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["fireball"] = new AbilityBinding
                {
                    AbilityId = "mage-fireball-1", Key = "1", Enabled = true, Required = true,
                },
                ["frostbolt"] = new AbilityBinding
                {
                    AbilityId = "mage-frostbolt-1", Key = "2", Enabled = true, Required = true,
                },
                ["arcane-blast"] = new AbilityBinding
                {
                    AbilityId = "mage-arcane-blast-1", Key = "3", Enabled = true, Required = false,
                    MinimumLevel = 55, MaximumLevel = 60,
                },
            },
            Rules = new List<CombatRule>
            {
                new()
                {
                    Id = "frostbolt-opening", Ability = "frostbolt", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, PlayerInCombat = false,
                        AbilityUsable = true, CooldownReady = true,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
                new()
                {
                    Id = "fireball-execute", Ability = "fireball", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, TargetHealthBelowPercent = 25,
                        CooldownReady = true, ResourceAtLeast = 30,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
                new()
                {
                    Id = "fireball-main", Ability = "fireball", Enabled = true,
                    When = new RuleConditions
                    {
                        TargetHostile = true, CooldownReady = true, ResourceAtLeast = 20,
                    },
                    Acknowledgement = AcknowledgementKind.Cooldown,
                },
            },
        };
    }

    private static TelemetryFrame CreateMageFrame(
        int level, bool inCombat, int resource, double targetHealthPercent)
    {
        var now = DateTimeOffset.UtcNow;
        int targetMaxHp = 5000;
        int targetCurrent = (int)(targetMaxHp * targetHealthPercent / 100.0);

        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: Guid.NewGuid().ToString("D"),
                Sequence: 100,
                ProducerFrameMilliseconds: 16,
                ReceivedAtUtc: now,
                Age: TimeSpan.FromMilliseconds(10)),
            Player: new UnitState(
                Id: "mage-1",
                Name: "TestMage",
                Level: level,
                Calling: "Mage",
                IsPlayer: true,
                Relation: "friendly",
                Health: new HealthState(4000, 4000),
                Resource: new ResourceState("Mana", resource, 150),
                InCombat: inCombat,
                Cast: null),
            Target: new UnitState(
                Id: "target-1",
                Name: "TestMob",
                Level: 50,
                Calling: null,
                IsPlayer: false,
                Relation: "hostile",
                Health: new HealthState(targetCurrent, targetMaxHp),
                Resource: null,
                InCombat: true,
                Cast: null),
            Abilities: new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
            {
                ["mage-fireball-1"] = new AbilityState(
                    "mage-fireball-1", "Fireball", true, true, true, 0, 1500, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
                ["mage-frostbolt-1"] = new AbilityState(
                    "mage-frostbolt-1", "Frostbolt", true, true, true, 0, 2000, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
                ["mage-arcane-blast-1"] = new AbilityState(
                    "mage-arcane-blast-1", "Arcane Blast", true, true, true, 0, 2500, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
            }.AsReadOnly(),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }
}
