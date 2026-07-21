namespace BotDs.Core;

/// <summary>
/// Detects unexpected ability cooldown transitions while armed that are not
/// explained by the coordinator's pending BotDs action (M8 external-action conflict).
/// </summary>
public static class ExternalActionConflictDetector
{
    /// <summary>
    /// Returns true when a profile-enabled ability entered cooldown without a matching pending action.
    /// </summary>
    public static bool TryDetect(
        CombatProfile profile,
        TelemetryFrame previous,
        TelemetryFrame current,
        ActionDecision? pendingAction,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        detail = string.Empty;

        if (!previous.IsAbilitiesKnown || !current.IsAbilitiesKnown)
            return false;

        // Require a newer sequence so we do not compare identical snapshots.
        if (current.Provider.Sequence <= previous.Provider.Sequence)
            return false;

        if (!string.Equals(previous.Provider.SessionId, current.Provider.SessionId, StringComparison.Ordinal))
            return false;

        foreach ((string alias, AbilityBinding binding) in profile.Abilities)
        {
            if (binding is not { Enabled: true })
                continue;
            if (string.IsNullOrWhiteSpace(binding.AbilityId))
                continue;

            if (!previous.Abilities.TryGetValue(binding.AbilityId, out AbilityState? prevAbility)
                || prevAbility is null)
                continue;
            if (!current.Abilities.TryGetValue(binding.AbilityId, out AbilityState? currAbility)
                || currAbility is null)
                continue;

            bool wasReady = prevAbility.CooldownRemainingMilliseconds is null or <= 0;
            bool nowOnCooldown = currAbility.CooldownRemainingMilliseconds is > 0;
            if (!wasReady || !nowOnCooldown)
                continue;

            // Explained by our own pending dispatch for this ability.
            if (pendingAction is not null
                && string.Equals(pendingAction.AbilityId, binding.AbilityId, StringComparison.Ordinal))
            {
                continue;
            }

            detail =
                $"External action conflict: ability '{binding.AbilityId}' (alias '{alias}') entered cooldown " +
                "while armed without a matching BotDs pending action.";
            return true;
        }

        return false;
    }
}
