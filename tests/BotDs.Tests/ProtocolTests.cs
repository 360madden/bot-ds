using System.Text;
using BotDs.Core;
using BotDs.Reader.V5;

namespace BotDs.Tests;

public sealed class V5Crc32Tests
{
    [Fact]
    public void Compute_EmptySpan_ReturnsExpected()
    {
        uint crc = V5Crc32.Compute([]);
        Assert.Equal(0x00000000u, crc);
    }

    [Fact]
    public void Compute_KnownVector_Matches()
    {
        // "123456789" → CRC-32/ISO-HDLC = 0xCBF43926
        byte[] data = "123456789"u8.ToArray();
        uint crc = V5Crc32.Compute(data);
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void ValidateBuffer_TooSmall_ReturnsFalse()
    {
        bool valid = V5Crc32.ValidateBuffer(new byte[100], out _);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateBuffer_EmptySlot_ValidCRC()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        V5Crc32.WriteCrc(slot, payloadLength: 0);
        bool valid = V5Crc32.ValidateBuffer(slot, out uint crc);
        Assert.True(valid);
        Assert.NotEqual(0u, crc);
    }

    [Fact]
    public void ValidateBuffer_ModifiedPayload_InvalidatesCRC()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        int payloadLen = 10;
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLen);
        // Fill payload with known data
        for (int i = 0; i < payloadLen; i++)
            slot[V5Constants.PayloadOffset + i] = (byte)i;
        V5Crc32.WriteCrc(slot, (uint)payloadLen);
        // Modify payload after CRC was written
        slot[V5Constants.PayloadOffset] = 0xFF;
        bool valid = V5Crc32.ValidateBuffer(slot, out _);
        Assert.False(valid);
    }

    [Fact]
    public void WriteCrc_RoundTrip_Validates()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        // Write header fields
        int payloadLen = 64;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), 1u);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLen);
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;

        // Fill payload
        for (int i = 0; i < payloadLen; i++)
            slot[V5Constants.PayloadOffset + i] = (byte)i;

        V5Crc32.WriteCrc(slot, (uint)payloadLen);
        Assert.True(V5Crc32.ValidateBuffer(slot, out uint computed));
        Assert.Equal(computed, BitConverter.ToUInt32(slot.AsSpan(V5Constants.HdrCrc32Offset)));
    }
}

public sealed class V5ParserTests
{
    [Fact]
    public void Parse_TooSmallBuffer_ReturnsBufferTooSmall()
    {
        V5ParseResult result = V5Parser.Parse(new byte[100], 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.BufferTooSmall, result.Failure);
    }

