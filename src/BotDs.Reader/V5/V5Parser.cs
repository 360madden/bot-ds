using System.Runtime.InteropServices;

namespace BotDs.Reader.V5;

/// <summary>
/// Structured parse failure with an explicit reason code.
/// Every field in the protocol that can be invalid produces a distinct failure.
/// </summary>
public enum V5ParseFailure
{
    None = 0,

    // Sentinel failures
    SentinelNotFound,
    SentinelMagicInvalid,
    SentinelTotalSizeMismatch,
    SentinelSlotSizeMismatch,

    // Buffer-level failures
    BufferTooSmall,
    PayloadLengthExceedsBuffer,
    ProtocolVersionMismatch,
    ReservedFieldNonZero,
    CrcMismatch,
    FlagsReservedBitsSet,

    // Section-level failures
    SectionTypeUnknown,
    SectionHeaderTruncated,
    SectionLengthExceedsBuffer,
    SectionPayloadTruncated,

    // ProviderInfo failures
    ProviderInfoTruncated,
    ProviderClientVersionTooLong,
    ProviderSchemaVersionMismatch,

    // Unit state failures
    UnitStringTooLong,
    UnitTruncated,

    // Ability failures
    AbilityCountExceedsMax,
    AbilitiesTruncated,

    // Aura failures
    AuraCountExceedsMax,
    AurasTruncated,
    AuraNameLengthExceedsMax,

    // Section tracking
    DuplicateSectionType,
    SectionsMaskMismatch,
    SectionsOutOfOrder,

    // Section payload strictness
    SectionPayloadLengthMismatch,

    // Heartbeat
    HeartbeatWithExcessSections,

    // General
    SectionsMaskReservedBitsSet,
    PayloadContainsTrailingData,
}

/// <summary>
/// Result of parsing a single buffer slot.
/// </summary>
public sealed record V5ParseResult(
    bool IsValid,
    V5ParseFailure Failure,
    string? FailureDetail,
    ParsedV5Frame? Frame)
{
    public static V5ParseResult Ok(ParsedV5Frame frame) => new(true, V5ParseFailure.None, null, frame);

    public static V5ParseResult Fail(V5ParseFailure failure, string? detail = null) =>
        new(false, failure, detail, null);
}

