using System.Collections.ObjectModel;
using System.Text.Json;
using BotDs.Core;

namespace BotDs.Tests;

/// <summary>
/// Phase 2 offline DryRun decision harness. Ability IDs are taken from
/// profiles/draft-warrior-45-live.json (observed live). Test-only keys "1"/"2".
/// </summary>
public sealed class DryRunMultiTickTests
{
    private const string AbilityA = "A01BB8C035B6A96DD";
    private const string AbilityB = "A05213A7D60B13F6B";

    [Fact]
    public void MultiTick_ReadyThenCdThenSecond_ProducesExpectedRuleFires()
    {
        CombatProfile profile = CreateTwoAbilityProfile();
        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));

        // Tick 0: A ready → fire rule-a
        EvaluationResult t0 = evaluator.Evaluate(profile, Frame(AbilityA, readyA: true, AbilityB, readyB: false, hostile: true));
        Assert.True(t0.HasAction, t0.Message);
        Assert.Equal("rule-a", t0.Action!.RuleId);
        Assert.Equal(AbilityA, t0.Action.AbilityId);

        // Tick 1: A on CD, B ready → fire rule-b
        EvaluationResult t1 = evaluator.Evaluate(profile, Frame(AbilityA, readyA: false, AbilityB, readyB: true, hostile: true));
        Assert.True(t1.HasAction, t1.Message);
        Assert.Equal("rule-b", t1.Action!.RuleId);
        Assert.Equal(AbilityB, t1.Action.AbilityId);

        // Tick 2: both on CD → no action
        EvaluationResult t2 = evaluator.Evaluate(profile, Frame(AbilityA, readyA: false, AbilityB, readyB: false, hostile: true));
        Assert.False(t2.HasAction);

        // Tick 3: both ready but friendly target → no action (hostile required)
        EvaluationResult t3 = evaluator.Evaluate(profile, Frame(AbilityA, readyA: true, AbilityB, readyB: true, hostile: false));
        Assert.False(t3.HasAction);
    }

    [Fact]
    public void DraftWarriorAbilityIds_StillPresentInCheckedInDraftProfile()
    {
        // Guards the offline harness against drift if draft fixture is rewritten.
        string root = FindRepoRoot();
        string path = Path.Combine(root, "profiles", "draft-warrior-45-live.json");
        Assert.True(File.Exists(path), "draft-warrior-45-live.json missing");
        string json = File.ReadAllText(path);
        Assert.Contains(AbilityA, json, StringComparison.Ordinal);
        Assert.Contains(AbilityB, json, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
    }

    private static CombatProfile CreateTwoAbilityProfile() => new()
    {
        ProfileVersion = 1,
        Enabled = true,
        Id = "offline-dryrun-two",
        Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 45, MaximumLevel = 45 },
        Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new AbilityBinding { AbilityId = AbilityA, Key = "1", Enabled = true, Required = false },
            ["b"] = new AbilityBinding { AbilityId = AbilityB, Key = "2", Enabled = true, Required = false },
        },
        Rules =
        [
            new CombatRule
            {
                Id = "rule-a",
                Ability = "a",
                Enabled = true,
                When = new RuleConditions { TargetHostile = true, AbilityUsable = true, CooldownReady = true },
            },
            new CombatRule
            {
                Id = "rule-b",
                Ability = "b",
                Enabled = true,
                When = new RuleConditions { TargetHostile = true, AbilityUsable = true, CooldownReady = true },
            },
        ],
    };

    private static TelemetryFrame Frame(string idA, bool readyA, string idB, bool readyB, bool hostile)
    {
        var abilities = new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
        {
            [idA] = MakeAbility(idA, readyA),
            [idB] = MakeAbility(idB, readyB),
        };

        return new TelemetryFrame(
            Provider: new ProviderStatus(
                ProviderHealth.Healthy, "5", Guid.NewGuid().ToString("D"), 10, 100,
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(30)),
            Player: new UnitState(
                Id: "p1", Name: "Atank", Level: 45, Calling: "warrior",
                IsPlayer: true, Relation: "friendly",
                Health: new HealthState(100, 100), Resource: new ResourceState("power", 100, 100),
                InCombat: true, Cast: null),
            Target: new UnitState(
                Id: "t1", Name: "Mob", Level: 40, Calling: null,
                IsPlayer: false, Relation: hostile ? "hostile" : "friendly",
                Health: new HealthState(50, 100), Resource: null,
                InCombat: true, Cast: null),
            Abilities: new ReadOnlyDictionary<string, AbilityState>(abilities),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true,
            TargetKnownness: TargetKnownness.KnownTarget,
            GameInputReady: true);
    }

    private static AbilityState MakeAbility(string id, bool ready) => new(
        Id: id,
        Name: id,
        Available: true,
        Usable: ready,
        InRange: true,
        CooldownRemainingMilliseconds: ready ? 0 : 3000,
        CooldownDurationMilliseconds: 6000,
        TargetId: null,
        Costs: ReadOnlyDictionary<string, int>.Empty,
        CastTimeMilliseconds: 0,
        IsChannel: false,
        IsPassive: false);

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotDs.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotDs.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Repo root not found");
    }
}
