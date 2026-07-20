-- BotDsBridge V5 Protocol Emitter
-- Skeleton producing the V5 double-buffer telemetry envelope.
-- Published APIs used: Inspect.Time.Frame, Inspect.System.Version,
-- Inspect.System.Secure, Inspect.Unit.Detail, Inspect.Ability.New.Detail,
-- Inspect.Buff.Detail.
-- All other game-state functions are stubbed and marked TODO for
-- conformance testing against the current gamigo client.
--
-- Protocol Region Layout (16400 bytes total):
--   Offset  0: Sentinel magic "BotDsV05" (8 bytes) + TotalSize (4) + BufferSlotSize (4)
--   Offset 16: Buffer A (8192 bytes) — header (28) + payload (8164 max)
--   Offset 8208: Buffer B (8192 bytes) — identical layout, toggled per frame
-- The wire format is logically double-buffered: each frame replaces one slot and
-- carries a CRC. The current skeleton stores that image in an immutable Lua
-- string, however, so every helper below creates a new allocation. It therefore
-- does NOT yet guarantee a stable virtual address or observable in-place
-- CRC-last writes. Those guarantees require stable mutable backing storage and
-- must be solved before this emitter is used for live telemetry.

local PROTOCOL_VERSION = 5
local BUFFER_SLOT_SIZE = 8192
local REGION_TOTAL_SIZE = 16400
local SENTINEL_MAGIC = "BotDsV05"

-- Section types (uint16 LE)
local SECTION_PROVIDER_INFO = 0x0001
local SECTION_PLAYER        = 0x0002
local SECTION_TARGET        = 0x0003
local SECTION_ABILITIES     = 0x0004
local SECTION_PLAYER_AURAS  = 0x0005
local SECTION_TARGET_AURAS  = 0x0006

-- Sections mask bits
local MASK_PROVIDER_INFO = 0x00000001
local MASK_PLAYER        = 0x00000002
local MASK_TARGET        = 0x00000004
local MASK_ABILITIES     = 0x00000008
local MASK_PLAYER_AURAS  = 0x00000010
local MASK_TARGET_AURAS  = 0x00000020

-- Header offsets within a buffer slot
local HDR_SEQUENCE           = 0
local HDR_PRODUCER_FRAME_MS  = 4
local HDR_SECTIONS_MASK      = 8
local HDR_HEARTBEAT_INTERVAL = 12
local HDR_PAYLOAD_LENGTH     = 16
local HDR_PROTOCOL_VERSION   = 20
local HDR_FLAGS              = 21
local HDR_RESERVED           = 22
local HDR_CRC32              = 24
local HEADER_SIZE            = 28
local PAYLOAD_OFFSET         = 28

-- Sentinel offsets
local SENTINEL_MAGIC_OFFSET     = 0
local SENTINEL_TOTAL_SIZE       = 8
local SENTINEL_BUFFER_SLOT_SIZE = 12

-- Buffer A at offset 16, Buffer B at offset 8208.
-- These offsets are relative to the start of the 16400-byte region string.
-- The sentinel occupies bytes 0–15; buffer data starts at byte 16.
local BUFFER_A_OFFSET = 16
local BUFFER_B_OFFSET = 16 + BUFFER_SLOT_SIZE

----------------------------------------------------------------------
-- CRC32 (CRC-32/ISO-HDLC: polynomial 0xEDB88320 reflected)
--
-- RIFT's addon Lua environment may not include the LuaJIT "bit" library.
-- Therefore all uint32 bitwise operations (band, bxor, bor, rshift) are
-- implemented below in pure Lua using integer arithmetic on the double-
-- precision number type. Lua numbers can exactly represent integers up
-- to 2^53, so the 32-bit range fits without precision loss.
----------------------------------------------------------------------
local UINT32 = 4294967296

-- Clamp `value` into the 32-bit unsigned integer range [0, 2^32-1].
-- Uses modulo 2^32 (via math.floor to avoid floating inaccuracies at the boundary).
-- This is the foundation for all pure-Lua bitwise operations below.
local function normalize_u32(value)
    return math.floor(value) % UINT32
end

-- Pure-Lua bitwise AND: iterates over each bit position, accumulating
-- powers of two where both operands have a 1 bit.
local function band(left, right)
    left = normalize_u32(left)
    right = normalize_u32(right)
    local result = 0
    local place = 1
    while left > 0 and right > 0 do
        local leftBit = left % 2
        local rightBit = right % 2
        if leftBit == 1 and rightBit == 1 then
            result = result + place
        end
        left = math.floor(left / 2)
        right = math.floor(right / 2)
        place = place * 2
    end
    return result
