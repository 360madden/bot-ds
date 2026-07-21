using System.Collections.ObjectModel;
using BotDs.App.Services;
using BotDs.Core;

namespace BotDs.Tests;

public sealed class DraftProfileAuthoringTests
{
    // Real ability ids from profiles/draft-warrior-45-live.json (observed live; not invented).
    private const string AbilityA = "A01BB8C035B6A96DD";
    private const string AbilityB = "A05213A7D60B13F6B";

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "0")]
    [InlineData(11, "-")]
    [InlineData(12, "=")]
    [InlineData(0, null)]
    [InlineData(13, null)]
    public void DefaultKeyForBarSlot_MatchesCommonMainBarDefaults(int slot, string? expected)
    {
        Assert.Equal(expected, DraftProfileBuilder.DefaultKeyForBarSlot(slot));
    }

    [Fact]
    public void TryBuild_NoBar_AllKeysEmpty_AndValidatesDisabled()
    {
        TelemetryFrame frame = Frame(
            abilities:
            [
                Ability(AbilityA, "Strike"),
                Ability(AbilityB, "Bash"),
            ],
            bar: null);

        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out string? error);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal(0, draft!.KeyHintsFromActionBar);
        Assert.False(draft.ValidatedProfile.Enabled);
        Assert.Equal(2, draft.AbilityCount);
        Assert.All(draft.ValidatedProfile.Abilities.Values, b => Assert.Equal("", b.Key));
        Assert.Contains("Strike", draft.ProfileJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_WithActionBar_SuggestsKeysOnlyForPlacedAbilities()
    {
        TelemetryFrame frame = Frame(
            abilities:
            [
                Ability(AbilityA, "Strike"),
                Ability(AbilityB, "Bash"),
            ],
            bar:
            [
                new ActionBarSlotState(1, AbilityA),
                new ActionBarSlotState(2, AbilityB),
                new ActionBarSlotState(3, ""),
            ]);

        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out string? error);
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal(2, draft!.KeyHintsFromActionBar);

        AbilityBinding strike = draft.ValidatedProfile.Abilities.Values
            .Single(b => b.AbilityId == AbilityA);
        AbilityBinding bash = draft.ValidatedProfile.Abilities.Values
            .Single(b => b.AbilityId == AbilityB);
        Assert.Equal("1", strike.Key);
        Assert.Equal("2", bash.Key);
        Assert.All(draft.ValidatedProfile.Abilities.Values, b => Assert.False(b.Enabled));
    }

    [Fact]
    public void TryBuild_DuplicateBarSlots_FirstSlotWins()
    {
        TelemetryFrame frame = Frame(
            abilities: [Ability(AbilityA, "Strike")],
            bar:
            [
                new ActionBarSlotState(1, AbilityA),
                new ActionBarSlotState(5, AbilityA),
            ]);

        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out _);
        Assert.NotNull(draft);
        AbilityBinding b = draft!.ValidatedProfile.Abilities.Values.Single();
        Assert.Equal("1", b.Key);
    }

    [Fact]
    public void TryBuild_CollidingNames_ProduceUniqueAliases()
    {
        TelemetryFrame frame = Frame(
            abilities:
            [
                Ability(AbilityA, "Strike"),
                Ability(AbilityB, "Strike"),
            ],
            bar: null);

        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out _);
        Assert.NotNull(draft);
        Assert.Equal(2, draft!.ValidatedProfile.Abilities.Count);
        Assert.Equal(2, draft.ValidatedProfile.Abilities.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TryBuild_MissingInventory_Fails()
    {
        TelemetryFrame frame = Frame(abilities: [], bar: null) with { IsAbilitiesKnown = false };
        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out string? error);
        Assert.Null(draft);
        Assert.Contains("inventory", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuild_NamesSidecar_ContainsDisplayNames()
    {
        TelemetryFrame frame = Frame(
            abilities: [Ability(AbilityA, "Strike")],
            bar: null);
        DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out _);
        Assert.NotNull(draft);
        Assert.Contains("Strike", draft!.NamesJson, StringComparison.Ordinal);
        Assert.Contains(AbilityA, draft.NamesJson, StringComparison.Ordinal);
    }

    private static AbilityState Ability(string id, string name) => new(
        Id: id,
        Name: name,
        Available: true,
        Usable: true,
        InRange: true,
        CooldownRemainingMilliseconds: 0,
        CooldownDurationMilliseconds: 1000,
        TargetId: null,
        Costs: ReadOnlyDictionary<string, int>.Empty,
        CastTimeMilliseconds: 0,
        IsChannel: false,
        IsPassive: false);

    private static TelemetryFrame Frame(
        IReadOnlyList<AbilityState> abilities,
        IReadOnlyList<ActionBarSlotState>? bar)
    {
        var map = abilities.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                ProviderHealth.Healthy, "5", Guid.NewGuid().ToString("D"), 1, 100,
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(20)),
            Player: new UnitState(
                Id: "p1", Name: "Atank", Level: 45, Calling: "warrior",
                IsPlayer: true, Relation: "friendly",
                Health: new HealthState(100, 100), Resource: new ResourceState("power", 100, 100),
                InCombat: false, Cast: null),
            Target: null,
            Abilities: new ReadOnlyDictionary<string, AbilityState>(map),
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            IsPlayerAurasKnown: true,
            IsTargetAurasKnown: true,
            TargetKnownness: TargetKnownness.KnownNoTarget,
            GameInputReady: true,
            ActionBarSlots: bar,
            IsActionBarKnown: bar is not null,
            ActionBarPage: bar is not null ? 1 : null);
    }
}
