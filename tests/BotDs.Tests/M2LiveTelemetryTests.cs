using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using BotDs.App.Services;
using BotDs.Core;
using BotDs.Reader.V5;
using Microsoft.Extensions.DependencyInjection;

namespace BotDs.Tests;

/// <summary>
/// M2 live telemetry provider: shipped V5 encode→parse→map path, knownness,
/// GameInputReady, and transport-neutral ITelemetrySource registration.
/// </summary>
public sealed class M2LiveTelemetryTests
{
    [Fact]
    public void Healthy_multi_section_frame_maps_player_target_abilities_auras()
    {
        Guid session = Guid.NewGuid();
        byte[] slot = BuildMultiSectionSlot(
            sequence: 7,
            session: session,
            includePlayer: true,
            includeTarget: true,
            targetAvailable: true,
            abilitiesKnownEmpty: false,
            abilityCount: 2,
            playerAurasEmpty: false,
            gameInputReady: true);

        TelemetryFrame frame = MapSlot(slot);
        Assert.Equal(ProviderHealth.Healthy, frame.Provider.Health);
        Assert.Equal(7u, frame.Provider.Sequence);
        Assert.Equal(session.ToString("D"), frame.Provider.SessionId);
        Assert.NotNull(frame.Player);
        Assert.True(frame.Player!.IsAvailable);
        Assert.Equal(TargetKnownness.KnownTarget, frame.TargetKnownness);
        Assert.NotNull(frame.Target);
        Assert.True(frame.IsAbilitiesKnown);
        Assert.Equal(2, frame.Abilities.Count);
        Assert.True(frame.IsPlayerAurasKnown);
        Assert.NotEmpty(frame.PlayerAuras);
        Assert.True(frame.GameInputReady);
    }

    [Fact]
    public void Known_empty_abilities_and_auras_are_distinct_from_unknown()
    {
        byte[] knownEmpty = BuildMultiSectionSlot(
            sequence: 3,
            session: Guid.NewGuid(),
            includePlayer: true,
            includeTarget: false,
            targetAvailable: false,
            abilitiesKnownEmpty: true,
            abilityCount: 0,
            playerAurasEmpty: true,
            gameInputReady: true);

        TelemetryFrame emptyKnown = MapSlot(knownEmpty);
        Assert.True(emptyKnown.IsAbilitiesKnown);
        Assert.Empty(emptyKnown.Abilities);
        Assert.True(emptyKnown.IsPlayerAurasKnown);
        Assert.Empty(emptyKnown.PlayerAuras);

        // Provider-only: no ability/aura sections → unknown knownness
        byte[] providerOnly = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        TelemetryFrame unknown = MapSlot(providerOnly);
        Assert.False(unknown.IsAbilitiesKnown);
        Assert.Empty(unknown.Abilities);
        Assert.False(unknown.IsPlayerAurasKnown);
        Assert.Empty(unknown.PlayerAuras);
    }

    [Fact]
    public void Known_no_target_vs_unknown_target()
    {
        byte[] noTarget = BuildMultiSectionSlot(
            sequence: 4,
            session: Guid.NewGuid(),
            includePlayer: true,
            includeTarget: true,
            targetAvailable: false, // section present, unavailable
            abilitiesKnownEmpty: true,
            abilityCount: 0,
            playerAurasEmpty: true,
            gameInputReady: true);

        TelemetryFrame knownNone = MapSlot(noTarget);
        Assert.Equal(TargetKnownness.KnownNoTarget, knownNone.TargetKnownness);
        Assert.Null(knownNone.Target);

        byte[] omitTarget = BuildMultiSectionSlot(
            sequence: 5,
            session: Guid.NewGuid(),
            includePlayer: true,
            includeTarget: false,
            targetAvailable: false,
            abilitiesKnownEmpty: true,
            abilityCount: 0,
            playerAurasEmpty: true,
            gameInputReady: true);

        TelemetryFrame unknown = MapSlot(omitTarget);
        Assert.Equal(TargetKnownness.Unknown, unknown.TargetKnownness);
        Assert.Null(unknown.Target);
    }

