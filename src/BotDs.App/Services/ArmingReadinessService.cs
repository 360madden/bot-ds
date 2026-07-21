using BotDs.Core;

namespace BotDs.App.Services;

/// <summary>
/// Centralized arming readiness check shared by the dashboard, evaluator,
/// and action coordinator. Evaluates all pre-arm conditions in one place.
/// </summary>
public sealed class ArmingReadinessService
{
    private readonly SnapshotPublisher _publisher;
    private readonly ProfileService _profiles;
    private readonly TimeProvider _timeProvider;

    public ArmingReadinessService(
        SnapshotPublisher publisher,
        ProfileService profiles,
        TimeProvider? timeProvider = null)
    {
        _publisher = publisher;
        _profiles = profiles;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Evaluates all pre-arm conditions. Returns a result with a boolean
    /// <c>CanArm</c> flag and a list of human-readable blockers.
    /// </summary>
    public ReadinessResult Evaluate(TimeSpan maxTelemetryAge)
    {
        var blockers = new List<string>();
        var warnings = new List<ArmingWarning>();
        TelemetryFrame frame = _publisher.Latest;

        // ── Profile checks ──────────────────────────────────
        CombatProfile? profile = _profiles.ActiveProfile;
        if (profile is null)
        {
            blockers.Add("No active profile selected.");
        }
        else
        {
            if (!profile.Enabled)
            {
                blockers.Add("Active profile is disabled.");
            }

            if (profile.Abilities is null || !profile.Abilities.Values.Any(b => b is { Enabled: true }))
            {
                blockers.Add("Profile has no enabled ability bindings.");
            }

            if (profile.Rules is null || !profile.Rules.Any(r => r is { Enabled: true }))
            {
                blockers.Add("Profile has no enabled combat rules.");
            }

            // Check that at least one enabled rule references an enabled binding
            if (profile.Abilities is not null && profile.Rules is not null)
            {
                bool hasActionableRule = profile.Rules
                    .Where(r => r is { Enabled: true })
                    .Any(r => !string.IsNullOrWhiteSpace(r.Ability)
                        && profile.Abilities.TryGetValue(r.Ability, out var b)
                        && b is { Enabled: true });
                if (!hasActionableRule)
                {
                    blockers.Add("No enabled rule references an enabled ability binding.");
                }
            }
        }

        // ── Provider checks (always evaluate — even without a profile) ──
        var provider = frame.Provider;
        if (provider is null)
        {
            blockers.Add("Telemetry provider is unavailable.");
            return new ReadinessResult(false, blockers, warnings, profile, frame);
        }

        if (!provider.IsUsable(maxTelemetryAge, _timeProvider.GetUtcNow()))
        {
            blockers.Add($"Telemetry is not healthy and fresh (health={provider.Health}, age={provider.Age.TotalMilliseconds:F0}ms).");
        }

        if (provider.IsTruncated)
        {
            blockers.Add("Telemetry frame is truncated.");
        }

        // ── Player checks ───────────────────────────────────
        var player = frame.Player;
        if (player is null || !player.IsAvailable)
        {
            blockers.Add("Player state is unavailable.");
        }
        else
        {
            if (player.Health.IsDead)
                blockers.Add("Player is dead.");
            if (player.Level is null)
                blockers.Add("Player level is unknown.");

            // Progression matching (profile optional)
            if (profile?.Character is not null && player.Level is not null)
            {
                if (profile.Character.MinimumLevel.HasValue && player.Level < profile.Character.MinimumLevel)
                    blockers.Add($"Player level {player.Level} is below profile minimum {profile.Character.MinimumLevel}.");
                if (profile.Character.MaximumLevel.HasValue && player.Level > profile.Character.MaximumLevel)
                    blockers.Add($"Player level {player.Level} is above profile maximum {profile.Character.MaximumLevel}.");
            }

            // Calling match
            if (profile?.Character?.Calling is not null
                && !string.Equals(profile.Character.Calling, player.Calling, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add($"Profile calling '{profile.Character.Calling}' does not match player '{player.Calling}'.");
            }

            // Resource knownness
            if (player.Resource is null)
            {
                warnings.Add(new ArmingWarning("Player resource is unknown — resource conditions cannot be evaluated."));
            }
        }

        // ── Target checks ───────────────────────────────────
        var target = frame.Target;
        if (target is not null && target.IsAvailable)
        {
            if (!target.IsHostile)
                blockers.Add("Selected target is not hostile.");
            if (target.Health.IsDead)
                blockers.Add("Selected target is dead.");
        }
        else if (frame.TargetKnownness is TargetKnownness.KnownNoTarget)
        {
            blockers.Add("No live target selected.");
        }
        else if (frame.TargetKnownness is TargetKnownness.Unknown)
        {
            blockers.Add("Target state is unknown (inspection incomplete).");
        }
        else
        {
            blockers.Add("No live target selected.");
        }

        // ── Game input readiness (M2) ───────────────────────
        if (frame.GameInputReady is false)
            blockers.Add("Game input is not ready (chat/edit focus or blocked UI context).");
        else if (frame.GameInputReady is null)
            warnings.Add(new ArmingWarning("Game input readiness is unknown."));

        // ── Ability inventory checks ────────────────────────
        if (!frame.IsAbilitiesKnown)
        {
            warnings.Add(new ArmingWarning("Ability inventory is unknown — ability conditions may not evaluate."));
        }

        // Required ability reconciliation — only when a profile is active
        if (profile?.Abilities is not null && frame.Abilities is not null)
        {
            int playerLevel = player?.Level ?? 0;
            foreach ((string alias, AbilityBinding binding) in profile.Abilities)
            {
                if (binding is not { Enabled: true, Required: true }) continue;
                if (!IsLevelInRange(binding, playerLevel)) continue;
                if (string.IsNullOrWhiteSpace(binding.AbilityId))
                {
                    blockers.Add($"Required ability '{alias}' has no abilityId.");
                    continue;
                }
                if (!frame.IsAbilitiesKnown)
                {
                    warnings.Add(new ArmingWarning("Required ability check skipped — ability inventory is unknown."));
                    continue;
                }
                if (!frame.Abilities.TryGetValue(binding.AbilityId, out var ability)
                    || ability is null || !ability.Available)
                {
                    blockers.Add($"Required ability '{binding.AbilityId}' (alias: {alias}) is not available in telemetry.");
                }
            }
        }

        if (!frame.IsPlayerAurasKnown)
        {
            warnings.Add(new ArmingWarning("Player aura state is unknown — aura conditions may not evaluate."));
        }

        if (!frame.IsTargetAurasKnown)
        {
            warnings.Add(new ArmingWarning("Target aura state is unknown — aura conditions may not evaluate."));
        }

        return new ReadinessResult(
            blockers.Count == 0,
            blockers,
            warnings,
            profile,
            frame);
    }

    private static bool IsLevelInRange(AbilityBinding binding, int playerLevel)
    {
        if (playerLevel <= 0) return true; // level unknown — let evaluator handle it
        if (binding.MinimumLevel.HasValue && playerLevel < binding.MinimumLevel.Value) return false;
        if (binding.MaximumLevel.HasValue && playerLevel > binding.MaximumLevel.Value) return false;
        return true;
    }
}

/// <summary>
/// Result of an arming readiness evaluation.
/// </summary>
/// <param name="CanArm">True when all blocker conditions pass.</param>
/// <param name="Blockers">Human-readable reasons arming is blocked.</param>
/// <param name="Warnings">Non-blocking concerns to surface to the user.</param>
/// <param name="Profile">The active profile used for the evaluation, or null.</param>
/// <param name="Frame">The telemetry frame used for the evaluation, or null.</param>
public sealed record ReadinessResult(
    bool CanArm,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<ArmingWarning> Warnings,
    CombatProfile? Profile,
    TelemetryFrame? Frame);

/// <summary>
/// A non-blocking readiness concern surfaced to the user.
/// </summary>
/// <param name="Message">Human-readable warning.</param>
public sealed record ArmingWarning(string Message);