/// <summary>
/// Bounded, defensive parser for the V5 protocol buffer format.
/// Every parse function validates bounds explicitly before reading.
/// Unknown or invalid data produces a structured failure, never a crash or guess.
/// </summary>
public static class V5Parser
{
    /// <summary>
    /// Parse a complete 8192-byte buffer slot into a ParsedV5Frame.
    /// Does NOT validate CRC — the caller must validate CRC before calling this
    /// or call <see cref="ParseAndValidate"/> instead.
    /// </summary>
    public static V5ParseResult Parse(ReadOnlySpan<byte> buffer, int bufferIndex)
    {
        if (buffer.Length < V5Constants.BufferSlotSize)
            return V5ParseResult.Fail(V5ParseFailure.BufferTooSmall,
                $"Buffer length {buffer.Length} < {V5Constants.BufferSlotSize}");

        // Parse header
        V5BufferHeader header = MemoryMarshal.Read<V5BufferHeader>(buffer[..V5Constants.HeaderSize]);

        if (header.ProtocolVersion != V5Constants.ProtocolVersion)
            return V5ParseResult.Fail(V5ParseFailure.ProtocolVersionMismatch,
                $"Expected version {V5Constants.ProtocolVersion}, got {header.ProtocolVersion}");

        if (!header.IsReservedZero())
            return V5ParseResult.Fail(V5ParseFailure.ReservedFieldNonZero,
                $"Reserved field is {header.Reserved} (expected 0)");

        if ((header.Flags & 0xFC) != 0) // bits 2-7 must be zero
            return V5ParseResult.Fail(V5ParseFailure.FlagsReservedBitsSet,
                $"Reserved flags bits set: 0x{header.Flags:X2}");

        if (header.SectionsMask > 0x3F) // bits 6-31 must be zero
            return V5ParseResult.Fail(V5ParseFailure.SectionsMaskReservedBitsSet,
                $"Reserved section mask bits set: 0x{header.SectionsMask:X8}");

        // Heartbeat frames MUST carry exactly ProviderInfo and no game-state sections.
        if (header.IsHeartbeat && header.SectionsMask != V5Constants.MaskProviderInfo)
            return V5ParseResult.Fail(V5ParseFailure.HeartbeatWithExcessSections,
                $"Heartbeat frame must have exactly ProviderInfo section (mask 0x{V5Constants.MaskProviderInfo:X8}), got mask 0x{header.SectionsMask:X8}");

        uint payloadLength = header.PayloadLength;
        if (payloadLength > V5Constants.MaxPayloadLength)
            return V5ParseResult.Fail(V5ParseFailure.PayloadLengthExceedsBuffer,
                $"PayloadLength {payloadLength} > max {V5Constants.MaxPayloadLength}");

        ReadOnlySpan<byte> payload = buffer.Slice(V5Constants.PayloadOffset, (int)payloadLength);

        // Parse sections
        ParsedProviderInfo? provider = null;
        ParsedUnitState? player = null;
        ParsedUnitState? target = null;
        List<ParsedAbilityState> abilities = [];
        List<ParsedAuraState> playerAuras = [];
        List<ParsedAuraState> targetAuras = [];

        uint actualMask = 0;
        uint lastKnownMask = 0; // enforce ascending mask order per PROTOCOL.md §3.2

        int offset = 0;
        while (offset + V5Constants.SectionHeaderSize <= payload.Length)
        {
            V5SectionHeader sectionHeader = MemoryMarshal.Read<V5SectionHeader>(payload[offset..]);

            if (sectionHeader.SectionLength > payload.Length - offset - V5Constants.SectionHeaderSize)
                return V5ParseResult.Fail(V5ParseFailure.SectionLengthExceedsBuffer,
                    $"Section type 0x{sectionHeader.SectionType:X4} length {sectionHeader.SectionLength} exceeds remaining buffer");

            ReadOnlySpan<byte> sectionData = payload.Slice(
                offset + V5Constants.SectionHeaderSize,
                sectionHeader.SectionLength);

            uint sectionMask = GetKnownSectionMask(sectionHeader.SectionType);
            if (sectionMask != 0)
            {
                // Reject duplicate known section types
                if ((actualMask & sectionMask) != 0)
                    return V5ParseResult.Fail(V5ParseFailure.DuplicateSectionType,
                        $"Duplicate known section type 0x{sectionHeader.SectionType:X4}");

                // Require ascending mask order per protocol §3.2
                if (sectionMask <= lastKnownMask)
                    return V5ParseResult.Fail(V5ParseFailure.SectionsOutOfOrder,
                        $"Section type 0x{sectionHeader.SectionType:X4} mask 0x{sectionMask:X8} out of order (previous mask 0x{lastKnownMask:X8})");

                actualMask |= sectionMask;
                lastKnownMask = sectionMask;
            }

            V5ParseResult sectionResult = sectionHeader.SectionType switch
            {
                V5Constants.SectionTypeProviderInfo => ParseProviderInfo(sectionData, out provider),
                V5Constants.SectionTypePlayer => ParseUnitState(sectionData, out player),
                V5Constants.SectionTypeTarget => ParseUnitState(sectionData, out target),
                V5Constants.SectionTypeAbilities => ParseAbilities(sectionData, abilities),
                V5Constants.SectionTypePlayerAuras => ParseAuras(sectionData, playerAuras),
                V5Constants.SectionTypeTargetAuras => ParseAuras(sectionData, targetAuras),
                _ => V5ParseResult.Fail(V5ParseFailure.SectionTypeUnknown,
                    $"Unknown section type 0x{sectionHeader.SectionType:X4}")
            };

            if (!sectionResult.IsValid)
                return sectionResult;

            offset += V5Constants.SectionHeaderSize + sectionHeader.SectionLength;
        }

        // Require exact equality between actual section bits and header mask.
        // A mask bit without a section, or a section without its mask bit, must fail.
        if (actualMask != header.SectionsMask)
            return V5ParseResult.Fail(V5ParseFailure.SectionsMaskMismatch,
                $"Actual section mask 0x{actualMask:X8} differs from header SectionsMask 0x{header.SectionsMask:X8}");

        if (offset < payload.Length)
            return V5ParseResult.Fail(V5ParseFailure.PayloadContainsTrailingData,
                $"Payload has {payload.Length - offset} trailing bytes after sections");

        var frame = new ParsedV5Frame(
            header, provider, player, target,
            abilities.AsReadOnly(), playerAuras.AsReadOnly(), targetAuras.AsReadOnly(),
            bufferIndex);

        return V5ParseResult.Ok(frame);
    }

