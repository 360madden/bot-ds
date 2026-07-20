using System.Collections.ObjectModel;
using BotDs.Core;

namespace BotDs.Reader.V5;

/// <summary>
/// Maps parsed V5 protocol data into BotDs.Core domain models (TelemetryFrame, UnitState, etc.).
/// The mapping is explicit; unknown or absent fields become nulls in the domain model.
/// </summary>
public static class V5HealthMapper
{
    /// <summary>
    /// Convert a StableReadResult into a Core ProviderStatus.
    /// </summary>
    public static ProviderStatus ToProviderStatus(StableReadResult result, DateTimeOffset receivedAt,
        long sourceGeneration = 0, TimeSpan gameStateEvidenceAge = default)
    {
        ParsedV5Frame? frame = result.Frame;
        ParsedProviderInfo? info = frame?.Provider;

        TimeSpan age = frame is null ? TimeSpan.MaxValue : result.Age;

        string healthReason = result.FailureDetail ?? result.TransportHealth.ToString();
        bool isHeartbeat = frame?.Header.IsHeartbeat ?? false;

        return new ProviderStatus(
            Health: result.TransportHealth,
            ProtocolVersion: "5",
            SessionId: info?.SessionId.ToString("D") ?? "",
            Sequence: frame?.Header.Sequence ?? 0,
            ProducerFrameMilliseconds: frame?.Header.ProducerFrameMs ?? 0,
            ReceivedAtUtc: receivedAt,
            Age: age,
            ClientVersion: info?.ClientVersion,
            Fault: result.TransportHealth is ProviderHealth.Faulted or ProviderHealth.Disconnected
                ? healthReason
                : null,
            IsTruncated: result.TransportHealth == ProviderHealth.Faulted,
            IsHeartbeat: isHeartbeat,
            SourceGeneration: sourceGeneration,
            GameStateEvidenceAge: isHeartbeat ? gameStateEvidenceAge : age);
    }

    /// <summary>
    /// Convert a parsed V5 frame into a Core TelemetryFrame.
    /// When no frame is present, preserves the provider health/fault status
    /// and marks all game-state completeness as unknown.
    /// </summary>
    public static TelemetryFrame ToTelemetryFrame(
        StableReadResult result,
        DateTimeOffset receivedAt,
        long sourceGeneration = 0,
        TimeSpan gameStateEvidenceAge = default)
    {
        if (result.Frame is not { } frame)
        {
            ProviderStatus provider = ToProviderStatus(result, receivedAt, sourceGeneration, gameStateEvidenceAge);
            return new TelemetryFrame(
                Provider: provider,
                Player: null,
                Target: null,
                Abilities: ReadOnlyDictionary<string, AbilityState>.Empty,
                PlayerAuras: [],
                TargetAuras: [],
                IsPlayerAurasKnown: false,
                IsTargetAurasKnown: false);
        }

        ProviderStatus status = ToProviderStatus(result, receivedAt, sourceGeneration, gameStateEvidenceAge);

        UnitState? player = ToUnitState(frame.Player);
        UnitState? target = ToUnitState(frame.Target);

        Dictionary<string, AbilityState> abilities = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParsedAbilityState a in frame.Abilities)
        {
            if (string.IsNullOrEmpty(a.AbilityId)) continue;
            abilities[a.AbilityId] = ToAbilityState(a);
        }

        List<AuraState> playerAuras = frame.PlayerAuras
            .Where(a => !string.IsNullOrEmpty(a.AuraId))
            .Select(ToAuraState)
            .ToList();

        List<AuraState> targetAuras = frame.TargetAuras
            .Where(a => !string.IsNullOrEmpty(a.AuraId))
            .Select(ToAuraState)
            .ToList();

        bool abilitiesKnown = frame.Header.HasSection(V5Constants.MaskAbilities);
        bool playerAurasKnown = frame.Header.HasSection(V5Constants.MaskPlayerAuras);
        bool targetAurasKnown = frame.Header.HasSection(V5Constants.MaskTargetAuras);

