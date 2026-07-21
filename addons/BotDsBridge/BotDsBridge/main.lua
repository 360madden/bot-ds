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

-- Keep in sync with RiftAddon.toc Version=
local ADDON_VERSION = "0.2.0"
local PROTOCOL_VERSION = 5
local BUFFER_SLOT_SIZE = 8192
local REGION_TOTAL_SIZE = 16400
local SENTINEL_MAGIC = "BotDsV05"

-- Chat/console output (RIFT: Command.Console.Display; print falls back to console).
local function chat_print(message)
    local line = tostring(message or "")
    local ok = false
    if type(Command) == "table"
        and type(Command.Console) == "table"
        and type(Command.Console.Display) == "function" then
        ok = pcall(Command.Console.Display, "general", true, line, false)
    end
    if not ok and type(print) == "function" then
        print(line)
    end
end

-- Section types (uint16 LE)
local SECTION_PROVIDER_INFO = 0x0001
local SECTION_PLAYER        = 0x0002
local SECTION_TARGET        = 0x0003
local SECTION_ABILITIES     = 0x0004
local SECTION_PLAYER_AURAS  = 0x0005
local SECTION_TARGET_AURAS  = 0x0006
local SECTION_ACTION_BAR    = 0x0007

-- Sections mask bits
local MASK_PROVIDER_INFO = 0x00000001
local MASK_PLAYER        = 0x00000002
local MASK_TARGET        = 0x00000004
local MASK_ABILITIES     = 0x00000008
local MASK_PLAYER_AURAS  = 0x00000010
local MASK_TARGET_AURAS  = 0x00000020
local MASK_ACTION_BAR    = 0x00000040

-- Schema v2: ability records include display name (80-byte fixed)
local SCHEMA_VERSION = 2
local ABILITY_RECORD_SIZE = 80

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
-- Byte-buffer helpers (performance-critical)
--
-- regionBytes is a 1-based array of 0..255 values. Mutating it is O(1) per
-- write. We materialize ONE immutable string per publish for the external
-- scanner (BotDsBridgeRegion). Do NOT rebuild 16KB via string.sub per byte.
----------------------------------------------------------------------
-- Declared before helpers so closures capture this local (not a global).
local regionBytes = nil
local unpackValues = unpack or table.unpack

local function bytes_to_string(bytes, fromIdx, toIdx)
    local parts = {}
    local chunk = {}
    local n = 0
    local partCount = 0
    for i = fromIdx, toIdx do
        n = n + 1
        chunk[n] = bytes[i]
        if n == 256 then
            partCount = partCount + 1
            parts[partCount] = string.char(unpackValues(chunk, 1, 256))
            n = 0
        end
    end
    if n > 0 then
        partCount = partCount + 1
        parts[partCount] = string.char(unpackValues(chunk, 1, n))
    end
    return table.concat(parts, "", 1, partCount)
end

-- In-place region writers (1-based positions into regionBytes)
local function rb_u8(pos, val)
    regionBytes[pos] = band(val, 0xFF)
end

local function rb_u16_le(pos, val)
    regionBytes[pos] = band(val, 0xFF)
    regionBytes[pos + 1] = band(rshift(val, 8), 0xFF)
end

local function rb_u32_le(pos, val)
    regionBytes[pos] = band(val, 0xFF)
    regionBytes[pos + 1] = band(rshift(val, 8), 0xFF)
    regionBytes[pos + 2] = band(rshift(val, 16), 0xFF)
    regionBytes[pos + 3] = band(rshift(val, 24), 0xFF)
end

local function rb_copy_string(pos, data)
    for i = 1, #data do
        regionBytes[pos + i - 1] = string.byte(data, i)
    end
end

-- Legacy string helpers kept only for small section encoding (player/target).
-- Prefer table builders for large records (abilities).
local function write_u8(str, pos, val)
    return string.sub(str, 1, pos - 1) .. string.char(band(val, 0xFF)) .. string.sub(str, pos + 1)
end

local function write_u16_le(str, pos, val)
    return string.sub(str, 1, pos - 1)
        .. string.char(band(val, 0xFF), band(rshift(val, 8), 0xFF))
        .. string.sub(str, pos + 2)