end

-- Pure-Lua bitwise XOR: accumulates powers of two where bits differ.
local function bxor(left, right)
    left = normalize_u32(left)
    right = normalize_u32(right)
    local result = 0
    local place = 1
    while left > 0 or right > 0 do
        if (left % 2) ~= (right % 2) then
            result = result + place
        end
        left = math.floor(left / 2)
        right = math.floor(right / 2)
        place = place * 2
    end
    return result
end

-- Pure-Lua bitwise OR: derived via (A + B - (A & B)).
-- Uses the identity A|B = A + B - (A&B) to avoid a third bit-iteration loop.
local function bor(left, right)
    return normalize_u32(left + right - band(left, right))
end

-- Pure-Lua logical right-shift: divide by 2^count after normalizing to uint32.
local function rshift(value, count)
    return math.floor(normalize_u32(value) / (2 ^ count))
end

-- Pre-compute the 256-entry CRC-32 lookup table (reflected polynomial 0xEDB88320).
-- Each entry is the CRC of a single byte value, computed once at module load.
local crc32_table = {}
for i = 0, 255 do
    local crc = i
    for _ = 1, 8 do
        if band(crc, 1) ~= 0 then
            crc = bxor(rshift(crc, 1), 0xEDB88320)
        else
            crc = rshift(crc, 1)
        end
    end
    crc32_table[i] = crc
end

-- Single-byte CRC step: XOR the running CRC with the new byte, look up
-- the low 8 bits in the precomputed table, and combine with the shifted CRC.
local function crc32_update(crc, byte)
    local index = band(bxor(crc, byte), 0xFF)
    return bxor(band(rshift(crc, 8), 0x00FFFFFF), crc32_table[index])
end

-- Compute CRC32 over a contiguous range of the region string.
-- startOffset is zero-based; length is the byte count.
-- Uses the standard init=0xFFFFFFFF, final-XOR=0xFFFFFFFF convention.
local function crc32_region(region, startOffset, length)
    local crc = 0xFFFFFFFF
    for i = 1, length do
        local b = string.byte(region, startOffset + i)
        crc = crc32_update(crc, b)
    end
    return bxor(crc, 0xFFFFFFFF)
end

-- Compute CRC32 over two non-contiguous ranges concatenated logically.
-- Used by write_frame to cover header[0..23] + payload as a single CRC.
local function crc32_combined(region, offset1, len1, offset2, len2)
    local crc = 0xFFFFFFFF
    for i = 1, len1 do
        local b = string.byte(region, offset1 + i)
        crc = crc32_update(crc, b)
    end
    for i = 1, len2 do
        local b = string.byte(region, offset2 + i)
        crc = crc32_update(crc, b)
    end
    return bxor(crc, 0xFFFFFFFF)
end

----------------------------------------------------------------------
-- Little-endian write helpers
--
-- WARNING: Lua strings are immutable. Every write helper below creates a new
-- string by concatenating `sub(1, pos-1)` + encoded bytes + `sub(pos+N)`.
-- write_frame calls these helpers thousands of times, so the temporary allocation
-- volume is much larger than one 16400-byte copy per frame. This intentionally
-- simple provider skeleton is not suitable for live publication. A future
-- transport must avoid the repeated copies and, more importantly, provide the
-- stable virtual address required by the external Reader.
--
-- All positions are 1-based (Lua string indexing convention).
----------------------------------------------------------------------
-- Write a single unsigned byte at position pos (1-based).
local function write_u8(str, pos, val)
    return string.sub(str, 1, pos - 1) .. string.char(band(val, 0xFF)) .. string.sub(str, pos + 1)
end

-- Write uint16 in little-endian order: low byte first, then high byte.
local function write_u16_le(str, pos, val)
    return string.sub(str, 1, pos - 1)
        .. string.char(band(val, 0xFF), band(rshift(val, 8), 0xFF))
        .. string.sub(str, pos + 2)
end

-- Write uint32 in little-endian order: bytes 0,1,2,3 from LSB to MSB.
local function write_u32_le(str, pos, val)
    return string.sub(str, 1, pos - 1)
        .. string.char(
            band(val, 0xFF),
            band(rshift(val, 8), 0xFF),
            band(rshift(val, 16), 0xFF),
            band(rshift(val, 24), 0xFF))
        .. string.sub(str, pos + 4)
end

