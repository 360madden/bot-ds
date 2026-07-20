-- BotDsBridge V5 Protocol Emitter
-- Skeleton producing the V5 double-buffer telemetry envelope.
-- Published APIs used: Inspect.Time.Frame, Inspect.System.Version,
-- Inspect.System.Secure, Inspect.Unit.Detail, Inspect.Ability.New.Detail,
-- Inspect.Buff.Detail.
-- All other game-state functions are stubbed and marked TODO for
-- conformance testing against the current gamigo client.

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

-- Buffer A at offset 16, Buffer B at offset 8208
local BUFFER_A_OFFSET = 16
local BUFFER_B_OFFSET = 16 + BUFFER_SLOT_SIZE

----------------------------------------------------------------------
-- CRC32 (CRC-32/ISO-HDLC: polynomial 0xEDB88320 reflected)
----------------------------------------------------------------------
local UINT32 = 4294967296

local function normalize_u32(value)
    return math.floor(value) % UINT32
end

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

local function bor(left, right)
    return normalize_u32(left + right - band(left, right))
end

local function rshift(value, count)
    return math.floor(normalize_u32(value) / (2 ^ count))
end

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

local function crc32_update(crc, byte)
    local index = band(bxor(crc, byte), 0xFF)
    return bxor(band(rshift(crc, 8), 0x00FFFFFF), crc32_table[index])
end

-- Compute CRC32 over a range of bytes in the region string.
-- startOffset and length are byte positions in the string.
local function crc32_region(region, startOffset, length)
    local crc = 0xFFFFFFFF
    for i = 1, length do
        local b = string.byte(region, startOffset + i)
        crc = crc32_update(crc, b)
    end
    return bxor(crc, 0xFFFFFFFF)
end

-- Compute CRC32 over two concatenated ranges
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
-- Little-endian write helpers (write into string at 1-based byte index)
----------------------------------------------------------------------
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
    -- Convert signed int32 to unsigned bit pattern
    if val < 0 then val = val + 0x100000000 end
    return write_u32_le(str, pos, val)
end

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
local function generate_uuid_binary()
    -- Generate a random 16-byte UUID (v4 style).
    -- RIFT addon environment may have math.random; we build a simple
    -- random source from Inspect.Time.Frame() and entity IDs when available.
    local t = Inspect.Time.Frame() or 0
    local bytes = {}
    for i = 1, 16 do
        -- Mix frame time, index, and constant to produce reasonably unique bytes
        local v = normalize_u32((t * 2654435761) + (i * 1836311903) + 0x9E3779B9)
        bytes[i] = band(rshift(v, (i % 4) * 8), 0xFF)
        t = t + 1
    end
    -- Set version 4 (random) and variant 1
    bytes[7] = bor(band(bytes[7], 0x0F), 0x40)
    bytes[9] = bor(band(bytes[9], 0x3F), 0x80)
    local unpackValues = unpack or table.unpack
    return string.char(unpackValues(bytes))
end

local function init_region()
    -- Allocate the full region as a Lua string
    region = string.rep("\0", REGION_TOTAL_SIZE)

    -- Write sentinel
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
----------------------------------------------------------------------

-- Encode a length-prefixed ASCII string at current write offset
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

-- Encode ProviderInfo section. Returns (section_bytes, mask_bit_set)
local function encode_provider_info()
    local buf = ""
    -- Fake wall clock: use frame time as an approximation
    -- (os.time may not be available; frame time is monotonic)
    local producerFrameMs = math.floor((Inspect.Time.Frame() or 0) * 1000)

    local clientVersion = ""
    -- TODO: Inspect.System.Version() returns client build info
    -- local ver = Inspect.System.Version()
    -- if ver then clientVersion = ver .. "" end

    -- Section data layout (offsets from section data start):
    --  0:16 SessionId
    -- 16:4  ProducerFrameMs (uint32 LE)
    -- 20:4  MaxTelemetryAgeMs (uint32 LE)
    -- 24:2  ClientVersionLength
    -- 26:N  ClientVersion
    -- 26+N:1 SchemaVersion
    -- 27+N:1 Reserved
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

-- Encode a unit state section (Player or Target)
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

-- Encode abilities list section
local function encode_abilities()
    local buf = ""
    local count = 0
    local records = ""

    -- TODO: Use Inspect.Ability.New.List() and Inspect.Ability.New.Detail()
    -- local ids = Inspect.Ability.New.List()
    -- if ids then
    --     for _, abilityId in ipairs(ids) do
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
    --             if count >= 128 then break end
    --         end
    --     end
    -- end

    buf = write_u16_le(buf, 1, count)
    buf = buf .. records
    return buf, count > 0
end

-- Encode auras list section
local function encode_auras(unitSpecifier)
    local buf = ""
    local count = 0
    local records = ""

    -- TODO: Use Inspect.Buff.List(unitSpecifier) and Inspect.Buff.Detail()
    -- local buffIds = Inspect.Buff.List(unitSpecifier)
    -- if buffIds then
    --     for _, buffId in ipairs(buffIds) do
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
    --             if count >= 64 then break end
    --         end
    --     end
    -- end

    buf = write_u16_le(buf, 1, count)
    buf = buf .. records
    return buf, count > 0
end

----------------------------------------------------------------------
-- Frame emission
----------------------------------------------------------------------

-- Build the complete payload (section data) for a frame.
-- Returns payload string and sectionsMask.
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

    -- Encode sections as TLV
    for _, section in ipairs(sections) do
        payload = write_u16_le(payload, #payload + 1, section.type)
        payload = write_u16_le(payload, #payload + 1, #section.data)
        payload = payload .. section.data
        sectionsMask = bor(sectionsMask, section.mask)
    end

    return payload, sectionsMask
end

-- Write one frame into a specific buffer slot (offset within the region string).
-- Returns the updated region string.
local function write_frame(region, bufferOffset)
    sequence = sequence + 1
    local frameTime = math.floor((Inspect.Time.Frame() or 0) * 1000)
    local isSecure = false

    -- TODO: Inspect.System.Secure()
    -- isSecure = Inspect.System.Secure() or false

    local payload, sectionsMask = build_payload()
    local payloadLength = #payload

    if payloadLength > BUFFER_SLOT_SIZE - HEADER_SIZE then
        -- Payload too large; emit minimal provider-only frame
        local provBytes, provMask = encode_provider_info()
        payload = write_u16_le("", 1, SECTION_PROVIDER_INFO)
        payload = write_u16_le(payload, 3, #provBytes)
        payload = payload .. provBytes
        payloadLength = #payload
        sectionsMask = provMask
    end

    -- Write header
    region = write_u32_le(region, bufferOffset + HDR_SEQUENCE + 1, sequence)
    region = write_u32_le(region, bufferOffset + HDR_PRODUCER_FRAME_MS + 1, frameTime)
    region = write_u32_le(region, bufferOffset + HDR_SECTIONS_MASK + 1, sectionsMask)
    region = write_u32_le(region, bufferOffset + HDR_HEARTBEAT_INTERVAL + 1, heartbeatIntervalMs)
    region = write_u32_le(region, bufferOffset + HDR_PAYLOAD_LENGTH + 1, payloadLength)
    region = write_u8(region, bufferOffset + HDR_PROTOCOL_VERSION + 1, PROTOCOL_VERSION)

    local flags = 0
    -- Mark as heartbeat if only provider info present (no game state sections)
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
----------------------------------------------------------------------
local function on_update_begin()
    if not region then
        init_region()
    end

    -- Alternate buffer: 0 -> A (offset 16), 1 -> B (offset 8208)
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
