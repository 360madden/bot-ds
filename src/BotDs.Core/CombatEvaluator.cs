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

public sealed class CombatEvaluator(TimeSpan maximumTelemetryAge)
{
    public EvaluationResult Evaluate(CombatProfile profile, TelemetryFrame frame)
    {
        if (!frame.Provider.IsUsable(maximumTelemetryAge))
            return Stop(StopReason.TelemetryStale, $"Provider is {frame.Provider.Health}; age={frame.Provider.Age.TotalMilliseconds:F0} ms.");

        UnitState? player = frame.Player;
        if (player is null || !player.IsAvailable)
            return Stop(StopReason.PlayerUnavailable, "Player state is unavailable.");
        if (player.Health.IsDead)
            return Stop(StopReason.PlayerDead, "Player health is zero.");
        if (!MatchesCharacter(profile.Character, player, out string? mismatch))
            return Stop(StopReason.ProfileMismatch, mismatch);

        UnitState? target = frame.Target;
        if (target is null || !target.IsAvailable || target.Health.IsDead)
            return new EvaluationResult(ControllerState.WaitingForTarget, null, [], Message: "Waiting for a live selected target.");
        if (!target.IsHostile)
            return new EvaluationResult(ControllerState.WaitingForTarget, null, [], Message: "Selected target is not hostile.");

        List<RuleRejection> rejections = [];
        foreach (CombatRule rule in profile.Rules)
        {
            if (!rule.Enabled)
            {
                rejections.Add(new RuleRejection(rule.Id, ["Rule is disabled."]));
                continue;
            }

            AbilityBinding binding = profile.Abilities[rule.Ability];
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
        if (!binding.Enabled)
            reasons.Add("Ability binding is disabled.");
        if (player.Level < binding.MinimumLevel || player.Level > binding.MaximumLevel)
            reasons.Add("Player level is outside the ability binding range.");
        if (!frame.Abilities.TryGetValue(binding.AbilityId, out AbilityState? ability) || !ability.Available)
        {
            reasons.Add("Ability is unavailable.");
            return reasons;
        }

        RuleConditions when = rule.When;
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
        if (when.ResourceAtLeast is int required && player.Resource?.Current < required)
            reasons.Add($"Resource is below {required}.");

        CheckPercent(player.Health.Percent, when.PlayerHealthBelowPercent, when.PlayerHealthAbovePercent, "Player health", reasons);
        CheckPercent(target.Health.Percent, when.TargetHealthBelowPercent, when.TargetHealthAbovePercent, "Target health", reasons);
        CheckAuras(frame.PlayerAuras, when.RequiredPlayerAuras, when.ForbiddenPlayerAuras, "player", reasons);
        CheckAuras(frame.TargetAuras, when.RequiredTargetAuras, when.ForbiddenTargetAuras, "target", reasons);
        return reasons;
    }

    private static bool MatchesCharacter(CharacterRequirements requirements, UnitState player, out string? mismatch)
    {
        if (!string.Equals(requirements.Calling, player.Calling, StringComparison.OrdinalIgnoreCase))
        {
            mismatch = $"Profile calling '{requirements.Calling}' does not match player calling '{player.Calling}'.";
            return false;
        }
        if (player.Level is null || player.Level < requirements.MinimumLevel || player.Level > requirements.MaximumLevel)
        {
            mismatch = "Player level is outside the profile range.";
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
        IReadOnlyList<AuraState> current,
        IReadOnlyList<string> required,
        IReadOnlyList<string> forbidden,
        string owner,
        List<string> reasons)
    {
        HashSet<string> ids = current.Select(aura => aura.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in required)
            if (!ids.Contains(id))
                reasons.Add($"Required {owner} aura '{id}' is absent.");
        foreach (string id in forbidden)
            if (ids.Contains(id))
                reasons.Add($"Forbidden {owner} aura '{id}' is present.");
    }

    private static EvaluationResult Stop(StopReason reason, string? message) =>
        new(ControllerState.Stopped, null, [], reason, message);
}
