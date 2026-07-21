using System.Collections.ObjectModel;

namespace BotDs.Core;

public enum ProviderHealth
{
    Disconnected,
    Discovering,
    Synchronizing,
    Healthy,
    Degraded,
    Stale,
    Faulted,
}

public enum ControllerState
{
    Disarmed,
    WaitingForPlayer,
    WaitingForTarget,
    Evaluating,
    ActionPending,
    Armed,
    Stopped,
    Faulted,
}

public enum StopReason
{
    None,
    UserRequested,
    EmergencyStop,
    ProviderUnavailable,
    TelemetryStale,
    IntegrityFailure,
    SequenceDiscontinuity,
    PlayerUnavailable,
    PlayerDead,
    ProfileMismatch,
    ForegroundMismatch,
    ProcessExited,
    LoadingOrZoning,
    ActionNotAcknowledged,
    RateLimited,
    ExternalActionConflict,
    UnhandledError,
}

public sealed record ProviderStatus(
    ProviderHealth Health,
    string ProtocolVersion,
    string SessionId,
    ulong Sequence,
    long ProducerFrameMilliseconds,
    DateTimeOffset ReceivedAtUtc,
    TimeSpan Age,
    string? ClientVersion = null,
    string? Fault = null,
    bool IsTruncated = false,
    bool IsHeartbeat = false,
    long SourceGeneration = 0,
    TimeSpan GameStateEvidenceAge = default)
{
    public bool IsUsable(TimeSpan maximumAge) => IsUsable(maximumAge, DateTimeOffset.UtcNow);

    public bool IsUsable(TimeSpan maximumAge, DateTimeOffset nowUtc)
    {
        if (Health != ProviderHealth.Healthy || IsTruncated || maximumAge < TimeSpan.Zero)
            return false;

        // Heartbeats don't carry fresh game-state evidence — use carried evidence age
        TimeSpan baseAge = IsHeartbeat ? GameStateEvidenceAge : Age;
        if (baseAge < TimeSpan.Zero || nowUtc < ReceivedAtUtc)
            return false;
        TimeSpan elapsed = nowUtc - ReceivedAtUtc;
        if (baseAge.Ticks > TimeSpan.MaxValue.Ticks - elapsed.Ticks)
            return false;
        TimeSpan effectiveAge = TimeSpan.FromTicks(baseAge.Ticks + elapsed.Ticks);
        return effectiveAge <= maximumAge;
    }

}

public sealed record HealthState(int? Current, int? Maximum)
{
    public double? Percent => Current is >= 0 && Maximum is > 0
        ? Math.Clamp(Current.Value * 100d / Maximum.Value, 0d, 100d)
        : null;

    public bool IsDead => Current == 0 && Maximum is > 0;
}

public sealed record ResourceState(string Kind, int? Current, int? Maximum)
{
    public double? Percent => Current is >= 0 && Maximum is > 0
        ? Math.Clamp(Current.Value * 100d / Maximum.Value, 0d, 100d)
        : null;
}

public sealed record CastState(
    string? AbilityId,
    string? Name,
    int? RemainingMilliseconds,
    int? DurationMilliseconds,
    bool? IsChannel,
    bool? IsInterruptible)
{
    public bool IsCasting => RemainingMilliseconds is > 0;
}

public sealed record UnitState(
    string? Id,
    string? Name,
    int? Level,
    string? Calling,
    bool? IsPlayer,
    string? Relation,
    HealthState Health,
    ResourceState? Resource,
    bool? InCombat,
    CastState? Cast)
{
    public bool IsHostile => string.Equals(Relation, "hostile", StringComparison.OrdinalIgnoreCase);
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Id) && Health.Maximum is > 0 && Health.Current is >= 0;
}

public sealed record AbilityState(
    string Id,
    string Name,
    bool Available,
    bool? Usable,
    bool? InRange,
    int? CooldownRemainingMilliseconds,
    int? CooldownDurationMilliseconds,
    string? TargetId,
    IReadOnlyDictionary<string, int> Costs,
    int? CastTimeMilliseconds,
    bool? IsChannel,
    bool? IsPassive)
{
    public bool IsReady => Available && CooldownRemainingMilliseconds is 0;
}

public sealed record AuraState(
    string Id,
    string Name,
    string? CasterId,
    int Stacks,
    int? RemainingMilliseconds,
    bool IsDebuff);

/// <summary>
/// Explicit selected-target knownness (M2 / PLAN.md). Distinct from a null
/// <see cref="UnitState"/> residual: omitted inspection vs known empty target.
/// </summary>
public enum TargetKnownness
{
    /// <summary>Target section absent or inspection incomplete — do not treat as no-target.</summary>
    Unknown = 0,

    /// <summary>Inspection succeeded and the player has no selected target.</summary>
    KnownNoTarget = 1,

    /// <summary>A selected target is available in telemetry.</summary>
    KnownTarget = 2,
}

/// <summary>
/// Transport-neutral live telemetry source (M2). Implementations publish
/// normalized frames without combat-profile or calling-specific logic.
/// </summary>
public interface ITelemetrySource
{
    /// <summary>Latest assembled observation snapshot (may be empty/disconnected).</summary>
    TelemetryFrame Current { get; }
}

/// <summary>Observed action-bar slot placement (ability id only; keys remain user-configured).</summary>
public sealed record ActionBarSlotState(int Slot, string AbilityId);

public sealed record TelemetryFrame(
    ProviderStatus Provider,
    UnitState? Player,
    UnitState? Target,
    IReadOnlyDictionary<string, AbilityState> Abilities,
    IReadOnlyList<AuraState> PlayerAuras,
    IReadOnlyList<AuraState> TargetAuras,
    bool IsAbilitiesKnown = false,
    bool IsPlayerAurasKnown = false,
    bool IsTargetAurasKnown = false,
    TargetKnownness TargetKnownness = TargetKnownness.Unknown,
    bool? GameInputReady = null,
    IReadOnlyList<ActionBarSlotState>? ActionBarSlots = null,
    bool IsActionBarKnown = false,
    int? ActionBarPage = null)
{
    public static TelemetryFrame Empty(DateTimeOffset now) => new(
        new ProviderStatus(ProviderHealth.Disconnected, "", "", 0, 0, now, TimeSpan.MaxValue,
            IsHeartbeat: false, SourceGeneration: 0, GameStateEvidenceAge: TimeSpan.MaxValue),
        null,
        null,
        ReadOnlyDictionary<string, AbilityState>.Empty,
        [],
        [],
        IsAbilitiesKnown: false,
        IsPlayerAurasKnown: false,
        IsTargetAurasKnown: false,
        TargetKnownness: TargetKnownness.Unknown,
        GameInputReady: null,
        ActionBarSlots: null,
        IsActionBarKnown: false,
        ActionBarPage: null);
}