-- Write int32 as its unsigned two's-complement bit pattern in LE.
-- Negative values are converted by adding 2^32, then written as uint32 LE.
local function write_i32_le(str, pos, val)
    -- Convert signed int32 to unsigned bit pattern
    if val < 0 then val = val + 0x100000000 end
    return write_u32_le(str, pos, val)
end

-- Write a length-prefixed ASCII string: uint16 LE length, then raw bytes.
-- maxLen truncates the string if it exceeds the field limit.
local function write_ascii(str, pos, text, maxLen)
    maxLen = maxLen or #text
    local result = write_u16_le(str, pos, #text)
    pos = pos + 2
    for i = 1, #text do
        if i <= maxLen then
            local b = string.byte(text, i)
            result = write_u8(result, pos + i - 1, b)
        end
    end
    return result
end

-- Write a fixed-width ASCII field. Shorter strings are space-padded (0x20);
-- longer strings are truncated. Used for ability/aura ID fields.
local function write_fixed_ascii(str, pos, text, fieldSize)
    local result = str
    for i = 1, fieldSize do
        local b = 0x20 -- space padding
        if i <= #text then
            b = string.byte(text, i)
        end
        result = write_u8(result, pos + i - 1, b)
    end
    return result
end

----------------------------------------------------------------------
-- Region and session state
--
-- `region` is the current 16400-byte logical image. The write helpers treat it
-- as a byte array, but each write creates a new string and reassigns `region`.
-- The sentinel bytes at offset 0 remain unchanged in the logical image. Their
-- physical address is not stable, so they are not yet a reliable long-lived
-- anchor for the external C# Reader.
--
-- `sessionId` is a 16-byte binary UUID (v4-style) generated at addon load.
-- It persists for the lifetime of the addon; a new session starts on /reloadui.
--
-- `writeBufferIndex` toggles 0↔1 each frame to alternate between Buffer A and B.
-- `sequence` starts at 1 and increments monotonically per frame. A reset to 1
-- with a new sessionId signals a session restart to the Reader.
----------------------------------------------------------------------
local region = nil         -- the 16400-byte backing string
local sessionId = ""       -- 16-byte binary UUID
local sequence = 0
local writeBufferIndex = 0 -- 0 = Buffer A, 1 = Buffer B
local heartbeatIntervalMs = 50
local maxTelemetryAgeMs = 500

----------------------------------------------------------------------
-- Initialization
----------------------------------------------------------------------

-- Generate a 16-byte binary UUID (v4-style random).
--
-- The RIFT addon environment does not expose a cryptographic RNG or
-- os.time(); we derive bytes from Inspect.Time.Frame() (monotonic frame
-- time in seconds since client start) combined with Knuth-inspired
-- multiplicative constants to produce reasonably unique bits:
--   - 0x9E3779B9 = 2^32 / φ (the golden ratio constant, for mixing)
--   - 2654435761  = floor(2^32 * (√5-1)/2) (another φ-based constant)
--   - 1836311903  = floor(2^32 * 0.6180339887) (fractional part of φ)
-- These provide good bit dispersion without needing math.random.
--
-- Bytes 7 and 9 are then masked to set UUID version 4 (random) and
-- variant 1 (RFC 4122), so the 16 bytes form a valid v4 UUID when
-- displayed as hex.
----------------------------------------------------------------------
local function generate_uuid_binary()
    local t = Inspect.Time.Frame() or 0
    local bytes = {}
    for i = 1, 16 do
        local v = normalize_u32((t * 2654435761) + (i * 1836311903) + 0x9E3779B9)
        bytes[i] = band(rshift(v, (i % 4) * 8), 0xFF)
        t = t + 1
    end
    bytes[7] = bor(band(bytes[7], 0x0F), 0x40)
    bytes[9] = bor(band(bytes[9], 0x3F), 0x80)
    local unpackValues = unpack or table.unpack
    return string.char(unpackValues(bytes))
end

-- Allocate the full 16400-byte region string (zero-filled) and write the
-- sentinel header. Its bytes are retained in each later logical image and are
-- the scan target used by the C# Reader. Immutable string replacement means
-- the allocation address itself may still change.
-- See PROTOCOL.md §2 for the sentinel layout.
local function init_region()
    region = string.rep("\0", REGION_TOTAL_SIZE)

    -- Write sentinel magic
    for i = 1, #SENTINEL_MAGIC do
        region = write_u8(region, SENTINEL_MAGIC_OFFSET + i, string.byte(SENTINEL_MAGIC, i))
    end
    region = write_u32_le(region, SENTINEL_MAGIC_OFFSET + SENTINEL_TOTAL_SIZE + 1, REGION_TOTAL_SIZE)
    region = write_u32_le(region, SENTINEL_MAGIC_OFFSET + SENTINEL_BUFFER_SLOT_SIZE + 1, BUFFER_SLOT_SIZE)

    sessionId = generate_uuid_binary()
    sequence = 0
    writeBufferIndex = 0
end

----------------------------------------------------------------------
-- Section encoders
--
-- Each section encoder returns (section_bytes, has_data):
--   section_bytes — the raw TLV data (type+length prefix added by caller)
--   has_data      — truthy if the section should be included in the frame;
--                   currently always false for game-state sections (TODO stubs).
--
-- Wire encoding follows PROTOCOL.md §4: type-length-value with uint16 LE
-- type and length. String fields use length-prefixed encoding (uint16 LE
-- length followed by raw bytes), never null-terminated.
----------------------------------------------------------------------

-- Encode a length-prefixed ASCII string: writes uint16 LE byte count,
-- then the string bytes (truncated to maxLen). Returns (buf, nextOffset).
-- This is the standard wire format for all variable-length string fields.
local function encode_ascii_field(buf, offset, text, maxLen)
    maxLen = maxLen or 128
    local tlen = math.min(#text, maxLen)
    buf = write_u16_le(buf, offset, tlen)
    offset = offset + 2
    for i = 1, tlen do
        buf = write_u8(buf, offset + i - 1, string.byte(text, i))
    end
    return buf, offset + tlen
end

-- Build the ProviderInfo section (PROTOCOL.md §4.1).
-- This is the only section always present in every frame. It carries:
--   - SessionId (16 bytes): binary UUID that changes only on addon reload
--   - ProducerFrameMs (uint32 LE): monotonic frame time from Inspect.Time.Frame(),
--     NOT epoch time; the Reader uses it for candidate ordering while freshness
--     is measured on the Reader's own monotonic clock
--   - MaxTelemetryAgeMs (uint32 LE): emitter's acceptable staleness window
--   - ClientVersion: Inspect.System.Version() string (currently empty — TODO)
--   - SchemaVersion: always 1 (the section layout version within protocol v5)
--   - Reserved: must be 0
--
-- Always returns (section_bytes, MASK_PROVIDER_INFO) regardless of state.
local function encode_provider_info()
    local buf = ""
    local producerFrameMs = math.floor((Inspect.Time.Frame() or 0) * 1000)

    local clientVersion = ""
    -- TODO: Inspect.System.Version() returns client build info
    -- local ver = Inspect.System.Version()
    -- if ver then clientVersion = ver .. "" end

    local pos = 1
    -- SessionId: 16 bytes
    for i = 1, 16 do
        buf = buf .. string.char(string.byte(sessionId, i))
    end
    pos = pos + 16
    -- ProducerFrameMs: 4 bytes (uint32 LE)
    buf = buf .. write_u32_le("", 1, producerFrameMs)
    pos = pos + 4
    -- MaxTelemetryAgeMs: 4 bytes
    buf = buf .. write_u32_le("", 1, maxTelemetryAgeMs)
    pos = pos + 4
    -- ClientVersionLength: 2 bytes
    buf = buf .. write_u16_le("", 1, #clientVersion)
    pos = pos + 2
    -- ClientVersion: N bytes
    for i = 1, #clientVersion do
        buf = buf .. string.char(string.byte(clientVersion, i))
    end
    pos = pos + #clientVersion
    -- SchemaVersion: 1 byte
    buf = buf .. string.char(1)
    pos = pos + 1
    -- Reserved: 1 byte (MUST be 0)
    buf = buf .. string.char(0)

    return buf, MASK_PROVIDER_INFO
end

-- Build a unit state section (PROTOCOL.md §4.2).
--
-- Null/unknown fields use sentinel values:
--   int32 fields: -1 (0xFFFFFFFF in two's complement LE)
--   string fields: 0-length prefix
--   relation: 0 = unknown
--   flags: bit 2 (IsAvailable) cleared when unit detail lookup failed
--
-- The section layout interleaves fixed-size fields (Level, Health*, Resource*,
-- CastRemainingMs, CastDurationMs) with length-prefixed strings. The C# Reader
-- parses these in order; the layout is fixed per PROTOCOL.md §4.2 table.
--
-- Returns (section_bytes, is_available). When is_available is false, the
-- caller skips the section entirely (no TLV entry emitted).
local function encode_unit_state(unitSpecifier)
    -- unitSpecifier: e.g. "player", "player.target"
    local buf = ""
    local avail = false
    local id = ""
    local name = ""
    local level = -1
    local calling = ""
    local flags = 0
    local relation = 0
    local healthCur = -1
    local healthMax = -1
    local resCur = -1
    local resMax = -1
    local resKind = ""
    local castId = ""
    local castName = ""
    local castRemain = -1
    local castDur = -1
    local castFlags = 0

    -- TODO: Use Inspect.Unit.Detail(unitSpecifier) to populate fields
    -- local detail = Inspect.Unit.Detail(unitSpecifier)
    -- if detail then
    --     avail = true
    --     flags = V5Constants.UnitFlagIsAvailable
    --     id = tostring(detail.id or "")
    --     name = detail.name or ""
    --     level = detail.level or -1
    --     if detail.player then flags = flags | V5Constants.UnitFlagIsPlayer end
    --     if detail.combat then flags = flags | V5Constants.UnitFlagInCombat end
    --     if detail.relation == "hostile" then relation = 1
    --     elseif detail.relation == "friendly" then relation = 2
    --     elseif detail.relation == "neutral" then relation = 3 end
    --     healthCur = detail.health or -1
    --     healthMax = detail.healthMax or -1
    --     -- Resource: pick first non-nil power type
    --     if detail.mana then resCur = detail.mana; resKind = "mana"
    --     elseif detail.energy then resCur = detail.energy; resKind = "energy"
    --     elseif detail.power then resCur = detail.power; resKind = "power"
    --     elseif detail.charge then resCur = detail.charge; resKind = "charge" end
    --     -- Castbar
    --     local castbar = Inspect.Unit.Castbar(unitSpecifier)
    --     if castbar then
    --         castId = tostring(castbar.abilityId or "")
    --         castName = castbar.name or ""
    --         castRemain = castbar.remaining or -1
    --         castDur = castbar.duration or -1
    --         if castbar.channel then castFlags = castFlags | V5Constants.CastFlagIsChannel end
    --         if castbar.uninterruptible then castFlags = castFlags | V5Constants.CastFlagIsUninterruptible end
    --     end
    -- end

    -- Id
    local pos
    buf, pos = encode_ascii_field(buf, #buf + 1, id, 64)
    -- Name (UTF-8 — but detail.name is likely ASCII; encode as length-prefixed)
    buf = write_u16_le(buf, pos, #name)
    pos = pos + 2
    for i = 1, #name do
        buf = write_u8(buf, pos + i - 1, string.byte(name, i))
    end
    pos = pos + #name
    -- Level
    buf = write_i32_le(buf, pos, level)
    pos = pos + 4
    -- Calling
    buf, pos = encode_ascii_field(buf, pos, calling, 16)
    -- Flags ([pos], 1 byte)
    buf = write_u8(buf, pos, flags)
    pos = pos + 1
    -- Relation
    buf = write_u8(buf, pos, relation)
    pos = pos + 1
    -- Health
    buf = write_i32_le(buf, pos, healthCur)
    pos = pos + 4
    buf = write_i32_le(buf, pos, healthMax)
    pos = pos + 4
    -- Resource
    buf = write_i32_le(buf, pos, resCur)
    pos = pos + 4
    buf = write_i32_le(buf, pos, resMax)
    pos = pos + 4
    -- ResourceKind
    buf, pos = encode_ascii_field(buf, pos, resKind, 16)
    -- CastAbilityId
    buf, pos = encode_ascii_field(buf, pos, castId, 32)
    -- CastName (UTF-8)
    buf = write_u16_le(buf, pos, #castName)
    pos = pos + 2
    for i = 1, #castName do
        buf = write_u8(buf, pos + i - 1, string.byte(castName, i))
    end
    pos = pos + #castName
    -- Cast timing
    buf = write_i32_le(buf, pos, castRemain)
    pos = pos + 4
    buf = write_i32_le(buf, pos, castDur)
    pos = pos + 4
    -- CastFlags
    buf = write_u8(buf, pos, castFlags)

    return buf, avail
end

-- Build the abilities list section (PROTOCOL.md §4.3).
--
-- Each ability record is a fixed 46 bytes for efficient random-access parsing:
-- the C# Reader can compute record offsets without per-record length scanning.
-- AbilityId is space-padded to 32 bytes; a first byte of '\0' marks an empty slot.
-- Count is capped at 128 to keep total section size predictable.
--
-- Returns (section_bytes, is_known). A successful inventory query sets is_known
-- even when count is zero, allowing the Reader to distinguish known-empty from
-- unavailable telemetry. The current stub leaves is_known false.
local function encode_abilities()
    local buf = ""
    local count = 0
    local isKnown = false
    local records = ""

    -- TODO: Use Inspect.Ability.New.List() and Inspect.Ability.New.Detail()
    -- local ids = Inspect.Ability.New.List()
    -- if ids then
    --     local complete = true
    --     for _, abilityId in ipairs(ids) do
    --         if count >= 128 then
    --             complete = false
    --             break
    --         end
    --         local detail = Inspect.Ability.New.Detail(abilityId)
    --         if detail then
    --             local rec = string.rep("\0", 46)
    --             rec = write_fixed_ascii(rec, 1, tostring(detail.id or abilityId), 32)
    --             rec = write_i32_le(rec, 33, detail.currentCooldownRemaining or -1)
    --             rec = write_i32_le(rec, 37, detail.currentCooldownDuration or -1)
    --             rec = write_i32_le(rec, 41, detail.castingTime or -1)
    --             local aFlags = 0
    --             if not detail.unusable then aFlags = aFlags | 0x01 end -- available
    --             if detail.usable then aFlags = aFlags | 0x02 end
    --             if not detail.outOfRange then aFlags = aFlags | 0x04 end
    --             if detail.passive then aFlags = aFlags | 0x08 end
    --             if detail.channeled then aFlags = aFlags | 0x10 end
    --             rec = write_u8(rec, 45, aFlags)
    --             rec = write_u8(rec, 46, 0) -- resource cost (simplified)
    --             records = records .. rec
    --             count = count + 1
    --         else
    --             complete = false
    --         end
    --     end
    --     isKnown = complete
    -- end

    buf = write_u16_le(buf, 1, count)
    buf = buf .. records
    return buf, isKnown
end

-- Build an auras list section (PROTOCOL.md §4.4).
--
-- Each aura record is a fixed 70 bytes. RemainingMs is a signed int16 value:
-- 0..32767 milliseconds are representable and -1 means unknown. Longer aura
-- durations cannot be encoded safely by V5 and require a future protocol.
-- Count is capped at 64.
--
-- Returns (section_bytes, is_known), including a zero-count section after a
-- successful query. The current stub leaves is_known false.
local function encode_auras(unitSpecifier)
    local buf = ""
    local count = 0
    local isKnown = false
    local records = ""

    -- TODO: Use Inspect.Buff.List(unitSpecifier) and Inspect.Buff.Detail()
    -- local buffIds = Inspect.Buff.List(unitSpecifier)
    -- if buffIds then
    --     local complete = true
    --     for _, buffId in ipairs(buffIds) do
    --         if count >= 64 then
    --             complete = false
    --             break
    --         end
    --         local detail = Inspect.Buff.Detail(unitSpecifier, buffId)
    --         if detail then
    --             local rec = string.rep("\0", 70)
    --             rec = write_fixed_ascii(rec, 1, tostring(detail.buffId or buffId), 32)
    --             local aname = detail.name or ""
    --             rec = write_u16_le(rec, 33, #aname)
    --             for i = 1, math.min(#aname, 32) do
    --                 rec = write_u8(rec, 35 + i - 1, string.byte(aname, i))
    --             end
    --             rec = write_u8(rec, 67, detail.stacks or 0)
    --             local aFlags = 0
    --             if detail.debuff then aFlags = aFlags | 0x01 end
    --             if detail.curse then aFlags = aFlags | 0x02 end
    --             if detail.disease then aFlags = aFlags | 0x04 end
    --             if detail.poison then aFlags = aFlags | 0x08 end
    --             rec = write_u8(rec, 68, aFlags)
    --             local remain = detail.remaining or -1
    --             if remain >= 0 then remain = remain & 0xFFFF end
    --             rec = write_u16_le(rec, 69, remain)
    --             records = records .. rec
    --             count = count + 1
    --         else
    --             complete = false
    --         end
    --     end
    --     isKnown = complete
    -- end

    buf = write_u16_le(buf, 1, count)
    buf = buf .. records
    return buf, isKnown
end

----------------------------------------------------------------------
-- Frame emission
--
-- Each frame cycle:
--   1. build_payload() collects all available sections into a TLV payload
--   2. write_frame() writes header+payload into the selected buffer slot,
--      applies the CRC, and zeroes unused payload space
--   3. on_update_begin() alternates the target buffer and triggers the write
--
-- The function constructs the CRC field after the header and payload. That is
-- the required logical wire order. Because the current backing value is an
-- immutable string replaced as a whole, this does not provide observable
-- in-place CRC-last publication or a stable-address torn-read guarantee.
----------------------------------------------------------------------

-- Collect all sections, encode each as TLV (type uint16 LE | length uint16 LE | data),
-- concatenate into a single payload string, and compute the sectionsMask bitfield.
-- Sections are emitted in ascending mask order: ProviderInfo → Player → Target →
-- Abilities → PlayerAuras → TargetAuras (bits 0–5). This ordering is fixed so
-- the Reader can validate section sequence.
local function build_payload()
    local payload = ""
    local sectionsMask = 0

    -- Collect sections
    local sections = {}

    -- ProviderInfo (always included)
    local providerBytes, providerMask = encode_provider_info()
    table.insert(sections, {type = SECTION_PROVIDER_INFO, data = providerBytes, mask = providerMask})

    -- Player
    local playerBytes, playerAvail = encode_unit_state("player")
    if playerAvail then
        table.insert(sections, {type = SECTION_PLAYER, data = playerBytes, mask = MASK_PLAYER})
    end

    -- Target
    local targetBytes, targetAvail = encode_unit_state("player.target")
    if targetAvail then
        table.insert(sections, {type = SECTION_TARGET, data = targetBytes, mask = MASK_TARGET})
    end

    -- Abilities
    local abilityBytes, abilityAvail = encode_abilities()
    if abilityAvail then
        table.insert(sections, {type = SECTION_ABILITIES, data = abilityBytes, mask = MASK_ABILITIES})
    end

    -- Player Auras
    local pAuraBytes, pAuraAvail = encode_auras("player")
    if pAuraAvail then
        table.insert(sections, {type = SECTION_PLAYER_AURAS, data = pAuraBytes, mask = MASK_PLAYER_AURAS})
    end

    -- Target Auras
    local tAuraBytes, tAuraAvail = encode_auras("player.target")
    if tAuraAvail then
        table.insert(sections, {type = SECTION_TARGET_AURAS, data = tAuraBytes, mask = MASK_TARGET_AURAS})
    end

    for _, section in ipairs(sections) do
        payload = write_u16_le(payload, #payload + 1, section.type)
        payload = write_u16_le(payload, #payload + 1, #section.data)
        payload = payload .. section.data
        sectionsMask = bor(sectionsMask, section.mask)
    end

    return payload, sectionsMask
end

-- Write one complete frame into the specified buffer slot.
--
-- Logical CRC-last construction order:
--   1. Write header fields (sequence, frameTime, sectionsMask, ... flags, reserved)
--   2. Write payload bytes
--   3. Zero remaining payload space (prevents stale data from prior writes)
--   4. Zero the CRC field → compute CRC32 over header[0..23]+payload → write CRC
--
-- If the payload exceeds BUFFER_SLOT_SIZE-HEADER_SIZE (8164 bytes), publish
-- nothing: leave both slots untouched and roll back the local sequence. A fresh
-- heartbeat would hide data loss and could make a preserved combat snapshot
-- appear current. Leaving the prior frame in place makes the Reader age it stale.
--
-- The CRC covers exactly header bytes 0–23 (Sequence through Reserved, 24 bytes)
-- plus the full PayloadLength bytes, per PROTOCOL.md §5.2. The CRC field itself
-- (bytes 24–27) is excluded from coverage (zeroed during computation).
local function write_frame(region, bufferOffset)
    sequence = sequence + 1
    local frameTime = math.floor((Inspect.Time.Frame() or 0) * 1000)
    local isSecure = false

    -- TODO: Inspect.System.Secure()
    -- isSecure = Inspect.System.Secure() or false

    local payload, sectionsMask = build_payload()
    local payloadLength = #payload

    if payloadLength > BUFFER_SLOT_SIZE - HEADER_SIZE then
        sequence = sequence - 1
        return region
    end

    -- Write header
    region = write_u32_le(region, bufferOffset + HDR_SEQUENCE + 1, sequence)
    region = write_u32_le(region, bufferOffset + HDR_PRODUCER_FRAME_MS + 1, frameTime)
    region = write_u32_le(region, bufferOffset + HDR_SECTIONS_MASK + 1, sectionsMask)
    region = write_u32_le(region, bufferOffset + HDR_HEARTBEAT_INTERVAL + 1, heartbeatIntervalMs)
    region = write_u32_le(region, bufferOffset + HDR_PAYLOAD_LENGTH + 1, payloadLength)
    region = write_u8(region, bufferOffset + HDR_PROTOCOL_VERSION + 1, PROTOCOL_VERSION)

    local flags = 0
    -- Heartbeat classification: a frame with only ProviderInfo (no game-state
    -- sections) is marked as a heartbeat. The Reader uses heartbeats for
    -- freshness/liveness checks only — it MUST NOT treat them as valid game
    -- state updates. Heartbeat frames still increment Sequence.
    if sectionsMask == MASK_PROVIDER_INFO then
        flags = bor(flags, 0x01)
    end
    if isSecure then
        flags = bor(flags, 0x02)
    end
    region = write_u8(region, bufferOffset + HDR_FLAGS + 1, flags)
    region = write_u16_le(region, bufferOffset + HDR_RESERVED + 1, 0)

    -- Write payload
    for i = 1, payloadLength do
        region = write_u8(region, bufferOffset + PAYLOAD_OFFSET + i, string.byte(payload, i))
    end

    -- Zero remaining payload space (clean up from previous writes)
    for i = payloadLength + 1, BUFFER_SLOT_SIZE - HEADER_SIZE do
        region = write_u8(region, bufferOffset + PAYLOAD_OFFSET + i, 0)
    end

    -- Zero CRC field, then compute and write CRC
    region = write_u32_le(region, bufferOffset + HDR_CRC32 + 1, 0)

    -- CRC covers header bytes 0..23 + full payload
    local crc = crc32_combined(region,
        bufferOffset + 1, HDR_CRC32,                         -- header covered portion (offset 0..23)
        bufferOffset + PAYLOAD_OFFSET + 1, payloadLength)    -- payload

    region = write_u32_le(region, bufferOffset + HDR_CRC32 + 1, crc)

    return region
end

----------------------------------------------------------------------
-- Frame handler — called each frame via Event.System.Update.Begin
--
-- Alternating double-buffer write:
--   Frame 1 → Buffer A (offset 16),   writeBufferIndex flips to 1
--   Frame 2 → Buffer B (offset 8208), writeBufferIndex flips to 0
--   Frame 3 → Buffer A, ...
--
-- With stable mutable backing storage, this pattern would leave one slot
-- untouched while the other is updated, and CRC-last publication would detect
-- a read of the active slot. The current immutable-string skeleton only
-- preserves the logical A/B contents; it does not yet provide those physical
-- memory guarantees.
--
-- The first call happens synchronously from on_addon_load() to populate
-- the initial frame before any event-driven update fires.
----------------------------------------------------------------------
local function on_update_begin()
    if not region then
        init_region()
    end

    local bufferOffset
    if writeBufferIndex == 0 then
        bufferOffset = BUFFER_A_OFFSET
        writeBufferIndex = 1
    else
        bufferOffset = BUFFER_B_OFFSET
        writeBufferIndex = 0
    end

    region = write_frame(region, bufferOffset)
end

----------------------------------------------------------------------
-- Addon lifecycle
--
-- RIFT addon entry flow:
--   1. The game loads BotDsBridge/main.lua and executes it as a script.
--   2. The final line `on_addon_load()` runs.
--   3. on_addon_load initializes the region, attaches to Event.System.Update.Begin
--      via Command.Event.Attach, then immediately calls on_update_begin() to
--      produce the first frame.
--   4. Subsequently, on_update_begin fires once per rendered frame.
--
-- Event attachment uses pcall to prevent the addon from crashing the client
-- if the RIFT API surface differs from expectations (e.g., missing Event
-- tables in future client versions).
--
-- The SessionId is regenerated on every addon load (/reloadui). A new
-- SessionId with Sequence=1 signals a fresh session to the Reader; the
-- old session's sequence counter is discarded.
----------------------------------------------------------------------
local function on_addon_load()
    init_region()

    if type(Command) == "table"
        and type(Command.Event) == "table"
        and type(Command.Event.Attach) == "function"
        and type(Event) == "table"
        and type(Event.System) == "table"
        and type(Event.System.Update) == "table"
        and Event.System.Update.Begin ~= nil then
        pcall(Command.Event.Attach, Event.System.Update.Begin, on_update_begin, "BotDsBridge.onUpdateBegin")
    end

    on_update_begin()
end

-- Entry point (RIFT addon loader calls this after loading the file)
on_addon_load()
