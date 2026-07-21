using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BotDs.Reader.V5;

/// <summary>
/// Wire-format constants and layout for the BotDs V5 protocol.
/// Every offset and size here must match PROTOCOL.md exactly.
/// </summary>
public static class V5Constants
{
    // ── Sentinel ──────────────────────────────────────────────
    public const int SentinelOffset = 0;
    public const int SentinelSize = 16;

    /// <summary>ASCII "BotDsV05" as a little-endian uint64 pair.</summary>
    public static ReadOnlySpan<byte> SentinelMagic => "BotDsV05"u8;

    public const int SentinelMagicLength = 8;

    // Offsets within the 16-byte sentinel
    public const int SentinelTotalSizeOffset = 8;   // uint32 LE
    public const int SentinelSlotSizeOffset = 12;   // uint32 LE

    // ── Region ────────────────────────────────────────────────
    public const int RegionTotalSize = 16400;
    public const int BufferSlotSize = 8192;
    public const int BufferCount = 2;

    // Buffer A and B offsets relative to the sentinel start
    public const int BufferAOffset = 16;
    public const int BufferBOffset = 16 + BufferSlotSize; // 8208

    // ── Buffer Header (per-slot, 28 bytes) ────────────────────
    public const int HeaderSize = 28;

    public const int HdrSequenceOffset = 0;          // uint32 LE
    public const int HdrProducerFrameMsOffset = 4;   // uint32 LE
    public const int HdrSectionsMaskOffset = 8;      // uint32 LE
    public const int HdrHeartbeatIntervalMsOffset = 12; // uint32 LE
    public const int HdrPayloadLengthOffset = 16;    // uint32 LE
    public const int HdrProtocolVersionOffset = 20;  // uint8
    public const int HdrFlagsOffset = 21;            // uint8
    public const int HdrReservedOffset = 22;         // uint16 LE
    public const int HdrCrc32Offset = 24;            // uint32 LE

    // ── Crc coverage range: header bytes 0..23 + payload ─────
    public const int CrcCoveredHeaderLength = 24;    // Sequence .. Reserved

    // ── Payload ───────────────────────────────────────────────
    public const int PayloadOffset = HeaderSize; // 28
    public const int MaxPayloadLength = BufferSlotSize - HeaderSize; // 8164

    // ── Protocol version ──────────────────────────────────────
    public const byte ProtocolVersion = 5;

    // ── Schema version ────────────────────────────────────────
    /// <summary>Minimum accepted provider schema (v1 = 46-byte abilities, no name).</summary>
    public const byte SchemaVersionMin = 1;
    /// <summary>Schema 2: ability records include display name (80-byte fixed records).</summary>
    public const byte SchemaVersionCurrent = 2;
    /// <summary>Legacy ability record size before schema v2 names.</summary>
    public const int AbilityRecordSizeV1 = 46;

    // ── Flags ─────────────────────────────────────────────────
    public const byte FlagIsHeartbeat = 0x01;
    public const byte FlagIsSecure = 0x02;
    /// <summary>When set with <see cref="FlagGameInputReadyKnown"/>, game input is ready.</summary>
    public const byte FlagGameInputReady = 0x04;
    /// <summary>When set, <see cref="FlagGameInputReady"/> is meaningful (known).</summary>
    public const byte FlagGameInputReadyKnown = 0x08;
    /// <summary>Bits 4–7 must remain zero (reserved).</summary>
    public const byte FlagsReservedMask = 0xF0;

    // ── Section types ─────────────────────────────────────────
    public const ushort SectionTypeProviderInfo = 0x0001;
    public const ushort SectionTypePlayer = 0x0002;
    public const ushort SectionTypeTarget = 0x0003;
    public const ushort SectionTypeAbilities = 0x0004;
    public const ushort SectionTypePlayerAuras = 0x0005;
    public const ushort SectionTypeTargetAuras = 0x0006;
    public const ushort SectionTypeActionBar = 0x0007;

    // ── Sections mask bits ────────────────────────────────────
    public const uint MaskProviderInfo = 0x00000001;
    public const uint MaskPlayer = 0x00000002;
    public const uint MaskTarget = 0x00000004;
    public const uint MaskAbilities = 0x00000008;
    public const uint MaskPlayerAuras = 0x00000010;
    public const uint MaskTargetAuras = 0x00000020;
    public const uint MaskActionBar = 0x00000040;

    public const int MaxActionBarSlots = 12;
    public const int ActionBarSlotRecordSize = 33; // slot:1 + abilityId:32

    // ── Section TLV header ────────────────────────────────────
    public const int SectionHeaderSize = 4; // type:2 + length:2