    /// <summary>
    /// Validate CRC then parse. The combined operation for convenience.
    /// </summary>
    public static V5ParseResult ParseAndValidate(ReadOnlySpan<byte> buffer, int bufferIndex)
    {
        if (!V5Crc32.ValidateBuffer(buffer, out _))
            return V5ParseResult.Fail(V5ParseFailure.CrcMismatch, "CRC validation failed");

        return Parse(buffer, bufferIndex);
    }

    // ── Section parsers ───────────────────────────────────────

    private static V5ParseResult ParseProviderInfo(ReadOnlySpan<byte> data, out ParsedProviderInfo? result)
    {
        result = null;

        // ProviderInfo layout: sessionId(16) + wallClock(4) + maxAge(4) + versionLen(2) + version + schema(1) + reserved(1)
        // Minimum to read versionLen field is 26 bytes.
        const int minSizeToReadVersionLen = 26;

        if (data.Length < minSizeToReadVersionLen)
            return V5ParseResult.Fail(V5ParseFailure.ProviderInfoTruncated,
                $"ProviderInfo data length {data.Length} < minimum {minSizeToReadVersionLen} to read version length");

        ushort versionLen = BitConverter.ToUInt16(data[24..]);

        if (versionLen > V5Constants.MaxClientVersionLength)
            return V5ParseResult.Fail(V5ParseFailure.ProviderClientVersionTooLong,
                $"Client version length {versionLen} > max {V5Constants.MaxClientVersionLength}");

        int expectedSize = 28 + versionLen;
        if (data.Length != expectedSize)
            return V5ParseResult.Fail(V5ParseFailure.SectionPayloadLengthMismatch,
                $"ProviderInfo expected {expectedSize} bytes (versionLen={versionLen}), got {data.Length}");

        // Parse fixed fields
        ReadOnlySpan<byte> sessionIdBytes = data[..16];
        Guid sessionId = new(sessionIdBytes, bigEndian: false);

        uint producerFrameMs = BitConverter.ToUInt32(data[16..]);
        uint maxAgeMs = BitConverter.ToUInt32(data[20..]);

        string clientVersion = versionLen == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(data.Slice(26, versionLen));

        byte schemaVersion = data[26 + versionLen];
        byte reserved = data[27 + versionLen];

        if (schemaVersion != V5Constants.SchemaVersionCurrent)
            return V5ParseResult.Fail(V5ParseFailure.ProviderSchemaVersionMismatch,
                $"ProviderInfo SchemaVersion {schemaVersion} != expected {V5Constants.SchemaVersionCurrent}");

        if (reserved != 0)
            return V5ParseResult.Fail(V5ParseFailure.ReservedFieldNonZero,
                $"ProviderInfo reserved byte is {reserved} (expected 0)");

        result = new ParsedProviderInfo(sessionId, producerFrameMs, maxAgeMs, clientVersion, schemaVersion);
        return V5ParseResult.Ok(null!);
    }

    private static V5ParseResult ParseUnitState(ReadOnlySpan<byte> data, out ParsedUnitState? result)
    {
        result = null;
        int offset = 0;

        // Id
        if (!ReadLengthPrefixedAscii(data, ref offset, V5Constants.MaxUnitIdLength, out string? id))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "Unit Id exceeds max length");
        if (offset > data.Length) return V5ParseResult.Fail(V5ParseFailure.UnitTruncated, "Unit Id truncated");

