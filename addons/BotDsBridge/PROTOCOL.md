# BotDs V5 Wire Protocol Specification v1.0

**Status**: authoritative. This document defines the single wire contract shared by the C# Reader and the Lua addon emitter. Every byte offset, size, sentinel, flag, section mask, and CRC rule specified here is normative.

---

## 1. Memory Region

The protocol occupies a contiguous memory region of **16400 bytes** allocated by the Lua addon as a backing string (`string.rep("\0", 16400)`). The external Reader locates this region by scanning process memory for the sentinel magic.

```
Offset   Size    Field
------   ----    -----
0        8       Sentinel magic: ASCII "BotDsV05"
8        4       TotalSize: uint32 LE = 16400
12       4       BufferSlotSize: uint32 LE = 8192

16       8192    Buffer A
8208     8192    Buffer B
```

## 2. Sentinel

The sentinel at offset 0 serves as the scan target for the external Reader. Its magic must be unique enough to avoid false positives in random process memory.

| Offset | Size | Type | Value |
|--------|------|------|-------|
| 0 | 8 | ASCII | `BotDsV05` (0x3530567344746F42 LE) |
| 8 | 4 | uint32 LE | 16400 (total region size for bounds checking) |
| 12 | 4 | uint32 LE | 8192 (single buffer slot size) |

The writer never modifies the sentinel after initial allocation. The sentinel's fixed position and magic provide a stable reference point for the Reader to locate the double buffer.

## 3. Buffer Slot Layout

Each 8192-byte buffer slot is independent. The writer writes to exactly one slot per frame, strictly alternating between A (offset 16) and B (offset 8208). After writing payload, the writer MUST update the CRC field last so that a torn read yields a CRC mismatch.

```
Offset   Size  Field
------   ----  -----
0        4     Sequence (uint32 LE) — monotonic per session, starts at 1
4        4     ProducerFrameMs (uint32 LE) — Inspect.Time.Frame() value
8        4     SectionsMask (uint32 LE) — bitmask of populated sections
12       4     HeartbeatIntervalMs (uint32 LE) — emitter's configured heartbeat interval
16       4     PayloadLength (uint32 LE) — bytes of section data following header
20       1     ProtocolVersion (uint8) — MUST be 5
21       1     Flags (uint8) — see §3.1
22       2     Reserved (uint16 LE) — MUST be 0
24       4     CRC32 (uint32 LE) — see §5
28       N     Payload — section data, N = PayloadLength
```

**Maximum PayloadLength**: 8192 − 28 = 8164 bytes.

### 3.1 Flags

| Bit | Name | Meaning when set |
|-----|------|-----------------|
| 0 | IsHeartbeat | This frame contains only ProviderInfo and no game-state sections; the reader should use it for freshness only |
| 1 | IsSecure | RIFT secure mode is active (Inspected via System.Secure) |
| 2–7 | Reserved | MUST be 0 |

### 3.2 SectionsMask

Each bit corresponds to a section type present in the payload:

| Bit | Mask | Section |
|-----|------|---------|
| 0 | 0x00000001 | ProviderInfo |
| 1 | 0x00000002 | Player unit state |
| 2 | 0x00000004 | Target unit state |
| 3 | 0x00000008 | Abilities list |
| 4 | 0x00000010 | Player auras list |
| 5 | 0x00000020 | Target auras list |
| 6–31 | — | Reserved, MUST be 0 |

Sections appear in the payload in ascending mask order (ProviderInfo first, then Player, Target, Abilities, PlayerAuras, TargetAuras). The ascending order follows bits 0 through 5. The SectionsMask describes which are present; a section MAY be omitted even if its mask bit is 0 and the reader MUST NOT assume presence from mask alone.

## 4. Section Encoding

Every section in the payload uses type-length-value (TLV) encoding:

```
Offset  Size  Field
------  ----  -----
0       2     SectionType (uint16 LE)
2       2     SectionLength (uint16 LE) — bytes of SectionData, excluding type+length header
4       N     SectionData (N = SectionLength, max 65535 but constrained by buffer)
```

### 4.1 ProviderInfo (0x0001)

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 0 | 16 | bytes | SessionId (UUID/GUID as raw 16 bytes) |
| 16 | 4 | uint32 LE | ProducerFrameMs (monotonic frame milliseconds, e.g. Inspect.Time.Frame(); NOT epoch) |
| 20 | 4 | uint32 LE | MaxTelemetryAgeMs (maximum acceptable unchanged-sequence age) |
| 24 | 2 | uint16 LE | ClientVersionLength |
| 26 | N | ASCII | ClientVersion (Inspect.System.Version external string, NOT null-terminated) |
| — | 1 | uint8 | SchemaVersion (protocol schema version, currently 1) |
| — | 1 | uint8 | Reserved (MUST be 0) |

Minimum ProviderInfo data length (with empty client version): 28 bytes.
Maximum client version string length: 128 bytes.

