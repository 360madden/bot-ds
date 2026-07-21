using System.Collections.ObjectModel;
using BotDs.Core;

namespace BotDs.Tests;

public sealed class ActionAcknowledgementTests
{
    [Fact]
    public void Cooldown_ack_matches_when_ability_enters_cooldown()
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 10);
        TelemetryFrame pre = Frame(seq: 10, cooldownRemainingMs: 0);
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);

        TelemetryFrame post = Frame(seq: 11, session: pre.Provider.SessionId, cooldownRemainingMs: 1500);
        Assert.Equal(AcknowledgementMatch.Matched, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Fact]
    public void Cooldown_ack_pending_when_sequence_not_advanced()
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 10);
        TelemetryFrame pre = Frame(seq: 10, cooldownRemainingMs: 0);
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);

        TelemetryFrame post = Frame(seq: 10, session: pre.Provider.SessionId, cooldownRemainingMs: 1500);
        Assert.Equal(AcknowledgementMatch.Pending, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Fact]
    public void Session_change_invalidates_pending_action()
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 10);
        TelemetryFrame pre = Frame(seq: 10, cooldownRemainingMs: 0);
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);

        TelemetryFrame post = Frame(seq: 11, session: "other-session", cooldownRemainingMs: 1500);
        Assert.Equal(AcknowledgementMatch.Invalidated, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Fact]
    public void Target_change_invalidates_pending_action()
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", AcknowledgementKind.Cooldown, 10);
        TelemetryFrame pre = Frame(seq: 10, cooldownRemainingMs: 0, targetId: "t1");
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);

        TelemetryFrame post = Frame(seq: 11, session: pre.Provider.SessionId, cooldownRemainingMs: 1500, targetId: "t2");
        Assert.Equal(AcknowledgementMatch.Invalidated, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Fact]
    public void Cast_ack_matches_new_player_cast()
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", AcknowledgementKind.Cast, 5);
        TelemetryFrame pre = Frame(seq: 5, cooldownRemainingMs: 0, castAbilityId: null);
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);

        TelemetryFrame post = Frame(seq: 6, session: pre.Provider.SessionId, castAbilityId: "1001", castRemainingMs: 800);
        Assert.Equal(AcknowledgementMatch.Matched, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Theory]
    [InlineData(AcknowledgementKind.Resource)]
    [InlineData(AcknowledgementKind.Aura)]
    [InlineData(AcknowledgementKind.CombatEvent)]
    public void Weak_acknowledgements_remain_pending_without_explicit_predicates(
        AcknowledgementKind acknowledgement)
    {
        ActionDecision decision = new("r1", "slice", "1001", "1", acknowledgement, 10);
        TelemetryFrame pre = Frame(seq: 10);
        PendingActionBaseline baseline = ActionAcknowledgementMatcher.CaptureBaseline(
            decision, pre, DateTimeOffset.UtcNow);
        TelemetryFrame post = Frame(seq: 11, session: pre.Provider.SessionId) with
        {
            Player = pre.Player! with { Resource = new ResourceState("Power", 75, 100) },
            PlayerAuras = [new AuraState("a1", "Aura", "player-1", 1, 1000, false)],
        };

        Assert.Equal(AcknowledgementMatch.Pending, ActionAcknowledgementMatcher.TryMatch(baseline, post));
    }

    [Fact]
    public void Binding_tracker_blocks_unverified_required_bindings_for_live()
    {
        var tracker = new BindingVerificationTracker();
        var profile = new CombatProfile
        {
            Id = "p1",
            Enabled = true,
            Character = new CharacterRequirements { Calling = "Warrior" },
            Abilities = new Dictionary<string, AbilityBinding>
            {
                ["slice"] = new AbilityBinding { AbilityId = "1001", Key = "1", Enabled = true, Required = true },
            },
            Rules = [new CombatRule { Id = "r1", Ability = "slice", Enabled = true }],
        };

        IReadOnlyList<string> blockers = tracker.GetLiveBlockers(profile, playerLevel: 45);
        Assert.Single(blockers);
        Assert.Contains("Unverified", blockers[0], StringComparison.OrdinalIgnoreCase);

        tracker.MarkVerified("slice");
        Assert.Empty(tracker.GetLiveBlockers(profile, 45));
    }

    [Fact]
    public void Binding_tracker_invalidates_on_profile_or_session_change()
    {
        var tracker = new BindingVerificationTracker();
        tracker.Align("p1", "session-a");
        tracker.MarkVerified("slice");
        Assert.Equal(BindingVerificationState.Verified, tracker.GetState("slice"));

        Assert.True(tracker.Align("p1", "session-b"));
        Assert.Equal(BindingVerificationState.Unverified, tracker.GetState("slice"));
    }

    private static TelemetryFrame Frame(
        ulong seq,
        int? cooldownRemainingMs = 0,
        string? session = null,
        string? targetId = "target-1",
        string? castAbilityId = null,
        int? castRemainingMs = null)
    {
        var now = DateTimeOffset.UtcNow;
        string sessionId = session ?? "session-1";
        CastState? cast = castAbilityId is null
            ? null
            : new CastState(castAbilityId, "Slice", castRemainingMs ?? 500, 1000, false, true);

        return new TelemetryFrame(
            Provider: new ProviderStatus(
                Health: ProviderHealth.Healthy,
                ProtocolVersion: "5",
                SessionId: sessionId,
                Sequence: seq,
                ProducerFrameMilliseconds: 16,
                ReceivedAtUtc: now,
                Age: TimeSpan.FromMilliseconds(5),
                SourceGeneration: 1),
            Player: new UnitState(
                Id: "player-1", Name: "Test", Level: 45, Calling: "Warrior",
                IsPlayer: true, Relation: "friendly",
                Health: new HealthState(5000, 5000),
                Resource: new ResourceState("Power", 100, 100),
                InCombat: true, Cast: cast),
            Target: targetId is null
                ? null
                : new UnitState(
                    Id: targetId, Name: "Mob", Level: 45, Calling: null,
                    IsPlayer: false, Relation: "hostile",
                    Health: new HealthState(3000, 3000),
                    Resource: null, InCombat: true, Cast: null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(
                new Dictionary<string, AbilityState>
                {
                    ["1001"] = new AbilityState(
                        "1001", "Slice", true, true, true,
                        cooldownRemainingMs, 1500, null,
                        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()),
                        0, false, false),
                }),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true);
    }
}