    [Fact]
    public void Parse_WrongVersion_ReturnsVersionMismatch()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = 4;
        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.ProtocolVersionMismatch, result.Failure);
    }

    [Fact]
    public void Parse_ReservedNonZero_ReturnsReservedNonZero()
    {
        byte[] slot = CreateMinimalSlot();
        slot[V5Constants.HdrReservedOffset] = 0x01;
        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.ReservedFieldNonZero, result.Failure);
    }

    [Fact]
    public void Parse_ReservedFlagsSet_ReturnsFlagsReserved()
    {
        byte[] slot = CreateMinimalSlot();
        slot[V5Constants.HdrFlagsOffset] = 0xFC;
        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.FlagsReservedBitsSet, result.Failure);
    }

    [Fact]
    public void Parse_EmptySlot_ReturnsValidFrame()
    {
        byte[] slot = CreateMinimalSlot();
        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public void ParseAndValidate_BadCrc_ReturnsCrcMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        V5ParseResult result = V5Parser.ParseAndValidate(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.CrcMismatch, result.Failure);
    }

    [Fact]
    public void ParseAndValidate_GoodCrc_ReturnsValid()
    {
        byte[] slot = CreateMinimalSlot();
        V5Crc32.WriteCrc(slot, payloadLength: 0);
        V5ParseResult result = V5Parser.ParseAndValidate(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
    }

    [Fact]
    public void Parse_ProviderInfoSection_ReadsCorrectly()
    {
        byte[] slot = CreateMinimalSlot();

        // Build minimal ProviderInfo section data
        List<byte> sectionData = [];
        // SessionId: 16 bytes
        Guid sessionId = Guid.NewGuid();
        sectionData.AddRange(sessionId.ToByteArray());
        // ProducerWallClockMs: 4 bytes (uint32)
        sectionData.AddRange(BitConverter.GetBytes(1000u));
        // MaxTelemetryAgeMs: 4 bytes (uint32)
        sectionData.AddRange(BitConverter.GetBytes(500u));
        // ClientVersionLength: 2 bytes
        sectionData.AddRange(BitConverter.GetBytes((ushort)0));
        // ClientVersion: 0 bytes
        // SchemaVersion: 1 byte
        sectionData.Add(V5Constants.SchemaVersionCurrent);
        // Reserved: 1 byte
        sectionData.Add(0);

        // Write section header + data to payload
        int offset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)V5Constants.SectionTypeProviderInfo);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)sectionData.Count);
        offset += 2;
        sectionData.CopyTo(slot, offset);

        int payloadLength = 4 + sectionData.Count;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame!.Provider);
        Assert.Equal(sessionId, result.Frame.Provider.SessionId);
        Assert.Equal(1000u, result.Frame.Provider.ProducerFrameMs);
        Assert.Equal(500u, result.Frame.Provider.MaxTelemetryAgeMs);
    }

    [Fact]
    public void Parse_AbilitiesSection_ReadsCorrectly()
    {
        byte[] slot = CreateMinimalSlot();

        // Build abilities section: count(2) + records(count * AbilityRecordSize)
        ushort count = 1;
        List<byte> sectionData = [];
        sectionData.AddRange(BitConverter.GetBytes(count));

        // One ability record (schema v2, 80 bytes)
        byte[] record = new byte[V5Constants.AbilityRecordSize];
        // AbilityId at offset 0 (32 bytes)
        "test_ability_01"u8.CopyTo(record.AsSpan(0));
        // Pad remaining space in id field with spaces
        for (int i = "test_ability_01".Length; i < 32; i++)
            record[i] = 0x20;
        // CooldownRemainingMs at offset 32
        BitConverter.TryWriteBytes(record.AsSpan(32), 0);
        // CooldownDurationMs at offset 36
        BitConverter.TryWriteBytes(record.AsSpan(36), 10000);
        // CastTimeMs at offset 40
        BitConverter.TryWriteBytes(record.AsSpan(40), -1);
        // Flags at offset 44
        record[44] = V5Constants.AbilityFlagAvailable | V5Constants.AbilityFlagUsable | V5Constants.AbilityFlagInRange;
        // ResourceCost at offset 45
        record[45] = 5;
        // NameLength + Name at 46
        const string abilityName = "Test Ability";
        BitConverter.TryWriteBytes(record.AsSpan(46), (ushort)abilityName.Length);
        Encoding.UTF8.GetBytes(abilityName).CopyTo(record.AsSpan(48));

        sectionData.AddRange(record);

        int offset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)V5Constants.SectionTypeAbilities);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)sectionData.Count);
        offset += 2;
        sectionData.CopyTo(slot, offset);

        int payloadLength = 4 + sectionData.Count;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskAbilities);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.Single(result.Frame!.Abilities);
        Assert.Equal("test_ability_01", result.Frame.Abilities[0].AbilityId);
        Assert.True(result.Frame.Abilities[0].Available);
        Assert.True(result.Frame.Abilities[0].Usable);
        Assert.Equal(5, result.Frame.Abilities[0].ResourceCost);
        Assert.Equal("Test Ability", result.Frame.Abilities[0].Name);
    }

    [Fact]
    public void Parse_TooManyAbilities_ReturnsCountExceedsMax()
    {
        byte[] slot = CreateMinimalSlot();
        List<byte> sectionData = [];
        sectionData.AddRange(BitConverter.GetBytes((ushort)(V5Constants.MaxAbilities + 1)));

        int offset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)V5Constants.SectionTypeAbilities);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)sectionData.Count);
        offset += 2;
        sectionData.CopyTo(slot, offset);

        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)(4 + sectionData.Count));
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskAbilities);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.AbilityCountExceedsMax, result.Failure);
    }

    [Fact]
    public void Parse_UnknownSectionType_SkippedGracefully()
    {
        byte[] slot = CreateMinimalSlot();

        // Write an unknown section type
        int offset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)0x00FF);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)0);
        offset += 2;

        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), 4u);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionTypeUnknown, result.Failure);
    }

    [Fact]
    public void Parse_MaskBitWithoutSection_ReturnsMaskMismatch()
    {
        // Header claims a PlayerAuras section via mask bit, but no such TLV in payload.
        byte[] slot = CreateMinimalSlot();
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayerAuras);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionsMaskMismatch, result.Failure);
        Assert.Contains("Actual section mask 0x00000000", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AuraSectionWithoutMaskBit_ReturnsMaskMismatch()
    {
        // Payload contains a valid empty PlayerAuras section, but the header mask does not assert it.
        byte[] slot = CreateMinimalSlot();

        byte[] sectionData = [0, 0]; // count = 0
        WriteSection(slot, V5Constants.SectionTypePlayerAuras, sectionData);
        int payloadLength = V5Constants.SectionHeaderSize + sectionData.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        // SectionsMask deliberately left at 0 (no bit for PlayerAuras)

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionsMaskMismatch, result.Failure);
    }

    [Fact]
    public void Parse_DuplicateAuraSection_ReturnsDuplicateSectionType()
    {
        byte[] slot = CreateMinimalSlot();

        // Build two identical (empty) PlayerAuras sections back-to-back.
        byte[] auraSection = [0, 0]; // count = 0
        int offset = V5Constants.PayloadOffset;
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypePlayerAuras, auraSection);
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypePlayerAuras, auraSection);

        int payloadLength = offset - V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayerAuras);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.DuplicateSectionType, result.Failure);
        Assert.Contains("Duplicate known section type", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DuplicateProviderSection_ReturnsDuplicateSectionType()
    {
        byte[] slot = CreateMinimalSlot();

        // Build two identical ProviderInfo sections back-to-back.
        byte[] providerSection = BuildProviderSection(Guid.NewGuid(), 1000u);
        int offset = V5Constants.PayloadOffset;
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypeProviderInfo, providerSection);
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypeProviderInfo, providerSection);

        int payloadLength = offset - V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.DuplicateSectionType, result.Failure);
    }

    [Fact]
    public void Parse_EmptyAuraSection_ReturnsValid()
    {
        // A PlayerAuras section with count=0 is valid and present.
        byte[] slot = CreateMinimalSlot();

        byte[] auraSection = [0, 0]; // count = 0
        WriteSection(slot, V5Constants.SectionTypePlayerAuras, auraSection);
        int payloadLength = V5Constants.SectionHeaderSize + auraSection.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayerAuras);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame);
        Assert.Empty(result.Frame!.PlayerAuras);
    }

    // ── Adversarial: exact-length section rejections ───────────

    [Fact]
    public void Parse_ProviderInfoTooLong_ReturnsSectionPayloadLengthMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        // Build a valid ProviderInfo (28 bytes) then append one extra byte
        byte[] provider = BuildProviderSection(Guid.NewGuid(), 1000u);
        byte[] extra = new byte[provider.Length + 1];
        provider.CopyTo(extra, 0);
        extra[^1] = 0xFF; // trailing undeclared byte

        WriteSection(slot, V5Constants.SectionTypeProviderInfo, extra);
        int payloadLength = V5Constants.SectionHeaderSize + extra.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionPayloadLengthMismatch, result.Failure);
        Assert.Contains("ProviderInfo", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ProviderInfoSchemaMismatch_ReturnsProviderSchemaVersionMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        List<byte> data = [];
        data.AddRange(Guid.NewGuid().ToByteArray());
        data.AddRange(BitConverter.GetBytes(1000u));
        data.AddRange(BitConverter.GetBytes(500u));
        data.AddRange(BitConverter.GetBytes((ushort)0));
        data.Add(99); // wrong schema
        data.Add(0);  // reserved

        WriteSection(slot, V5Constants.SectionTypeProviderInfo, [.. data]);
        int payloadLength = V5Constants.SectionHeaderSize + data.Count;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.ProviderSchemaVersionMismatch, result.Failure);
    }

    [Fact]
    public void Parse_UnitTooLong_ReturnsSectionPayloadLengthMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        byte[] unit = BuildMinimalUnitSection();
        byte[] extra = new byte[unit.Length + 1];
        unit.CopyTo(extra, 0);
        extra[^1] = 0xAA;

        WriteSection(slot, V5Constants.SectionTypePlayer, extra);
        int payloadLength = V5Constants.SectionHeaderSize + extra.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayer);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionPayloadLengthMismatch, result.Failure);
        Assert.Contains("Unit", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AbilitiesTooLong_ReturnsSectionPayloadLengthMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        // count=1 → expected 2+AbilityRecordSize, give one extra byte
        int expected = 2 + V5Constants.AbilityRecordSize;
        byte[] data = new byte[expected + 1];
        BitConverter.TryWriteBytes(data, (ushort)1);
        data[expected] = 0xBB;

        WriteSection(slot, V5Constants.SectionTypeAbilities, data);
        int payloadLength = V5Constants.SectionHeaderSize + data.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskAbilities);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionPayloadLengthMismatch, result.Failure);
        Assert.Contains("Abilities", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AurasTooLong_ReturnsSectionPayloadLengthMismatch()
    {
        byte[] slot = CreateMinimalSlot();
        // count=1 → expected 2+70=72 bytes, give 73
        byte[] data = new byte[73];
        BitConverter.TryWriteBytes(data, (ushort)1);
        data[72] = 0xCC;

        WriteSection(slot, V5Constants.SectionTypePlayerAuras, data);
        int payloadLength = V5Constants.SectionHeaderSize + data.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayerAuras);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionPayloadLengthMismatch, result.Failure);
        Assert.Contains("Auras", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyAuraTrailingByte_ReturnsSectionPayloadLengthMismatch()
    {
        // count=0 aura section with one extra trailing byte: [0, 0, 0xFF] → 3 bytes ≠ expected 2
        byte[] slot = CreateMinimalSlot();
        byte[] data = [0, 0, 0xFF];

        WriteSection(slot, V5Constants.SectionTypePlayerAuras, data);
        int payloadLength = V5Constants.SectionHeaderSize + data.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskPlayerAuras);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionPayloadLengthMismatch, result.Failure);
        Assert.Contains("Auras", result.FailureDetail, StringComparison.Ordinal);
    }

    // ── Adversarial: heartbeat validation ──────────────────────

    [Fact]
    public void Parse_HeartbeatWithGameSection_ReturnsHeartbeatWithExcessSections()
    {
        byte[] slot = CreateMinimalSlot();
        // Heartbeat flag set, but SectionsMask includes Player section (game-state)
        slot[V5Constants.HdrFlagsOffset] = V5Constants.FlagIsHeartbeat;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset),
            V5Constants.MaskProviderInfo | V5Constants.MaskPlayer);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.HeartbeatWithExcessSections, result.Failure);
    }

    [Fact]
    public void Parse_HeartbeatWithZeroSectionsMask_ReturnsHeartbeatWithExcessSections()
    {
        byte[] slot = CreateMinimalSlot();
        slot[V5Constants.HdrFlagsOffset] = V5Constants.FlagIsHeartbeat;
        // SectionsMask is 0, but heartbeat requires exactly ProviderInfo
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), 0u);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.HeartbeatWithExcessSections, result.Failure);
    }

    [Fact]
    public void Parse_ValidHeartbeat_ReturnsValidFrame()
    {
        byte[] slot = CreateMinimalSlot();
        slot[V5Constants.HdrFlagsOffset] = V5Constants.FlagIsHeartbeat;

        byte[] provider = BuildProviderSection(Guid.NewGuid(), 500u);
        WriteSection(slot, V5Constants.SectionTypeProviderInfo, provider);
        int payloadLength = V5Constants.SectionHeaderSize + provider.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame!.Provider);
        Assert.True(result.Frame.Header.IsHeartbeat);
    }

    [Fact]
    public void Parse_ProviderOnlyWithoutHeartbeatFlag_ReturnsValidFrame()
    {
        // A frame with only ProviderInfo and no heartbeat flag is legal.
        byte[] slot = CreateMinimalSlot();
        // Heartbeat flag NOT set (default 0)

        byte[] provider = BuildProviderSection(Guid.NewGuid(), 500u);
        WriteSection(slot, V5Constants.SectionTypeProviderInfo, provider);
        int payloadLength = V5Constants.SectionHeaderSize + provider.Length;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame!.Provider);
        Assert.False(result.Frame.Header.IsHeartbeat);
    }

    // ── Adversarial: section ordering ──────────────────────────

    [Fact]
    public void Parse_SectionsOutOfOrder_ReturnsSectionsOutOfOrder()
    {
        byte[] slot = CreateMinimalSlot();

        // Write Player (mask 0x02) then ProviderInfo (mask 0x01) — descending order
        byte[] unit = BuildMinimalUnitSection();
        byte[] provider = BuildProviderSection(Guid.NewGuid(), 1000u);

        int offset = V5Constants.PayloadOffset;
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypePlayer, unit);
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypeProviderInfo, provider);

        int payloadLength = offset - V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset),
            V5Constants.MaskPlayer | V5Constants.MaskProviderInfo);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.False(result.IsValid);
        Assert.Equal(V5ParseFailure.SectionsOutOfOrder, result.Failure);
        Assert.Contains("out of order", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SectionsAscendingOrder_ReturnsValidFrame()
    {
        byte[] slot = CreateMinimalSlot();

        byte[] provider = BuildProviderSection(Guid.NewGuid(), 1000u);
        byte[] unit = BuildMinimalUnitSection();
        byte[] abilities = BuildEmptyAbilitiesSection();

        int offset = V5Constants.PayloadOffset;
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypeProviderInfo, provider);
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypePlayer, unit);
        offset = WriteSectionAt(slot, offset, V5Constants.SectionTypeAbilities, abilities);

        int payloadLength = offset - V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), (uint)payloadLength);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset),
            V5Constants.MaskProviderInfo | V5Constants.MaskPlayer | V5Constants.MaskAbilities);

        V5ParseResult result = V5Parser.Parse(slot, 0);
        Assert.True(result.IsValid, result.FailureDetail);
        Assert.NotNull(result.Frame!.Provider);
        Assert.NotNull(result.Frame.Player);
        Assert.Single(result.Frame.Abilities);
    }

    // ── Helpers ───────────────────────────────────────────────

    /// <summary>
    /// Write a section header and data into the slot starting at PayloadOffset.
    /// </summary>
    private static void WriteSection(byte[] slot, ushort sectionType, byte[] sectionData)
    {
        WriteSectionAt(slot, V5Constants.PayloadOffset, sectionType, sectionData);
    }

    /// <summary>
    /// Write a section header and data at a specific offset. Returns the next offset.
    /// </summary>
    private static int WriteSectionAt(byte[] slot, int offset, ushort sectionType, byte[] sectionData)
    {
        BitConverter.TryWriteBytes(slot.AsSpan(offset), sectionType);
        offset += 2;
        BitConverter.TryWriteBytes(slot.AsSpan(offset), (ushort)sectionData.Length);
        offset += 2;
        sectionData.CopyTo(slot, offset);
        return offset + sectionData.Length;
    }

    /// <summary>
    /// Build minimal ProviderInfo section data bytes.
    /// </summary>
    private static byte[] BuildProviderSection(Guid sessionId, uint producerFrameMs)
    {
        List<byte> data = [];
        data.AddRange(sessionId.ToByteArray());
        data.AddRange(BitConverter.GetBytes(producerFrameMs));
        data.AddRange(BitConverter.GetBytes(500u));
        data.AddRange(BitConverter.GetBytes((ushort)0));
        data.Add(V5Constants.SchemaVersionCurrent);  // schema
        data.Add(0);  // reserved
        return [.. data];
    }

    /// <summary>
    /// Build a minimal Unit section with all empty strings and sentinel values.
    /// Wire layout: each string field is length-prefixed (2 bytes) + data.
    /// Total: 2+0+2+0+4+2+0+1+1+4+4+4+4+2+0+2+0+2+0+4+4+1 = 43 bytes.
    /// </summary>
    private static byte[] BuildMinimalUnitSection()
    {
        List<byte> data = [];

        // IdLength (0) + Id (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // NameLength (0) + Name (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // Level (0)
        data.AddRange(BitConverter.GetBytes(0));
        // CallingLength (0) + Calling (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // Flags (0)
        data.Add(0);
        // Relation (0)
        data.Add(0);
        // HealthCurrent (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // HealthMaximum (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // ResourceCurrent (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // ResourceMaximum (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // ResourceKindLength (0) + ResourceKind (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // CastAbilityIdLength (0) + CastAbilityId (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // CastNameLength (0) + CastName (0 bytes)
        data.AddRange(BitConverter.GetBytes((ushort)0));
        // CastRemainingMs (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // CastDurationMs (-1 sentinel)
        data.AddRange(BitConverter.GetBytes(V5Constants.NullInt32));
        // CastFlags (0)
        data.Add(0);

        return [.. data];
    }

    /// <summary>
    /// Build an abilities section with count=1 and one fully-populated record.
    /// </summary>
    private static byte[] BuildEmptyAbilitiesSection()
    {
        List<byte> data = [];
        data.AddRange(BitConverter.GetBytes((ushort)1)); // count=1

        byte[] record = new byte[V5Constants.AbilityRecordSize];
        // Empty ability id (space-padded)
        for (int i = 0; i < 32; i++)
            record[i] = 0x20;
        // CastTimeMs = -1 sentinel
        BitConverter.TryWriteBytes(record.AsSpan(32), 0);
        BitConverter.TryWriteBytes(record.AsSpan(36), 0);
        BitConverter.TryWriteBytes(record.AsSpan(40), V5Constants.NullInt32);
        record[44] = 0; // flags
        record[45] = 0; // resource cost
        data.AddRange(record);

        return [.. data];
    }

    private static byte[] CreateMinimalSlot()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        // Set protocol version
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        // Set a sequence number
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), 1u);
        return slot;
    }
}

