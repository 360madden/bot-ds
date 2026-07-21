namespace BotDs.Core;

/// <summary>
/// Per-binding key calibration state for the active profile generation (PLAN.md §10 / M8).
/// </summary>
public enum BindingVerificationState
{
    Unverified = 0,
    Verified = 1,
    Mismatch = 2,
}

/// <summary>
/// Snapshot of binding verification for dashboard and readiness checks.
/// </summary>
public sealed record BindingVerificationSnapshot(
    long Generation,
    string? ProfileId,
    string? ProviderSessionId,
    IReadOnlyDictionary<string, BindingVerificationState> States);

/// <summary>
/// Tracks Unverified/Verified/Mismatch for ability aliases on the active profile generation.
/// Invalidated by profile identity changes and provider session changes.
/// </summary>
public sealed class BindingVerificationTracker
{
    private readonly object _lock = new();
    private long _generation = 1;
    private string? _profileId;
    private string? _providerSessionId;
    private readonly Dictionary<string, BindingVerificationState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public long Generation
    {
        get { lock (_lock) return _generation; }
    }

    public BindingVerificationSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new BindingVerificationSnapshot(
                _generation,
                _profileId,
                _providerSessionId,
                new Dictionary<string, BindingVerificationState>(_states, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Align tracker with the active profile and provider session.
    /// Returns true when generation advanced (all prior verification cleared).
    /// First assignment of a null identity does not clear manually recorded states;
    /// only a real profile/session change invalidates verification.
    /// </summary>
    public bool Align(string? profileId, string? providerSessionId)
    {
        lock (_lock)
        {
            bool profileChanged = _profileId is not null
                && !string.Equals(_profileId, profileId, StringComparison.OrdinalIgnoreCase);
            bool sessionChanged = _providerSessionId is not null
                && !string.Equals(_providerSessionId, providerSessionId, StringComparison.Ordinal);

            // Capture first-seen identities without wiping manual pre-arm verification.
            if (!profileChanged && !sessionChanged)
            {
                _profileId ??= profileId;
                _providerSessionId ??= providerSessionId;
                return false;
            }

            _profileId = profileId;
            _providerSessionId = providerSessionId;
            _states.Clear();
            _generation++;
            return true;
        }
    }

    public void InvalidateAll(string reason)
    {
        lock (_lock)
        {
            _states.Clear();
            _generation++;
            _ = reason;
        }
    }

    public BindingVerificationState GetState(string abilityAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abilityAlias);
        lock (_lock)
        {
            return _states.TryGetValue(abilityAlias, out BindingVerificationState state)
                ? state
                : BindingVerificationState.Unverified;
        }
    }

    public void SetState(string abilityAlias, BindingVerificationState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abilityAlias);
        lock (_lock)
        {
            _states[abilityAlias] = state;
        }
    }

    public void MarkVerified(string abilityAlias) => SetState(abilityAlias, BindingVerificationState.Verified);

    public void MarkMismatch(string abilityAlias) => SetState(abilityAlias, BindingVerificationState.Mismatch);

    /// <summary>
    /// Returns aliases that block Live arming: enabled required in-range bindings
    /// that are not currently Verified (Unverified or Mismatch).
    /// </summary>
    public IReadOnlyList<string> GetLiveBlockers(CombatProfile profile, int? playerLevel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var blockers = new List<string>();

        lock (_lock)
        {
            foreach ((string alias, AbilityBinding binding) in profile.Abilities)
            {
                if (binding is not { Enabled: true, Required: true })
                    continue;
                if (!IsLevelInRange(binding, playerLevel))
                    continue;

                BindingVerificationState state = _states.TryGetValue(alias, out BindingVerificationState s)
                    ? s
                    : BindingVerificationState.Unverified;

                if (state == BindingVerificationState.Verified)
                    continue;

                blockers.Add(state == BindingVerificationState.Mismatch
                    ? $"Binding '{alias}' verification is Mismatch — re-calibrate before Live."
                    : $"Binding '{alias}' is Unverified — calibrate in DryRun before Live.");
            }
        }

        return blockers;
    }

    private static bool IsLevelInRange(AbilityBinding binding, int? playerLevel)
    {
        if (playerLevel is null or <= 0)
            return true;
        if (binding.MinimumLevel.HasValue && playerLevel < binding.MinimumLevel.Value)
            return false;
        if (binding.MaximumLevel.HasValue && playerLevel > binding.MaximumLevel.Value)
            return false;
        return true;
    }
}