### 4.2 Unit State (0x0002 Player, 0x0003 Target)

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 4 | 2 | uint16 LE | IdLength | — |
| 6 | N | ASCII | Id (unit specifier or numeric ID string) | 0 length |
| — | 2 | uint16 LE | NameLength | — |
| — | N | UTF-8 | Name | 0 length |
| — | 4 | int32 LE | Level | −1 |
| — | 2 | uint16 LE | CallingLength | — |
| — | N | ASCII | Calling | 0 length |
| — | 1 | uint8 | Flags (see §4.2.1) | — |
| — | 1 | uint8 | Relation (0=unknown, 1=hostile, 2=friendly, 3=neutral) | 0 |
| — | 4 | int32 LE | HealthCurrent | −1 |
| — | 4 | int32 LE | HealthMaximum | −1 |
| — | 4 | int32 LE | ResourceCurrent (primary resource: mana/energy/power) | −1 |
| — | 4 | int32 LE | ResourceMaximum | −1 |
| — | 2 | uint16 LE | ResourceKindLength | — |
| — | N | ASCII | ResourceKind ("mana", "energy", "power", "charge", "") | 0 length |
| — | 2 | uint16 LE | CastAbilityIdLength | — |
| — | N | ASCII | CastAbilityId | 0 length |
| — | 2 | uint16 LE | CastNameLength | — |
| — | N | UTF-8 | CastName | 0 length |
| — | 4 | int32 LE | CastRemainingMs | −1 |
| — | 4 | int32 LE | CastDurationMs | −1 |
| — | 1 | uint8 | CastFlags (bit0=isChannel, bit1=isUninterruptible) | 0 |

Maximum string lengths: Id 64, Name 32, Calling 16, ResourceKind 16, CastAbilityId 32, CastName 32.

#### 4.2.1 Unit Flags

| Bit | Name |
|-----|------|
| 0 | IsPlayer (1 = player character, 0 = NPC) |
| 1 | InCombat |
| 2 | IsAvailable (unit detail was successfully retrieved) |
| 3–7 | Reserved |

### 4.3 Abilities List (0x0004)

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 4 | 2 | uint16 LE | Count (number of ability records, 0–128) |
| 6 | N×46 | — | Ability records (each 46 bytes fixed, see below) |

**Ability Record** (46 bytes, fixed-length for scanning efficiency):

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 0 | 32 | ASCII | AbilityId (space-padded) | — |
| 32 | 4 | int32 LE | CooldownRemainingMs | −1 |
| 36 | 4 | int32 LE | CooldownDurationMs | −1 |
| 40 | 4 | int32 LE | CastTimeMs | −1 |
| 44 | 1 | uint8 | Flags (bit0=available, bit1=usable, bit2=inRange, bit3=passive, bit4=channeled) | — |
| 45 | 1 | uint8 | ResourceCost (primary resource cost, 0–255; for exact costs use ability detail) | — |

Fixed-length records allow the parser to compute offsets without per-record length scanning. String fields use ASCII space-padding to fill their 32-byte allocation. A zero-length ability ID (first byte '\0') indicates an empty/unused record slot.

### 4.4 Auras List (0x0005 Player, 0x0006 Target)

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 4 | 2 | uint16 LE | Count (number of aura records, 0–64) |
| 6 | N×70 | — | Aura records (each 70 bytes fixed) |

**Aura Record** (70 bytes):

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 0 | 32 | ASCII | AuraId (space-padded) | first byte '\0' |
| 32 | 2 | uint16 LE | NameLength | — |
| 34 | 32 | UTF-8 | Name (null-padded) | — |
| 66 | 1 | uint8 | Stacks (0 if unknown) | — |
| 67 | 1 | uint8 | Flags (bit0=isDebuff, bit1=isCurse, bit2=isDisease, bit3=isPoison) | — |
| 68 | 2 | int16 LE | RemainingMsLow (lower 16 bits of remaining ms; −1 if unknown) | −1 |

`RemainingMsLow` stores the lower 16 bits of the aura remaining time in milliseconds. Total remaining = `RemainingMsLow` (if ≥ 0) else unknown.

## 5. CRC32

### 5.1 Algorithm

CRC32 using the standard **CRC-32/ISO-HDLC** polynomial (0xEDB88320 reflected, 0xFFFFFFFF initial, 0xFFFFFFFF final XOR).

### 5.2 Coverage

The CRC32 field covers:
- Header bytes 0–23 (Sequence through Reserved, 24 bytes)
- Payload bytes (all section data, PayloadLength bytes)

The CRC field at offset 24 is zeroed during computation. The writer MUST:
1. Set CRC field to 0
2. Compute CRC32 over header[0..23] + payload[0..PayloadLength−1]
3. Write computed CRC to header offset 24

### 5.3 Validation

The reader MUST:
1. Save the CRC field value
2. Zero the CRC field
3. Compute CRC32 over the same range
4. Compare to saved value
5. Restore the CRC field (or discard the copy)

CRC mismatch means the buffer is corrupted or torn (writer was mid-write). The reader MUST NOT consume a frame with a CRC mismatch.

## 6. Double-Buffer Protocol

