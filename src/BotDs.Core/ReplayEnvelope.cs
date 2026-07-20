using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotDs.Core;

/// <summary>
/// Versioned replay envelope for deterministic testing. Captures observation
/// snapshots, monotonic time advances, controller commands, generations,
/// focus/process events, source faults, and dispatch outcomes.
/// </summary>
public sealed record ReplayEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("frames")]
    public required IReadOnlyList<ReplayFrame> Frames { get; init; }

    public static ReplayEnvelope Create(IReadOnlyList<ReplayFrame> frames) => new() { Frames = frames };
}

/// <summary>
/// A single step in a replay sequence. Contains a snapshot at this tick,
/// plus any control events injected between this tick and the next.
/// </summary>
public sealed record ReplayFrame
{
    /// <summary>Monotonic tick counter for deterministic ordering.</summary>
    [JsonPropertyName("tick")]
    public required long Tick { get; init; }

    /// <summary>Elapsed time since replay start (for time-dependent checks).</summary>
    [JsonPropertyName("elapsedMs")]
    public required long ElapsedMs { get; init; }

    /// <summary>The telemetry snapshot at this tick.</summary>
    [JsonPropertyName("snapshot")]
    public required ReplaySnapshot Snapshot { get; init; }

    /// <summary>Control commands injected at this tick (arm, disarm, estop, etc).</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyList<ReplayCommand> Commands { get; init; } = [];

    /// <summary>Source events at this tick (attach, detach, fault, session change).</summary>
    [JsonPropertyName("sourceEvents")]
    public IReadOnlyList<ReplaySourceEvent> SourceEvents { get; init; } = [];

    /// <summary>Expected output from this tick (null if no dispatch expected).</summary>
    [JsonPropertyName("expectedDispatch")]
    public ReplayDispatch? ExpectedDispatch { get; init; }
}

/// <summary>
/// A telemetry snapshot at a replay tick, containing only the fields
/// needed by the evaluator and coordinator for deterministic replay.
/// </summary>
public sealed record ReplaySnapshot
{
    [JsonPropertyName("provider")]
    public required ReplayProviderStatus Provider { get; init; }

    [JsonPropertyName("player")]
    public ReplayUnitState? Player { get; init; }

    [JsonPropertyName("target")]
    public ReplayUnitState? Target { get; init; }

    [JsonPropertyName("abilities")]
    public IReadOnlyDictionary<string, ReplayAbilityState> Abilities { get; init; } =
        new Dictionary<string, ReplayAbilityState>();

    [JsonPropertyName("playerAuras")]
    public IReadOnlyList<ReplayAuraState> PlayerAuras { get; init; } = [];

    [JsonPropertyName("targetAuras")]
    public IReadOnlyList<ReplayAuraState> TargetAuras { get; init; } = [];

    [JsonPropertyName("isAbilitiesKnown")]
    public bool IsAbilitiesKnown { get; init; } = true;

    [JsonPropertyName("isPlayerAurasKnown")]
    public bool IsPlayerAurasKnown { get; init; } = true;

    [JsonPropertyName("isTargetAurasKnown")]
    public bool IsTargetAurasKnown { get; init; } = true;

    /// <summary>Convert to a TelemetryFrame for evaluator consumption.</summary>
    public TelemetryFrame ToFrame()
    {
        var now = DateTimeOffset.UtcNow;
        return new TelemetryFrame(
            Provider: Provider.ToProviderStatus(now),
            Player: Player?.ToUnitState(),
            Target: Target?.ToUnitState(),
            Abilities: Abilities.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToAbilityState(),
                StringComparer.OrdinalIgnoreCase)
                .AsReadOnly(),
            PlayerAuras: PlayerAuras.Select(a => a.ToAuraState()).ToList(),
            TargetAuras: TargetAuras.Select(a => a.ToAuraState()).ToList(),
            IsAbilitiesKnown,
            IsPlayerAurasKnown,
            IsTargetAurasKnown);
    }
}

public sealed record ReplayProviderStatus
{
    public required ProviderHealth Health { get; init; }
    public string ProtocolVersion { get; init; } = "5";
    public string SessionId { get; init; } = "replay";
    public ulong Sequence { get; init; }
    public long ProducerFrameMilliseconds { get; init; }
    public long AgeMs { get; init; }

    public ProviderStatus ToProviderStatus(DateTimeOffset now) => new(
        Health,
        ProtocolVersion,
        SessionId,
        Sequence,
        ProducerFrameMilliseconds,
        now,
        TimeSpan.FromMilliseconds(AgeMs));
}

public sealed record ReplayUnitState
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public int? Level { get; init; }
    public string? Calling { get; init; }
    public bool? IsPlayer { get; init; }
    public string? Relation { get; init; }
    public int CurrentHealth { get; init; }
    public int MaxHealth { get; init; }
    public string? ResourceKind { get; init; }
    public int? ResourceCurrent { get; init; }
    public int? ResourceMax { get; init; }
    public bool? InCombat { get; init; }

    public UnitState ToUnitState() => new(
        Id,
        Name,
        Level,
        Calling,
        IsPlayer,
        Relation,
        new HealthState(CurrentHealth, MaxHealth),
        ResourceKind is not null
            ? new ResourceState(ResourceKind, ResourceCurrent, ResourceMax)
            : null,
        InCombat,
        null);
}

public sealed record ReplayAbilityState
{
    public required string Id { get; init; }
    public bool Available { get; init; } = true;
    public bool? Usable { get; init; } = true;
    public bool? InRange { get; init; } = true;
    public int CooldownRemainingMs { get; init; }

    public AbilityState ToAbilityState() => new(
        Id,
        Id,
        Available,
        Usable,
        InRange,
        CooldownRemainingMs,
        1500,
        null,
        new Dictionary<string, int>().AsReadOnly(),
        0,
        false,
        false);
}

public sealed record ReplayAuraState
{
    public required string Id { get; init; }
    public int Stacks { get; init; } = 1;

    public AuraState ToAuraState() => new(Id, Id, null, Stacks, null, false);
}

public enum ReplayCommandKind
{
    Arm,
    Disarm,
    EmergencyStop,
    ClearStop,
    SetProfile,
}

public sealed record ReplayCommand
{
    public required ReplayCommandKind Kind { get; init; }
    public string? Detail { get; init; }
}

public enum ReplaySourceEventKind
{
    Attach,
    Detach,
    Fault,
    SessionChange,
    ProcessExit,
}

public sealed record ReplaySourceEvent
{
    public required ReplaySourceEventKind Kind { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Expected dispatch at a tick. Used in replay tests to assert
/// that the evaluator produces the correct action.
/// </summary>
public sealed record ReplayDispatch
{
    public required string RuleId { get; init; }
    public required string AbilityAlias { get; init; }
    public required string AbilityId { get; init; }
    public required string Key { get; init; }
    public AcknowledgementKind Acknowledgement { get; init; } = AcknowledgementKind.Cooldown;
}