    // ── Bounds ────────────────────────────────────────────────
    public const int MaxAbilities = 128;
    public const int MaxAurasPerList = 64;
    public const int MaxUnitIdLength = 64;
    public const int MaxUnitNameLength = 32;
    public const int MaxCallingLength = 16;
    public const int MaxResourceKindLength = 16;
    public const int MaxCastAbilityIdLength = 32;
    public const int MaxCastNameLength = 32;
    public const int MaxClientVersionLength = 128;

    // ── Fixed record sizes ────────────────────────────────────
    // Schema v2: id:32 + cdRemain:4 + cdDur:4 + castTime:4 + flags:1 + cost:1 + nameLen:2 + name:32 = 80
    public const int AbilityRecordSize = 80;
    public const int AuraRecordSize = 70;     // id:32 + nameLen:2 + name:32 + stacks:1 + flags:1 + remaining:2
    public const int AbilityIdFieldSize = 32;
    public const int AbilityNameFieldSize = 32;
    public const int AuraIdFieldSize = 32;
    public const int AuraNameFieldSize = 32;

    // ── Unit sentinel values ──────────────────────────────────
    public const int NullInt32 = -1;
    public const short NullInt16 = -1;
    public const byte RelationUnknown = 0;
    public const byte RelationHostile = 1;
    public const byte RelationFriendly = 2;
    public const byte RelationNeutral = 3;

    public const byte UnitFlagIsPlayer = 0x01;
    public const byte UnitFlagInCombat = 0x02;
    public const byte UnitFlagIsAvailable = 0x04;

    public const byte CastFlagIsChannel = 0x01;
    public const byte CastFlagIsUninterruptible = 0x02;

    public const byte AbilityFlagAvailable = 0x01;
    public const byte AbilityFlagUsable = 0x02;
    public const byte AbilityFlagInRange = 0x04;
    public const byte AbilityFlagPassive = 0x08;
    public const byte AbilityFlagChanneled = 0x10;

    public const byte AuraFlagIsDebuff = 0x01;
    public const byte AuraFlagIsCurse = 0x02;
    public const byte AuraFlagIsDisease = 0x04;
    public const byte AuraFlagIsPoison = 0x08;
}

/// <summary>
/// Parsed buffer header before CRC validation. Layout matches protocol.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct V5BufferHeader
{
    public uint Sequence;
    public uint ProducerFrameMs;
    public uint SectionsMask;
    public uint HeartbeatIntervalMs;
    public uint PayloadLength;
    public byte ProtocolVersion;
    public byte Flags;
    public ushort Reserved;
    public uint Crc32;

    public bool IsHeartbeat => (Flags & V5Constants.FlagIsHeartbeat) != 0;
    public bool IsSecure => (Flags & V5Constants.FlagIsSecure) != 0;
    public bool IsGameInputReadyKnown => (Flags & V5Constants.FlagGameInputReadyKnown) != 0;
    public bool IsGameInputReady => IsGameInputReadyKnown && (Flags & V5Constants.FlagGameInputReady) != 0;

    public bool HasSection(uint mask) => (SectionsMask & mask) != 0;

    public readonly bool IsReservedZero() => Reserved == 0;
}

/// <summary>
/// Parsed sentinel from the memory region.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct V5Sentinel
{
    public ulong MagicLow;  // first 8 bytes of magic
    public uint TotalSize;
    public uint BufferSlotSize;

    public readonly bool IsMagicValid()
    {
        Span<byte> magicBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(magicBytes, MagicLow);
        return magicBytes.SequenceEqual(V5Constants.SentinelMagic);
    }
}

/// <summary>
/// Parsed TLV section header.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct V5SectionHeader
{
    public ushort SectionType;
    public ushort SectionLength;
}

/// <summary>
/// Fixed-size ability record as it appears on the wire (schema v2, 80 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
public struct V5AbilityRecord
{
    // 32 bytes ASCII, space-padded
    public unsafe fixed byte AbilityId[32];
    public int CooldownRemainingMs;
    public int CooldownDurationMs;
    public int CastTimeMs;
    public byte Flags;
    public byte ResourceCost;
    public ushort NameLength;
    public unsafe fixed byte Name[32];

    public string GetAbilityId()
    {
        unsafe
        {
            fixed (byte* ptr = AbilityId)
            {
                int len = 0;
                while (len < 32 && ptr[len] != 0 && ptr[len] != 0x20) len++;
                return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(ptr, len);
            }
        }
    }

    public string GetName()
    {
        unsafe
        {
            fixed (byte* ptr = Name)
            {
                int len = Math.Min((int)NameLength, 32);
                int actual = 0;
                while (actual < len && ptr[actual] != 0) actual++;
                return actual == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(ptr, actual);
            }
        }
    }
}