        // Name
        if (!ReadLengthPrefixedUtf8(data, ref offset, V5Constants.MaxUnitNameLength, out string? name))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "Unit Name exceeds max length");
        if (offset > data.Length) return V5ParseResult.Fail(V5ParseFailure.UnitTruncated, "Unit Name truncated");

        // Fixed fields
        if (offset + 4 > data.Length) return Truncated("Level");
        int level = BitConverter.ToInt32(data[offset..]);
        offset += 4;

        // Calling
        if (!ReadLengthPrefixedAscii(data, ref offset, V5Constants.MaxCallingLength, out string? calling))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "Calling exceeds max length");
        if (offset > data.Length) return Truncated("Calling");

        // Flags, Relation
        if (offset + 2 > data.Length) return Truncated("Flags/Relation");
        byte flags = data[offset++];
        byte relation = data[offset++];

        // Health
        if (offset + 8 > data.Length) return Truncated("Health");
        int healthCur = BitConverter.ToInt32(data[offset..]);
        offset += 4;
        int healthMax = BitConverter.ToInt32(data[offset..]);
        offset += 4;

        // Resource
        if (offset + 8 > data.Length) return Truncated("Resource");
        int resCur = BitConverter.ToInt32(data[offset..]);
        offset += 4;
        int resMax = BitConverter.ToInt32(data[offset..]);
        offset += 4;

        // ResourceKind
        if (!ReadLengthPrefixedAscii(data, ref offset, V5Constants.MaxResourceKindLength, out string? resKind))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "ResourceKind exceeds max length");
        if (offset > data.Length) return Truncated("ResourceKind");

        // CastAbilityId
        if (!ReadLengthPrefixedAscii(data, ref offset, V5Constants.MaxCastAbilityIdLength, out string? castId))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "CastAbilityId exceeds max length");
        if (offset > data.Length) return Truncated("CastAbilityId");

        // CastName
        if (!ReadLengthPrefixedUtf8(data, ref offset, V5Constants.MaxCastNameLength, out string? castName))
            return V5ParseResult.Fail(V5ParseFailure.UnitStringTooLong, "CastName exceeds max length");
        if (offset > data.Length) return Truncated("CastName");

        // Cast timing
        if (offset + 8 > data.Length) return Truncated("CastTiming");
        int castRemain = BitConverter.ToInt32(data[offset..]);
        offset += 4;
        int castDur = BitConverter.ToInt32(data[offset..]);
        offset += 4;

        // CastFlags
        if (offset + 1 > data.Length) return Truncated("CastFlags");
        byte castFlags = data[offset++];

        // Unit body must end exactly after CastFlags — no trailing bytes.
        if (offset != data.Length)
            return V5ParseResult.Fail(V5ParseFailure.SectionPayloadLengthMismatch,
                $"Unit section expected exactly {offset} bytes, got {data.Length}");

        result = new ParsedUnitState(
            id, name, level, calling, flags, relation,
            healthCur, healthMax, resCur, resMax, resKind,
            castId, castName, castRemain, castDur, castFlags);
        return V5ParseResult.Ok(null!);

        static V5ParseResult Truncated(string field) =>
            V5ParseResult.Fail(V5ParseFailure.UnitTruncated, $"Unit field '{field}' truncated");
    }

    private static V5ParseResult ParseAbilities(ReadOnlySpan<byte> data, List<ParsedAbilityState> abilities)
    {
        if (data.Length < 2)
            return V5ParseResult.Fail(V5ParseFailure.AbilitiesTruncated, "Ability count truncated");

        ushort count = BitConverter.ToUInt16(data);
        if (count > V5Constants.MaxAbilities)
            return V5ParseResult.Fail(V5ParseFailure.AbilityCountExceedsMax,
                $"Ability count {count} > max {V5Constants.MaxAbilities}");

        int expectedSize = 2 + count * V5Constants.AbilityRecordSize;
        if (data.Length != expectedSize)
            return V5ParseResult.Fail(V5ParseFailure.SectionPayloadLengthMismatch,
                $"Abilities expected {expectedSize} bytes for {count} records, got {data.Length}");

        abilities.Clear();
        abilities.Capacity = Math.Max(abilities.Capacity, count);

        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(offset, V5Constants.AbilityRecordSize);
            V5AbilityRecord rec = MemoryMarshal.Read<V5AbilityRecord>(record);

            abilities.Add(new ParsedAbilityState(
                rec.GetAbilityId(),
                rec.CooldownRemainingMs,
                rec.CooldownDurationMs,
                rec.CastTimeMs,
                rec.Flags,
                rec.ResourceCost));

            offset += V5Constants.AbilityRecordSize;
        }

        return V5ParseResult.Ok(null!);
    }

    private static V5ParseResult ParseAuras(ReadOnlySpan<byte> data, List<ParsedAuraState> auras)
    {
        if (data.Length < 2)
            return V5ParseResult.Fail(V5ParseFailure.AurasTruncated, "Aura count truncated");

        ushort count = BitConverter.ToUInt16(data);
        if (count > V5Constants.MaxAurasPerList)
            return V5ParseResult.Fail(V5ParseFailure.AuraCountExceedsMax,
                $"Aura count {count} > max {V5Constants.MaxAurasPerList}");

        int expectedSize = 2 + count * V5Constants.AuraRecordSize;
        if (data.Length != expectedSize)
            return V5ParseResult.Fail(V5ParseFailure.SectionPayloadLengthMismatch,
                $"Auras expected {expectedSize} bytes for {count} records, got {data.Length}");

        auras.Clear();
        auras.Capacity = Math.Max(auras.Capacity, count);

        int offset = 2;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> record = data.Slice(offset, V5Constants.AuraRecordSize);
            V5AuraRecord rec = MemoryMarshal.Read<V5AuraRecord>(record);

            if (rec.NameLength > V5Constants.AuraNameFieldSize)
                return V5ParseResult.Fail(V5ParseFailure.AuraNameLengthExceedsMax,
                    $"Aura name length {rec.NameLength} > max {V5Constants.AuraNameFieldSize}");

            auras.Add(new ParsedAuraState(
                rec.GetAuraId(),
                rec.GetName(),
                rec.Stacks,
                rec.Flags,
                rec.RemainingMsLow >= 0 ? rec.RemainingMsLow : V5Constants.NullInt32));

            offset += V5Constants.AuraRecordSize;
        }

        return V5ParseResult.Ok(null!);
    }

    // ── Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Map a known section type to its SectionsMask bit. Returns 0 for unknown types.
    /// </summary>
    private static uint GetKnownSectionMask(ushort sectionType) => sectionType switch
    {
        V5Constants.SectionTypeProviderInfo => V5Constants.MaskProviderInfo,
        V5Constants.SectionTypePlayer => V5Constants.MaskPlayer,
        V5Constants.SectionTypeTarget => V5Constants.MaskTarget,
        V5Constants.SectionTypeAbilities => V5Constants.MaskAbilities,
        V5Constants.SectionTypePlayerAuras => V5Constants.MaskPlayerAuras,
        V5Constants.SectionTypeTargetAuras => V5Constants.MaskTargetAuras,
        _ => 0,
    };

    private static bool ReadLengthPrefixedAscii(
        ReadOnlySpan<byte> data, ref int offset, int maxLen, out string? result)
    {
        result = null;
        if (offset + 2 > data.Length) return false;

        ushort len = BitConverter.ToUInt16(data[offset..]);
        offset += 2;

        if (len > maxLen) return false;
        if (len == 0) { result = string.Empty; return true; }
        if (offset + len > data.Length) return false;

        result = System.Text.Encoding.ASCII.GetString(data.Slice(offset, len));
        offset += len;
        return true;
    }

    private static bool ReadLengthPrefixedUtf8(
        ReadOnlySpan<byte> data, ref int offset, int maxLen, out string? result)
    {
        result = null;
        if (offset + 2 > data.Length) return false;

        ushort len = BitConverter.ToUInt16(data[offset..]);
        offset += 2;

        if (len > maxLen) return false;
        if (len == 0) { result = string.Empty; return true; }
        if (offset + len > data.Length) return false;

        result = System.Text.Encoding.UTF8.GetString(data.Slice(offset, len));
        offset += len;
        return true;
    }
}
