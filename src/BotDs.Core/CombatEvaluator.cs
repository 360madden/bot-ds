namespace BotDs.Core;

public sealed record RuleRejection(string RuleId, IReadOnlyList<string> Reasons);

public sealed record ActionDecision(
    string RuleId,
    string AbilityAlias,
    string AbilityId,
    string Key,
    AcknowledgementKind Acknowledgement,
    ulong FrameSequence);

public sealed record EvaluationResult(
    ControllerState State,
    ActionDecision? Action,
    IReadOnlyList<RuleRejection> Rejections,
    StopReason StopReason = StopReason.None,
    string? Message = null)
{
    public bool HasAction => Action is not null;
}

public sealed class CombatEvaluator(TimeSpan maximumTelemetryAge, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public EvaluationResult Evaluate(CombatProfile profile, TelemetryFrame frame)
    {
        if (profile is null)
            return Stop(StopReason.IntegrityFailure, "Profile is null.");
        if (!profile.Enabled)
            return Stop(StopReason.ProfileMismatch, "Profile is disabled.");
        if (frame is null)
            return Stop(StopReason.IntegrityFailure, "Frame is null.");

        if (frame.Provider is null)
            return Stop(StopReason.TelemetryStale, "Provider status is null.");
        if (!frame.Provider.IsUsable(maximumTelemetryAge, _timeProvider.GetUtcNow()))
            return Stop(StopReason.TelemetryStale, $"Provider is not healthy and fresh; health={frame.Provider.Health}; truncated={frame.Provider.IsTruncated}; reported age={frame.Provider.Age.TotalMilliseconds:F0} ms.");

        if (profile.Character is null)
            return Stop(StopReason.ProfileMismatch, "Profile has no character requirements.");

        UnitState? player = frame.Player;
        if (player is null || player.Health is null || !player.IsAvailable)
            return Stop(StopReason.PlayerUnavailable, "Player state is unavailable.");
        if (player.Health.IsDead)
            return Stop(StopReason.PlayerDead, "Player health is zero.");
        if (!MatchesCharacter(profile.Character, player, out string? mismatch))
            return Stop(StopReason.ProfileMismatch, mismatch);

        // Defense-in-depth: enabled profiles must omit Build entirely. Disabled
        // profiles may retain a nonblank draft value, but they never reach here.
        if (profile.Character.Build is not null)
            return Stop(StopReason.ProfileMismatch, "V5 does not observe build identity; enabled profile must not specify a character build.");

        if (profile.Abilities is null || profile.Rules is null || frame.Abilities is null)
            return Stop(StopReason.IntegrityFailure, "Profile has no abilities or rules.");

        // Required binding reconciliation: applicable required bindings must be present and available.
        List<AbilityBinding> applicableRequired = [];
        foreach (AbilityBinding binding in profile.Abilities.Values)
        {
            if (binding is null || !binding.Enabled || !binding.Required)
                continue;
            if (IsLevelInRange(binding, player.Level))
                applicableRequired.Add(binding);
        }

        if (applicableRequired.Count > 0)
        {
            if (!frame.IsAbilitiesKnown)
                return Stop(StopReason.ProviderUnavailable,
                    $"Ability inventory is unknown but the profile has {applicableRequired.Count} required ability binding(s).");

            foreach (AbilityBinding binding in applicableRequired)
            {
                if (string.IsNullOrWhiteSpace(binding.AbilityId))
                    return Stop(StopReason.IntegrityFailure, "Required ability binding has no abilityId.");
                if (!frame.Abilities.TryGetValue(binding.AbilityId, out AbilityState? ability) || ability is null || !ability.Available)
                    return Stop(StopReason.ProfileMismatch,
                        $"Required ability '{binding.AbilityId}' is not available in telemetry.");
            }
        }

        // Prefer an available target object (covers legacy fixtures without TargetKnownness).
        // Explicit KnownNoTarget / Unknown still fail closed when no usable target is present.
        UnitState? target = frame.Target;
        if (target is null || target.Health is null || !target.IsAvailable)
        {
            string waitMsg = frame.TargetKnownness switch
            {
                TargetKnownness.KnownNoTarget => "Waiting for a live selected target.",
                TargetKnownness.Unknown => "Target state is unknown.",
                _ => "Waiting for a live selected target.",
            };
            return new EvaluationResult(ControllerState.WaitingForTarget, null, [], Message: waitMsg);
        }
        if (target.Health.IsDead)
            return new EvaluationResult(ControllerState.WaitingForTarget, null, [], Message: "Waiting for a live selected target.");
        if (!target.IsHostile)
            return new EvaluationResult(ControllerState.WaitingForTarget, null, [], Message: "Selected target is not hostile.");

        List<RuleRejection> rejections = [];
        bool anyRuleReachable = false;
        foreach (CombatRule rule in profile.Rules)
        {
            if (rule is null)
            {
                rejections.Add(new RuleRejection("(null)", ["Rule is null."]));
                continue;
            }
            if (!rule.Enabled)
            {
                rejections.Add(new RuleRejection(rule.Id, ["Rule is disabled."]));
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Ability)
                || !profile.Abilities.TryGetValue(rule.Ability, out AbilityBinding? binding)
                || binding is null)
            {
                rejections.Add(new RuleRejection(rule.Id, [$"Ability alias '{rule.Ability}' not found in profile."]));
                continue;
            }
            if (!binding.Enabled)
            {
                rejections.Add(new RuleRejection(rule.Id, [$"Ability binding '{rule.Ability}' is disabled."]));
                continue;
            }

            // A progression profile may contain rules for levels other than the
            // current player level. Those rules are valid profile data, but they
            // are not executable in this evaluation.
            if (IsLevelInRange(binding, player.Level))
                anyRuleReachable = true;
            List<string> reasons = EvaluateRule(rule, binding, frame, player, target);
            if (reasons.Count > 0)
            {
                rejections.Add(new RuleRejection(rule.Id, reasons));
                continue;
            }

            return new EvaluationResult(
                ControllerState.Armed,
                new ActionDecision(
                    rule.Id,
                    rule.Ability,
                    binding.AbilityId,
                    binding.Key,
                    rule.Acknowledgement,
                    frame.Provider.Sequence),
                rejections);
        }

        // Defense-in-depth: if all rules/bindings are disabled, stop rather than looping forever.
        if (!anyRuleReachable)
            return Stop(StopReason.IntegrityFailure, "Profile is enabled but has no enabled rule with an enabled binding.");

        return new EvaluationResult(ControllerState.Evaluating, null, rejections, Message: "No combat rule is currently valid.");
    }

    private static List<string> EvaluateRule(
        CombatRule rule,
        AbilityBinding binding,
        TelemetryFrame frame,
        UnitState player,
        UnitState target)
    {
        List<string> reasons = [];
        if (binding is null)
        {
            reasons.Add("Ability binding is null.");
            return reasons;
        }
        if (!binding.Enabled)
            reasons.Add("Ability binding is disabled.");
        if (string.IsNullOrWhiteSpace(binding.AbilityId))
        {
            reasons.Add("Ability binding has no abilityId.");
            return reasons;
        }
        if (binding.MinimumLevel.HasValue && player.Level < binding.MinimumLevel)
            reasons.Add("Player level is below the ability binding minimum.");
        if (binding.MaximumLevel.HasValue && player.Level > binding.MaximumLevel)
            reasons.Add("Player level is above the ability binding maximum.");
        if (!frame.IsAbilitiesKnown)
        {
            reasons.Add("Ability inventory is unknown.");
            return reasons;
        }
        if (!frame.Abilities.TryGetValue(binding.AbilityId, out AbilityState? ability) || ability is null || !ability.Available)
        {
            reasons.Add($"Ability '{binding.AbilityId}' is unavailable.");
            return reasons;
        }

        RuleConditions? when = rule.When;
        if (when is null)
        {
            reasons.Add("Rule has no conditions.");
            return reasons;
        }
        CheckBoolean(when.TargetIsPlayer, target.IsPlayer, "Target player classification is unknown or mismatched.", reasons);
        CheckBoolean(when.PlayerInCombat, player.InCombat, "Player combat state is unknown or mismatched.", reasons);
        CheckBoolean(when.TargetInCombat, target.InCombat, "Target combat state is unknown or mismatched.", reasons);
        CheckBoolean(when.TargetCasting, target.Cast?.IsCasting, "Target casting state is unknown or mismatched.", reasons);
        CheckBoolean(when.TargetCastInterruptible, target.Cast?.IsInterruptible, "Target interruptibility is unknown or mismatched.", reasons);

        if (when.TargetHostile && !target.IsHostile)
            reasons.Add("Target is not hostile.");
        if (when.AbilityUsable && ability.Usable != true)
            reasons.Add("Ability is not known usable.");
        if (when.AbilityInRange && ability.InRange != true)
            reasons.Add("Ability is not known in range.");
        if (when.CooldownReady && !ability.IsReady)
            reasons.Add("Ability cooldown is not ready.");
        if (when.ResourceAtLeast is int required)
        {
            if (player.Resource?.Current is not int current)
                reasons.Add("Resource is unknown.");
            else if (current < required)
                reasons.Add($"Resource is below {required}.");
        }

        if (player.Health is not null)
            CheckPercent(player.Health.Percent, when.PlayerHealthBelowPercent, when.PlayerHealthAbovePercent, "Player health", reasons);
        else
            reasons.Add("Player health is unknown.");
        if (target.Health is not null)
            CheckPercent(target.Health.Percent, when.TargetHealthBelowPercent, when.TargetHealthAbovePercent, "Target health", reasons);
        else
            reasons.Add("Target health is unknown.");
        CheckAuras(frame.PlayerAuras, frame.IsPlayerAurasKnown, when.RequiredPlayerAuras, when.ForbiddenPlayerAuras, "player", reasons);
        CheckAuras(frame.TargetAuras, frame.IsTargetAurasKnown, when.RequiredTargetAuras, when.ForbiddenTargetAuras, "target", reasons);
        return reasons;
    }

    private static bool MatchesCharacter(CharacterRequirements requirements, UnitState player, out string? mismatch)
    {
        if (requirements is null)
        {
            mismatch = "Profile has no character requirements.";
            return false;
        }
        if (!string.Equals(requirements.Calling, player.Calling, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = $"Profile calling '{requirements.Calling}' does not match player calling '{player.Calling}'.";
            return false;
        }
        if (player.Level is null)
        {
            mismatch = "Player level is unknown.";
            return false;
        }
        if (requirements.MinimumLevel.HasValue && player.Level < requirements.MinimumLevel)
        {
            mismatch = $"Player level {player.Level} is below profile minimum {requirements.MinimumLevel}.";
            return false;
        }
        if (requirements.MaximumLevel.HasValue && player.Level > requirements.MaximumLevel)
        {
            mismatch = $"Player level {player.Level} is above profile maximum {requirements.MaximumLevel}.";
            return false;
        }
        mismatch = null;
        return true;
    }

    private static void CheckBoolean(bool? expected, bool? actual, string reason, List<string> reasons)
    {
        if (expected is not null && actual != expected)
            reasons.Add(reason);
    }

    private static void CheckPercent(double? actual, double? below, double? above, string label, List<string> reasons)
    {
        if ((below is not null || above is not null) && actual is null)
        {
            reasons.Add($"{label} is unknown.");
            return;
        }
        if (below is double upper && actual >= upper)
            reasons.Add($"{label} is not below {upper:F1}%.");
        if (above is double lower && actual <= lower)
            reasons.Add($"{label} is not above {lower:F1}%.");
    }

    private static void CheckAuras(
        IReadOnlyList<AuraState>? current,
        bool known,
        IReadOnlyList<string>? required,
        IReadOnlyList<string>? forbidden,
        string owner,
        List<string> reasons)
    {
        bool hasRequired = required is not null && required.Count > 0;
        bool hasForbidden = forbidden is not null && forbidden.Count > 0;

        if (!hasRequired && !hasForbidden)
            return;

        if (!known)
        {
            reasons.Add($"{owner} aura state is unknown.");
            return;
        }

        HashSet<string> ids = (current ?? (IReadOnlyList<AuraState>)[])
            .Where(aura => aura is not null && !string.IsNullOrWhiteSpace(aura.Id))
            .Select(aura => aura.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in required!)
            if (!ids.Contains(id))
                reasons.Add($"Required {owner} aura '{id}' is absent.");
        foreach (string id in forbidden!)
            if (ids.Contains(id))
                reasons.Add($"Forbidden {owner} aura '{id}' is present.");
    }

    private static EvaluationResult Stop(StopReason reason, string? message) =>
        new(ControllerState.Stopped, null, [], reason, message);

    private static bool IsLevelInRange(AbilityBinding binding, int? playerLevel)
    {
        if (playerLevel is null) return false;
        if (binding.MinimumLevel.HasValue && playerLevel.Value < binding.MinimumLevel.Value) return false;
        if (binding.MaximumLevel.HasValue && playerLevel.Value > binding.MaximumLevel.Value) return false;
        return true;
    }
}
