# BotDs V5 Wire Protocol Specification v1.0

**Status**: authoritative. This document defines the single wire contract shared by the C# Reader and the Lua addon emitter. Every byte offset, size, sentinel, flag, section mask, and CRC rule specified here is normative.

---

## 1. Memory Region

The protocol defines a contiguous **16400-byte** memory image. The external Reader locates the image by scanning process memory for the sentinel magic. A conforming live publisher must keep this image at a stable readable virtual address while updating it according to the writer rules in section 6. The current Lua skeleton represents the image with an immutable string and does not yet satisfy that backing-storage invariant.

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

The writer never changes the sentinel bytes after initialization. In a conforming live publisher, the sentinel's fixed position and magic provide a stable reference point for the Reader. Retaining the bytes in a newly allocated immutable string is not sufficient because the virtual address may change.

## 3. Buffer Slot Layout

Each 8192-byte buffer slot is independent. The writer writes to exactly one slot per frame, strictly alternating between A (offset 16) and B (offset 8208). The region must use stable mutable backing storage. After writing the payload in place, the writer MUST update the CRC field last so that a torn read yields a CRC mismatch.

```
Offset   Size  Field
------   ----  -----
0        4     Sequence (uint32 LE) — monotonic per session, starts at 1
4        4     ProducerFrameMs (uint32 LE) — Inspect.Time.Frame() converted to milliseconds
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
| 2 | GameInputReady | When bit 3 is also set: combat key input is ready (not chat/edit-focus blocked) |
| 3 | GameInputReadyKnown | Bit 2 is meaningful; clear = readiness unknown |
| 4–7 | Reserved | MUST be 0 |

**Target knownness (M2):** Target section omitted ⇒ unknown. Target section present with `UnitFlagIsAvailable` clear ⇒ known no target. Present with available unit ⇒ known target.

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

Sections appear in the payload in ascending mask order (ProviderInfo first, then Player, Target, Abilities, PlayerAuras, TargetAuras). The ascending order follows bits 0 through 5. A section is present if and only if its mask bit is set. The Reader rejects duplicate sections, out-of-order sections, and any mismatch between the parsed section set and `SectionsMask`.

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
| 26 | N | ASCII | ClientVersion (`Inspect.System.Version().external` preferred; never `tostring(table)`, NOT null-terminated) |
| — | 1 | uint8 | SchemaVersion (currently **2** — ability records include name; action bar section optional) |
| — | 1 | uint8 | Reserved (MUST be 0) |

Minimum ProviderInfo data length (with empty client version): 28 bytes.
Maximum client version string length: 128 bytes.

### 4.2 Unit State (0x0002 Player, 0x0003 Target)

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 0 | 2 | uint16 LE | IdLength | — |
| 2 | N | ASCII | Id (unit specifier or numeric ID string) | 0 length |
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
| 0 | 2 | uint16 LE | Count (number of ability records, 0–128) |
| 2 | N×80 | — | Ability records (each 80 bytes fixed, schema v2) |

**Ability Record** (80 bytes, fixed-length for scanning efficiency; **schema version 2**):

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 0 | 32 | ASCII | AbilityId (space-padded) | — |
| 32 | 4 | int32 LE | CooldownRemainingMs | −1 |
| 36 | 4 | int32 LE | CooldownDurationMs | −1 |
| 40 | 4 | int32 LE | CastTimeMs | −1 |
| 44 | 1 | uint8 | Flags (bit0=available, bit1=usable, bit2=inRange, bit3=passive, bit4=channeled) | — |
| 45 | 1 | uint8 | ResourceCost (primary resource cost, 0–255) | — |
| 46 | 2 | uint16 LE | NameLength | 0 |
| 48 | 32 | UTF-8 | Name (null-padded; display name from `Inspect.Ability.New.Detail().name`) | empty |

**Emitter notes (RIFT current client):**
- Detail API times (`currentCooldownRemaining`, `currentCooldownDuration`, `cooldown`, `castingTime`) are **seconds**; the bridge converts to milliseconds on the wire.
- Usability is primarily `not detail.unusable` (Detail docs list `unusable`, not a stable `usable` member). Passive abilities set the passive bit and clear usable.
- Fixed-length records allow the parser to compute offsets without per-record length scanning.

### 4.5 Action Bar (0x0007) — calibration only

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 0 | 1 | uint8 | Page (`Action.Bar.Page.Get()` when available) |
| 1 | 1 | uint8 | SlotCount (0–12) |
| 2 | N×33 | — | Slot records: `slot` (uint8 1–12) + AbilityId (32 ASCII space-padded; empty = no ability) |

Keys are **not** observable; this section maps bar slots → ability ids for operator calibration only.

### 4.4 Auras List (0x0005 Player, 0x0006 Target)

| Offset | Size | Type | Field |
|--------|------|------|-------|
| 0 | 2 | uint16 LE | Count (number of aura records, 0–64) |
| 2 | N×70 | — | Aura records (each 70 bytes fixed) |

**Aura Record** (70 bytes):

| Offset | Size | Type | Field | Null sentinel |
|--------|------|------|-------|---------------|
| 0 | 32 | ASCII | AuraId (space-padded) | first byte '\0' |
| 32 | 2 | uint16 LE | NameLength | — |
| 34 | 32 | UTF-8 | Name (null-padded) | — |
| 66 | 1 | uint8 | Stacks (0 if unknown) | — |
| 67 | 1 | uint8 | Flags (bit0=isDebuff, bit1=isCurse, bit2=isDisease, bit3=isPoison) | — |
| 68 | 2 | int16 LE | RemainingMs (0–32767; −1 if unknown) | −1 |

`RemainingMs` is a signed 16-bit millisecond value. Values from 0 through 32767 are representable; `-1` means unknown. Longer durations require a future protocol version rather than truncation or wrapping.

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

CRC mismatch means the buffer is corrupted or may have been read while a conforming in-place writer was updating it. The Reader MUST NOT consume a frame with a CRC mismatch.

## 6. Double-Buffer Protocol

### 6.1 Writer Rules (Lua Emitter)

1. Allocate one stable, mutable, contiguous 16400-byte region and initialize both buffers with zeros.
2. On the first frame, set Sequence=1, write into Buffer A.
3. On each subsequent frame:
   a. Increment Sequence by 1.
   b. Alternate target buffer (A → B → A → B...).
   c. Write header and payload into the target buffer.
   d. Compute and write CRC32 LAST.
4. Heartbeat frames (no game state change, conditions unchanged) MUST have IsHeartbeat flag set and only include the ProviderInfo section. They MUST still increment Sequence.
5. Game-observed timestamps (ProducerFrameMs) MUST come from `Inspect.Time.Frame()`.
6. The SessionId MUST persist for the lifetime of the addon load and change on reload.
7. If a complete payload would exceed 8164 bytes, the Writer MUST leave both slots and Sequence unchanged. It MUST NOT publish a heartbeat in place of dropped game state; the unchanged frame must age stale at the Reader.
8. A list section is complete only when enumeration and every detail lookup succeed without hitting the record cap. Omit incomplete list sections so consumers treat them as unknown rather than known-empty or complete.

### 6.2 Reader Rules (C# Parser)

1. Locate the sentinel magic "BotDsV05" in memory.
2. Validate TotalSize and BufferSlotSize.
3. On each read cycle:
   a. Copy both Buffer A and Buffer B into local memory.
   b. Validate CRC32 and parse each buffer independently.
   c. If only one frame is valid, select it.
   d. For frames in the same session, select by wrap-aware uint32 serial-number ordering. If sequence values are equal, every other header identity field must also match; otherwise selection is ambiguous.
   e. For frames from different sessions, select by wrap-aware `ProducerFrameMs` ordering.
   f. A difference of exactly `0x80000000`, a cross-session producer-time tie, conflicting equal-sequence headers, or the absence of a unique newest candidate is ambiguous and fails closed.
   g. If neither frame is valid, the transport is faulted.
4. Validate ProtocolVersion == 5; reject otherwise.
5. Validate Reserved fields are zero; reject otherwise.
6. Track Sequence continuity: gaps indicate missed frames; a new SessionId establishes a new baseline.

### 6.3 Torn-Read Semantics

A torn read occurs when the Reader copies a slot while a conforming Writer is actively updating that same stable allocation. CRC32 validation detects the inconsistent slot because the CRC field is written last. The Reader then falls back to the other slot, which the Writer is not touching. This guarantee depends on one stable mutable allocation and in-place ordered writes; immutable whole-value replacement does not provide it.

### 6.4 Current Lua Skeleton Limitation

`BotDsBridge/main.lua` currently rebuilds immutable Lua strings during every field write. Consequently:

- the 16400-byte image may move to a different virtual address;
- intermediate and stale copies may coexist until garbage collection;
- CRC-last is construction order, not an observable in-place publication barrier;
- alternating logical slots does not guarantee that the Reader sees one stable physical slot; and
- repeated full-string copies create excessive temporary allocation volume.

The addon is therefore a provider-envelope prototype only. Live publication requires either a current-client facility that exposes stable mutable storage or a transport redesign. Reader relocation scans reduce the effect of occasional movement but cannot establish this invariant for a region that may move on every write.

## 7. Sequence And Session Continuity

### 7.1 Session Model

A session begins when the addon is loaded (new SessionId UUID). Sequence starts at 1 and increments monotonically.

### 7.2 Continuity Rules

| Condition | Health impact |
|-----------|--------------|
| New SessionId | Normal: establish a new session baseline |
| Sequence increments by exactly 1 | Normal: continuous (Healthy) |
| Sequence increments by >1 | Degraded: gap detected |
| Sequence does not increment beyond the accepted maximum age | Stale: writer may be paused |
| Sequence wraps (unlikely with uint32) | Degraded but still valid |
| Sequence decrements without new SessionId | Faulted: protocol violation |
| Sequence difference is exactly `0x80000000` | Faulted: ordering is ambiguous |

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
| Sentinel not found | Disconnected |
| Sentinel found but invalid, or no valid CRC on either slot | Faulted |
| Valid CRC with a forward gap or wrap | Degraded |
| Valid CRC with a decrement or ambiguous ordering | Faulted |
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
- The Reader rejects reserved SectionsMask bits and unknown section types. Adding section types or mask bits requires a protocol version bump unless a future protocol explicitly defines capability negotiation and safe unknown-section skipping.

## 11. Cross-Platform Notes

- All multi-byte integers are **little-endian** (LE), matching the x86/x64 platform.
- Strings are length-prefixed, NOT null-terminated.
- Fixed-length string fields use space-padding (ASCII 0x20) for remaining bytes.
- `Inspect.Time.Frame()` returns monotonic frame time in seconds; the Lua emitter multiplies it by 1000 and floors it for the uint32 millisecond field.
- `ProducerFrameMs` is monotonic time since client start, not epoch time. Reader freshness is based on how long the selected sequence remains unchanged.
