-- BotDsConformance — M1 API Surface Probe
-- Non-actuating addon that inspects every relevant RIFT API and dumps
-- structured results to the client log via print(). Run /reloadui to trigger
-- a fresh inspection, then check the RIFT client log file.
--
-- This addon does NOT publish telemetry or interact with any external tool.
-- It only reads RIFT state and prints to the client log.

local function safe(name, fn)
    local ok, result = pcall(fn)
    if not ok then
        print(string.format("[BotDsConformance] ERROR %s: %s", name, tostring(result)))
        return nil, tostring(result)
    end
    return result, nil
end

local function inspect_all()
    print("=== BotDsConformance M1 Probe ===")
    print(string.format("FrameTime: %s", tostring(Inspect.Time.Frame() or "nil")))

    -- Runtime environment
    print(string.format("LuaJIT: %s", tostring(type(jit) ~= "nil" and jit.version or "NOT present")))
    print(string.format("FFI: %s", tostring(type(ffi) ~= "nil" and "available" or "NOT present")))
    print(string.format("bit library: %s", tostring(type(bit) ~= "nil" and "available" or "NOT present")))
    local gcCount = collectgarbage("count")
    print(string.format("GC memory (KB): %s", tostring(gcCount)))

    -- System info
    local ver, verErr = safe("System.Version", Inspect.System.Version)
    print(string.format("ClientVersion: %s (err=%s)", tostring(ver), tostring(verErr)))

    local secure, secErr = safe("System.Secure", Inspect.System.Secure)
    print(string.format("SecureMode: %s (err=%s)", tostring(secure), tostring(secErr)))

    -- Player unit
    local player, pErr = safe("Unit.Detail(player)", function() return Inspect.Unit.Detail("player") end)
    if player then
        print("--- Player ---")
        for k, v in pairs(player) do
            print(string.format("  player.%s = %s (type=%s)", k, tostring(v), type(v)))
        end
        -- Castbar
        local cast, cErr = safe("Unit.Castbar(player)", function() return Inspect.Unit.Castbar("player") end)
        if cast then
            print("  --- Castbar ---")
            for k, v in pairs(cast) do
                print(string.format("    castbar.%s = %s (type=%s)", k, tostring(v), type(v)))
            end
        else
            print(string.format("  castbar: nil (err=%s)", tostring(cErr)))
        end
    else
        print(string.format("Player: nil (err=%s)", tostring(pErr)))
    end

    -- Target
    local target, tErr = safe("Unit.Detail(player.target)", function() return Inspect.Unit.Detail("player.target") end)
    if target then
        print("--- Target ---")
        for k, v in pairs(target) do
            print(string.format("  target.%s = %s (type=%s)", k, tostring(v), type(v)))
        end
        -- Target castbar
        local tcast, tcErr = safe("Unit.Castbar(player.target)", function() return Inspect.Unit.Castbar("player.target") end)
        if tcast then
            print("  --- Target Castbar ---")
            for k, v in pairs(tcast) do
                print(string.format("    tcastbar.%s = %s (type=%s)", k, tostring(v), type(v)))
            end
        end
    else
        print(string.format("Target: nil/invalid (err=%s)", tostring(tErr)))
    end

    -- Abilities
    local abilityIds, aErr = safe("Ability.New.List", Inspect.Ability.New.List)
    if abilityIds then
        local count = 0
        for _ in pairs(abilityIds) do count = count + 1 end
        print(string.format("Abilities: %d total", count))
        local sampled = 0
        for _, id in ipairs(abilityIds) do
            if sampled >= 5 then break end
            local detail, dErr = safe("Ability.New.Detail(" .. tostring(id) .. ")", function() return Inspect.Ability.New.Detail(id) end)
            if detail then
                print(string.format("  ability[%s]:", tostring(id)))
                for k, v in pairs(detail) do
                    print(string.format("    %s = %s (type=%s)", k, tostring(v), type(v)))
                end
            else
                print(string.format("  ability[%s]: ERROR %s", tostring(id), tostring(dErr)))
            end
            sampled = sampled + 1
        end
    else
        print(string.format("Abilities: nil (err=%s)", tostring(aErr)))
    end

    -- Player auras
    local pBuffs, pbErr = safe("Buff.List(player)", function() return Inspect.Buff.List("player") end)
    if pBuffs then
        local count = 0
        for _ in pairs(pBuffs) do count = count + 1 end
        print(string.format("PlayerBuffs: %d total", count))
        local sampled = 0
        for _, id in ipairs(pBuffs) do
            if sampled >= 3 then break end
            local detail, dErr = safe("Buff.Detail(player," .. tostring(id) .. ")", function() return Inspect.Buff.Detail("player", id) end)
            if detail then
                print(string.format("  playerBuff[%s]:", tostring(id)))
                for k, v in pairs(detail) do
                    print(string.format("    %s = %s (type=%s)", k, tostring(v), type(v)))
                end
            end
            sampled = sampled + 1
        end
    else
        print(string.format("PlayerBuffs: nil (err=%s)", tostring(pbErr)))
    end

    -- Target auras
    if target then
        local tBuffs, tbErr = safe("Buff.List(player.target)", function() return Inspect.Buff.List("player.target") end)
        if tBuffs then
            local count = 0
            for _ in pairs(tBuffs) do count = count + 1 end
            print(string.format("TargetBuffs: %d total", count))
            local sampled = 0
            for _, id in ipairs(tBuffs) do
                if sampled >= 3 then break end
                local detail, dErr = safe("Buff.Detail(player.target," .. tostring(id) .. ")", function() return Inspect.Buff.Detail("player.target", id) end)
                if detail then
                    print(string.format("  targetBuff[%s]:", tostring(id)))
                    for k, v in pairs(detail) do
                        print(string.format("    %s = %s (type=%s)", k, tostring(v), type(v)))
                    end
                end
                sampled = sampled + 1
            end
        else
            print(string.format("TargetBuffs: nil (err=%s)", tostring(tbErr)))
        end
    end

    -- Action bars
    print("--- Action Bars ---")
    local page, pErr = safe("Action.Bar.Page.Get", Action.Bar.Page.Get)
    print(string.format("CurrentPage: %s (err=%s)", tostring(page), tostring(pErr)))

    for slot = 1, 12 do
        local action, aErr = safe("Action.Get(" .. slot .. ")", function() return Action.Get(slot) end)
        if action then
            print(string.format("  slot[%d]: type=%s id=%s", slot, tostring(action.type), tostring(action.id)))
        else
            print(string.format("  slot[%d]: nil/empty (err=%s)", slot, tostring(aErr)))
        end
    end

    -- Unit list
    local units, uErr = safe("Unit.List", Inspect.Unit.List)
    if units then
        local count = 0
        for _ in pairs(units) do count = count + 1 end
        print(string.format("VisibleUnits: %d total", count))
    else
        print(string.format("VisibleUnits: nil (err=%s)", tostring(uErr)))
    end

    print("=== End Conformance Probe ===")
end

-- Event handler
local function on_load()
    print("[BotDsConformance] Addon loaded — waiting for first frame")
    -- Inspection deferred to on_frame (first rendered frame)
end

-- Attach to System.Update.Begin for the first frame only
local ran = false
local function on_frame()
    if not ran then
        ran = true
        -- Run on first rendered frame to ensure all APIs are initialized
        inspect_all()
    end
end

if type(Command) == "table"
    and type(Command.Event) == "table"
    and type(Command.Event.Attach) == "function"
    and type(Event) == "table"
    and type(Event.System) == "table"
    and type(Event.System.Update) == "table"
    and Event.System.Update.Begin ~= nil then
    pcall(Command.Event.Attach, Event.System.Update.Begin, on_frame, "BotDsConformance.onFrame")
end

on_load()