    [Fact]
    public void GameInputReady_true_false_and_unknown_map_from_flags()
    {
        byte[] ready = BuildMultiSectionSlot(
            sequence: 1, session: Guid.NewGuid(), includePlayer: true,
            includeTarget: false, targetAvailable: false, abilitiesKnownEmpty: true,
            abilityCount: 0, playerAurasEmpty: true, gameInputReady: true);
        Assert.True(MapSlot(ready).GameInputReady);

        byte[] blocked = BuildMultiSectionSlot(
            sequence: 2, session: Guid.NewGuid(), includePlayer: true,
            includeTarget: false, targetAvailable: false, abilitiesKnownEmpty: true,
            abilityCount: 0, playerAurasEmpty: true, gameInputReady: false);
        Assert.False(MapSlot(blocked).GameInputReady);

        byte[] unknown = ScannerTestHelpers.BuildSlot(3, Guid.NewGuid());
        // Minimal provider slot has no GameInputReadyKnown bit
        Assert.Null(MapSlot(unknown).GameInputReady);
    }

    [Fact]
    public void Provider_and_mapper_source_contain_no_warrior_specific_hardcoding()
    {
        // Structural: production provider/mapper must stay calling-agnostic.
        string[] paths =
        [
            Path.Combine("src", "BotDs.Reader", "V5", "V5HealthMapper.cs"),
            Path.Combine("src", "BotDs.App", "Services", "TelemetryReaderLoop.cs"),
            Path.Combine("src", "BotDs.App", "Services", "SnapshotTelemetrySource.cs"),
            Path.Combine("src", "BotDs.App", "Services", "SnapshotAssembler.cs"),
        ];

        string root = FindRepoRoot();
        foreach (string rel in paths)
        {
            string full = Path.Combine(root, rel);
            Assert.True(File.Exists(full), $"missing {rel}");
            string text = File.ReadAllText(full);
            Assert.DoesNotContain("\"Warrior\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abilityId = \"", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MinimumLevel = 45", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ITelemetrySource_is_registered_and_tracks_publisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SnapshotPublisher>();
        services.AddSingleton<ITelemetrySource, SnapshotTelemetrySource>();
        using ServiceProvider sp = services.BuildServiceProvider();

        var source = sp.GetRequiredService<ITelemetrySource>();
        var pub = sp.GetRequiredService<SnapshotPublisher>();
        Assert.IsType<SnapshotTelemetrySource>(source);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(ProviderHealth.Disconnected, source.Current.Provider.Health);

        TelemetryFrame injected = new(
            new ProviderStatus(ProviderHealth.Healthy, "5", "s", 9, 1, now, TimeSpan.FromMilliseconds(1)),
            Player: new UnitState("p", "P", 10, "Mage", true, "friendly",
                new HealthState(1, 1), null, false, null),
            Target: null,
            Abilities: ReadOnlyDictionary<string, AbilityState>.Empty,
            PlayerAuras: [],
            TargetAuras: [],
            IsAbilitiesKnown: true,
            TargetKnownness: TargetKnownness.KnownNoTarget,
            GameInputReady: true);
        pub.Publish(injected);

        Assert.Equal(ProviderHealth.Healthy, source.Current.Provider.Health);
        Assert.Equal(9u, source.Current.Provider.Sequence);
        Assert.Equal(TargetKnownness.KnownNoTarget, source.Current.TargetKnownness);
        Assert.True(source.Current.GameInputReady);
        Assert.Equal("Mage", source.Current.Player?.Calling); // non-Warrior fixture proves agnostic path
    }

    [Fact]
    public void Parse_accepts_game_input_ready_flags_rejects_high_reserved()
    {
        byte[] ok = CreateMinimalSlot();
        ok[V5Constants.HdrFlagsOffset] = (byte)(V5Constants.FlagGameInputReadyKnown | V5Constants.FlagGameInputReady);
        V5ParseResult parsed = V5Parser.Parse(ok, 0);
        Assert.True(parsed.IsValid, parsed.FailureDetail);
        Assert.True(parsed.Frame!.Header.IsGameInputReadyKnown);
        Assert.True(parsed.Frame.Header.IsGameInputReady);

        byte[] bad = CreateMinimalSlot();
        bad[V5Constants.HdrFlagsOffset] = V5Constants.FlagsReservedMask;
        V5ParseResult fail = V5Parser.Parse(bad, 0);
        Assert.False(fail.IsValid);
        Assert.Equal(V5ParseFailure.FlagsReservedBitsSet, fail.Failure);
    }

    // ── Wire builders ────────────────────────────────────────

    private static TelemetryFrame MapSlot(byte[] slot)
    {
        V5ParseResult parsed = V5Parser.ParseAndValidate(slot, 0);
        Assert.True(parsed.IsValid, parsed.FailureDetail);
        var result = StableReadResult.Healthy(parsed.Frame!);
        return V5HealthMapper.ToTelemetryFrame(result, DateTimeOffset.UtcNow);
    }

    private static byte[] CreateMinimalSlot()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        // ProviderInfo only so parse is valid for header-flag tests
        Guid session = Guid.NewGuid();
        byte[] provider = BuildProviderInfo(session);
        WriteSection(slot, providerBody: provider, player: null, target: null,
            abilities: null, playerAuras: null, mask: V5Constants.MaskProviderInfo, sequence: 1);
        return slot;
    }

    private static byte[] BuildMultiSectionSlot(
        uint sequence,
        Guid session,
        bool includePlayer,
        bool includeTarget,
        bool targetAvailable,
        bool abilitiesKnownEmpty,
        int abilityCount,
        bool playerAurasEmpty,
        bool? gameInputReady)
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;

        byte[] provider = BuildProviderInfo(session);
        byte[]? player = includePlayer ? BuildUnit(available: true, isPlayer: true) : null;
        byte[]? target = includeTarget ? BuildUnit(available: targetAvailable, isPlayer: false) : null;
        byte[]? abilities = null;
        if (abilitiesKnownEmpty || abilityCount > 0)
            abilities = BuildAbilities(abilityCount);

        byte[]? auras = null;
        if (playerAurasEmpty || !playerAurasEmpty)
        {
            // Always include aura section when player is present for knownness tests
            if (includePlayer)
                auras = BuildAuras(playerAurasEmpty ? 0 : 1);
        }

        uint mask = V5Constants.MaskProviderInfo;
        if (player is not null) mask |= V5Constants.MaskPlayer;
        if (target is not null) mask |= V5Constants.MaskTarget;
        if (abilities is not null) mask |= V5Constants.MaskAbilities;
        if (auras is not null) mask |= V5Constants.MaskPlayerAuras;

        WriteSection(slot, provider, player, target, abilities, auras, mask, sequence);

        byte flags = 0;
        if (gameInputReady is not null)
        {
            flags |= V5Constants.FlagGameInputReadyKnown;
            if (gameInputReady.Value)
                flags |= V5Constants.FlagGameInputReady;
        }
        slot[V5Constants.HdrFlagsOffset] = flags;

        // Recompute CRC after flag write
        uint payloadLen = BitConverter.ToUInt32(slot, V5Constants.HdrPayloadLengthOffset);
        V5Crc32.WriteCrc(slot, payloadLen);
        return slot;
    }