end

local function write_u32_le(str, pos, val)
    return string.sub(str, 1, pos - 1)
        .. string.char(
            band(val, 0xFF),
            band(rshift(val, 8), 0xFF),
            band(rshift(val, 16), 0xFF),
            band(rshift(val, 24), 0xFF))
        .. string.sub(str, pos + 4)
end

local function write_i32_le(str, pos, val)
    if val < 0 then val = val + 0x100000000 end
    return write_u32_le(str, pos, val)
end

local function write_fixed_ascii(str, pos, text, fieldSize)
    local result = str
    for i = 1, fieldSize do
        local b = 0x20
        if i <= #text then
            b = string.byte(text, i)
        end
        result = write_u8(result, pos + i - 1, b)
    end
    return result
end

-- RIFT Inspect.Ability.New.Detail returns times in *seconds*; wire uses milliseconds.
local function seconds_to_ms(v)
    if type(v) ~= "number" then return -1 end
    if v < 0 then return -1 end
    -- Cap at ~24d of ms so int32 stays positive after floor
    local ms = math.floor(v * 1000 + 0.5)
    if ms > 2147483647 then return 2147483647 end
    return ms
end

-- Build fixed ability record (schema v2, 80 bytes) without per-byte string mutation.
-- Layout: id:32, cdRemain:i32, cdDur:i32, castTime:i32, flags:u8, cost:u8, nameLen:u16, name:32
local function build_ability_record(abilityId, cdRemainMs, cdDurMs, castTimeMs, flags, resourceCost, name)
    local rec = {}
    local id = tostring(abilityId or "")
    for i = 1, 32 do
        if i <= #id then rec[i] = string.byte(id, i) else rec[i] = 0x20 end
    end
    local function put_i32(off, val)
        if val < 0 then val = val + 0x100000000 end
        rec[off] = band(val, 0xFF)
        rec[off + 1] = band(rshift(val, 8), 0xFF)
        rec[off + 2] = band(rshift(val, 16), 0xFF)
        rec[off + 3] = band(rshift(val, 24), 0xFF)
    end
    put_i32(33, cdRemainMs or -1)
    put_i32(37, cdDurMs or -1)
    put_i32(41, castTimeMs or -1)
    rec[45] = band(flags or 0, 0xFF)
    rec[46] = band(resourceCost or 0, 0xFF)
    local n = tostring(name or "")
    if #n > 32 then n = string.sub(n, 1, 32) end
    rec[47] = band(#n, 0xFF)
    rec[48] = band(rshift(#n, 8), 0xFF)
    for i = 1, 32 do
        if i <= #n then rec[48 + i] = string.byte(n, i) else rec[48 + i] = 0 end
    end
    return string.char(unpackValues(rec, 1, ABILITY_RECORD_SIZE))
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
-- regionBytes declared above with write helpers
local region = nil         -- materialized string for scanner (updated each publish)
local sessionId = ""       -- 16-byte binary UUID
local sequence = 0
local writeBufferIndex = 0 -- 0 = Buffer A, 1 = Buffer B
local heartbeatIntervalMs = 50
local maxTelemetryAgeMs = 500
-- Publish cadence: ~10 Hz wire publish (balances freshness vs GC copies in process memory).
-- Sequence and the scannable string MUST advance together — throttling materialize while
-- still incrementing sequence caused ContinuityDegraded gaps (~20 seq) on every relocate.
local PUBLISH_INTERVAL_S = 0.10
local lastPublishTime = 0
-- Heavy inventory Inspects are slower (abilities/auras)
local INVENTORY_INTERVAL_S = 0.50
local lastInventoryTime = 0
local cachedAbilityBytes = nil
local cachedAbilityKnown = false
local cachedPlayerAuraBytes = nil
local cachedPlayerAuraKnown = false
local cachedTargetAuraBytes = nil
local cachedTargetAuraKnown = false
local cachedActionBarBytes = nil
local cachedActionBarKnown = false
-- Inspect.System.Version() returns a table { build, external, internal } — never tostring(table).
local cachedClientVersion = nil

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

-- Materialize the scannable string after every completed frame write so the
-- external scanner observes the same sequence high-water mark as regionBytes.
local function materialize_region_string()
    region = bytes_to_string(regionBytes, 1, REGION_TOTAL_SIZE)
    BotDsBridgeRegion = region
end

local function init_region()
    regionBytes = {}
    for i = 1, REGION_TOTAL_SIZE do
        regionBytes[i] = 0
    end

    for i = 1, #SENTINEL_MAGIC do
        rb_u8(SENTINEL_MAGIC_OFFSET + i, string.byte(SENTINEL_MAGIC, i))
    end
    rb_u32_le(SENTINEL_MAGIC_OFFSET + SENTINEL_TOTAL_SIZE + 1, REGION_TOTAL_SIZE)
    rb_u32_le(SENTINEL_MAGIC_OFFSET + SENTINEL_BUFFER_SLOT_SIZE + 1, BUFFER_SLOT_SIZE)

    sessionId = generate_uuid_binary()
    sequence = 0
    writeBufferIndex = 0
    lastPublishTime = 0
    lastInventoryTime = 0
    cachedAbilityBytes = nil
    cachedAbilityKnown = false
    cachedPlayerAuraBytes = nil
    cachedPlayerAuraKnown = false
    cachedTargetAuraBytes = nil
    cachedTargetAuraKnown = false
    cachedActionBarBytes = nil
    cachedActionBarKnown = false
    cachedClientVersion = nil
    materialize_region_string()
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
--   - ClientVersion: Inspect.System.Version().external (prefer), else build/internal
--   - SchemaVersion: SCHEMA_VERSION (2 = ability name + action bar)
--   - Reserved: must be 0
--
-- Always returns (section_bytes, MASK_PROVIDER_INFO) regardless of state.

-- Resolve and cache client version once per session. Live clients return a table
-- { build, external, internal }; tostring(table) yields "table: 0x..." and must
-- never be published (observed live as unstable garbage version strings).
local function resolve_client_version()
    if cachedClientVersion ~= nil then
        return cachedClientVersion
    end
    local version = ""
    local ok, ver = pcall(Inspect.System.Version)
    if ok and ver ~= nil then
        if type(ver) == "string" then
            version = ver
        elseif type(ver) == "table" then
            local external = ver.external
            local build = ver.build
            local internal = ver.internal
            if type(external) == "string" and #external > 0 then
                version = external
            elseif type(build) == "string" and #build > 0 then
                version = build
            elseif type(internal) == "string" and #internal > 0 then
                version = internal
            elseif type(external) == "number" or type(build) == "number" or type(internal) == "number" then
                -- Some clients may return numeric members; prefer external then build.
                local n = external or build or internal
                version = tostring(n)
            end
        end
    end
    -- Protocol max client version length is 128 ASCII bytes.
    if #version > 128 then
        version = string.sub(version, 1, 128)
    end
    cachedClientVersion = version
    return version
end

local function encode_provider_info()
    local buf = ""
    local producerFrameMs = math.floor((Inspect.Time.Frame() or 0) * 1000)
    local clientVersion = resolve_client_version()

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
    -- SchemaVersion: 1 byte (v2 = ability name field)
    buf = buf .. string.char(SCHEMA_VERSION)
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
    -- Returns: section_bytes, emit_section
    -- For player.target:
    --   emit_section=false → TargetKnownness.Unknown (inspection incomplete)
    --   emit_section=true + flags without IsAvailable → KnownNoTarget
    --   emit_section=true + IsAvailable → KnownTarget
    local buf = ""
    local avail = false
    local emit = false
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

    local isTarget = (unitSpecifier == "player.target")
    local ok, detail = pcall(Inspect.Unit.Detail, unitSpecifier)
    if not ok then
        -- pcall failure: unknown for targets (omit section); player stays omitted
        return "", false
    end

    if detail then
        avail = true
        emit = true
        flags = 0x04 -- UnitFlagIsAvailable
        id = tostring(detail.id or "")
        name = detail.name or ""
        level = detail.level or -1
        if detail.calling then calling = tostring(detail.calling) end
        if detail.player then
            flags = bor(flags, 0x01) -- UnitFlagIsPlayer
            if detail.pvp then flags = bor(flags, 0x08) end
        end
        if detail.combat then flags = bor(flags, 0x02) end -- UnitFlagInCombat
        if detail.relation == "hostile" then relation = 1
        elseif detail.relation == "friendly" then relation = 2
        elseif detail.relation == "neutral" then relation = 3 end
        healthCur = detail.health or -1
        healthMax = detail.healthMax or -1
        -- Resource: pick first non-nil power type (even zero values)
        if detail.mana ~= nil then
            resCur = detail.mana; resKind = "mana"
        elseif detail.energy ~= nil then
            resCur = detail.energy; resKind = "energy"
        elseif detail.power ~= nil then
            resCur = detail.power; resKind = "power"
        elseif detail.charge ~= nil then
            resCur = detail.charge; resKind = "charge"
        end
        if detail.manaMax ~= nil then resMax = detail.manaMax
        elseif detail.energyMax ~= nil then resMax = detail.energyMax
        elseif detail.powerMax ~= nil then resMax = detail.powerMax
        elseif detail.chargeMax ~= nil then resMax = detail.chargeMax end
        -- Castbar
        local cok, castbar = pcall(Inspect.Unit.Castbar, unitSpecifier)
        if cok and castbar then
            castId = tostring(castbar.abilityId or "")
            castName = castbar.name or ""
            castRemain = castbar.remaining or -1
            castDur = castbar.duration or -1
            if castbar.channel then castFlags = bor(castFlags, 0x01) end -- CastFlagIsChannel
            if castbar.uninterruptible then castFlags = bor(castFlags, 0x02) end -- CastFlagIsUninterruptible
        end
    elseif isTarget then
        -- Successful inspect with nil detail: known no selected target (M2).
        emit = true
        avail = false
        flags = 0 -- IsAvailable clear
    else
        return "", false
    end

    if not emit then
        return "", false
    end

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

    -- emit_section true even for known-no-target (avail may be false)
    return buf, emit
end

-- Build the abilities list section (PROTOCOL.md §4.3).
--
-- Ability records are fixed 80-byte schema-v2 layouts (see PROTOCOL.md §4.3).
-- RIFT Detail API: times are seconds; `unusable` is the primary usability signal
-- (there is no reliable `usable` member in Inspect.Ability.New.Detail docs).
--
-- Returns (section_bytes, is_known).
local function encode_abilities()
    local count = 0
    local isKnown = false
    local parts = {}

    local ok, ids = pcall(Inspect.Ability.New.List)
    if ok and ids then
        local complete = true
        -- ipairs may miss dict-style id maps used by some clients; also try pairs
        local iterList = ids
        if type(ids) == "table" and ids[1] == nil then
            iterList = {}
            for k, v in pairs(ids) do
                if v then iterList[#iterList + 1] = (type(k) == "string" and k) or v end
            end
        end
        for _, abilityId in ipairs(iterList) do
            if count >= 64 then
                -- Cap for FPS: full book is too expensive every inventory tick
                complete = false
                break
            end
            local dok, detail = pcall(Inspect.Ability.New.Detail, abilityId)
            if dok and detail then
                -- Available = we resolved detail for a listed ability
                local aFlags = 0x01
                local isPassive = detail.passive and true or false
                if isPassive then
                    aFlags = bor(aFlags, 0x08)
                else
                    -- usable when not marked unusable (and not passive cast)
                    if not detail.unusable then
                        aFlags = bor(aFlags, 0x02)
                    end
                end
                -- explicit usable true from events/clients that expose it
                if detail.usable == true then
                    aFlags = bor(aFlags, 0x02)
                elseif detail.usable == false then
                    aFlags = band(aFlags, 0xFD) -- clear usable bit
                end
                if not detail.outOfRange then aFlags = bor(aFlags, 0x04) end
                if detail.channeled then aFlags = bor(aFlags, 0x10) end

                local cost = 0
                if type(detail.costPower) == "number" then cost = detail.costPower
                elseif type(detail.costEnergy) == "number" then cost = detail.costEnergy
                elseif type(detail.costMana) == "number" then cost = detail.costMana
                end
                if cost < 0 then cost = 0 end
                if cost > 255 then cost = 255 end

                parts[#parts + 1] = build_ability_record(
                    detail.id or abilityId,
                    seconds_to_ms(detail.currentCooldownRemaining),
                    seconds_to_ms(detail.currentCooldownDuration or detail.cooldown),
                    seconds_to_ms(detail.castingTime),
                    aFlags,
                    cost,
                    detail.name or "")
                count = count + 1
            else
                complete = false
            end
        end
        isKnown = complete or count > 0
    end

    local header = string.char(band(count, 0xFF), band(rshift(count, 8), 0xFF))
    return header .. table.concat(parts), isKnown
end

-- Action bar observation for key calibration (does not invent keys).
-- Wire: page:u8, count:u8, then count × (slot:u8 + abilityId:32 space-padded).
local function encode_action_bar()
    local page = 0
    local pok, pval = pcall(function()
        if type(Action) == "table" and type(Action.Bar) == "table"
            and type(Action.Bar.Page) == "table" and type(Action.Bar.Page.Get) == "function" then
            return Action.Bar.Page.Get()
        end
        return nil
    end)
    if pok and type(pval) == "number" then page = math.floor(pval) end
    if page < 0 then page = 0 end
    if page > 255 then page = 255 end

    local slots = {}
    local slotCount = 0
    for slot = 1, 12 do
        local aok, action = pcall(function()
            if type(Action) == "table" and type(Action.Get) == "function" then
                return Action.Get(slot)
            end
            return nil
        end)
        local id = ""
        if aok and type(action) == "table" then
            local t = action.type
            if t == "ability" or t == nil or t == "Ability" then
                if action.id then id = tostring(action.id) end
            end
        end
        local rec = { band(slot, 0xFF) }
        for i = 1, 32 do
            if i <= #id then rec[i + 1] = string.byte(id, i) else rec[i + 1] = 0x20 end
        end
        slots[#slots + 1] = string.char(unpackValues(rec, 1, 33))
        slotCount = slotCount + 1
    end

    local header = string.char(band(page, 0xFF), band(slotCount, 0xFF))
    return header .. table.concat(slots), true
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

    local ok, buffIds = pcall(Inspect.Buff.List, unitSpecifier)
    if ok and buffIds then
        local complete = true
        for _, buffId in ipairs(buffIds) do
            if count >= 64 then
                complete = false
                break
            end
            local dok, detail = pcall(Inspect.Buff.Detail, unitSpecifier, buffId)
            if dok and detail then
                local rec = string.rep("\0", 70)
                rec = write_fixed_ascii(rec, 1, tostring(detail.buffId or buffId), 32)
                local aname = detail.name or ""
                rec = write_u16_le(rec, 33, #aname)
                for i = 1, math.min(#aname, 32) do
                    rec = write_u8(rec, 35 + i - 1, string.byte(aname, i))
                end
                rec = write_u8(rec, 67, detail.stacks or 0)
                local aFlags = 0
                if detail.debuff then aFlags = bor(aFlags, 0x01) end
                if detail.curse then aFlags = bor(aFlags, 0x02) end
                if detail.disease then aFlags = bor(aFlags, 0x04) end
                if detail.poison then aFlags = bor(aFlags, 0x08) end
                rec = write_u8(rec, 68, aFlags)
                local remain = detail.remaining or -1
                if remain >= 0 then
                    remain = band(remain, 0xFFFF)
                end
                rec = write_u16_le(rec, 69, remain)
                records = records .. rec
                count = count + 1
            else
                complete = false
            end
        end
        isKnown = complete
    end

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
local function build_payload(refreshInventory)
    local sectionsMask = 0
    local chunks = {}

    local function add_section(stype, data, mask)
        chunks[#chunks + 1] = string.char(
            band(stype, 0xFF), band(rshift(stype, 8), 0xFF),
            band(#data, 0xFF), band(rshift(#data, 8), 0xFF))
        chunks[#chunks + 1] = data
        sectionsMask = bor(sectionsMask, mask)
    end

    local providerBytes, providerMask = encode_provider_info()
    add_section(SECTION_PROVIDER_INFO, providerBytes, providerMask or MASK_PROVIDER_INFO)

    local playerBytes, playerAvail = encode_unit_state("player")
    if playerAvail then
        add_section(SECTION_PLAYER, playerBytes, MASK_PLAYER)
    end

    local targetBytes, targetAvail = encode_unit_state("player.target")
    if targetAvail then
        add_section(SECTION_TARGET, targetBytes, MASK_TARGET)
    end

    if refreshInventory then
        local abilityBytes, abilityKnown = encode_abilities()
        cachedAbilityBytes = abilityBytes
        cachedAbilityKnown = abilityKnown
        local pAuraBytes, pAuraKnown = encode_auras("player")
        cachedPlayerAuraBytes = pAuraBytes
        cachedPlayerAuraKnown = pAuraKnown
        local tAuraBytes, tAuraKnown = encode_auras("player.target")
        cachedTargetAuraBytes = tAuraBytes
        cachedTargetAuraKnown = tAuraKnown
        local barBytes, barKnown = encode_action_bar()
        cachedActionBarBytes = barBytes
        cachedActionBarKnown = barKnown
    end

    if cachedAbilityKnown and cachedAbilityBytes then
        add_section(SECTION_ABILITIES, cachedAbilityBytes, MASK_ABILITIES)
    end
    if cachedPlayerAuraKnown and cachedPlayerAuraBytes then
        add_section(SECTION_PLAYER_AURAS, cachedPlayerAuraBytes, MASK_PLAYER_AURAS)
    end
    if cachedTargetAuraKnown and cachedTargetAuraBytes then
        add_section(SECTION_TARGET_AURAS, cachedTargetAuraBytes, MASK_TARGET_AURAS)
    end
    if cachedActionBarKnown and cachedActionBarBytes then
        add_section(SECTION_ACTION_BAR, cachedActionBarBytes, MASK_ACTION_BAR)
    end

    return table.concat(chunks), sectionsMask
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
local function crc32_bytes(startPos, len1, start2, len2)
    local crc = 0xFFFFFFFF
    for i = 0, len1 - 1 do
        crc = crc32_update(crc, regionBytes[startPos + i])
    end
    for i = 0, len2 - 1 do
        crc = crc32_update(crc, regionBytes[start2 + i])
    end
    return bxor(crc, 0xFFFFFFFF)
end

local function write_frame(bufferOffset, inputReady, refreshInventory)
    sequence = sequence + 1
    local frameTime = math.floor((Inspect.Time.Frame() or 0) * 1000)
    local isSecure = false

    local ok, sec = pcall(Inspect.System.Secure)
    if ok then isSecure = sec or false end

    local payload, sectionsMask = build_payload(refreshInventory)
    local payloadLength = #payload

    if payloadLength > BUFFER_SLOT_SIZE - HEADER_SIZE then
        sequence = sequence - 1
        return
    end

    local base = bufferOffset + 1 -- 1-based index of slot start in regionBytes

    rb_u32_le(base + HDR_SEQUENCE, sequence)
    rb_u32_le(base + HDR_PRODUCER_FRAME_MS, frameTime)
    rb_u32_le(base + HDR_SECTIONS_MASK, sectionsMask)
    rb_u32_le(base + HDR_HEARTBEAT_INTERVAL, heartbeatIntervalMs)
    rb_u32_le(base + HDR_PAYLOAD_LENGTH, payloadLength)
    rb_u8(base + HDR_PROTOCOL_VERSION, PROTOCOL_VERSION)

    local flags = 0
    if sectionsMask == MASK_PROVIDER_INFO then
        flags = bor(flags, 0x01)
    end
    if isSecure then
        flags = bor(flags, 0x02)
    end
    flags = bor(flags, 0x08) -- GameInputReadyKnown
    if inputReady then
        flags = bor(flags, 0x04) -- GameInputReady
    end
    rb_u8(base + HDR_FLAGS, flags)
    rb_u16_le(base + HDR_RESERVED, 0)

    -- Payload only (CRC covers PayloadLength — no need to zero the rest of the slot)
    rb_copy_string(base + PAYLOAD_OFFSET, payload)

    rb_u32_le(base + HDR_CRC32, 0)
    local crc = crc32_bytes(base + HDR_SEQUENCE, HDR_CRC32, base + PAYLOAD_OFFSET, payloadLength)
    rb_u32_le(base + HDR_CRC32, crc)

    -- Always publish the wire image with this sequence (no deferred materialize).
    materialize_region_string()
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
local indicatorFrame = nil
local gameInputReady = true

-- Check if game input is ready (not in chat, keybind screen, or modal dialog).
-- Must not index optional UI tables at the call site: pcall(UI.Textfield.Focus)
-- still evaluates UI.Textfield before entering pcall and errors when nil.
local function check_game_input_ready()
    local ok, ready = pcall(function()
        -- Chat / edit focus (API may be absent on some client builds)
        if type(UI) == "table" and type(UI.Textfield) == "table"
            and type(UI.Textfield.Focus) == "function" then
            local focus = UI.Textfield.Focus()
            if focus then
                return false
            end
        end
        -- Optional alternate focus probe used by some clients
        if type(UI) == "table" and type(UI.Textfield) == "table"
            and type(UI.Textfield.GetFocus) == "function" then
            local focus = UI.Textfield.GetFocus()
            if focus then
                return false
            end
        end
        return true
    end)
    if not ok then
        -- Unknown input readiness: prefer ready so we still emit frames;
        -- C# treats nil/unknown separately when flags are known.
        return true
    end
    return ready and true or false
end

-- Update UI indicator color based on state
local function update_indicator()
    if not indicatorFrame then return end
    local r, g, b = 0.3, 1.0, 0.3 -- green = healthy
    if not gameInputReady then
        r, g, b = 1.0, 0.7, 0.2 -- yellow = input blocked
    end
    if not region then
        r, g, b = 1.0, 0.3, 0.3 -- red = not initialized
    end
    pcall(indicatorFrame.SetFontColor, indicatorFrame, r, g, b, 1.0)
end

local function on_update_begin()
    -- Throttle: full publish is too expensive every render frame (~60Hz+).
    local now = Inspect.Time.Frame() or 0
    if lastPublishTime > 0 and (now - lastPublishTime) < PUBLISH_INTERVAL_S then
        return
    end
    lastPublishTime = now

    gameInputReady = check_game_input_ready()

    if not regionBytes then
        init_region()
    end

    local refreshInventory = (lastInventoryTime == 0)
        or ((now - lastInventoryTime) >= INVENTORY_INTERVAL_S)
    if refreshInventory then
        lastInventoryTime = now
    end

    local bufferOffset
    if writeBufferIndex == 0 then
        bufferOffset = BUFFER_A_OFFSET
        writeBufferIndex = 1
    else
        bufferOffset = BUFFER_B_OFFSET
        writeBufferIndex = 0
    end

    write_frame(bufferOffset, gameInputReady, refreshInventory)
    update_indicator()
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

    -- Create small UI indicator showing addon status
    local ok, ctx = pcall(UI.CreateContext, "BotDsBridge")
    if ok and ctx then
        local fok, frame = pcall(UI.CreateFrame, "Text", "BotDsStatus", ctx)
        if fok and frame then
            pcall(frame.SetPoint, frame, "TOPLEFT", "UIParent", "TOPLEFT", 10, 10)
            pcall(frame.SetWidth, frame, 160)
            pcall(frame.SetHeight, frame, 20)
            pcall(frame.SetBackgroundColor, frame, 0, 0, 0, 0.6)
            pcall(frame.SetText, frame, "BotDs Bridge v" .. ADDON_VERSION)
            pcall(frame.SetFontColor, frame, 0.3, 1.0, 0.3, 1.0)
            indicatorFrame = frame
        end
    end

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

    chat_print(string.format(
        "BotDs Bridge v%s initialized (protocol v%d).",
        ADDON_VERSION, PROTOCOL_VERSION))
end

-- Entry point (RIFT addon loader calls this after loading the file)
on_addon_load()