public sealed class V5HealthMapperTests
{
    [Fact]
    public void ToProviderStatus_NullFrame_Disconnected()
    {
        var result = new StableReadResult(null, ProviderHealth.Disconnected, ContinuityResult.Valid, 0, "no frame");
        ProviderStatus status = V5HealthMapper.ToProviderStatus(result, DateTimeOffset.UtcNow);
        Assert.Equal(ProviderHealth.Disconnected, status.Health);
    }

    [Fact]
    public void ToTelemetryFrame_NullFrame_ReturnsEmpty()
    {
        var result = StableReadResult.Disconnected("test");
        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(result, DateTimeOffset.UtcNow);
        Assert.Equal(ProviderHealth.Disconnected, frame.Provider.Health);
        Assert.Null(frame.Player);
        Assert.Null(frame.Target);
        Assert.Empty(frame.Abilities);
    }

    [Fact]
    public void ToTelemetryFrame_HealthyFrame_MapsCorrectly()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Build a minimal healthy frame from a buffer
        byte[] slot = CreateMinimalSlot();
        V5Crc32.WriteCrc(slot, payloadLength: 0);

        V5ParseResult parseResult = V5Parser.Parse(slot, 0);
        Assert.True(parseResult.IsValid);

        var readerResult = StableReadResult.Healthy(parseResult.Frame!);
        TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(readerResult, now);

