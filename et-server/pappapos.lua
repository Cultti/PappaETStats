local modname = "PappaPos"
local version = "1.0"

-- Store player positions
local savedPositions = {}

local function log(message)
    et.G_Print(string.format("^2[%s]^7 %s\n", modname, tostring(message)))
end

function et_InitGame(levelTime, randomSeed, restart)
    et.RegisterModname(modname .. " " .. version)
    log("loaded")
end

local function safe_number(v)
    return tonumber(v) or 0
end

function et_ClientCommand(clientNum, command)
    --log("Called")
    local gamestate = safe_number(et.trap_Cvar_Get("gamestate"))
    if (gamestate ~= et.GS_WARMUP and gamestate ~= et.GS_WARMUP_COUNTDOWN) then
        --log("Wrong gamestate: " .. gamestate)
        return 0
    end

    --log("Command: " .. command)

    if command == "savepos" then
        savedPositions[clientNum] = {
            origin = et.gentity_get(clientNum, "ps.origin"),
            angles = et.gentity_get(clientNum, "ps.viewangles")
        }
        et.trap_SendServerCommand(clientNum, "cp \"Position saved!\"")
        return 1
    end
    
    if command == "loadpos" then
        local saved = savedPositions[clientNum]
        if saved then
            et.gentity_set(clientNum, "ps.velocity", {0, 0, 0}) -- Stop player movement
            et.gentity_set(clientNum, "ps.origin", saved.origin)
            et.gentity_set(clientNum, "ps.viewangles", saved.angles)
            et.trap_SendServerCommand(clientNum, "cp \"Position loaded!\"")
        else
            et.trap_SendServerCommand(clientNum, "cp \"No saved position!\"")
        end
        return 1
    end
end