/// <summary>
/// Fixed-size aura record as it appears on the wire.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 70)]
public struct V5AuraRecord
{
    public unsafe fixed byte AuraId[32];
    public ushort NameLength;
    public unsafe fixed byte Name[32];
    public byte Stacks;
    public byte Flags;
    public short RemainingMsLow;

    public string GetAuraId()
    {
        unsafe
        {
            fixed (byte* ptr = AuraId)
            {
                int len = 0;
                while (len < 32 && ptr[len] != 0 && ptr[len] != 0x20) len++;
                return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(ptr, len);
            }
        }
    }

    public string GetName()
    {
        unsafe
        {
            fixed (byte* ptr = Name)
            {
                int len = Math.Min((int)NameLength, 32);
                int actual = 0;
                while (actual < len && ptr[actual] != 0) actual++;
                return actual == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(ptr, actual);
            }
        }
    }
}

/// <summary>
/// Parsed provider-info section data.
/// </summary>
public sealed record ParsedProviderInfo(
    Guid SessionId,
    uint ProducerFrameMs,
    uint MaxTelemetryAgeMs,
    string ClientVersion,
    byte SchemaVersion);

/// <summary>
/// Parsed unit state from a Player or Target section.
/// </summary>
public sealed record ParsedUnitState(
    string? Id,
    string? Name,
    int Level,
    string? Calling,
    byte Flags,
    byte Relation,
    int HealthCurrent,
    int HealthMaximum,
    int ResourceCurrent,
    int ResourceMaximum,
    string? ResourceKind,
    string? CastAbilityId,
    string? CastName,
    int CastRemainingMs,
    int CastDurationMs,
    byte CastFlags)
{
    public bool IsPlayer => (Flags & V5Constants.UnitFlagIsPlayer) != 0;
    public bool InCombat => (Flags & V5Constants.UnitFlagInCombat) != 0;
    public bool IsAvailable => (Flags & V5Constants.UnitFlagIsAvailable) != 0;
    public bool IsChannel => (CastFlags & V5Constants.CastFlagIsChannel) != 0;
    public bool IsUninterruptible => (CastFlags & V5Constants.CastFlagIsUninterruptible) != 0;
    public bool IsHostile => Relation == V5Constants.RelationHostile;
    public bool HasHealth => HealthCurrent != V5Constants.NullInt32 && HealthMaximum != V5Constants.NullInt32;
    public bool IsCasting => CastRemainingMs > 0;
}

/// <summary>
/// Parsed ability state.
/// </summary>
public sealed record ParsedAbilityState(
    string AbilityId,
    int CooldownRemainingMs,
    int CooldownDurationMs,
    int CastTimeMs,
    byte Flags,
    byte ResourceCost,
    string Name = "")
{
    public bool Available => (Flags & V5Constants.AbilityFlagAvailable) != 0;
    public bool Usable => (Flags & V5Constants.AbilityFlagUsable) != 0;
    public bool InRange => (Flags & V5Constants.AbilityFlagInRange) != 0;
    public bool IsPassive => (Flags & V5Constants.AbilityFlagPassive) != 0;
    public bool IsChanneled => (Flags & V5Constants.AbilityFlagChanneled) != 0;
}

/// <summary>One action-bar slot observation (slot index + ability id when present).</summary>
public sealed record ParsedActionBarSlot(byte Slot, string AbilityId);

/// <summary>Action bar page + slot placements for calibration (keys still user-configured).</summary>
public sealed record ParsedActionBar(byte Page, IReadOnlyList<ParsedActionBarSlot> Slots);

/// <summary>
/// Parsed aura state.
/// </summary>
public sealed record ParsedAuraState(
    string AuraId,
    string Name,
    byte Stacks,
    byte Flags,
    int RemainingMs)
{
    public bool IsDebuff => (Flags & V5Constants.AuraFlagIsDebuff) != 0;
    public bool IsCurse => (Flags & V5Constants.AuraFlagIsCurse) != 0;
    public bool IsDisease => (Flags & V5Constants.AuraFlagIsDisease) != 0;
    public bool IsPoison => (Flags & V5Constants.AuraFlagIsPoison) != 0;
}

/// <summary>
/// Complete parsed frame from a single buffer slot.
/// </summary>
public sealed record ParsedV5Frame(
    V5BufferHeader Header,
    ParsedProviderInfo? Provider,
    ParsedUnitState? Player,
    ParsedUnitState? Target,
    IReadOnlyList<ParsedAbilityState> Abilities,
    IReadOnlyList<ParsedAuraState> PlayerAuras,
    IReadOnlyList<ParsedAuraState> TargetAuras,
    int BufferIndex,
    ParsedActionBar? ActionBar = null);
