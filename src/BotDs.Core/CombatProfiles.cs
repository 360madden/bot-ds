using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotDs.Core;

public enum AcknowledgementKind
{
    Cast,
    Cooldown,
    Resource,
    Aura,
    CombatEvent,
}

public sealed record CombatProfile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }
    public int ProfileVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public required string Id { get; init; }
    public required CharacterRequirements Character { get; init; }
    public required Dictionary<string, AbilityBinding> Abilities { get; init; }
    public required List<CombatRule> Rules { get; init; }
}

public sealed record CharacterRequirements
{
    public required string Calling { get; init; }
    public int? MinimumLevel { get; init; }
    public int? MaximumLevel { get; init; }
    public string? Build { get; init; }
}

public sealed record AbilityBinding
{
    public required string AbilityId { get; init; }
    public required string Key { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; } = true;
    public int? MinimumLevel { get; init; }
    public int? MaximumLevel { get; init; }
}

public sealed record CombatRule
{
    public required string Id { get; init; }
    public required string Ability { get; init; }
    public bool Enabled { get; init; } = true;
    public RuleConditions When { get; init; } = new();
    public AcknowledgementKind Acknowledgement { get; init; } = AcknowledgementKind.Cooldown;
}

public sealed record RuleConditions
{
    public bool TargetHostile { get; init; } = true;
    public bool? TargetIsPlayer { get; init; }
    public bool? PlayerInCombat { get; init; }
    public bool? TargetInCombat { get; init; }
    public bool AbilityUsable { get; init; } = true;
    public bool AbilityInRange { get; init; } = true;
    public bool CooldownReady { get; init; } = true;
    public bool? TargetCasting { get; init; }
    public bool? TargetCastInterruptible { get; init; }
    public double? PlayerHealthBelowPercent { get; init; }
    public double? PlayerHealthAbovePercent { get; init; }
    public double? TargetHealthBelowPercent { get; init; }
    public double? TargetHealthAbovePercent { get; init; }
    public int? ResourceAtLeast { get; init; }
    public List<string> RequiredPlayerAuras { get; init; } = [];
    public List<string> ForbiddenPlayerAuras { get; init; } = [];
    public List<string> RequiredTargetAuras { get; init; } = [];
    public List<string> ForbiddenTargetAuras { get; init; } = [];
}

