using System.Collections.ObjectModel;
using System.Text;
using BotDs.Core;
using BotDs.Reader.V5;

namespace BotDs.Tests;

/// <summary>
/// P0: shipped encode layout → parse → map path for ability usable/CD/name fidelity.
/// Fixtures are protocol bytes (not a reimplementation of production Lua).
/// </summary>
public sealed class AbilityFidelityTests
{
    [Fact]
    public void ParseAndMap_UsableReadyWithName_ExposesHonestFields()
    {
        byte[] section = BuildAbilitiesSection(
        [
            BuildAbilityRecord(
                id: "A01BB8C035B6A96DD",
                name: "Strike",
                cdRemainMs: 0,
                cdDurMs: 6000,
                castMs: 0,
                flags: (byte)(V5Constants.AbilityFlagAvailable
                    | V5Constants.AbilityFlagUsable
                    | V5Constants.AbilityFlagInRange),
                cost: 10),
        ]);

        AbilityState state = ParseAndMapSingle(section);
        Assert.Equal("A01BB8C035B6A96DD", state.Id);
        Assert.Equal("Strike", state.Name);
        Assert.True(state.Available);
        Assert.True(state.Usable);
        Assert.True(state.InRange);
        Assert.Equal(0, state.CooldownRemainingMilliseconds);
        Assert.Equal(6000, state.CooldownDurationMilliseconds);
        Assert.True(state.IsReady);
    }

