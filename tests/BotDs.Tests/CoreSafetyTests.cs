using BotDs.Core;

namespace BotDs.Tests;

/// <summary>
/// Adversarial safety tests for fail-closed provider staleness (elapsed-time-aware)
/// and profile-build identity enforcement.
/// </summary>
public sealed class CoreSafetyTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    /// <summary>Deterministic clock for tests that need to control elapsed time.</summary>
    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    // =====================================================================
    // Requirement 1: Elapsed time since ReceivedAtUtc must age frames out.
    // =====================================================================

    [Fact]
    public void Evaluate_FreshFrame_IsUsable()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(MaxAge, clock);

        TelemetryFrame frame = CreateHealthyFrame(now);
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.NotEqual(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_FrameStalesAfterElapsedSinceReceipt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(TimeSpan.FromSeconds(10), clock);

        // Frame received with Age=0, then 11 seconds pass.
        TelemetryFrame frame = CreateHealthyFrame(now);
        clock.Advance(TimeSpan.FromSeconds(11));

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
        Assert.Contains("not healthy and fresh", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_OnceHealthyFrameDoesNotRemainUsableForever()
    {
        // A frame with Age=0 received at T0. Max age is 5s.
        // After 6 seconds, even though the reported Age is still 0,
        // effective age is 6s > 5s.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(TimeSpan.FromSeconds(5), clock);

        TelemetryFrame frame = CreateHealthyFrame(now);

        // Verify it is usable initially.
        EvaluationResult initial = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.NotEqual(StopReason.TelemetryStale, initial.StopReason);

        // Advance past max age.
        clock.Advance(TimeSpan.FromSeconds(6));
        EvaluationResult stale = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, stale.StopReason);
    }

    [Fact]
    public void Evaluate_AgeAtReceiptPlusElapsed_DoesNotDoubleCount()
    {
        // Frame reports Age=2s at receipt time. ReceivedAtUtc is 3s ago. Max age is 10s.
        // Effective age should be 2s + 3s = 5s, NOT 5s + 3s (double-counting).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(TimeSpan.FromSeconds(10), clock);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Provider = new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: now - TimeSpan.FromSeconds(3),
                Age: TimeSpan.FromSeconds(2)),
        };

        // Effective age = 2s + 3s = 5s, which is < 10s -> usable.
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.NotEqual(StopReason.TelemetryStale, result.StopReason);

        // Advance 6 more seconds: effective = 2s + 9s = 11s > 10s -> stale.
        clock.Advance(TimeSpan.FromSeconds(6));
        EvaluationResult stale = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, stale.StopReason);
    }

    [Fact]
    public void Evaluate_FutureReceivedAtUtc_FailsClosed()
    {
        // If ReceivedAtUtc is in the future (clock skew), effective age is negative.
        // Negative effective age must fail closed (stale), not pass.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(MaxAge, clock);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Provider = new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: now + TimeSpan.FromSeconds(10),
                Age: TimeSpan.Zero),
        };

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_NegativeAgeAtReceipt_FailsClosed()
    {
        // A malicious or buggy producer reports negative Age.
        // Effective age could be negative if Age is negative and elapsed is small.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var clock = new TestTimeProvider(now);
        var evaluator = new CombatEvaluator(MaxAge, clock);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Provider = new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: now,
                Age: TimeSpan.FromMinutes(-1)),
        };

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void ProviderStatus_DeterministicOverload_UsesAgeAtReceiptPlusElapsed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProviderStatus status = CreateHealthyFrame(now).Provider with
        {
            ReceivedAtUtc = now - TimeSpan.FromSeconds(3),
            Age = TimeSpan.FromSeconds(2),
        };

        Assert.True(status.IsUsable(TimeSpan.FromSeconds(5), now));
        Assert.False(status.IsUsable(TimeSpan.FromSeconds(5), now.AddTicks(1)));
    }

    [Fact]
    public void ProviderStatus_OneArgumentOverload_RejectsOldReceiptWithoutWaiting()
    {
        ProviderStatus status = CreateHealthyFrame().Provider with
        {
            ReceivedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            Age = TimeSpan.Zero,
        };

        Assert.False(status.IsUsable(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProviderStatus_EffectiveAgeOverflow_FailsClosed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProviderStatus status = CreateHealthyFrame(now).Provider with
        {
            ReceivedAtUtc = now - TimeSpan.FromTicks(1),
            Age = TimeSpan.MaxValue,
        };

        Assert.False(status.IsUsable(TimeSpan.MaxValue, now));
    }

    [Fact]
    public void ProviderStatus_FutureReceiptAndNegativeReportedAge_FailClosedIndependently()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProviderStatus status = CreateHealthyFrame(now).Provider;

        Assert.False((status with { ReceivedAtUtc = now.AddTicks(1) }).IsUsable(MaxAge, now));
        Assert.False((status with { Age = TimeSpan.FromTicks(-1) }).IsUsable(MaxAge, now));
    }

    // =====================================================================
    // Requirement 2: Profile Build without telemetry build identity.
    // =====================================================================

    [Fact]
    public void Evaluate_ProfileRequiresBuild_TelemetryHasNone_FailsClosed()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = "MyBuild",
            },
        };

        TelemetryFrame frame = CreateHealthyFrame();
        // Player has no Build set (null default).
        frame = frame with { Player = CreatePlayerState() };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
        Assert.Contains("build", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MyBuild", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ProfileRequiresBuild_TelemetryBuildMismatches_FailsClosed()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = "ExpectedBuild",
            },
        };

        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Build = "DifferentBuild" },
        };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(StopReason.ProfileMismatch, result.StopReason);
        Assert.Contains("build", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ProfileRequiresBuild_TelemetryBuildMatches_Passes()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = "MyBuild",
            },
        };

        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Build = "MyBuild" },
        };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        // Should not stop on ProfileMismatch; the frame has a matching target+rule
        // so it reaches Armed.
        Assert.NotEqual(StopReason.ProfileMismatch, result.StopReason);
        Assert.Equal(ControllerState.Armed, result.State);
    }

    [Fact]
    public void Evaluate_ProfileNoBuildRequirement_TelemetryHasNoBuild_Passes()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = null,
            },
        };

        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Build = null },
        };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(StopReason.ProfileMismatch, result.StopReason);
    }

    [Fact]
    public void Evaluate_ProfileEmptyBuildRequirement_TelemetryHasNoBuild_Passes()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = "",
            },
        };

        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Build = null },
        };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(StopReason.ProfileMismatch, result.StopReason);
    }

    // =====================================================================
    // Adversarial edge cases.
    // =====================================================================

    [Fact]
    public void Evaluate_DegradedProvider_FailsClosed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evaluator = new CombatEvaluator(MaxAge);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Provider = new ProviderStatus(
                Health: ProviderHealth.Degraded,
                ProtocolVersion: "5",
                SessionId: "test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: now,
                Age: TimeSpan.Zero),
        };

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_TruncatedProvider_FailsClosed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evaluator = new CombatEvaluator(MaxAge);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Provider = new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: now,
                Age: TimeSpan.Zero,
                IsTruncated: true),
        };

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_DefaultConstructor_UsesSystemClock()
    {
        // The evaluator without explicit TimeProvider should work with the real clock.
        var evaluator = new CombatEvaluator(TimeSpan.FromHours(1));
        TelemetryFrame frame = CreateHealthyFrame(DateTimeOffset.UtcNow);
        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.NotEqual(StopReason.TelemetryStale, result.StopReason);
    }

    [Fact]
    public void Evaluate_BuildCaseInsensitiveMatch_Passes()
    {
        var profile = CreateCompatibleProfile() with
        {
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
                Build = "MyBuild",
            },
        };

        TelemetryFrame frame = CreateHealthyFrame() with
        {
            Player = CreatePlayerState() with { Build = "mybuild" },
        };

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(StopReason.ProfileMismatch, result.StopReason);
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static CombatProfile CreateCompatibleProfile() => new()
    {
        Id = "safety-test",
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

    private static TelemetryFrame CreateHealthyFrame(DateTimeOffset? now = null)
    {
        DateTimeOffset time = now ?? DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: Guid.NewGuid().ToString("D"),
                Sequence: 1,
                ProducerFrameMilliseconds: (int)time.ToUnixTimeMilliseconds(),
                ReceivedAtUtc: time,
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