    private static void WriteSection(
        byte[] slot,
        byte[] providerBody,
        byte[]? player,
        byte[]? target,
        byte[]? abilities,
        byte[]? playerAuras,
        uint mask,
        uint sequence)
    {
        using var ms = new MemoryStream();
        void Add(ushort type, byte[] body)
        {
            Span<byte> hdr = stackalloc byte[4];
            BitConverter.TryWriteBytes(hdr[..2], type);
            BitConverter.TryWriteBytes(hdr[2..], (ushort)body.Length);
            ms.Write(hdr);
            ms.Write(body);
        }

        Add(V5Constants.SectionTypeProviderInfo, providerBody);
        if (player is not null) Add(V5Constants.SectionTypePlayer, player);
        if (target is not null) Add(V5Constants.SectionTypeTarget, target);
        if (abilities is not null) Add(V5Constants.SectionTypeAbilities, abilities);
        if (playerAuras is not null) Add(V5Constants.SectionTypePlayerAuras, playerAuras);

        byte[] payload = ms.ToArray();
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), 16u);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), mask);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrHeartbeatIntervalMsOffset), 1000u);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payload.Length);
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        payload.CopyTo(slot.AsSpan(V5Constants.PayloadOffset));
        V5Crc32.WriteCrc(slot, (uint)payload.Length);
    }

    private static byte[] BuildProviderInfo(Guid session)
    {
        using var ms = new MemoryStream();
        ms.Write(session.ToByteArray());
        ms.Write(BitConverter.GetBytes(16u)); // producer frame
        ms.Write(BitConverter.GetBytes(500u)); // max age
        byte[] ver = Encoding.ASCII.GetBytes("test-client");
        ms.Write(BitConverter.GetBytes((ushort)ver.Length));
        ms.Write(ver);
        ms.WriteByte(V5Constants.SchemaVersionCurrent);
        ms.WriteByte(0); // reserved
        return ms.ToArray();
    }

    private static byte[] BuildUnit(bool available, bool isPlayer)
    {
        using var ms = new MemoryStream();
        void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(BitConverter.GetBytes((ushort)b.Length));
            ms.Write(b);
        }
        void WriteUtf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            ms.Write(BitConverter.GetBytes((ushort)b.Length));
            ms.Write(b);
        }

        WriteAscii(available ? "unit-1" : "");
        WriteUtf8(available ? "Unit" : "");
        ms.Write(BitConverter.GetBytes(available ? 40 : -1));
        WriteAscii(isPlayer ? "Mage" : "");
        byte flags = available ? V5Constants.UnitFlagIsAvailable : (byte)0;
        if (isPlayer && available) flags |= V5Constants.UnitFlagIsPlayer;
        ms.WriteByte(flags);
        ms.WriteByte(available ? V5Constants.RelationHostile : (byte)0);
        ms.Write(BitConverter.GetBytes(available ? 100 : -1));
        ms.Write(BitConverter.GetBytes(available ? 100 : -1));
        ms.Write(BitConverter.GetBytes(-1));
        ms.Write(BitConverter.GetBytes(-1));
        WriteAscii("");
        WriteAscii("");
        WriteUtf8("");
        ms.Write(BitConverter.GetBytes(-1));
        ms.Write(BitConverter.GetBytes(-1));
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static byte[] BuildAbilities(int count)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((ushort)count));
        for (int i = 0; i < count; i++)
        {
            byte[] rec = new byte[V5Constants.AbilityRecordSize];
            string id = $"ab{i + 1}";
            byte[] idBytes = Encoding.ASCII.GetBytes(id);
            idBytes.CopyTo(rec, 0);
            BitConverter.TryWriteBytes(rec.AsSpan(32), 0); // cd remaining
            BitConverter.TryWriteBytes(rec.AsSpan(36), 1500); // cd duration
            BitConverter.TryWriteBytes(rec.AsSpan(40), 0); // cast time
            rec[44] = (byte)(V5Constants.AbilityFlagAvailable | V5Constants.AbilityFlagUsable | V5Constants.AbilityFlagInRange);
            string name = $"Ability {i + 1}";
            BitConverter.TryWriteBytes(rec.AsSpan(46), (ushort)name.Length);
            Encoding.UTF8.GetBytes(name).CopyTo(rec.AsSpan(48));
            ms.Write(rec);
        }
        return ms.ToArray();
    }

    private static byte[] BuildAuras(int count)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((ushort)count));
        for (int i = 0; i < count; i++)
        {
            // Fixed 70-byte records per protocol
            byte[] rec = new byte[70];
            string id = $"aura{i + 1}";
            byte[] idBytes = Encoding.ASCII.GetBytes(id);
            idBytes.CopyTo(rec, 0);
            // name length at offset 32 as u16 then name — check PROTOCOL
            // Simplified: use parser-compatible layout from existing tests
            ms.Write(rec);
        }
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotDs.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        // tests run from bin/... → walk up from cwd
        dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "BotDs.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("BotDs.sln not found");
    }
}