    [Fact]
    public void ParseAndMap_OnCooldown_NotReady()
    {
        byte[] section = BuildAbilitiesSection(
        [
            BuildAbilityRecord(
                id: "A05213A7D60B13F6B",
                name: "Shield Bash",
                cdRemainMs: 2500,
                cdDurMs: 10000,
                castMs: -1,
                flags: (byte)(V5Constants.AbilityFlagAvailable | V5Constants.AbilityFlagInRange),
                cost: 0),
        ]);

        AbilityState state = ParseAndMapSingle(section);
        Assert.Equal("Shield Bash", state.Name);
        Assert.True(state.Available);
        Assert.False(state.Usable);
        Assert.Equal(2500, state.CooldownRemainingMilliseconds);
        Assert.Equal(10000, state.CooldownDurationMilliseconds);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void ParseAndMap_EmptyName_FallsBackToId()
    {
        byte[] section = BuildAbilitiesSection(
        [
            BuildAbilityRecord(
                id: "A0CDE363AA305A3B5",
                name: "",
                cdRemainMs: -1,
                cdDurMs: -1,
                castMs: -1,
                flags: (byte)(V5Constants.AbilityFlagAvailable | V5Constants.AbilityFlagPassive),
                cost: 0),
        ]);

        AbilityState state = ParseAndMapSingle(section);
        Assert.Equal("A0CDE363AA305A3B5", state.Name);
        Assert.True(state.IsPassive);
        Assert.Null(state.CooldownRemainingMilliseconds);
    }

    [Fact]
    public void Parse_SchemaV1_AbilityRecord46Bytes_StillMaps()
    {
        // Dual-schema: pre-reload bridge frames remain readable.
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((ushort)1));
        byte[] rec = new byte[V5Constants.AbilityRecordSizeV1];
        Encoding.ASCII.GetBytes("A01BB8C035B6A96DD").CopyTo(rec, 0);
        for (int i = "A01BB8C035B6A96DD".Length; i < 32; i++) rec[i] = 0x20;
        BitConverter.TryWriteBytes(rec.AsSpan(32), 0);
        BitConverter.TryWriteBytes(rec.AsSpan(36), 5000);
        BitConverter.TryWriteBytes(rec.AsSpan(40), -1);
        rec[44] = (byte)(V5Constants.AbilityFlagAvailable | V5Constants.AbilityFlagUsable | V5Constants.AbilityFlagInRange);
        rec[45] = 0;
        ms.Write(rec);

        // Build slot with provider schema v1 + abilities
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), 1u);
        uint mask = V5Constants.MaskProviderInfo | V5Constants.MaskAbilities;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), mask);

        using var payload = new MemoryStream();
        // ProviderInfo schema 1
        Guid sid = Guid.NewGuid();
        payload.Write(BitConverter.GetBytes((ushort)V5Constants.SectionTypeProviderInfo));
        byte[] prov = new byte[28];
        sid.TryWriteBytes(prov);
        BitConverter.TryWriteBytes(prov.AsSpan(16), 100u);
        BitConverter.TryWriteBytes(prov.AsSpan(20), 500u);
        prov[26] = 1; // schema v1
        payload.Write(BitConverter.GetBytes((ushort)prov.Length));
        payload.Write(prov);
        // Abilities
        byte[] abil = ms.ToArray();
        payload.Write(BitConverter.GetBytes((ushort)V5Constants.SectionTypeAbilities));
        payload.Write(BitConverter.GetBytes((ushort)abil.Length));
        payload.Write(abil);

        byte[] payloadBytes = payload.ToArray();
        payloadBytes.CopyTo(slot, V5Constants.PayloadOffset);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadBytes.Length);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.Equal(1, result.Frame!.Provider!.SchemaVersion);
        Assert.Single(result.Frame.Abilities);
        Assert.Equal("A01BB8C035B6A96DD", result.Frame.Abilities[0].AbilityId);
        Assert.True(result.Frame.Abilities[0].Usable);
        Assert.Equal("", result.Frame.Abilities[0].Name);
    }

    [Fact]
    public void Parse_ActionBar_SlotsRoundTrip()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(2); // page
        ms.WriteByte(2); // count
        WriteActionSlot(ms, 1, "A01BB8C035B6A96DD");
        WriteActionSlot(ms, 2, "");
        byte[] barData = ms.ToArray();

        byte[] slot = CreateSlotWithSection(
            V5Constants.SectionTypeActionBar,
            V5Constants.MaskActionBar,
            barData);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame!.ActionBar);
        Assert.Equal(2, result.Frame.ActionBar!.Page);
        Assert.Equal(2, result.Frame.ActionBar.Slots.Count);
        Assert.Equal(1, result.Frame.ActionBar.Slots[0].Slot);
        Assert.Equal("A01BB8C035B6A96DD", result.Frame.ActionBar.Slots[0].AbilityId);
        Assert.Equal(2, result.Frame.ActionBar.Slots[1].Slot);
        Assert.Equal("", result.Frame.ActionBar.Slots[1].AbilityId);

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(
            StableReadResult.Healthy(result.Frame, TimeSpan.Zero),
            DateTimeOffset.UtcNow);
        Assert.True(frame.IsActionBarKnown);
        Assert.Equal(2, frame.ActionBarPage);
        Assert.NotNull(frame.ActionBarSlots);
        Assert.Equal(2, frame.ActionBarSlots!.Count);
    }

    [Fact]
    public void Evaluator_AbilityReadyVsOnCooldown_FireNoFire()
    {
        // P1: shipped evaluator path with realistic ability transitions (real-shaped ids).
        string abilityId = "A01BB8C035B6A96DD";
        CombatProfile profile = OneAbilityProfile(abilityId, "strike", "1");

        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        TelemetryFrame ready = FrameWithAbility(abilityId, usable: true, cdRemain: 0, hostile: true);
        EvaluationResult fire = evaluator.Evaluate(profile, ready);
        Assert.True(fire.HasAction, fire.Message ?? "expected fire");
        Assert.Equal(abilityId, fire.Action!.AbilityId);

        TelemetryFrame cooling = FrameWithAbility(abilityId, usable: false, cdRemain: 3000, hostile: true);
        EvaluationResult noFire = evaluator.Evaluate(profile, cooling);
        Assert.False(noFire.HasAction);
    }

    [Fact]
    public void Evaluator_MultiAbility_FirstReadyRuleWins()
    {
        // P3: multi-binding without Warrior hard-coding — ordered rules.
        string a1 = "A01BB8C035B6A96DD";
        string a2 = "A05213A7D60B13F6B";
        CombatProfile profile = new()
        {
            ProfileVersion = 1,
            Enabled = true,
            Id = "test-multi",
            Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 45, MaximumLevel = 45 },
            Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["first"] = new AbilityBinding { AbilityId = a1, Key = "1", Enabled = true, Required = false },
                ["second"] = new AbilityBinding { AbilityId = a2, Key = "2", Enabled = true, Required = false },
            },
            Rules =
            [
                new CombatRule
                {
                    Id = "use-first",
                    Ability = "first",
                    Enabled = true,
                    When = new RuleConditions { TargetHostile = true, AbilityUsable = true, CooldownReady = true },
                },
                new CombatRule
                {
                    Id = "use-second",
                    Ability = "second",
                    Enabled = true,
                    When = new RuleConditions { TargetHostile = true, AbilityUsable = true, CooldownReady = true },
                },
            ],
        };

        var evaluator = new CombatEvaluator(TimeSpan.FromMilliseconds(500));
        var abilities = new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
        {
            [a1] = Ability(a1, "One", usable: false, cd: 1000),
            [a2] = Ability(a2, "Two", usable: true, cd: 0),
        };
        TelemetryFrame frame = FrameWithAbilities(abilities, hostile: true);
        EvaluationResult result = evaluator.Evaluate(profile, frame);
        Assert.True(result.HasAction, result.Message ?? "expected second");
        Assert.Equal(a2, result.Action!.AbilityId);
        Assert.Equal("use-second", result.Action.RuleId);
    }

    private static CombatProfile OneAbilityProfile(string abilityId, string alias, string key) => new()
    {
        ProfileVersion = 1,
        Enabled = true,
        Id = "test-one-ability",
        Character = new CharacterRequirements { Calling = "Warrior", MinimumLevel = 45, MaximumLevel = 45 },
        Abilities = new Dictionary<string, AbilityBinding>(StringComparer.OrdinalIgnoreCase)
        {
            [alias] = new AbilityBinding
            {
                AbilityId = abilityId,
                Key = key,
                Enabled = true,
                Required = false,
            },
        },
        Rules =
        [
            new CombatRule
            {
                Id = "use-" + alias,
                Ability = alias,
                Enabled = true,
                When = new RuleConditions
                {
                    TargetHostile = true,
                    AbilityUsable = true,
                    CooldownReady = true,
                },
            },
        ],
    };

    private static AbilityState Ability(string id, string name, bool usable, int cd) => new(
        Id: id,
        Name: name,
        Available: true,
        Usable: usable,
        InRange: true,
        CooldownRemainingMilliseconds: cd,
        CooldownDurationMilliseconds: 6000,
        TargetId: null,
        Costs: ReadOnlyDictionary<string, int>.Empty,
        CastTimeMilliseconds: 0,
        IsChannel: false,
        IsPassive: false);

    private static TelemetryFrame FrameWithAbilities(
        Dictionary<string, AbilityState> abilities, bool hostile)
    {
        return new TelemetryFrame(
            Provider: new ProviderStatus(
                ProviderHealth.Healthy, "5", Guid.NewGuid().ToString("D"), 10, 100,
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50)),
            Player: new UnitState(
                Id: "player1", Name: "Atank", Level: 45, Calling: "warrior",
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

    private static TelemetryFrame FrameWithAbility(string abilityId, bool usable, int cdRemain, bool hostile)
    {
        var abilities = new Dictionary<string, AbilityState>(StringComparer.OrdinalIgnoreCase)
        {
            [abilityId] = Ability(abilityId, "Strike", usable, cdRemain),
        };
        return FrameWithAbilities(abilities, hostile);
    }

    private static AbilityState ParseAndMapSingle(byte[] abilitiesSection)
    {
        byte[] slot = CreateSlotWithSection(
            V5Constants.SectionTypeAbilities,
            V5Constants.MaskAbilities,
            abilitiesSection);
        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.Single(result.Frame!.Abilities);

        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(
            StableReadResult.Healthy(result.Frame, TimeSpan.Zero),
            DateTimeOffset.UtcNow);
        Assert.True(frame.IsAbilitiesKnown);
        Assert.Single(frame.Abilities);
        return frame.Abilities.Values.First();
    }

    private static byte[] BuildAbilitiesSection(IEnumerable<byte[]> records)
    {
        using var ms = new MemoryStream();
        var list = records.ToList();
        ms.Write(BitConverter.GetBytes((ushort)list.Count));
        foreach (byte[] rec in list)
            ms.Write(rec);
        return ms.ToArray();
    }

    private static byte[] BuildAbilityRecord(
        string id, string name, int cdRemainMs, int cdDurMs, int castMs, byte flags, byte cost)
    {
        byte[] rec = new byte[V5Constants.AbilityRecordSize];
        byte[] idBytes = Encoding.ASCII.GetBytes(id);
        idBytes.AsSpan(0, Math.Min(idBytes.Length, 32)).CopyTo(rec);
        for (int i = idBytes.Length; i < 32; i++) rec[i] = 0x20;
        BitConverter.TryWriteBytes(rec.AsSpan(32), cdRemainMs);
        BitConverter.TryWriteBytes(rec.AsSpan(36), cdDurMs);
        BitConverter.TryWriteBytes(rec.AsSpan(40), castMs);
        rec[44] = flags;
        rec[45] = cost;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        ushort nameLen = (ushort)Math.Min(nameBytes.Length, 32);
        BitConverter.TryWriteBytes(rec.AsSpan(46), nameLen);
        nameBytes.AsSpan(0, nameLen).CopyTo(rec.AsSpan(48));
        return rec;
    }

    private static void WriteActionSlot(MemoryStream ms, byte slot, string abilityId)
    {
        ms.WriteByte(slot);
        byte[] id = new byte[32];
        Array.Fill(id, (byte)0x20);
        byte[] idBytes = Encoding.ASCII.GetBytes(abilityId);
        idBytes.AsSpan(0, Math.Min(32, idBytes.Length)).CopyTo(id);
        ms.Write(id);
    }

    private static byte[] CreateSlotWithSection(ushort sectionType, uint mask, byte[] sectionData)
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), 1u);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), 100u);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), mask);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrHeartbeatIntervalMsOffset), 50u);

        int offset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), sectionType);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)sectionData.Length);
        offset += 2;
        sectionData.CopyTo(slot, offset);
        uint payloadLength = (uint)(4 + sectionData.Length);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), payloadLength);
        return slot;
    }
}