        Assert.Equal(ProviderHealth.Healthy, frame.Provider.Health);
        Assert.Equal("5", frame.Provider.ProtocolVersion);
        Assert.Null(frame.Player);
        Assert.Null(frame.Target);
    }

    /// <summary>
    /// Protocol-guaranteed unit flags must map explicit false to false,
    /// not null. A unit with all flags clear must produce IsPlayer=false
    /// and InCombat=false in the domain model.
    /// </summary>
    [Fact]
    public void ToUnitState_ExplicitFalseFlags_MapToFalseNotNull()
    {
        var parsed = CreateParsedUnit(flags: 0, relation: V5Constants.RelationHostile, available: true);
        UnitState? unit = InvokeToUnitState(parsed);
        Assert.NotNull(unit);
        Assert.False(unit!.IsPlayer);
        Assert.False(unit.InCombat);
        Assert.Equal("hostile", unit.Relation);
    }

    /// <summary>
    /// When IsAvailable is false the mapper must return null so residual
    /// wire data cannot make an unavailable unit appear available.
    /// </summary>
    [Fact]
    public void ToUnitState_UnavailableFlag_ReturnsNull()
    {
        // A fully-populated unit that is marked unavailable.
        var parsed = CreateParsedUnit(flags: 0, relation: V5Constants.RelationHostile, available: false);
        UnitState? unit = InvokeToUnitState(parsed);
        Assert.Null(unit);
    }

    /// <summary>
    /// Protocol-guaranteed ability flags must map explicit false to false.
    /// </summary>
    [Fact]
    public void ToAbilityState_ExplicitFalseFlags_MapToFalseNotNull()
    {
        var parsed = new ParsedAbilityState(
            "test_ability", 0, 5000, -1,
            Flags: V5Constants.AbilityFlagAvailable, // only Available set
            ResourceCost: 0,
            Name: "Test Ability");

        AbilityState state = InvokeToAbilityState(parsed);

        Assert.True(state.Available);
        Assert.False(state.Usable);
        Assert.False(state.InRange);
        Assert.False(state.IsChannel);
        Assert.False(state.IsPassive);
        Assert.Equal("Test Ability", state.Name);
        Assert.Equal(0, state.CooldownRemainingMilliseconds);
        Assert.Equal(5000, state.CooldownDurationMilliseconds);
    }

    /// <summary>
    /// Aura null sentinel: when the wire carries a negative RemainingMs
    /// (signalling "permanent" or "unknown"), the mapper must emit null
    /// for RemainingMilliseconds.
    /// </summary>
    [Fact]
    public void ToAuraState_NullRemainingSentinel_MapsToNull()
    {
        var parsed = new ParsedAuraState(
            "buff_stamina", "Stamina", Stacks: 1, Flags: 0,
            RemainingMs: V5Constants.NullInt32);

        AuraState state = InvokeToAuraState(parsed);

        Assert.Equal("buff_stamina", state.Id);
        Assert.Equal(1, state.Stacks);
        Assert.Null(state.RemainingMilliseconds);
        Assert.False(state.IsDebuff);
    }

    /// <summary>
    /// Aura with valid (non-negative) RemainingMs must map to the value.
    /// </summary>
    [Fact]
    public void ToAuraState_ValidRemaining_MapsToValue()
    {
        var parsed = new ParsedAuraState(
            "dot_bleed", "Bleed", Stacks: 3, Flags: V5Constants.AuraFlagIsDebuff,
            RemainingMs: 12000);

        AuraState state = InvokeToAuraState(parsed);

        Assert.Equal(12000, state.RemainingMilliseconds);
        Assert.True(state.IsDebuff);
    }

    // ── Helpers that invoke private mapper methods via explicit construction ──

    /// <summary>
    /// Build a ParsedUnitState with controlled flags for test verification.
    /// </summary>
    private static ParsedUnitState CreateParsedUnit(byte flags, byte relation, bool available)
    {
        byte effectiveFlags = flags;
        if (available)
            effectiveFlags |= V5Constants.UnitFlagIsAvailable;

        return new ParsedUnitState(
            Id: "unit-001",
            Name: "TestUnit",
            Level: 50,
            Calling: "Warrior",
            Flags: effectiveFlags,
            Relation: relation,
            HealthCurrent: 1000,
            HealthMaximum: 1000,
            ResourceCurrent: 100,
            ResourceMaximum: 100,
            ResourceKind: "mana",
            CastAbilityId: null,
            CastName: null,
            CastRemainingMs: V5Constants.NullInt32,
            CastDurationMs: V5Constants.NullInt32,
            CastFlags: 0);
    }

    /// <summary>
    /// Reach the private ToUnitState mapper via a full TelemetryFrame round-trip.
    /// We build a frame with a player section so the mapper exercises the unit path.
    /// </summary>
    private static UnitState? InvokeToUnitState(ParsedUnitState parsed)
    {
        var frame = new ParsedV5Frame(
            new V5BufferHeader { Sequence = 1, ProtocolVersion = 5 },
            new ParsedProviderInfo(Guid.NewGuid(), 0, 500, "test", 1),
            Player: parsed,
            Target: null,
            Abilities: [],
            PlayerAuras: [],
            TargetAuras: [],
            BufferIndex: 0);

        var result = StableReadResult.Healthy(frame);
        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, DateTimeOffset.UtcNow);
        return telemetry.Player;
    }

    /// <summary>
    /// Reach the private ToAbilityState mapper via TelemetryFrame round-trip.
    /// </summary>
    private static AbilityState InvokeToAbilityState(ParsedAbilityState parsed)
    {
        var frame = new ParsedV5Frame(
            new V5BufferHeader { Sequence = 1, ProtocolVersion = 5 },
            new ParsedProviderInfo(Guid.NewGuid(), 0, 500, "test", 1),
            Player: null,
            Target: null,
            Abilities: [parsed],
            PlayerAuras: [],
            TargetAuras: [],
            BufferIndex: 0);

        var result = StableReadResult.Healthy(frame);
        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, DateTimeOffset.UtcNow);
        Assert.True(telemetry.Abilities.TryGetValue(parsed.AbilityId, out AbilityState? state));
        return state!;
    }

    /// <summary>
    /// Reach the private ToAuraState mapper via TelemetryFrame round-trip.
    /// </summary>
    private static AuraState InvokeToAuraState(ParsedAuraState parsed)
    {
        var frame = new ParsedV5Frame(
            new V5BufferHeader { Sequence = 1, ProtocolVersion = 5 },
            new ParsedProviderInfo(Guid.NewGuid(), 0, 500, "test", 1),
            Player: null,
            Target: null,
            Abilities: [],
            PlayerAuras: [parsed],
            TargetAuras: [],
            BufferIndex: 0);

        var result = StableReadResult.Healthy(frame);
        TelemetryFrame telemetry = V5HealthMapper.ToTelemetryFrame(result, DateTimeOffset.UtcNow);
        Assert.Single(telemetry.PlayerAuras);
        return telemetry.PlayerAuras[0];
    }

    private static byte[] CreateMinimalSlot()
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), 1u);
        return slot;
    }
}