### 6.1 Writer Rules (Lua Emitter)

1. Initialize both buffers with all zeros.
2. On the first frame, set Sequence=1, write into Buffer A.
3. On each subsequent frame:
   a. Increment Sequence by 1.
   b. Alternate target buffer (A → B → A → B...).
   c. Write header and payload into the target buffer.
   d. Compute and write CRC32 LAST.
4. Heartbeat frames (no game state change, conditions unchanged) MUST have IsHeartbeat flag set and only include the ProviderInfo section. They MUST still increment Sequence.
5. Game-observed timestamps (ProducerFrameMs) MUST come from `Inspect.Time.Frame()`.
6. The SessionId MUST persist for the lifetime of the addon load and change on reload.

### 6.2 Reader Rules (C# Parser)

1. Locate the sentinel magic "BotDsV05" in memory.
2. Validate TotalSize and BufferSlotSize.
3. On each read cycle:
   a. Copy both Buffer A and Buffer B into local memory.
   b. Validate CRC32 for each buffer independently.
   c. Select the buffer with:
      - Valid CRC
      - Higher Sequence number
   d. If both have valid CRC, use the higher sequence.
   e. If only one has valid CRC, use it (writer was mid-write on the other).
   f. If neither has valid CRC, the transport is faulted.
4. Validate ProtocolVersion == 5; reject otherwise.
5. Validate Reserved fields are zero; reject otherwise.
6. Track Sequence continuity: gaps indicate missed frames; restart resets sequence to 1 with new SessionId.

### 6.3 Torn-Read Semantics

A torn read occurs when the Reader copies a buffer while the Writer is actively writing it. CRC32 validation reliably detects torn reads because the CRC field is written last. The Reader then falls back to the other buffer (which the writer is not touching). With two buffers, every consistent read is guaranteed to find at least one stable slot.

## 7. Sequence And Session Continuity

### 7.1 Session Model

A session begins when the addon is loaded (new SessionId UUID). Sequence starts at 1 and increments monotonically.

### 7.2 Continuity Rules

| Condition | Health impact |
|-----------|--------------|
| New SessionId with Sequence=1 | Normal: session restart (Healthy after first valid frame) |
| Sequence increments by exactly 1 | Normal: continuous (Healthy) |
| Sequence increments by >1 | Degraded: gap detected |
| Sequence does not increment for > HeartbeatIntervalMs × 3 | Stale: writer may be paused |
| Sequence wraps (unlikely with uint32) | Degraded but still valid |
| Sequence decrements without new SessionId | Faulted: protocol violation |

### 7.3 Staleness

A frame is stale when its sequence has not advanced within the accepted age:
```
(ReaderNow − ReaderTimeWhenSequenceLastChanged) > MaxTelemetryAgeMs
```
The emitter reports MaxTelemetryAgeMs in ProviderInfo. The reader MAY use a local override. The reader MUST NOT subtract `Inspect.Time.Frame()` from a UTC timestamp because those clocks have different epochs.

## 8. Protocol Health Mapping

The transport-level health maps to `BotDs.Core.ProviderHealth`:

| Transport Condition | ProviderHealth |
|--------------------|----------------|
| Sentinel not found or invalid | Disconnected |
| Sentinel found, no valid CRC on any buffer | Faulted |
| Valid CRC, but Sequence discontinuity | Degraded |
| Valid CRC, but frame age > MaxTelemetryAgeMs | Stale |
| Valid CRC, continuous, fresh | Healthy |
| Protocol version mismatch | Faulted |
| Reserved fields non-zero | Faulted |
| PayloadLength exceeds buffer capacity | Faulted |

## 9. Bounds And Limits

| Parameter | Value |
|-----------|-------|
| Buffer slot size | 8192 bytes |
| Maximum payload per frame | 8164 bytes |
| Maximum abilities per frame | 128 |
| Maximum auras per list per frame | 64 |
| Maximum string: Id fields | 64 bytes |
| Maximum string: Name fields | 32 bytes |
| Maximum sections per frame | 6 (one per type) |
| Maximum SectionLength | 8164 − (section headers overhead) |
| Heartbeat default interval | 50 ms |

## 10. Versioning

- **Protocol version 5**: this specification.
- Backward-incompatible changes require a protocol version bump.
- The Reader MUST reject any ProtocolVersion != 5.
- The SectionsMask reserved bits allow forward-compatible section additions without a version bump when the Reader can safely skip unknown sections.

## 11. Cross-Platform Notes

- All multi-byte integers are **little-endian** (LE), matching the x86/x64 platform.
- Strings are length-prefixed, NOT null-terminated.
- Fixed-length string fields use space-padding (ASCII 0x20) for remaining bytes.
- `Inspect.Time.Frame()` returns time in milliseconds since client start (monotonic, not wall clock).
- `ProducerFrameMs` is the emitter's monotonic frame-time value (e.g. Inspect.Time.Frame() or equivalent). It is NOT epoch time. Reader freshness is based on how long the selected sequence remains unchanged.