public sealed record ProfileValidationResult(CombatProfile? Profile, IReadOnlyList<string> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

public static class CombatProfileLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<ProfileValidationResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            CombatProfile? profile = await JsonSerializer.DeserializeAsync<CombatProfile>(
                stream,
                Options,
                cancellationToken);
            return Validate(profile);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ProfileValidationResult(null, [exception.Message]);
        }
    }

    public static ProfileValidationResult Validate(CombatProfile? profile)
    {
        List<string> errors = [];
        if (profile is null)
            return new ProfileValidationResult(null, ["Profile is empty."]);

        if (profile.ProfileVersion != 1)
            errors.Add($"Unsupported profileVersion {profile.ProfileVersion}.");
        if (string.IsNullOrWhiteSpace(profile.Id))
            errors.Add("Profile id is required.");
        if (profile.Character is null)
            errors.Add("Character requirements are required.");
        else
        {
            if (string.IsNullOrWhiteSpace(profile.Character.Calling))
                errors.Add("Character calling is required.");
            if (profile.Character.MinimumLevel is < 1)
                errors.Add("minimumLevel must be positive.");
            if (profile.Character.MaximumLevel is < 1)
                errors.Add("maximumLevel must be positive.");
            if (profile.Character.MinimumLevel > profile.Character.MaximumLevel)
                errors.Add("minimumLevel cannot exceed maximumLevel.");
            if (profile.Character.Build is not null)
            {
                if (string.IsNullOrWhiteSpace(profile.Character.Build))
                    errors.Add("Character build must not be whitespace.");
                else if (profile.Enabled)
                    errors.Add("Character build is not supported by the V5 protocol; enabled profiles must not specify a build.");
            }
        }

        if (profile.Abilities is null || profile.Abilities.Count == 0)
            errors.Add("At least one ability binding is required.");
        else
        {
            HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string alias, AbilityBinding binding) in profile.Abilities)
            {
                if (string.IsNullOrWhiteSpace(alias))
                    errors.Add("Ability aliases cannot be empty.");
                else if (!aliases.Add(alias))
                    errors.Add($"Duplicate ability alias '{alias}'.");
                if (binding is null)
                {
                    errors.Add($"Ability '{alias}' binding is null.");
                    continue;
                }
                if (binding.Enabled && string.IsNullOrWhiteSpace(binding.AbilityId))
                    errors.Add($"Ability '{alias}' requires an abilityId.");
                if (binding.Enabled && string.IsNullOrWhiteSpace(binding.Key))
                    errors.Add($"Ability '{alias}' requires a key.");
                if (binding.MinimumLevel > binding.MaximumLevel)
                    errors.Add($"Ability '{alias}' minimumLevel cannot exceed maximumLevel.");
                if (binding.MinimumLevel is < 1 || binding.MaximumLevel is < 1)
                    errors.Add($"Ability '{alias}' level bounds must be positive.");
            }
        }

        if (profile.Rules is null || profile.Rules.Count == 0)
            errors.Add("At least one combat rule is required.");
        else
        {
            HashSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (CombatRule rule in profile.Rules)
            {
                if (rule is null)
                {
                    errors.Add("Rule entry is null.");
                    continue;
                }
                bool hasRuleId = !string.IsNullOrWhiteSpace(rule.Id);
                bool hasAbilityAlias = !string.IsNullOrWhiteSpace(rule.Ability);
                if (!hasRuleId)
                    errors.Add("Rule id is required.");
                else if (!ruleIds.Add(rule.Id))
                    errors.Add($"Duplicate rule id '{rule.Id}'.");
                if (!hasAbilityAlias)
                    errors.Add($"Rule '{rule.Id}' ability is required.");
                else if (profile.Abilities is null || !profile.Abilities.ContainsKey(rule.Ability))
                    errors.Add($"Rule '{rule.Id}' references unknown ability alias '{rule.Ability}'.");
                if (rule.When is null)
                    errors.Add($"Rule '{rule.Id}' conditions are required.");
                else
                {
                    ValidatePercent(rule.Id, nameof(rule.When.PlayerHealthBelowPercent), rule.When.PlayerHealthBelowPercent, errors);
                    ValidatePercent(rule.Id, nameof(rule.When.PlayerHealthAbovePercent), rule.When.PlayerHealthAbovePercent, errors);
                    ValidatePercent(rule.Id, nameof(rule.When.TargetHealthBelowPercent), rule.When.TargetHealthBelowPercent, errors);
                    ValidatePercent(rule.Id, nameof(rule.When.TargetHealthAbovePercent), rule.When.TargetHealthAbovePercent, errors);
                    if (rule.When.ResourceAtLeast is < 0)
                        errors.Add($"Rule '{rule.Id}' resourceAtLeast cannot be negative.");
                    ValidateAuraIds(rule.Id, rule.When.RequiredPlayerAuras, nameof(rule.When.RequiredPlayerAuras), errors);
                    ValidateAuraIds(rule.Id, rule.When.ForbiddenPlayerAuras, nameof(rule.When.ForbiddenPlayerAuras), errors);
                    ValidateAuraIds(rule.Id, rule.When.RequiredTargetAuras, nameof(rule.When.RequiredTargetAuras), errors);
                    ValidateAuraIds(rule.Id, rule.When.ForbiddenTargetAuras, nameof(rule.When.ForbiddenTargetAuras), errors);
                }

                // Validate acknowledgement kind (M8: enum member check per PLAN.md §8)
                if (!IsValidAcknowledgement(rule.Acknowledgement))
                    errors.Add($"Rule '{rule.Id}' has an unsupported acknowledgement kind ({(int)rule.Acknowledgement}).");
            }
        }

        // Key binding validation (M5)
        errors.AddRange(KeyBindingValidator.ValidateAll(profile));

        // Enabled-profile structural requirements
        if (profile.Enabled)
        {
            if (profile.Abilities is null || !profile.Abilities.Values.Any(b => b is { Enabled: true }))
                errors.Add("Enabled profile must have at least one enabled ability binding.");

            if (profile.Rules is null || !profile.Rules.Any(r => r is { Enabled: true }))
                errors.Add("Enabled profile must have at least one enabled rule.");

            if (profile.Abilities is not null && profile.Rules is not null)
            {
                foreach (CombatRule rule in profile.Rules.Where(r => r is { Enabled: true }))
                {
                    if (!string.IsNullOrWhiteSpace(rule.Ability)
                        && profile.Abilities.TryGetValue(rule.Ability, out AbilityBinding? binding)
                        && binding is not null
                        && !binding.Enabled)
                        errors.Add($"Enabled rule '{rule.Id}' references disabled ability binding '{rule.Ability}'.");
                }

                int profileMin = profile.Character?.MinimumLevel ?? 1;
                int profileMax = profile.Character?.MaximumLevel ?? int.MaxValue;
                bool hasOverlappingPair = profile.Rules
                    .Where(r => r is not null && r.Enabled)
                    .SelectMany(r => (IEnumerable<AbilityBinding>)(
                        !string.IsNullOrWhiteSpace(r.Ability)
                        && profile.Abilities.TryGetValue(r.Ability, out AbilityBinding? b)
                        && b is { Enabled: true }
                            ? [b]
                            : []))
                    .Any(b => (b.MinimumLevel ?? 1) <= profileMax && (b.MaximumLevel ?? int.MaxValue) >= profileMin);
                if (!hasOverlappingPair && profile.Rules.Any(r => r is not null && r.Enabled))
                    errors.Add("No enabled rule references an enabled ability binding that overlaps the profile level range.");
            }
        }

        return new ProfileValidationResult(profile, errors);
    }

    private static void ValidatePercent(string ruleId, string field, double? value, List<string> errors)
    {
        if (value is < 0 or > 100)
            errors.Add($"Rule '{ruleId}' {field} must be between 0 and 100.");
    }

    private static void ValidateAuraIds(
        string ruleId,
        IReadOnlyList<string>? values,
        string field,
        List<string> errors)
    {
        if (values is null || values.Any(string.IsNullOrWhiteSpace))
            errors.Add($"Rule '{ruleId}' {field} must contain non-empty aura ids.");
    }

    /// <summary>
    /// Reject acknowledgement kinds that are not defined enum members.
    /// Per PLAN.md §8, only predicates proven by M1 live conformance may be enabled.
    /// Until M1 conformance data is available, all defined members are accepted
    /// and the allowlist is enforced as defense-in-depth against invalid JSON.
    /// </summary>
    private static bool IsValidAcknowledgement(AcknowledgementKind kind) =>
        Enum.IsDefined(kind);
}