public sealed class SessionTrackerTests
{
    [Fact]
    public void FirstFrame_ReturnsValid()
    {
        var tracker = new SessionTracker();
        ParsedV5Frame frame = CreateFrameWithProvider(Guid.NewGuid(), 1);
        ContinuityResult result = tracker.Evaluate(frame, out _);
        Assert.Equal(ContinuityResult.Valid, result);
    }

    [Fact]
    public void NormalIncrement_ReturnsValid()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();
        tracker.Evaluate(CreateFrameWithProvider(sessionId, 1), out _);
        ContinuityResult result = tracker.Evaluate(CreateFrameWithProvider(sessionId, 2), out _);
        Assert.Equal(ContinuityResult.Valid, result);
    }

    [Fact]
    public void GapDetected_ReturnsGap()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();
        tracker.Evaluate(CreateFrameWithProvider(sessionId, 1), out _);
        ContinuityResult result = tracker.Evaluate(CreateFrameWithProvider(sessionId, 5), out uint gap);
        Assert.Equal(ContinuityResult.Gap, result);
        Assert.Equal(3u, gap);
    }

    [Fact]
    public void SessionRestart_ReturnsRestart()
    {
        var tracker = new SessionTracker();
        tracker.Evaluate(CreateFrameWithProvider(Guid.NewGuid(), 1), out _);
        ContinuityResult result = tracker.Evaluate(CreateFrameWithProvider(Guid.NewGuid(), 1), out _);
        Assert.Equal(ContinuityResult.SessionRestart, result);
    }

    [Fact]
    public void SequenceDecrement_ReturnsDecrement()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();
        tracker.Evaluate(CreateFrameWithProvider(sessionId, 5), out _);
        ContinuityResult result = tracker.Evaluate(CreateFrameWithProvider(sessionId, 3), out _);
        Assert.Equal(ContinuityResult.SequenceDecrement, result);
    }

    private static ParsedV5Frame CreateFrameWithProvider(Guid sessionId, uint sequence)
    {
        var header = new V5BufferHeader
        {
            Sequence = sequence,
            Flags = 0,
            ProtocolVersion = V5Constants.ProtocolVersion,
        };

        var provider = new ParsedProviderInfo(sessionId, 0, 500, "test", 1);

        return new ParsedV5Frame(header, provider, null, null, [], [], [], 0);
    }
}

public sealed class SessionTrackerWrapTests
{
    [Fact]
    public void SequenceDecrement_DoesNotLowerHighWaterMark()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        // Establish baseline at seq 100
        tracker.Evaluate(CreateFrame(sessionId, 100), out _);

