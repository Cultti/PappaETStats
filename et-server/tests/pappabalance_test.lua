-- Run from the repository root: lua et-server/tests/pappabalance_test.lua
-- ET and HTTP/file operations are mocked; no real players or bot are contacted.
local load_balance = assert(loadfile("et-server/pappabalance.lua"))
local clients, chats, files, commands, cvars, payload, response, message, now, mode
local count = 0
local guidA = string.rep("A", 32)
local guidB = string.rep("B", 32)
package.config = "/\n;\n?\n!\n-\n"
package.loaded.dkjson = {
    encode = function(value) payload = value; return "payload" end,
    decode = function(value) if value == "response" then return response end end
}

local function setup()
    clients = {
        [0] = { guid = guidA, team = 1, connected = 2, ref = 0, name = "Alice" },
        [1] = { guid = guidB, team = 2, connected = 2, ref = 0, name = "Bob" }
    }
    chats, files, commands = {}, {}, {}
    cvars = { gamestate = "2", g_currentRound = "0", sv_maxclients = "2" }
    payload, response, message, now, mode = nil, nil, "", 100, "say"
    et = {
        GS_WARMUP = 2, EXEC_NOW = 0,
        trap_GetUserinfo = function(slot) return clients[slot].guid end,
        Info_ValueForKey = function(info) return info end,
        trap_Cvar_Get = function(key) return cvars[key] end,
        gentity_get = function(slot, key)
            local fields = { ["pers.connected"] = "connected", ["sess.sessionTeam"] = "team",
                ["sess.referee"] = "ref", ["pers.netname"] = "name" }
            return clients[slot][fields[key]]
        end,
        trap_SendServerCommand = function(slot, text) table.insert(chats, { slot = slot, text = text }) end,
        trap_Argv = function() return mode end,
        ConcatArgs = function() return message end,
        RegisterModname = function() end,
        G_Print = function() end,
        trap_SendConsoleCommand = function() end,
        G_globalSound = function() end
    }
    os.time = function() return now end
    os.tmpname = function() return "/tmp/voice-test" end
    os.remove = function(path) files[path] = nil end
    os.execute = function(command) table.insert(commands, command); return 0 end
    io.popen = function() error("Network must never block the game thread") end
    io.open = function(path, access)
        if access == "w" then
            return { write = function(_, data) files[path] = data end, close = function() end }
        end
        if files[path] == nil then return nil end
        return { read = function() return files[path] end, close = function() end }
    end
    load_balance()
    et_InitGame(0, 0, 0)
end

local function chat(slot, text)
    message = text
    return et_ClientCommand(slot, mode)
end

local function contains(text, slot)
    for _, item in ipairs(chats) do
        if (slot == nil or item.slot == slot) and item.text:find(text, 1, true) then return true end
    end
    return false
end

local function complete(results)
    response = { results = results }
    files["/tmp/voice-test.body"] = "response"
    files["/tmp/voice-test.status"] = "200"
    files["/tmp/voice-test.done"] = "0"
    et_RunFrame(1000)
end

local function test(name, run)
    setup()
    run()
    count = count + 1
    print("PASS " .. name)
end

test("self move preserves chat and only sends the caller's current team", function()
    assert(chat(1, "!voice") == 0)
    assert(#payload.players == 1)
    assert(payload.players[1].guid == guidB and payload.players[1].team == "allies")
    assert(#commands == 1 and commands[1]:sub(-1) == "&")
    assert(commands[1]:find("--max-time 15", 1, true))
    complete({ { guid = guidB, team = "allies", moved = true, message = "Moved to Blue room." } })
    assert(contains("Moved to Blue room.", 1))
    assert(not contains("Moved to Blue room.", 0))
    assert(files["/tmp/voice-test.body"] == nil)
end)

test("voice commands are denied during play, countdown, intermission and round two", function()
    for _, state in ipairs({ "0", "1", "3" }) do
        cvars.gamestate = state
        assert(chat(0, "!voice") == 0)
        assert(chat(0, "!voiceall") == 0)
    end
    cvars.gamestate = "2"
    cvars.g_currentRound = "1"
    chat(0, "!voice")
    assert(#commands == 0)
end)

test("missing game state fails closed", function()
    cvars.gamestate = nil
    chat(0, "!voice")
    assert(#commands == 0)
end)

test("non-referee cannot use voiceall and command remains visible", function()
    assert(chat(0, "!voiceall") == 0)
    assert(#commands == 0)
    assert(contains("Only referees", 0))
end)

test("referee voiceall includes both teams and publicly names the referee from team chat", function()
    clients[0].ref = 1
    mode = "say_team"
    assert(chat(0, "!voiceall") == 0)
    assert(#payload.players == 2)
    assert(contains("Alice^7 used !voiceall", -1))
    complete({
        { guid = guidA, team = "axis", moved = true, message = "Moved to Red room." },
        { guid = guidB, team = "allies", moved = false, message = "Not registered. Join Blue room. Register at https://et.aukko.net" }
    })
    assert(contains("Moved to Red room.", 0))
    assert(contains("https://et.aukko.net", 1))
    assert(contains("1/2 moves confirmed", 0))
end)

test("spectators cannot self-move but spectator referees can move active players", function()
    clients[0].team = 3
    chat(0, "!voice")
    assert(#commands == 0)
    clients[0].ref = 2
    chat(0, "!voiceall")
    assert(#payload.players == 1 and payload.players[1].guid == guidB)
end)

test("duplicate and overlapping requests are rejected", function()
    chat(0, "!voice")
    chat(0, "!voice")
    clients[1].ref = 1
    chat(1, "!voiceall")
    assert(#commands == 1)
    assert(contains("already in progress", 1))
end)

test("balance waits for pending voice moves to keep team assignments consistent", function()
    chat(0, "!voice")
    assert(chat(1, "!balance") == 1)
    assert(contains("wait for pending voice moves", -1))
end)

test("late reply is not delivered to a replacement player in the same slot", function()
    chat(0, "!voice")
    chats = {}
    clients[0].guid = string.rep("C", 32)
    complete({ { guid = guidA, team = "axis", moved = true, message = "Moved." } })
    assert(#chats == 0)
end)

test("team change during request produces a warning instead of a stale confirmation", function()
    chat(0, "!voice")
    clients[0].team = 2
    complete({ { guid = guidA, team = "axis", moved = true, message = "Moved to Red room." } })
    assert(contains("Your team changed", 0))
    assert(not contains("Moved to Red room.", 0))
end)

test("timeouts clear pending request without blocking frames", function()
    chat(0, "!voice")
    et_RunFrame(1000)
    assert(not contains("timed out", 0))
    now = now + 21
    et_RunFrame(2000)
    assert(contains("timed out", 0))
    chat(0, "!voice")
    assert(#commands == 2)
end)

test("invalid JSON is reported and never claims success", function()
    chat(0, "!voice")
    files["/tmp/voice-test.body"] = "invalid"
    files["/tmp/voice-test.status"] = "200"
    files["/tmp/voice-test.done"] = "0"
    et_RunFrame(1000)
    assert(contains("invalid response", 0))
end)

test("voice command matching is exact", function()
    chat(0, "!voiceallplease")
    chat(0, "!voice somebody")
    assert(#commands == 0)
end)

print(string.format("%d Lua tests passed", count))
