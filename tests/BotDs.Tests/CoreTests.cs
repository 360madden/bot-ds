using BotDs.Core;

namespace BotDs.Tests;

public sealed class CombatProfileLoaderTests
{
    [Fact]
    public async Task LoadAsync_FileNotFound_ReturnsInvalid()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");
        ProfileValidationResult result = await CombatProfileLoader.LoadAsync(path);
        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_NullProfile_ReturnsInvalid()
    {
        ProfileValidationResult result = CombatProfileLoader.Validate(null);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyProfile_MissingRequiredFields()
    {
        var profile = new CombatProfile
        {
            Id = "",
            Character = new CharacterRequirements { Calling = "" },
            Abilities = new Dictionary<string, AbilityBinding>(),
            Rules = new List<CombatRule>(),
        };

        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("calling", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("ability", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("rule", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_UnknownVersion_ReturnsError()
    {
        var profile = CreateValidProfile();
        profile = profile with { ProfileVersion = 99 };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("profileVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DuplicateRuleId_ReturnsError()
    {
        var profile = CreateValidProfile() with
        {
            Rules =
            [
                new CombatRule { Id = "dup", Ability = "attack", When = new RuleConditions() },
                new CombatRule { Id = "dup", Ability = "attack", When = new RuleConditions() },
            ]
        };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.Contains(result.Errors, e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_UnknownAbilityAlias_ReturnsError()
    {
        var profile = CreateValidProfile() with
        {
            Rules =
            [
                new CombatRule { Id = "r1", Ability = "nonexistent", When = new RuleConditions() },
            ]
        };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.Contains(result.Errors, e => e.Contains("unknown ability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DisabledBindingAllowsEmptyAbilityId()
    {
        var profile = new CombatProfile
        {
            Id = "test-disabled",
            ProfileVersion = 1,
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 1, MaximumLevel = 75 },
            Abilities = new Dictionary<string, AbilityBinding>
            {
                ["disabled-ability"] = new() { AbilityId = "", Key = "", Enabled = false },
            },
            Rules =
            [
                new CombatRule { Id = "r1", Ability = "disabled-ability", Enabled = false, When = new RuleConditions() },
            ]
        };
        // NOTE: The loader validates enabled bindings; disabled bindings skip empty id/key checks.
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_InvalidPercentRange_ReturnsError()
    {
        var profile = CreateValidProfile() with
        {
            Rules =
            [
                new CombatRule
                {
                    Id = "r1", Ability = "attack",
                    When = new RuleConditions { PlayerHealthBelowPercent = 150 }
                },
            ]
        };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.Contains(result.Errors, e => e.Contains("PlayerHealthBelowPercent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_NegativeMinimumLevel_ReturnsError()
    {
        var profile = CreateValidProfile() with
        {
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = -1 },
        };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.Contains(result.Errors, e => e.Contains("minimumLevel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_MinExceedsMaxLevel_ReturnsError()
    {
        var profile = CreateValidProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 50,
                MaximumLevel = 10,
            },
        };
        ProfileValidationResult result = CombatProfileLoader.Validate(profile);
        Assert.Contains(result.Errors, e => e.Contains("minimumLevel", StringComparison.OrdinalIgnoreCase)
            && e.Contains("maximumLevel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ValidProfile_Success()
    {
        ProfileValidationResult result = CombatProfileLoader.Validate(CreateValidProfile());
        Assert.True(result.IsValid);
        Assert.NotNull(result.Profile);
    }

    [Fact]
    public async Task LoadAsync_ValidJson_LoadsProfile()
    {
        string json = """
        {
            "profileVersion": 1,
            "id": "test-json",
            "character": { "calling": "Mage", "minimumLevel": 10, "maximumLevel": 50 },
            "abilities": {
                "fireball": { "abilityId": "ability_fireball", "key": "1", "enabled": true }
            },
            "rules": [
                { "id": "r1", "ability": "fireball", "when": { "targetHostile": true } }
            ]
        }
        """;
        string path = Path.Combine(Path.GetTempPath(), "test-profile-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            ProfileValidationResult result = await CombatProfileLoader.LoadAsync(path);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.Equal("test-json", result.Profile!.Id);
            Assert.Equal("Mage", result.Profile.Character.Calling);
            Assert.Single(result.Profile.Abilities);
            Assert.Single(result.Profile.Rules);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_SchemaAndDisabledProfile_AreAccepted()
    {
        string json = """
        {
            "$schema": "../schemas/combat-profile.schema.json",
            "profileVersion": 1,
            "enabled": false,
            "id": "disabled",
            "character": { "calling": "Warrior" },
            "abilities": {
                "placeholder": { "abilityId": "", "key": "", "enabled": false }
            },
            "rules": [
                { "id": "placeholder", "ability": "placeholder", "enabled": false, "when": {} }
            ]
        }
        """;
        string path = Path.Combine(Path.GetTempPath(), "disabled-profile-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            ProfileValidationResult result = await CombatProfileLoader.LoadAsync(path);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
            Assert.False(result.Profile!.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CombatProfile CreateValidProfile() => new()
    {
        Id = "test-profile",
        ProfileVersion = 1,
        Character = new CharacterRequirements
        {
            Calling = "Warrior",
            MinimumLevel = 1,
            MaximumLevel = 75,
            Build = "TestBuild",
        },
        Abilities = new Dictionary<string, AbilityBinding>
        {
            ["attack"] = new() { AbilityId = "ability_attack", Key = "1", Enabled = true },
        },
        Rules =
        [
            new CombatRule
            {
                Id = "always-attack",
                Ability = "attack",
                Enabled = true,
                When = new RuleConditions { TargetHostile = true },
            },
        ],
    };
}

public sealed class CombatEvaluatorTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    [Fact]
    public void Evaluate_NullProfile_ReturnsStopped()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = TelemetryFrame.Empty(DateTimeOffset.UtcNow);
        EvaluationResult result = evaluator.Evaluate(null!, frame);
        Assert.Equal(StopReason.IntegrityFailure, result.StopReason);
    }

    [Fact]
    public void Evaluate_NullFrame_ReturnsStopped()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), null!);
        Assert.Equal(StopReason.IntegrityFailure, result.StopReason);
    }

    [Fact]
    public void Evaluate_StaleProvider_ReturnsTelemetryStale()
    {
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(1));
        TelemetryFrame frame = CreateHealthyFrame();
        frame = frame with
        {
            Provider = frame.Provider with { Age = TimeSpan.FromMinutes(1) }
        };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_NoPlayer_ReturnsPlayerUnavailable()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with { Player = null };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.PlayerUnavailable, result.StopReason);
    }

    [Fact]
    public void Evaluate_PlayerDead_ReturnsPlayerDead()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with
            {
                Health = new HealthState(0, 100),
            }
        };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.PlayerDead, result.StopReason);
    }

    [Fact]
    public void Evaluate_WrongCalling_ReturnsProfileMismatch()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements { Calling = "Rogue" },
        };
        TelemetryFrame frame = CreateHealthyFrame();
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    [Fact]
    public void Evaluate_NoTarget_Waiting()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with { Target = null };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(ControllerState.WaitingForTarget, result.State);
    }

    [Fact]
    public void Evaluate_TargetDead_Waiting()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Target = CreateTargetState() with
            {
                Health = new HealthState(0, 100),
            }
        };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(ControllerState.WaitingForTarget, result.State);
    }

    [Fact]
    public void Evaluate_FriendlyTarget_Waiting()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Target = CreateTargetState() with
            {
                Relation = "friendly",
            }
        };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(ControllerState.WaitingForTarget, result.State);
    }

    [Fact]
    public void Evaluate_AbilityNotInFrame_Rejected()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Abilities = new Dictionary<string, AbilityState>(),
        };
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.NotEmpty(result.Rejections);
        Assert.Equal(ControllerState.Evaluating, result.State);
    }

    [Fact]
    public void Evaluate_MatchesRule_ReturnsAction()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        TelemetryFrame frame = CreateHealthyFrame();
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.True(result.HasAction);
        Assert.Equal("test-attack", result.Action!.RuleId);
        Assert.Equal("attack-ability-id", result.Action.AbilityId);
    }

    [Fact]
    public void Evaluate_DisabledRule_Skipped()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        var profile = CreateCompatibleProfile() with
        {
            Rules =
            [
                new CombatRule
                {
                    Id = "disabled-rule",
                    Ability = "attack",
                    Enabled = false,
                    When = new RuleConditions(),
                },
            ]
        };
        TelemetryFrame frame = CreateHealthyFrame();
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.False(result.HasAction);
        Assert.Single(result.Rejections);
        Assert.Contains(result.Rejections[0].Reasons, r => r.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_UnknownAbilityAlias_Rejected()
    {
        var evaluator = new CombatEvaluator(MaxAge);
        var profile = CreateCompatibleProfile() with
        {
            Rules =
            [
                new CombatRule
                {
                    Id = "bad-rule",
                    Ability = "nonexistent-alias",
                    Enabled = true,
                    When = new RuleConditions(),
                },
            ]
        };
        TelemetryFrame frame = CreateHealthyFrame();
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.False(result.HasAction);
        Assert.Single(result.Rejections);
    }

    [Fact]
    public void Evaluate_UnknownResource_DoesNotSatisfyThreshold()
    {
        CombatProfile profile = CreateCompatibleProfile();
        profile = profile with
        {
            Rules =
            [
                profile.Rules[0] with
                {
                    When = profile.Rules[0].When with { ResourceAtLeast = 10 },
                },
            ],
        };
        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Resource = null },
        };

        EvaluationResult result = new CombatEvaluator(MaxAge).Evaluate(profile, frame);

        Assert.False(result.HasAction);
        Assert.Contains(result.Rejections[0].Reasons, reason => reason.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_DisabledProfile_Stops()
    {
        CombatProfile profile = CreateCompatibleProfile() with { Enabled = false };

        EvaluationResult result = new CombatEvaluator(MaxAge).Evaluate(profile, CreateHealthyFrame());

        Assert.Equal(ControllerState.Stopped, result.State);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
    }

    private static CombatProfile CreateCompatibleProfile() => new()
    {
        Id = "eval-test",
        ProfileVersion = 1,
        Character = new CharacterRequirements
        {
            Calling = "Warrior",
            MinimumLevel = 1,
            MaximumLevel = 75,
        },
        Abilities = new Dictionary<string, AbilityBinding>
        {
            ["attack"] = new() { AbilityId = "attack-ability-id", Key = "1", Enabled = true },
        },
        Rules =
        [
            new CombatRule
            {
                Id = "test-attack",
                Ability = "attack",
                Enabled = true,
                When = new RuleConditions { TargetHostile = true },
            },
        ],
    };

    private static TelemetryFrame CreateHealthyFrame()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: Guid.NewGuid().ToString("D"),
                Sequence: 1,
                ProducerFrameMilliseconds: (int)now.ToUnixTimeMilliseconds(),
                ReceivedAtUtc: now,
                Age: TimeSpan.Zero),
            Player: CreatePlayerState(),
            Target: CreateTargetState(),
            Abilities: new Dictionary<string, AbilityState>
            {
                ["attack-ability-id"] = new AbilityState(
                    Id: "attack-ability-id",
                    Name: "Test Attack",
                    Available: true,
                    Usable: true,
                    InRange: true,
                    CooldownRemainingMilliseconds: 0,
                    CooldownDurationMilliseconds: 0,
                    TargetId: null,
                    Costs: new Dictionary<string, int>(),
                    CastTimeMilliseconds: null,
                    IsChannel: null,
                    IsPassive: null),
            },
            PlayerAuras: [],
            TargetAuras: []);
    }

    private static UnitState CreatePlayerState() => new(
        Id: "player-1",
        Name: "TestPlayer",
        Level: 45,
        Calling: "Warrior",
        IsPlayer: true,
        Relation: null,
        Health: new HealthState(100, 100),
        Resource: new ResourceState("mana", 50, 100),
        InCombat: true,
        Cast: null);

    private static UnitState CreateTargetState() => new(
        Id: "target-1",
        Name: "TestMob",
        Level: 45,
        Calling: null,
        IsPlayer: null,
        Relation: "hostile",
        Health: new HealthState(1000, 1000),
        Resource: null,
        InCombat: true,
        Cast: null);
}