        // Sequence goes backwards: 100 → 80
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 80), out _);

        Assert.Equal(ContinuityResult.SequenceDecrement, result);
        // High-water mark must NOT be lowered
        Assert.Equal(100u, tracker.TrustedHighWaterMark);
        // Last sequence must NOT be lowered to 80
        Assert.Equal(100u, tracker.LastSequence);
        Assert.True(tracker.IsDegraded);
    }

    [Fact]
    public void StickyDecrement_SubsequentLowFrame_StillFaulted()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, 100), out _);
        tracker.Evaluate(CreateFrame(sessionId, 80), out _); // decrement, HWM stays 100

        // Frame at 90 (below HWM 100) → still faulted
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 90), out _);

        Assert.Equal(ContinuityResult.SequenceDecrement, result);
        Assert.True(tracker.IsDegraded);
    }

    [Fact]
    public void StickyDecrement_RecoversAfterHighWaterMarkExceeded()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, 100), out _);
        tracker.Evaluate(CreateFrame(sessionId, 80), out _); // decrement, HWM stays 100

        // Frame at 101 (above HWM 100) → recovered
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 101), out _);

        Assert.Equal(ContinuityResult.Valid, result);
        Assert.False(tracker.IsDegraded);
        Assert.Equal(101u, tracker.LastSequence);
        Assert.Equal(101u, tracker.TrustedHighWaterMark);
    }

    [Fact]
    public void ValidSequenceWrap_NoGap_ReturnsSequenceWrap()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, uint.MaxValue), out _);

        // Wrap from uint.MaxValue to 0 (consecutive through wrap boundary)
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 0), out uint gapSize);

        Assert.Equal(ContinuityResult.SequenceWrap, result);
        Assert.Equal(0u, gapSize); // no frames lost
        Assert.False(tracker.IsDegraded);
        Assert.Equal(0u, tracker.LastSequence);
    }

    [Fact]
    public void SequenceWrap_WithGap_ReturnsSequenceWrapWithGapSize()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, uint.MaxValue - 2), out _);

        // Wrap: uint.MaxValue-2 → 3 (missing uint.MaxValue-1, uint.MaxValue, 0, 1, 2 = 5 frames)
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 3), out uint gapSize);

        Assert.Equal(ContinuityResult.SequenceWrap, result);
        // Gap: (uint.MaxValue - (uint.MaxValue-2)) + 3 = 2 + 3 = 5
        Assert.Equal(5u, gapSize);
    }

    [Fact]
    public void SequenceWrap_FromNearMaxToNearZero_HandlesCorrectly()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, uint.MaxValue - 1), out _);

        // Wrap: uint.MaxValue-1 → 1 (missing uint.MaxValue, 0 = 2 frames)
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 1), out uint gapSize);

        Assert.Equal(ContinuityResult.SequenceWrap, result);
        Assert.Equal(2u, gapSize);
    }

    [Fact]
    public void AmbiguousHalfRangeOrdering_FailsClosed()
    {
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        // Establish baseline
        tracker.Evaluate(CreateFrame(sessionId, 0), out _);

        // Exactly 0x80000000 ahead → ambiguous
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, 0x80000000), out _);

        Assert.Equal(ContinuityResult.SequenceDecrement, result);
        Assert.True(tracker.IsDegraded);
    }

    [Fact]
    public void SessionRestart_ClearsDegradedState()
    {
        var tracker = new SessionTracker();
        Guid sessionA = Guid.NewGuid();
        Guid sessionB = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionA, 100), out _);
        tracker.Evaluate(CreateFrame(sessionA, 80), out _); // degraded

        Assert.True(tracker.IsDegraded);

        // New session restarts
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionB, 1), out _);

        Assert.Equal(ContinuityResult.SessionRestart, result);
        Assert.False(tracker.IsDegraded);
        Assert.Equal(1u, tracker.LastSequence);
        Assert.Equal(1u, tracker.TrustedHighWaterMark);
    }

    [Fact]
    public void WrapTransition_AfterDegradedState_RecoversIfSequenceAdvances()
    {
        // Degraded after decrement, then a wrap that moves the sequence
        // clearly past the high-water mark should recover.
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        tracker.Evaluate(CreateFrame(sessionId, 100), out _);
        tracker.Evaluate(CreateFrame(sessionId, 50), out _); // decrement, HWM=100

        Assert.True(tracker.IsDegraded);

        // Now a frame past the HWM (101 > 100) → recovery
        ContinuityResult recovered = tracker.Evaluate(CreateFrame(sessionId, 101), out _);

        Assert.Equal(ContinuityResult.Valid, recovered);
        Assert.False(tracker.IsDegraded);
        Assert.Equal(101u, tracker.TrustedHighWaterMark);
    }

    [Fact]
    public void DuplicateSequence_AtUintMax_ReturnsValidNotGap()
    {
        // Regression: an exact duplicate at uint.MaxValue must remain Valid
        // so freshness can age it. The gap arithmetic lastSeq+1 wraps to 0,
        // which would otherwise produce a spurious huge gap.
        var tracker = new SessionTracker();
        Guid sessionId = Guid.NewGuid();

        // Establish baseline at uint.MaxValue
        tracker.Evaluate(CreateFrame(sessionId, uint.MaxValue), out _);

        // Send the same sequence again (duplicate)
        ContinuityResult result = tracker.Evaluate(CreateFrame(sessionId, uint.MaxValue), out uint gapSize);

        Assert.Equal(ContinuityResult.Valid, result);
        Assert.Equal(0u, gapSize);
        Assert.False(tracker.IsDegraded);
        // LastSequence and TrustedHighWaterMark stay at MaxValue (unchanged)
        Assert.Equal(uint.MaxValue, tracker.LastSequence);
        Assert.Equal(uint.MaxValue, tracker.TrustedHighWaterMark);
    }

    private static ParsedV5Frame CreateFrame(Guid sessionId, uint sequence)
    {
        var header = new V5BufferHeader
        {
            Sequence = sequence,
            Flags = 0,
            ProtocolVersion = V5Constants.ProtocolVersion,
        };

        var provider = new ParsedProviderInfo(sessionId, 0, 500, "test", 1);

        return new ParsedV5Frame(header, provider, null, null, [], [], [], 0);
    }
}

public sealed class StableReaderTests
{
    private static readonly Guid TestSessionId = Guid.Parse("e284b238-1948-4666-a575-df38486e659f");

