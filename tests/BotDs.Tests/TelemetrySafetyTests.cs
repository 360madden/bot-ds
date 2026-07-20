using BotDs.Core;
using BotDs.Reader.V5;

namespace BotDs.Tests;

/// <summary>
/// Adversarial telemetry-completeness tests.
///
/// Covers four production contracts that concurrent agents implement:
///   1. UnitState.IsAvailable is false when current health is null.
///   2. CombatEvaluator rejects aura-predicate rules when aura-known is false.
///   3. V5HealthMapper maps aura section mask bits to aura-known flags.
///   4. V5HealthMapper preserves Faulted health and detail when no frame is present.
///
/// These tests are intentionally written against the expected contract, not the
/// current implementation. They will FAIL until the concurrent changes land, then
/// serve as regression guards.
/// </summary>
public sealed class TelemetrySafetyTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    // =====================================================================
    // 1. UnitState.IsAvailable: null current health → unavailable
    // =====================================================================

    [Fact]
    public void UnitState_NullCurrentHealth_WithValidIdAndMax_IsUnavailable()
    {
        // Contract: IsAvailable is false when current health is null
        // even if Id and Maximum are present.
        var health = new HealthState(Current: null, Maximum: 100);
        var unit = new UnitState(
            Id: "player-1",
            Name: "Test",
            Level: 45,
            Calling: "Warrior",
            IsPlayer: true,
            Relation: "friendly",
            Health: health,
            Resource: null,
            InCombat: true,
            Cast: null);

        Assert.False(unit.IsAvailable);
    }

    [Fact]
    public void UnitState_CurrentZero_WithValidMax_IsAvailableAndDead()
    {
        // Contract: current=0 remains available (and dead).
        // A dead-but-observed player is still a known state.
        var health = new HealthState(Current: 0, Maximum: 100);
        var unit = new UnitState(
            Id: "player-1",
            Name: "Test",
            Level: 45,
            Calling: "Warrior",
            IsPlayer: true,
            Relation: "friendly",
            Health: health,
            Resource: null,
            InCombat: true,
            Cast: null);

        Assert.True(unit.IsAvailable);
        Assert.True(unit.Health.IsDead);
    }

    [Fact]
    public void UnitState_NullCurrent_WithValidIdAndMax_IsNotDead()
    {
        // Null current → not available and not dead (unknown state).
        // IsDead requires Current == 0, not null.
        var health = new HealthState(Current: null, Maximum: 100);
        var unit = new UnitState(
            Id: "player-1",
            Name: "Test",
            Level: 45,
            Calling: "Warrior",
            IsPlayer: true,
            Relation: "friendly",
            Health: health,
            Resource: null,
            InCombat: true,
            Cast: null);

        Assert.False(unit.IsAvailable);
        Assert.False(unit.Health.IsDead);
    }

    [Fact]
    public void UnitState_NullCurrent_WithValidMax_PercentIsNull()
    {
        // Health.Percent must be null when Current is unknown.
        var health = new HealthState(Current: null, Maximum: 100);
        Assert.Null(health.Percent);
    }

    [Fact]
    public void UnitState_EvaluatorRejectsNullCurrentHealth()
    {
        // The evaluator must treat null-current health as unavailable and stop.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evaluator = new CombatEvaluator(MaxAge);

        TelemetryFrame frame = CreateHealthyFrame(now) with
        {
            Player = CreatePlayerState() with
            {
                Health = new HealthState(Current: null, Maximum: 100),
            },
        };

        EvaluationResult result = evaluator.Evaluate(CreateCompatibleProfile(), frame);
        Assert.Equal(StopReason.PlayerUnavailable, result.StopReason);
    }

    // =====================================================================
    // 2. CombatEvaluator aura-known rejection semantics
    // =====================================================================

    [Fact]
    public void Evaluator_RequiredPlayerAura_UnknownAuras_RejectsRule()
    {
        // Contract: required aura predicate + aura-known=false → reject the rule.
        var profile = CreateAuraTestProfile(requiredPlayerAuras: ["buff-shield"]);
        var frame = CreateAuraTestFrame(playerAurasKnown: false, targetAurasKnown: true);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("aura", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_ForbiddenPlayerAura_UnknownAuras_RejectsRule()
    {
        // Contract: forbidden aura predicate + aura-known=false → reject the rule.
        var profile = CreateAuraTestProfile(forbiddenPlayerAuras: ["debuff-dot"]);
        var frame = CreateAuraTestFrame(playerAurasKnown: false, targetAurasKnown: true);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("aura", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_RequiredTargetAura_UnknownAuras_RejectsRule()
    {
        // Contract: required target aura + aura-known=false → reject.
        var profile = CreateAuraTestProfile(requiredTargetAuras: ["mark-vuln"]);
        var frame = CreateAuraTestFrame(playerAurasKnown: true, targetAurasKnown: false);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("aura", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_ForbiddenTargetAura_UnknownAuras_RejectsRule()
    {
        // Contract: forbidden target aura + aura-known=false → reject.
        var profile = CreateAuraTestProfile(forbiddenTargetAuras: ["boss-enrage"]);
        var frame = CreateAuraTestFrame(playerAurasKnown: true, targetAurasKnown: false);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("aura", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_NoAuraPredicates_UnknownAuras_DoesNotReject()
    {
        // Contract: unknown aura state does NOT reject a rule with no aura predicates.
        var profile = CreateAuraTestProfile(); // empty aura lists
        var frame = CreateAuraTestFrame(playerAurasKnown: false, targetAurasKnown: false);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        // Should reach Armed: no aura predicates → aura unknownness is irrelevant.
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    [Fact]
    public void Evaluator_KnownEmpty_Permits_ForbiddenPlayerAuraCondition()
    {
        // Contract: explicit known empty permits forbidden-aura conditions.
        // The forbidden aura is absent, so the rule should pass.
        var profile = CreateAuraTestProfile(forbiddenPlayerAuras: ["debuff-dot"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras: []); // known empty — no debuff present
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    [Fact]
    public void Evaluator_KnownEmpty_Permits_ForbiddenTargetAuraCondition()
    {
        // Contract: explicit known empty permits forbidden-aura conditions on target.
        var profile = CreateAuraTestProfile(forbiddenTargetAuras: ["boss-enrage"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            targetAuras: []); // known empty — no enrage present
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    [Fact]
    public void Evaluator_KnownEmpty_Rejects_RequiredPlayerAuraCondition()
    {
        // Contract: explicit known empty rejects required-aura conditions.
        // The required aura is absent, so the rule should fail.
        var profile = CreateAuraTestProfile(requiredPlayerAuras: ["buff-shield"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras: []); // known empty — buff not present
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("absent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_KnownEmpty_Rejects_RequiredTargetAuraCondition()
    {
        // Contract: explicit known empty rejects required-aura conditions on target.
        var profile = CreateAuraTestProfile(requiredTargetAuras: ["mark-vuln"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            targetAuras: []); // known empty — mark not present
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("absent", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_KnownPresent_ForbiddenAuraAbsent_Permits()
    {
        // Known aura list is populated but does NOT contain the forbidden aura → permit.
        var profile = CreateAuraTestProfile(forbiddenPlayerAuras: ["debuff-dot"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras:
            [
                new AuraState("other-buff", "Other", null, 1, null, IsDebuff: false),
            ]);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    [Fact]
    public void Evaluator_KnownPresent_ForbiddenAuraPresent_Rejects()
    {
        // Known aura list contains the forbidden aura → reject.
        var profile = CreateAuraTestProfile(forbiddenPlayerAuras: ["debuff-dot"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras:
            [
                new AuraState("debuff-dot", "DoT", null, 3, 5000, IsDebuff: true),
            ]);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("present", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Evaluator_KnownPresent_RequiredAuraPresent_Permits()
    {
        // Known aura list contains the required aura → permit.
        var profile = CreateAuraTestProfile(requiredPlayerAuras: ["buff-shield"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras:
            [
                new AuraState("buff-shield", "Shield", null, 2, 10000, IsDebuff: false),
            ]);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    // =====================================================================
    // 3. V5HealthMapper aura section mask → aura-known mapping
    // =====================================================================

    [Fact]
    public void V5HealthMapper_OmittedAuraMaskBits_AuraKnownFalse()
    {
        // Contract: omitted aura section mask bits → aura-known false.
        // Sections mask includes provider+player+target+abilities but NOT auras.
        V5BufferHeader header = CreateHeader(
            V5Constants.MaskProviderInfo
            | V5Constants.MaskPlayer
            | V5Constants.MaskTarget
            | V5Constants.MaskAbilities);

        ParsedV5Frame frame = CreateParsedFrame(header);
        StableReadResult result = StableReadResult.Healthy(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, now);

        Assert.False(telemetry.IsPlayerAurasKnown);
        Assert.False(telemetry.IsTargetAurasKnown);
    }

    [Fact]
    public void V5HealthMapper_PresentEmptyAuraMaskBits_AuraKnownTrue()
    {
        // Contract: present empty aura section bits → aura-known true.
        V5BufferHeader header = CreateHeader(
            V5Constants.MaskProviderInfo
            | V5Constants.MaskPlayer
            | V5Constants.MaskTarget
            | V5Constants.MaskAbilities
            | V5Constants.MaskPlayerAuras
            | V5Constants.MaskTargetAuras);

        ParsedV5Frame frame = CreateParsedFrame(header);
        StableReadResult result = StableReadResult.Healthy(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, now);

        Assert.True(telemetry.IsPlayerAurasKnown);
        Assert.True(telemetry.IsTargetAurasKnown);
    }

    [Fact]
    public void V5HealthMapper_OnlyPlayerAuraBit_PartialKnowledge()
    {
        // Contract: only player aura bit present → player known, target unknown.
        V5BufferHeader header = CreateHeader(
            V5Constants.MaskProviderInfo
            | V5Constants.MaskPlayer
            | V5Constants.MaskTarget
            | V5Constants.MaskAbilities
            | V5Constants.MaskPlayerAuras);

        ParsedV5Frame frame = CreateParsedFrame(header);
        StableReadResult result = StableReadResult.Healthy(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, now);

        Assert.True(telemetry.IsPlayerAurasKnown);
        Assert.False(telemetry.IsTargetAurasKnown);
    }

    [Fact]
    public void V5HealthMapper_OnlyTargetAuraBit_PartialKnowledge()
    {
        // Contract: only target aura bit present → target known, player unknown.
        V5BufferHeader header = CreateHeader(
            V5Constants.MaskProviderInfo
            | V5Constants.MaskPlayer
            | V5Constants.MaskTarget
            | V5Constants.MaskAbilities
            | V5Constants.MaskTargetAuras);

        ParsedV5Frame frame = CreateParsedFrame(header);
        StableReadResult result = StableReadResult.Healthy(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, now);

        Assert.False(telemetry.IsPlayerAurasKnown);
        Assert.True(telemetry.IsTargetAurasKnown);
    }

    [Fact]
    public void V5HealthMapper_PresentWithAuras_AuraKnownTrue()
    {
        // Contract: present aura section with actual aura data → aura-known true.
        V5BufferHeader header = CreateHeader(
            V5Constants.MaskProviderInfo
            | V5Constants.MaskPlayer
            | V5Constants.MaskTarget
            | V5Constants.MaskAbilities
            | V5Constants.MaskPlayerAuras
            | V5Constants.MaskTargetAuras);

        var playerAuras = new List<ParsedAuraState>
        {
            new("aura-1", "Shield", 1, V5Constants.AuraFlagIsDebuff, 5000),
        };

        ParsedV5Frame frame = CreateParsedFrame(header, playerAuras: playerAuras);
        StableReadResult result = StableReadResult.Healthy(frame);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, now);

        Assert.True(telemetry.IsPlayerAurasKnown);
        Assert.True(telemetry.IsTargetAurasKnown);
        Assert.Single(telemetry.PlayerAuras);
        Assert.Empty(telemetry.TargetAuras);
    }

    // =====================================================================
    // 4. V5HealthMapper Faulted preservation
    // =====================================================================

    [Fact]
    public void V5HealthMapper_Faulted_NoFrame_PreservesHealthAndDetail()
    {
        // Contract: ToTelemetryFrame on StableReadResult.Faulted with no frame
        // preserves ProviderHealth.Faulted and failure detail instead of
        // returning ProviderHealth.Disconnected.
        string faultDetail = "CRC validation failed after 3 consecutive errors";
        StableReadResult faulted = StableReadResult.Faulted(faultDetail);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(faulted, now);

        Assert.NotNull(frame.Provider);
        Assert.Equal(ProviderHealth.Faulted, frame.Provider.Health);
        Assert.Equal(faultDetail, frame.Provider.Fault);
    }

    [Fact]
    public void V5HealthMapper_Faulted_NoFrame_NullDetail_PreservesHealth()
    {
        // Contract: Faulted with no detail still preserves Faulted health,
        // not Disconnected.
        StableReadResult faulted = StableReadResult.Faulted(detail: null);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(faulted, now);

        Assert.NotNull(frame.Provider);
        Assert.Equal(ProviderHealth.Faulted, frame.Provider.Health);
    }

    [Fact]
    public void V5HealthMapper_Faulted_NoFrame_NullFrameFields()
    {
        // Contract: Faulted result with no frame yields null Player/Target
        // and empty ability/aura collections.
        StableReadResult faulted = StableReadResult.Faulted("something broke");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(faulted, now);

        Assert.Null(frame.Player);
        Assert.Null(frame.Target);
        Assert.NotNull(frame.Abilities);
        Assert.Empty(frame.Abilities);
        Assert.NotNull(frame.PlayerAuras);
        Assert.Empty(frame.PlayerAuras);
        Assert.NotNull(frame.TargetAuras);
        Assert.Empty(frame.TargetAuras);
    }

    [Fact]
    public void V5HealthMapper_Disconnected_NoFrame_PreservesHealth()
    {
        // Negative control: Disconnected should remain Disconnected.
        StableReadResult disconnected = StableReadResult.Disconnected("transport lost");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(disconnected, now);

        Assert.NotNull(frame.Provider);
        Assert.Equal(ProviderHealth.Disconnected, frame.Provider.Health);
        Assert.Equal("transport lost", frame.Provider.Fault);
    }

    [Fact]
    public void V5HealthMapper_Faulted_PreservesFailureDetail_InProviderStatus()
    {
        // The ProviderStatus.Fault field must carry the failure detail
        // so the evaluator can surface it in stop messages.
        string detail = "Both buffers have CRC mismatch (consecutive faults: 5)";
        StableReadResult faulted = StableReadResult.Faulted(detail);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ProviderStatus status = V5HealthMapper.ToProviderStatus(faulted, now);

        Assert.Equal(ProviderHealth.Faulted, status.Health);
        Assert.Equal(detail, status.Fault);
    }

    // =====================================================================
    // 5. Cross-cutting: aura-known end-to-end through evaluator
    // =====================================================================

    [Fact]
    public void Evaluator_MixedAuraKnowledge_BothSides_RejectsIfEitherUnknown()
    {
        // When player auras are unknown and rule requires target auras,
        // both sides must be known for the rule to proceed.
        var profile = CreateAuraTestProfile(
            requiredPlayerAuras: ["buff-shield"],
            requiredTargetAuras: ["mark-vuln"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: false);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        // Target aura unknown → reject.
        Assert.NotEqual(ControllerState.Armed, result.State);
    }

    [Fact]
    public void Evaluator_MixedAuraKnowledge_BothKnown_PermitsWhenSatisfied()
    {
        // Both sides known and all auras present → Armed.
        var profile = CreateAuraTestProfile(
            requiredPlayerAuras: ["buff-shield"],
            requiredTargetAuras: ["mark-vuln"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras:
            [
                new AuraState("buff-shield", "Shield", null, 1, 8000, IsDebuff: false),
            ],
            targetAuras:
            [
                new AuraState("mark-vuln", "Mark", null, 1, 12000, IsDebuff: true),
            ]);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    [Fact]
    public void Evaluator_KnownWithPresentRequiredAura_SatisfiesRule()
    {
        // Required player aura is present in the known list → no rejection.
        var profile = CreateAuraTestProfile(requiredPlayerAuras: ["buff-haste"]);
        var frame = CreateAuraTestFrame(
            playerAurasKnown: true,
            targetAurasKnown: true,
            playerAuras:
            [
                new AuraState("buff-haste", "Haste", null, 2, 15000, IsDebuff: false),
            ]);
        var evaluator = new CombatEvaluator(MaxAge);

        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.Equal(ControllerState.Armed, result.State);
        Assert.True(result.HasAction);
    }

    // =====================================================================
    // 6. Regression: TelemetryFrame defaults produce unknown aura state
    // =====================================================================

    [Fact]
    public void TelemetryFrame_Defaults_AreAuraUnknown()
    {
        // Contract: constructing a TelemetryFrame without explicit aura-known
        // flags must default both to false (unknown), forcing producers to
        // declare authoritative aura state.
        var frame = new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "default-test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: DateTimeOffset.UtcNow,
                Age: TimeSpan.Zero),
            Player: CreatePlayerState(),
            Target: CreateTargetState(),
            Abilities: new Dictionary<string, AbilityState>(),
            PlayerAuras: [],
            TargetAuras: []);

        Assert.False(frame.IsPlayerAurasKnown);
        Assert.False(frame.IsTargetAurasKnown);
    }

    [Fact]
    public void Evaluator_ForbiddenAura_DefaultsUnknown_RejectsRule()
    {
        // Contract: TelemetryFrame constructed without explicit aura-known
        // flags defaults to unknown (false). A forbidden-aura rule on that
        // frame must reject because the producer did not declare aura state.
        var profile = CreateAuraTestProfile(forbiddenPlayerAuras: ["debuff-dot"]);
        var frame = new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: "default-forbidden-test",
                Sequence: 1,
                ProducerFrameMilliseconds: 0,
                ReceivedAtUtc: DateTimeOffset.UtcNow,
                Age: TimeSpan.Zero),
            Player: CreatePlayerState(),
            Target: CreateTargetState(),
            Abilities: new Dictionary<string, AbilityState>
            {
                ["heal-id"] = new AbilityState(
                    Id: "heal-id",
                    Name: "Heal",
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
        // No IsPlayerAurasKnown/IsTargetAurasKnown passed → defaults false.

        var evaluator = new CombatEvaluator(MaxAge);
        EvaluationResult result = evaluator.Evaluate(profile, frame);

        Assert.False(frame.IsPlayerAurasKnown);
        Assert.NotEqual(ControllerState.Armed, result.State);
        Assert.Contains(result.Rejections, r =>
            r.RuleId == "aura-rule" &&
            r.Reasons.Any(reason =>
                reason.Contains("aura", StringComparison.OrdinalIgnoreCase)));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static CombatProfile CreateAuraTestProfile(
        List<string>? requiredPlayerAuras = null,
        List<string>? forbiddenPlayerAuras = null,
        List<string>? requiredTargetAuras = null,
        List<string>? forbiddenTargetAuras = null) => new()
        {
            Id = "aura-test",
            ProfileVersion = 1,
            Character = new CharacterRequirements
            {
                Calling = "Warrior",
                MinimumLevel = 1,
                MaximumLevel = 75,
            },
            Abilities = new Dictionary<string, AbilityBinding>
            {
                ["heal"] = new() { AbilityId = "heal-id", Key = "1", Enabled = true },
            },
            Rules =
        [
            new CombatRule
            {
                Id = "aura-rule",
                Ability = "heal",
                Enabled = true,
                When = new RuleConditions
                {
                    TargetHostile = true,
                    RequiredPlayerAuras = requiredPlayerAuras ?? [],
                    ForbiddenPlayerAuras = forbiddenPlayerAuras ?? [],
                    RequiredTargetAuras = requiredTargetAuras ?? [],
                    ForbiddenTargetAuras = forbiddenTargetAuras ?? [],
                },
            },
        ],
        };

    private static TelemetryFrame CreateAuraTestFrame(
        bool playerAurasKnown,
        bool targetAurasKnown,
        IReadOnlyList<AuraState>? playerAuras = null,
        IReadOnlyList<AuraState>? targetAuras = null) => new(
        Provider: new ProviderStatus(
            Health: ProviderHealth.Healthy,
            ProtocolVersion: "5",
            SessionId: "aura-test-session",
            Sequence: 1,
            ProducerFrameMilliseconds: 0,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Age: TimeSpan.Zero),
        Player: CreatePlayerState(),
        Target: CreateTargetState(),
        Abilities: new Dictionary<string, AbilityState>
        {
            ["heal-id"] = new AbilityState(
                Id: "heal-id",
                Name: "Heal",
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
        PlayerAuras: playerAuras ?? [],
        TargetAuras: targetAuras ?? [],
        IsPlayerAurasKnown: playerAurasKnown,
        IsTargetAurasKnown: targetAurasKnown);

    private static CombatProfile CreateCompatibleProfile() => new()
    {
        Id = "telemetry-safety-test",
        ProfileVersion = 1,
        Character = new CharacterRequirements
        {
            Calling = "Warrior",
            MinimumLevel = 1,
            MaximumLevel = 75,
        },
        Abilities = new Dictionary<string, AbilityBinding>
        {
            ["attack"] = new() { AbilityId = "attack-id", Key = "1", Enabled = true },
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
                ["attack-id"] = new AbilityState(
                    Id: "attack-id",
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
            TargetAuras: [],
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
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

    private static V5BufferHeader CreateHeader(uint sectionsMask) => new()
    {
        Sequence = 1,
        ProducerFrameMs = 0,
        SectionsMask = sectionsMask,
        HeartbeatIntervalMs = 0,
        PayloadLength = 0,
        ProtocolVersion = V5Constants.ProtocolVersion,
        Flags = 0,
        Reserved = 0,
        Crc32 = 0,
    };

    private static ParsedV5Frame CreateParsedFrame(
        V5BufferHeader header,
        IReadOnlyList<ParsedAuraState>? playerAuras = null,
        IReadOnlyList<ParsedAuraState>? targetAuras = null) => new(
        Header: header,
        Provider: new ParsedProviderInfo(Guid.NewGuid(), 0, 5000, "test-client", 1),
        Player: new ParsedUnitState(
            Id: "player-1",
            Name: "Player",
            Level: 45,
            Calling: "Warrior",
            Flags: V5Constants.UnitFlagIsPlayer | V5Constants.UnitFlagIsAvailable | V5Constants.UnitFlagInCombat,
            Relation: V5Constants.RelationFriendly,
            HealthCurrent: 100,
            HealthMaximum: 100,
            ResourceCurrent: 50,
            ResourceMaximum: 100,
            ResourceKind: "mana",
            CastAbilityId: null,
            CastName: null,
            CastRemainingMs: V5Constants.NullInt32,
            CastDurationMs: V5Constants.NullInt32,
            CastFlags: 0),
        Target: new ParsedUnitState(
            Id: "target-1",
            Name: "Target",
            Level: 45,
            Calling: null,
            Flags: V5Constants.UnitFlagIsAvailable | V5Constants.UnitFlagInCombat,
            Relation: V5Constants.RelationHostile,
            HealthCurrent: 1000,
            HealthMaximum: 1000,
            ResourceCurrent: V5Constants.NullInt32,
            ResourceMaximum: V5Constants.NullInt32,
            ResourceKind: null,
            CastAbilityId: null,
            CastName: null,
            CastRemainingMs: V5Constants.NullInt32,
            CastDurationMs: V5Constants.NullInt32,
            CastFlags: 0),
        Abilities: [],
        PlayerAuras: playerAuras ?? [],
        TargetAuras: targetAuras ?? [],
        BufferIndex: 0);
}
