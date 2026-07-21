namespace BotDs.Core;

/// <summary>
/// Result of comparing post-dispatch telemetry against a pending action baseline.
/// </summary>
public enum AcknowledgementMatch
{
    /// <summary>Evidence is not yet sufficient; keep waiting.</summary>
    Pending,

    /// <summary>Typed acknowledgement matched the pending action.</summary>
    Matched,

    /// <summary>
    /// Session, source generation, or target identity changed — discard the pending
    /// action without treating it as a successful acknowledgement.
    /// </summary>
    Invalidated,
}

/// <summary>
/// Snapshot of pre-dispatch evidence used by <see cref="ActionAcknowledgementMatcher"/>.
/// Captured while the telemetry fence is held, immediately before key dispatch.
/// </summary>
public sealed record PendingActionBaseline(
    ActionDecision Decision,
    string SessionId,
    long SourceGeneration,
    string? TargetId,
    int? AbilityCooldownRemainingMs,
    int? ResourceCurrent,
    string? PlayerCastAbilityId,
    IReadOnlySet<string> PlayerAuraIds,
    IReadOnlySet<string> TargetAuraIds,
    ulong HighWaterSequence,
    DateTimeOffset DispatchedAtUtc);

/// <summary>
/// Pure typed acknowledgement matcher (PLAN.md §8). Transport-neutral; no I/O.
/// </summary>
public static class ActionAcknowledgementMatcher
{
    public static PendingActionBaseline CaptureBaseline(
        ActionDecision decision,
        TelemetryFrame frame,
        DateTimeOffset dispatchedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(frame);

        int? cd = null;
        if (frame.IsAbilitiesKnown
            && frame.Abilities.TryGetValue(decision.AbilityId, out AbilityState? ability)
            && ability is not null)
        {
            cd = ability.CooldownRemainingMilliseconds;
        }

        return new PendingActionBaseline(
            Decision: decision,
            SessionId: frame.Provider.SessionId ?? string.Empty,
            SourceGeneration: frame.Provider.SourceGeneration,
            TargetId: frame.Target?.Id,
            AbilityCooldownRemainingMs: cd,
            ResourceCurrent: frame.Player?.Resource?.Current,
            PlayerCastAbilityId: frame.Player?.Cast?.AbilityId,
            PlayerAuraIds: ToIdSet(frame.PlayerAuras),
            TargetAuraIds: ToIdSet(frame.TargetAuras),
            HighWaterSequence: decision.FrameSequence,
            DispatchedAtUtc: dispatchedAtUtc);
    }

    public static AcknowledgementMatch TryMatch(PendingActionBaseline baseline, TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(frame);

        ProviderStatus provider = frame.Provider;
        if (provider is null)
            return AcknowledgementMatch.Pending;

        if (!string.Equals(provider.SessionId, baseline.SessionId, StringComparison.Ordinal))
            return AcknowledgementMatch.Invalidated;

        if (provider.SourceGeneration != baseline.SourceGeneration)
            return AcknowledgementMatch.Invalidated;

        // Evidence must come from a newer provider sequence than the pre-dispatch high-water mark.
        if (provider.Sequence <= baseline.HighWaterSequence)
            return AcknowledgementMatch.Pending;

        if (baseline.TargetId is not null)
        {
            string? currentTargetId = frame.Target?.Id;
            if (!string.Equals(currentTargetId, baseline.TargetId, StringComparison.Ordinal))
                return AcknowledgementMatch.Invalidated;
        }

        return baseline.Decision.Acknowledgement switch
        {
            AcknowledgementKind.Cast => MatchCast(baseline, frame),
            AcknowledgementKind.Cooldown => MatchCooldown(baseline, frame),
            AcknowledgementKind.Resource => MatchResource(baseline, frame),
            AcknowledgementKind.Aura => MatchAura(baseline, frame),
            AcknowledgementKind.CombatEvent => AcknowledgementMatch.Pending, // no combat-event section yet
            _ => AcknowledgementMatch.Pending,
        };
    }

    private static AcknowledgementMatch MatchCast(PendingActionBaseline baseline, TelemetryFrame frame)
    {
        CastState? cast = frame.Player?.Cast;
        if (cast is null || !cast.IsCasting)
            return AcknowledgementMatch.Pending;

        if (!string.Equals(cast.AbilityId, baseline.Decision.AbilityId, StringComparison.Ordinal))
            return AcknowledgementMatch.Pending;

        // Must be a new cast relative to the pre-dispatch observation.
        if (string.Equals(cast.AbilityId, baseline.PlayerCastAbilityId, StringComparison.Ordinal)
            && baseline.PlayerCastAbilityId is not null)
        {
            // Same ability already casting before dispatch — not fresh evidence.
            return AcknowledgementMatch.Pending;
        }

        return AcknowledgementMatch.Matched;
    }

    private static AcknowledgementMatch MatchCooldown(PendingActionBaseline baseline, TelemetryFrame frame)
    {
        if (!frame.IsAbilitiesKnown)
            return AcknowledgementMatch.Pending;

        if (!frame.Abilities.TryGetValue(baseline.Decision.AbilityId, out AbilityState? ability)
            || ability is null)
            return AcknowledgementMatch.Pending;

        int? current = ability.CooldownRemainingMilliseconds;
        if (current is null or <= 0)
            return AcknowledgementMatch.Pending;

        // Ability entered or re-entered cooldown after dispatch.
        int baselineCd = baseline.AbilityCooldownRemainingMs ?? 0;
        if (current.Value > 0 && (baselineCd <= 0 || current.Value > baselineCd))
            return AcknowledgementMatch.Matched;

        return AcknowledgementMatch.Pending;
    }

    private static AcknowledgementMatch MatchResource(PendingActionBaseline baseline, TelemetryFrame frame)
    {
        int? current = frame.Player?.Resource?.Current;
        if (current is null || baseline.ResourceCurrent is null)
            return AcknowledgementMatch.Pending;

        return current.Value != baseline.ResourceCurrent.Value
            ? AcknowledgementMatch.Matched
            : AcknowledgementMatch.Pending;
    }

    private static AcknowledgementMatch MatchAura(PendingActionBaseline baseline, TelemetryFrame frame)
    {
        // Without profile-declared aura id/transition predicates, only detect a
        // set-level change on the player aura list as weak provisional evidence.
        // Full aura acknowledgement requires M1-proven structured predicates.
        if (!frame.IsPlayerAurasKnown)
            return AcknowledgementMatch.Pending;

        var current = ToIdSet(frame.PlayerAuras);
        if (current.SetEquals(baseline.PlayerAuraIds))
            return AcknowledgementMatch.Pending;

        return AcknowledgementMatch.Matched;
    }

    private static HashSet<string> ToIdSet(IReadOnlyList<AuraState> auras)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (AuraState aura in auras)
        {
            if (!string.IsNullOrWhiteSpace(aura.Id))
                set.Add(aura.Id);
        }

        return set;
    }
}