    [Fact]
    public void Read_BothEmptyBuffers_ReturnsFaulted()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        StableReadResult result = reader.Read(
            s => { },
            s => { },
            DateTimeOffset.UtcNow);
        Assert.False(result.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, result.TransportHealth);
    }

    [Fact]
    public void Read_SecondBufferValidNewer_SelectsB()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] bufferA = new byte[V5Constants.BufferSlotSize];
        byte[] bufferB = new byte[V5Constants.BufferSlotSize];

        FillSlot(bufferA, sequence: 1);
        FillSlot(bufferB, sequence: 2);

        StableReadResult result = reader.Read(
            s => bufferA.CopyTo(s),
            s => bufferB.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable);
        Assert.Equal(1, result.Frame!.BufferIndex); // buffer B
    }

    [Fact]
    public void Read_FirstBufferNewer_SelectsA()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] bufferA = new byte[V5Constants.BufferSlotSize];
        byte[] bufferB = new byte[V5Constants.BufferSlotSize];

        FillSlot(bufferA, sequence: 3);
        FillSlot(bufferB, sequence: 2);

        StableReadResult result = reader.Read(
            s => bufferA.CopyTo(s),
            s => bufferB.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable);
        Assert.Equal(0, result.Frame!.BufferIndex); // buffer A
    }

    [Fact]
    public void Read_OneBadCRC_UsesOther()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] bufferA = new byte[V5Constants.BufferSlotSize];
        byte[] bufferB = new byte[V5Constants.BufferSlotSize];

        FillSlot(bufferA, sequence: 1);
        // Leave bufferB all zeros (invalid CRC)

        StableReadResult result = reader.Read(
            s => bufferA.CopyTo(s),
            s => bufferB.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable);
        Assert.Equal(0, result.Frame!.BufferIndex);
    }

    [Fact]
    public void Read_ThrowOnRead_ReturnsDisconnected()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        StableReadResult result = reader.Read(
            s => throw new InvalidOperationException("fail"),
            s => throw new InvalidOperationException("fail"),
            DateTimeOffset.UtcNow);
        Assert.Equal(ProviderHealth.Disconnected, result.TransportHealth);
    }

    [Fact]
    public void Read_SequenceRegression_ReturnsFaulted()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] buf1 = new byte[V5Constants.BufferSlotSize];
        byte[] buf2 = new byte[V5Constants.BufferSlotSize];

        // Read with sequence 5 (buf1 valid, buf2 all zeros/invalid)
        FillSlot(buf1, sequence: 5);
        StableReadResult r1 = reader.Read(
            s => buf1.CopyTo(s),
            s => buf2.CopyTo(s),
            DateTimeOffset.UtcNow);
        Assert.True(r1.IsUsable, $"First read should be usable: {r1.TransportHealth}");

        // Second read: only buf2 has valid data (seq 3), buf1 has empty (CRC fails).
        // Since 3 < 5, the reader detects regression.
        Array.Clear(buf1);
        FillSlot(buf2, sequence: 3);

        StableReadResult result = reader.Read(
            s => buf2.CopyTo(s),
            s => buf1.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.Equal(ProviderHealth.Faulted, result.TransportHealth);
    }

    [Fact]
    public void Read_UnchangedSequencePastMaximumAge_ReturnsStale()
    {
        var timeProvider = new ControllableTimeProvider();
        var reader = new StableReader(TimeSpan.FromMilliseconds(100), timeProvider);
        byte[] buffer = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        FillSlot(buffer, sequence: 1);

        StableReadResult first = reader.Read(s => buffer.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.True(first.IsUsable);

        timeProvider.Advance(TimeSpan.FromMilliseconds(101));
        StableReadResult stale = reader.Read(s => buffer.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow);

        Assert.Equal(ProviderHealth.Stale, stale.TransportHealth);
        Assert.Equal(TimeSpan.FromMilliseconds(101), stale.Age);
    }

    [Fact]
    public void Read_NewSessionMayRestartSequence()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] firstBuffer = new byte[V5Constants.BufferSlotSize];
        byte[] secondBuffer = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        FillSlot(firstBuffer, sequence: 50, TestSessionId);
        FillSlot(secondBuffer, sequence: 1, Guid.NewGuid());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        StableReadResult first = reader.Read(s => firstBuffer.CopyTo(s), s => empty.CopyTo(s), now);
        StableReadResult restarted = reader.Read(s => secondBuffer.CopyTo(s), s => empty.CopyTo(s), now.AddMilliseconds(10));

        Assert.True(first.IsUsable);
        Assert.True(restarted.IsUsable);
        Assert.Equal(ContinuityResult.SessionRestart, restarted.Continuity);
    }

    [Fact]
    public void Read_DoubleBufferSessionRestart_SelectsClearlyNewerProducerFrame()
    {
        Guid oldSession = TestSessionId;
        Guid newSession = Guid.Parse("F3C1A98B-7D42-4E1F-B6C0-2A8D5E9F0317");

        byte[] bufferOld = new byte[V5Constants.BufferSlotSize];
        byte[] bufferNew = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];

        FillSlot(bufferOld, sequence: 50, oldSession, producerFrameMs: 1_000);
        FillSlot(bufferNew, sequence: 1, newSession, producerFrameMs: 1_010);

        var reader = new StableReader(TimeSpan.FromSeconds(5));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Establish tracker on old session with seq 49.
        byte[] priorBuffer = new byte[V5Constants.BufferSlotSize];
        FillSlot(priorBuffer, sequence: 49, oldSession, producerFrameMs: 990);
        StableReadResult prior = reader.Read(
            s => priorBuffer.CopyTo(s), s => empty.CopyTo(s), now);
        Assert.True(prior.IsUsable, $"Prior read should be usable: {prior.TransportHealth}");

        StableReadResult result = reader.Read(
            s => bufferOld.CopyTo(s),
            s => bufferNew.CopyTo(s),
            now.AddMilliseconds(10));

        Assert.True(result.IsUsable,
            $"Result should be usable; got {result.TransportHealth} – {result.FailureDetail}");
        Assert.Equal(newSession.ToString("D"),
            result.Frame!.Provider!.SessionId.ToString("D"));
        Assert.Equal(1u, result.Frame.Header.Sequence);
        Assert.Equal(ContinuityResult.SessionRestart, result.Continuity);
    }

    [Fact]
    public void Read_DifferentSessionsWithAmbiguousProducerFrames_FailsClosed()
    {
        byte[] bufferA = new byte[V5Constants.BufferSlotSize];
        byte[] bufferB = new byte[V5Constants.BufferSlotSize];
        FillSlot(bufferA, sequence: 50, TestSessionId, producerFrameMs: 0);
        FillSlot(bufferB, sequence: 1, Guid.NewGuid(), producerFrameMs: 0);

        var reader = new StableReader(TimeSpan.FromSeconds(5));
        StableReadResult result = reader.Read(
            s => bufferA.CopyTo(s),
            s => bufferB.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.Equal(ProviderHealth.Faulted, result.TransportHealth);
        Assert.Null(result.Frame);
        Assert.Contains("Ambiguous sessions", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ForeignSessionWithOlderProducerFrame_IsNotSelected()
    {
        byte[] current = new byte[V5Constants.BufferSlotSize];
        byte[] foreign = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        FillSlot(current, sequence: 49, TestSessionId, producerFrameMs: 1_000);

        var reader = new StableReader(TimeSpan.FromSeconds(5));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(reader.Read(s => current.CopyTo(s), s => empty.CopyTo(s), now).IsUsable);

        FillSlot(current, sequence: 50, TestSessionId, producerFrameMs: 1_020);
        FillSlot(foreign, sequence: 1, Guid.NewGuid(), producerFrameMs: 1_010);
        StableReadResult result = reader.Read(
            s => current.CopyTo(s),
            s => foreign.CopyTo(s),
            now.AddMilliseconds(10));

        Assert.True(result.IsUsable);
        Assert.Equal(TestSessionId, result.Frame!.Provider!.SessionId);
        Assert.Equal(50u, result.Frame.Header.Sequence);
        Assert.Equal(ContinuityResult.Valid, result.Continuity);
    }

    [Fact]
    public void Read_DifferentSessions_ProducerFrameWrapSelectsNewerFrame()
    {
        Guid newSession = Guid.NewGuid();
        byte[] beforeWrap = new byte[V5Constants.BufferSlotSize];
        byte[] afterWrap = new byte[V5Constants.BufferSlotSize];
        FillSlot(beforeWrap, sequence: 50, TestSessionId, producerFrameMs: uint.MaxValue - 5);
        FillSlot(afterWrap, sequence: 1, newSession, producerFrameMs: 3);

        StableReadResult result = new StableReader(TimeSpan.FromSeconds(5)).Read(
            s => beforeWrap.CopyTo(s),
            s => afterWrap.CopyTo(s),
            DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable);
        Assert.Equal(newSession, result.Frame!.Provider!.SessionId);
        Assert.Equal(3u, result.Frame.Header.ProducerFrameMs);
    }

    // ── Freshness / TimeProvider tests ─────────────────────────

    [Fact]
    public void Read_WallClockRollback_DoesNotRefreshAge()
    {
        // wallClockNow can jump backward (NTP adjustment), but the monotonic
        // TimeProvider must never allow age to decrease.
        var timeProvider = new ControllableTimeProvider();
        var reader = new StableReader(TimeSpan.FromSeconds(5), timeProvider);
        byte[] buffer = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        FillSlot(buffer, sequence: 1);

        // Advance time and read
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        StableReadResult first = reader.Read(
            s => buffer.CopyTo(s), s => empty.CopyTo(s),
            DateTimeOffset.UtcNow);
        Assert.True(first.IsUsable);
        Assert.True(first.Age < TimeSpan.FromMilliseconds(10), $"Age should be ~0 on first read: {first.Age}");

        // Read again with no time advance but wallClockNow rolled back
        StableReadResult second = reader.Read(
            s => buffer.CopyTo(s), s => empty.CopyTo(s),
            DateTimeOffset.UtcNow.AddHours(-1));

        // Age should be monotonic (>= previous age), NOT negative or zeroed
        Assert.True(second.Age >= first.Age,
            $"Age should be monotonic despite wall clock rollback: first={first.Age}, second={second.Age}");
    }

    [Fact]
    public void Read_SequenceAdvance_ResetsFreshnessTimer()
    {
        var timeProvider = new ControllableTimeProvider();
        var reader = new StableReader(TimeSpan.FromSeconds(5), timeProvider);
        byte[] buf1 = new byte[V5Constants.BufferSlotSize];
        byte[] buf2 = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        FillSlot(buf1, sequence: 1);
        FillSlot(buf2, sequence: 2);

        // First read at seq 1
        reader.Read(s => buf1.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow);
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        // Second read at seq 2 → freshness timer resets
        StableReadResult result = reader.Read(
            s => buf2.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable);
        Assert.True(result.Age < TimeSpan.FromMilliseconds(10),
            $"Age should reset on new sequence: {result.Age.TotalMilliseconds}ms");
    }

    [Fact]
    public void Read_StickyDecrement_SubsequentLowFrameStillFaulted()
    {
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] buf1 = new byte[V5Constants.BufferSlotSize];
        byte[] buf2 = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];

        // Establish baseline at seq 100
        FillSlot(buf1, sequence: 100);
        StableReadResult establish = reader.Read(
            s => buf1.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.True(establish.IsUsable);

        // Sequence decrement: 100 → 80 → faulted, high-water mark stays at 100
        Array.Clear(buf1);
        FillSlot(buf2, sequence: 80);
        StableReadResult decrement = reader.Read(
            s => buf2.CopyTo(s), s => buf1.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.Equal(ProviderHealth.Faulted, decrement.TransportHealth);
        Assert.Equal(ContinuityResult.SequenceDecrement, decrement.Continuity);

        // Subsequent frame at 90 (still below high-water mark 100) → still faulted
        Array.Clear(buf2);
        FillSlot(buf1, sequence: 90);
        StableReadResult stillFaulted = reader.Read(
            s => buf1.CopyTo(s), s => buf2.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.Equal(ProviderHealth.Faulted, stillFaulted.TransportHealth);
        Assert.Equal(ContinuityResult.SequenceDecrement, stillFaulted.Continuity);

        // Frame at 101 (above high-water mark) → recovered
        Array.Clear(buf1);
        FillSlot(buf2, sequence: 101);
        StableReadResult recovered = reader.Read(
            s => buf2.CopyTo(s), s => buf1.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.True(recovered.IsUsable,
            $"Should recover above HWM; got {recovered.TransportHealth} – {recovered.FailureDetail}");
        Assert.Equal(ContinuityResult.Valid, recovered.Continuity);
    }

    [Fact]
    public void Read_SameSessionBufferSelection_UsesWrapAwareOrdering()
    {
        // Two buffers captured simultaneously within same session, one near
        // uint.MaxValue, the other near 0 after wrap. The reader must select
        // the frame that comes AFTER in wrap-aware order.
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] bufA = new byte[V5Constants.BufferSlotSize];
        byte[] bufB = new byte[V5Constants.BufferSlotSize];

        // Buffer A at uint.MaxValue - 1, Buffer B at 0 (wrapped forward)
        FillSlot(bufA, sequence: uint.MaxValue - 1, TestSessionId, producerFrameMs: 1000);
        FillSlot(bufB, sequence: 0, TestSessionId, producerFrameMs: 1010);

        StableReadResult result = reader.Read(
            s => bufA.CopyTo(s), s => bufB.CopyTo(s), DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable,
            $"Should be usable: {result.TransportHealth} – {result.FailureDetail}");
        // Buffer B (seq 0) should be selected because it's after seq uint.MaxValue-1 in wrap-aware ordering
        Assert.Equal(1, result.Frame!.BufferIndex);
        Assert.Equal(0u, result.Frame.Header.Sequence);
    }

    [Fact]
    public void Read_AmbiguousSameSessionBufferOrder_FailsClosed()
    {
        // When two buffers differ by exactly 0x80000000 (half the range),
        // ordering is ambiguous and the reader must fail closed.
        var reader = new StableReader(TimeSpan.FromSeconds(5));
        byte[] bufA = new byte[V5Constants.BufferSlotSize];
        byte[] bufB = new byte[V5Constants.BufferSlotSize];

        FillSlot(bufA, sequence: 0, TestSessionId);
        FillSlot(bufB, sequence: 0x80000000, TestSessionId);

        StableReadResult result = reader.Read(
            s => bufA.CopyTo(s), s => bufB.CopyTo(s), DateTimeOffset.UtcNow);

        Assert.Equal(ProviderHealth.Faulted, result.TransportHealth);
        Assert.Contains("Ambiguous same-session", result.FailureDetail, StringComparison.Ordinal);
    }

    private static void FillSlot(
        byte[] slot,
        uint sequence,
        Guid? sessionId = null,
        uint producerFrameMs = 0)
    {
        Array.Clear(slot);
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), producerFrameMs);

        byte[] provider = new byte[28];
        (sessionId ?? TestSessionId).TryWriteBytes(provider);
        BitConverter.TryWriteBytes(provider.AsSpan(16), producerFrameMs);
        BitConverter.TryWriteBytes(provider.AsSpan(20), 500u);
        provider[26] = V5Constants.SchemaVersionCurrent;

        int payloadOffset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset), (ushort)V5Constants.SectionTypeProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset + 2), (ushort)provider.Length);
        provider.CopyTo(slot, payloadOffset + V5Constants.SectionHeaderSize);
        uint payloadLength = (uint)(provider.Length + V5Constants.SectionHeaderSize);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), payloadLength);
        V5Crc32.WriteCrc(slot, payloadLength);
    }
}

/// <summary>
/// A controllable TimeProvider for testing freshness and monotonic clock behavior.
/// GetTimestamp returns raw ticks, and TimestampFrequency is set to TicksPerSecond
/// so the default GetElapsedTime implementation produces correct TimeSpan values.
/// </summary>
internal sealed class ControllableTimeProvider : TimeProvider
{
    private long _ticks;

    public ControllableTimeProvider(long startTicks = 0) => _ticks = startTicks;

    public override long GetTimestamp() => _ticks;

    /// <summary>
    /// Set to TimeSpan.TicksPerSecond so that timestamp deltas map 1:1 to TimeSpan ticks.
    /// </summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta) => _ticks += delta.Ticks;

    public void SetTicks(long ticks) => _ticks = ticks;
}
