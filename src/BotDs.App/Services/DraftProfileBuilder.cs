using System.Text.Json;
using System.Text.Json.Serialization;
using BotDs.Core;

namespace BotDs.App.Services;

/// <summary>
/// Pure helpers for scaffolding a disabled combat profile from live telemetry.
/// Keys are never invented: empty unless an action-bar slot suggests a common default.
/// </summary>
public static class DraftProfileBuilder
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ValidateOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Common default main-bar key for slot N (1-based). Null outside 1–12.
    /// Not observed from RIFT — operator must confirm.
    /// </summary>
    public static string? DefaultKeyForBarSlot(int slot) => slot switch
    {
        1 => "1",
        2 => "2",
        3 => "3",
        4 => "4",
        5 => "5",
        6 => "6",
        7 => "7",
        8 => "8",
        9 => "9",
        10 => "0",
        11 => "-",
        12 => "=",
        _ => null,
    };

    public static string SanitizeAlias(string seed, int index)
    {
        string raw = (seed ?? "").Trim();
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').Take(24).ToArray();
        string alias = chars.Length > 0 ? new string(chars) : $"ability{index}";
        if (alias.Length == 0 || char.IsDigit(alias[0])) alias = "a" + alias;
        return alias.ToLowerInvariant();
    }

    public static string SanitizeProfileId(string calling, int level)
    {
        string raw = $"draft-{calling.ToLowerInvariant()}-{level}-live";
        return new string(raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
    }

    /// <summary>
    /// Build key hints from action-bar slots. First slot wins if an ability appears twice.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildKeyHintsFromActionBar(TelemetryFrame frame)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!frame.IsActionBarKnown || frame.ActionBarSlots is null)
            return map;

        foreach (ActionBarSlotState slot in frame.ActionBarSlots)
        {
            if (string.IsNullOrWhiteSpace(slot.AbilityId)) continue;
            string? hint = DefaultKeyForBarSlot(slot.Slot);
            if (hint is null) continue;
            map.TryAdd(slot.AbilityId, hint);
        }

        return map;
    }

    public static DraftBuildResult? TryBuild(TelemetryFrame frame, out string? error)
    {
        error = null;
        if (!frame.IsAbilitiesKnown || frame.Abilities.Count == 0)
        {
            error = "No known ability inventory in live telemetry.";
            return null;
        }

        UnitState? player = frame.Player;
        if (player is null || !player.IsAvailable || player.Level is null)
        {
            error = "Player state is unavailable; cannot scaffold character section.";
            return null;
        }

        string calling = string.IsNullOrWhiteSpace(player.Calling) ? "unknown" : player.Calling.Trim();
        string callingNorm = char.ToUpperInvariant(calling[0]) + calling[1..].ToLowerInvariant();
        int level = player.Level.Value;
        string profileId = SanitizeProfileId(calling, level);

        IReadOnlyDictionary<string, string> keyHints = BuildKeyHintsFromActionBar(frame);
        var abilities = new Dictionary<string, DraftAbilityBinding>(StringComparer.OrdinalIgnoreCase);
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;

        foreach (var kv in frame.Abilities.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
        {
            AbilityState a = kv.Value;
            if (string.IsNullOrWhiteSpace(a.Id)) continue;
            index++;
            string aliasSeed = !string.IsNullOrWhiteSpace(a.Name)
                && !string.Equals(a.Name, a.Id, StringComparison.OrdinalIgnoreCase)
                ? a.Name
                : a.Id;
            string alias = SanitizeAlias(aliasSeed, index);
            string unique = alias;
            int n = 2;
            while (abilities.ContainsKey(unique))
            {
                unique = alias + n;
                n++;
            }

            keyHints.TryGetValue(a.Id, out string? suggestedKey);
            abilities[unique] = new DraftAbilityBinding(
                AbilityId: a.Id,
                Key: suggestedKey ?? "",
                Enabled: false,
                Required: false);
            nameMap[a.Id] = string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name;
        }

        if (abilities.Count == 0)
        {
            error = "Telemetry abilities had no usable ids.";
            return null;
        }

        string firstAlias = abilities.Keys.First();
        var draft = new DraftProfileDocument(
            ProfileVersion: 1,
            Enabled: false,
            Id: profileId,
            Character: new DraftCharacter(callingNorm, level, level),
            Abilities: abilities,
            Rules:
            [
                new DraftRule(
                    Id: "dryrun-first-ability",
                    Ability: firstAlias,
                    Enabled: false,
                    Acknowledgement: "cooldown",
                    When: new DraftWhen(true, true, true, true)),
            ]);

        string json = JsonSerializer.Serialize(draft, WriteOptions);
        string namesJson = JsonSerializer.Serialize(new
        {
            profileId,
            note = "Display names from live Detail API for authoring; not loaded by the combat engine.",
            names = nameMap,
        }, WriteOptions);

        CombatProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<CombatProfile>(json, ValidateOptions);
        }
        catch (JsonException ex)
        {
            error = "Draft serialization failed: " + ex.Message;
            return null;
        }

        ProfileValidationResult validation = CombatProfileLoader.Validate(profile);
        if (!validation.IsValid)
        {
            error = "Draft profile validation failed: " + string.Join("; ", validation.Errors);
            return null;
        }

        return new DraftBuildResult(
            ProfileId: profileId,
            ProfileJson: json,
            NamesJson: namesJson,
            AbilityCount: abilities.Count,
            Calling: callingNorm,
            Level: level,
            KeyHintsFromActionBar: keyHints.Count,
            ValidatedProfile: validation.Profile!);
    }
}

public sealed record DraftAbilityBinding(
    [property: JsonPropertyName("abilityId")] string AbilityId,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("required")] bool Required);

public sealed record DraftCharacter(
    [property: JsonPropertyName("calling")] string Calling,
    [property: JsonPropertyName("minimumLevel")] int MinimumLevel,
    [property: JsonPropertyName("maximumLevel")] int MaximumLevel);

public sealed record DraftWhen(
    [property: JsonPropertyName("targetHostile")] bool TargetHostile,
    [property: JsonPropertyName("abilityUsable")] bool AbilityUsable,
    [property: JsonPropertyName("abilityInRange")] bool AbilityInRange,
    [property: JsonPropertyName("cooldownReady")] bool CooldownReady);

public sealed record DraftRule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ability")] string Ability,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("acknowledgement")] string Acknowledgement,
    [property: JsonPropertyName("when")] DraftWhen When);

public sealed record DraftProfileDocument(
    [property: JsonPropertyName("profileVersion")] int ProfileVersion,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("character")] DraftCharacter Character,
    [property: JsonPropertyName("abilities")] Dictionary<string, DraftAbilityBinding> Abilities,
    [property: JsonPropertyName("rules")] IReadOnlyList<DraftRule> Rules);

public sealed record DraftBuildResult(
    string ProfileId,
    string ProfileJson,
    string NamesJson,
    int AbilityCount,
    string Calling,
    int Level,
    int KeyHintsFromActionBar,
    CombatProfile ValidatedProfile);
