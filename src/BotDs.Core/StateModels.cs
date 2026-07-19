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
    bool IsTruncated = false)
{
    public bool IsUsable(TimeSpan maximumAge) =>
        Health == ProviderHealth.Healthy &&
        !IsTruncated &&
        Age >= TimeSpan.Zero &&
        Age <= maximumAge;
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
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Id) && Health.Maximum is > 0;
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

public sealed record TelemetryFrame(
    ProviderStatus Provider,
    UnitState? Player,
    UnitState? Target,
    IReadOnlyDictionary<string, AbilityState> Abilities,
    IReadOnlyList<AuraState> PlayerAuras,
    IReadOnlyList<AuraState> TargetAuras)
{
    public static TelemetryFrame Empty(DateTimeOffset now) => new(
        new ProviderStatus(ProviderHealth.Disconnected, "", "", 0, 0, now, TimeSpan.MaxValue),
        null,
        null,
        ReadOnlyDictionary<string, AbilityState>.Empty,
        [],
        []);
}
