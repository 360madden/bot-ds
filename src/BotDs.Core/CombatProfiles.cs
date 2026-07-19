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
    public int ProfileVersion { get; init; } = 1;
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
        if (string.IsNullOrWhiteSpace(profile.Character.Calling))
            errors.Add("Character calling is required.");
        if (profile.Character.MinimumLevel is < 1)
            errors.Add("minimumLevel must be positive.");
        if (profile.Character.MaximumLevel is < 1)
            errors.Add("maximumLevel must be positive.");
        if (profile.Character.MinimumLevel > profile.Character.MaximumLevel)
            errors.Add("minimumLevel cannot exceed maximumLevel.");
        if (profile.Abilities.Count == 0)
            errors.Add("At least one ability binding is required.");
        if (profile.Rules.Count == 0)
            errors.Add("At least one combat rule is required.");

        foreach ((string alias, AbilityBinding binding) in profile.Abilities)
        {
            if (string.IsNullOrWhiteSpace(alias))
                errors.Add("Ability aliases cannot be empty.");
            if (binding.Enabled && string.IsNullOrWhiteSpace(binding.AbilityId))
                errors.Add($"Ability '{alias}' requires an abilityId.");
            if (binding.Enabled && string.IsNullOrWhiteSpace(binding.Key))
                errors.Add($"Ability '{alias}' requires a key.");
            if (binding.MinimumLevel > binding.MaximumLevel)
                errors.Add($"Ability '{alias}' minimumLevel cannot exceed maximumLevel.");
        }

        HashSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (CombatRule rule in profile.Rules)
        {
            if (!ruleIds.Add(rule.Id))
                errors.Add($"Duplicate rule id '{rule.Id}'.");
            if (!profile.Abilities.ContainsKey(rule.Ability))
                errors.Add($"Rule '{rule.Id}' references unknown ability alias '{rule.Ability}'.");
            ValidatePercent(rule.Id, nameof(rule.When.PlayerHealthBelowPercent), rule.When.PlayerHealthBelowPercent, errors);
            ValidatePercent(rule.Id, nameof(rule.When.PlayerHealthAbovePercent), rule.When.PlayerHealthAbovePercent, errors);
            ValidatePercent(rule.Id, nameof(rule.When.TargetHealthBelowPercent), rule.When.TargetHealthBelowPercent, errors);
            ValidatePercent(rule.Id, nameof(rule.When.TargetHealthAbovePercent), rule.When.TargetHealthAbovePercent, errors);
        }

        return new ProfileValidationResult(profile, errors);
    }

    private static void ValidatePercent(string ruleId, string field, double? value, List<string> errors)
    {
        if (value is < 0 or > 100)
            errors.Add($"Rule '{ruleId}' {field} must be between 0 and 100.");
    }
}