        return new TelemetryFrame(
            Provider: status,
            Player: player,
            Target: target,
            Abilities: new ReadOnlyDictionary<string, AbilityState>(abilities),
            PlayerAuras: playerAuras.AsReadOnly(),
            TargetAuras: targetAuras.AsReadOnly(),
            IsAbilitiesKnown: abilitiesKnown,
            IsPlayerAurasKnown: playerAurasKnown,
            IsTargetAurasKnown: targetAurasKnown);
    }

    // ── Individual field mappers ──────────────────────────────

    private static UnitState? ToUnitState(ParsedUnitState? parsed)
    {
        if (parsed is null)
            return null;

        // Honor UnitFlagIsAvailable: unavailable units must not surface
        // as available through residual wire fields.
        if (!parsed.IsAvailable)
            return null;

        HealthState health = new(
            Current: parsed.HealthCurrent != V5Constants.NullInt32 ? parsed.HealthCurrent : null,
            Maximum: parsed.HealthMaximum != V5Constants.NullInt32 ? parsed.HealthMaximum : null);

        ResourceState? resource = null;
        if (parsed.ResourceMaximum != V5Constants.NullInt32 || parsed.ResourceCurrent != V5Constants.NullInt32)
        {
            resource = new ResourceState(
                Kind: parsed.ResourceKind ?? "unknown",
                Current: parsed.ResourceCurrent != V5Constants.NullInt32 ? parsed.ResourceCurrent : null,
                Maximum: parsed.ResourceMaximum != V5Constants.NullInt32 ? parsed.ResourceMaximum : null);
        }

        CastState? cast = null;
        if (parsed.CastRemainingMs != V5Constants.NullInt32 || !string.IsNullOrEmpty(parsed.CastAbilityId))
        {
            cast = new CastState(
                AbilityId: string.IsNullOrEmpty(parsed.CastAbilityId) ? null : parsed.CastAbilityId,
                Name: string.IsNullOrEmpty(parsed.CastName) ? null : parsed.CastName,
                RemainingMilliseconds: parsed.CastRemainingMs != V5Constants.NullInt32 ? parsed.CastRemainingMs : null,
                DurationMilliseconds: parsed.CastDurationMs != V5Constants.NullInt32 ? parsed.CastDurationMs : null,
                IsChannel: (parsed.CastFlags & V5Constants.CastFlagIsChannel) != 0,
                IsInterruptible: (parsed.CastFlags & V5Constants.CastFlagIsUninterruptible) == 0);
        }

        string? relation = parsed.Relation switch
        {
            V5Constants.RelationHostile => "hostile",
            V5Constants.RelationFriendly => "friendly",
            V5Constants.RelationNeutral => "neutral",
            _ => null,
        };

        // Protocol-guaranteed flags: the Flags byte and ability Flags byte are
        // always present in their respective fixed records. Map explicit false
        // to false (not null) so consumers can distinguish "no" from "unknown".
        return new UnitState(
            Id: string.IsNullOrEmpty(parsed.Id) ? null : parsed.Id,
            Name: string.IsNullOrEmpty(parsed.Name) ? null : parsed.Name,
            Level: parsed.Level != V5Constants.NullInt32 ? parsed.Level : null,
            Calling: string.IsNullOrEmpty(parsed.Calling) ? null : parsed.Calling,
            IsPlayer: parsed.IsPlayer,
            Relation: relation,
            Health: health,
            Resource: resource,
            InCombat: (parsed.Flags & V5Constants.UnitFlagInCombat) != 0,
            Cast: cast);
    }

    private static AbilityState ToAbilityState(ParsedAbilityState parsed)
    {
        // Protocol-guaranteed flags: the ability Flags byte is always present
        // in the fixed-size record. Map explicit false to false (not null).
        return new AbilityState(
            Id: parsed.AbilityId,
            Name: parsed.AbilityId, // name not carried in the fixed record; use ID
            Available: parsed.Available,
            Usable: parsed.Usable,
            InRange: parsed.InRange,
            CooldownRemainingMilliseconds: parsed.CooldownRemainingMs >= 0 ? parsed.CooldownRemainingMs : null,
            CooldownDurationMilliseconds: parsed.CooldownDurationMs >= 0 ? parsed.CooldownDurationMs : null,
            TargetId: null, // not in current wire format
            Costs: new ReadOnlyDictionary<string, int>(new Dictionary<string, int>
            {
                ["primary"] = parsed.ResourceCost
            }),
            CastTimeMilliseconds: parsed.CastTimeMs >= 0 ? parsed.CastTimeMs : null,
            IsChannel: parsed.IsChanneled,
            IsPassive: parsed.IsPassive);
    }

    private static AuraState ToAuraState(ParsedAuraState parsed)
    {
        return new AuraState(
            Id: parsed.AuraId,
            Name: parsed.Name,
            CasterId: null, // not in current wire format
            Stacks: parsed.Stacks,
            RemainingMilliseconds: parsed.RemainingMs >= 0 ? parsed.RemainingMs : null,
            IsDebuff: parsed.IsDebuff);
    }
}
