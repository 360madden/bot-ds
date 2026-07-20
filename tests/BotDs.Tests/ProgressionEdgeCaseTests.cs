using BotDs.Core;

namespace BotDs.Tests;

/// <summary>
/// Tests for progression edge cases: level boundaries, ability learning/removal,
/// calling mismatches, and no-executable-rule scenarios.
/// </summary>
public sealed class ProgressionEdgeCaseTests
{
    [Fact]
    public void Ability_at_exact_minimum_level_is_reachable()
    {
        var profile = new CombatProfile
        {
            Id = "prog-test",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 60 },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true, MinimumLevel = 45 },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = true, When = new RuleConditions { TargetHostile = true } },
            },
        };

        var frame = CreateFrame(level: 45, calling: "Warrior");
        frame = frame with
        {
            Abilities = new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
            {
                ["1001"] = new AbilityState("1001", "Slash", true, true, true, 0, 1500, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
            }.AsReadOnly(),
        };

        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var result = evaluator.Evaluate(profile, frame);
        Assert.True(result.HasAction, "Ability at exact minimum level should be reachable");
    }

    [Fact]
    public void Ability_below_minimum_level_is_excluded()
    {
        var profile = new CombatProfile
        {
            Id = "prog-test",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 60 },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true, MinimumLevel = 45 },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = true, When = new RuleConditions { TargetHostile = true } },
            },
        };

        var frame = CreateFrame(level: 44, calling: "Warrior");
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.IntegrityFailure, result.StopReason);
    }

    [Fact]
    public void Ability_above_maximum_level_is_excluded()
    {
        var profile = new CombatProfile
        {
            Id = "prog-test",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 60 },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true, MaximumLevel = 50 },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = true, When = new RuleConditions { TargetHostile = true } },
            },
        };

        var frame = CreateFrame(level: 51, calling: "Warrior");
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.IntegrityFailure, result.StopReason);
    }

    [Fact]
    public void Optional_ability_missing_does_not_block_evaluation()
    {
        var profile = new CombatProfile
        {
            Id = "prog-test",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 60 },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true, Required = true },
                ["extra"] = new AbilityBinding { AbilityId = "9999", Key = "2", Enabled = true, Required = false },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = true, When = new RuleConditions { TargetHostile = true } },
            },
        };

        var frame = CreateFrame(level: 50, calling: "Warrior");
        frame = frame with
        {
            Abilities = new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
            {
                // "extra" ability (9999) is not in telemetry
                ["1001"] = new AbilityState("1001", "Slash", true, true, true, 0, 1500, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
            }.AsReadOnly(),
        };

        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var result = evaluator.Evaluate(profile, frame);
        Assert.True(result.HasAction, "Optional missing ability should not block");
    }

    [Fact]
    public void Disabled_profile_with_nonblank_build_is_allowed()
    {
        // Disabled profiles may reference a build (for drafts)
        var validation = CombatProfileLoader.Validate(new CombatProfile
        {
            Id = "draft",
            Enabled = false,
            Character = new CharacterRequirements { Calling = "Warrior", Build = "61BM/Warlord" },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = false, Required = false },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = false },
            },
        });

        Assert.True(validation.IsValid, "Disabled profile with build should be valid");
    }

    [Fact]
    public void Enabled_profile_with_build_is_rejected()
    {
        var validation = CombatProfileLoader.Validate(new CombatProfile
        {
            Id = "bad",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior", Build = "61BM/Warlord" },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["slash"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true },
            },
            Rules = new List<CombatRule>
            {
                new() { Id = "r1", Ability = "slash", Enabled = true },
            },
        });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("build"));
    }

    // ---- Helpers ----

    private static TelemetryFrame CreateFrame(int level, string calling)
    {
        var now = DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy, ProtocolVersion: "5",
                SessionId: Guid.NewGuid().ToString("D"), Sequence: 100,
                ProducerFrameMilliseconds: 16, ReceivedAtUtc: now,
                Age: TimeSpan.FromMilliseconds(10)),
            Player: new UnitState(
                Id: "player-1", Name: "Test", Level: level, Calling: calling,
                IsPlayer: true, Relation: "friendly",
                Health: new HealthState(5000, 5000),
                Resource: new ResourceState("Power", 100, 100),
                InCombat: true, Cast: null),
            Target: new UnitState(
                Id: "target-1", Name: "Mob", Level: 50, Calling: null,
                IsPlayer: false, Relation: "hostile",
                Health: new HealthState(3000, 3000),
                Resource: null, InCombat: true, Cast: null),
            Abilities: new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
            {
                ["1001"] = new AbilityState("1001", "Slash", true, true, true, 0, 1500, null,
                    new Dictionary<string, int>().AsReadOnly(), 0, false, false),
            }.AsReadOnly(),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }
}
