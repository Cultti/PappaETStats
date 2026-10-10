-- Run from the repository root: lua et-server/tests/pappaready_test.lua
-- ET, shell and file operations are mocked; no API requests are made.
local load_ready = assert(loadfile("et-server/pappaready.lua"))
local cvars, clients, posts, commands, removed, files, payload, key, now
local launchResult, writeFailure, closeFailure, tempSequence
local count = 0
local encoder = { encode = function(value) payload = value; return "json-payload" end }

local function setup(overrides)
    cvars = {
        pappa_stats_server_id = "cup-1", sv_hostname = "Cup", sv_maxclients = "64",
        mapname = "sw_goldrush_te", g_currentRound = "0", gamestate = "2"
    }
    for name, value in pairs(overrides or {}) do cvars[name] = value end
    clients, posts, commands, removed, files = {}, {}, {}, {}, {}
    payload, key, now, tempSequence = nil, "test-key\n", 1791048934, 0
    launchResult, writeFailure, closeFailure = true, false, false
    package.loaded.dkjson = encoder
    et = {
        GS_INITIALIZE = -1, GS_WARMUP = 2, GS_WARMUP_COUNTDOWN = 1,
        GS_PLAYING = 0, GS_INTERMISSION = 3,
        trap_Cvar_Get = function(name) return cvars[name] or "" end,
        trap_Cvar_Set = function(name, value) cvars[name] = value end,
        trap_GetUserinfo = function(client) return clients[client] and clients[client].guid or "" end,
        Info_ValueForKey = function(info) return info end,
        trap_Milliseconds = function() return 100 end,
        gentity_get = function(client, field) return clients[client] and clients[client][field] or 0 end,
        RegisterModname = function() end, G_Print = function() end
    }
    os.time = function() return now end
    os.tmpname = function()
        tempSequence = tempSequence + 1
        return "/tmp/ready 'test-" .. tempSequence
    end
    os.remove = function(path) removed[#removed + 1] = path; return true end
    os.execute = function(command)
        commands[#commands + 1] = command
        posts[#posts + 1] = payload
        return launchResult
    end
    io.open = function(path, mode)
        if mode == "w" then
            return {
                write = function(self, value)
                    if writeFailure then return nil, "disk full" end
                    files[path] = value
                    return self
                end,
                close = function() if closeFailure then return nil, "disk full" end; return true end
            }
        end
        files.keyPath = path
        if key == nil then return nil end
        return { read = function() return key end, close = function() return true end }
    end
    load_ready()
    et_InitGame(0, 0, 0)
end

local function player(slot, guid, team)
    clients[slot] = { guid = string.rep(guid, 32), ["pers.connected"] = 2,
        ["sess.sessionTeam"] = team or 1, ["pers.ready"] = 0 }
end

local function ready(slot, command)
    assert(et_ClientCommand(slot, command or "ready") == 0)
    clients[slot]["pers.ready"] = 1 -- Simulate the engine accepting the command.
end

local function countdown()
    cvars.gamestate = "1"
    et_RunFrame(100)
end

local function test(name, run)
    run()
    count = count + 1
    print("PASS " .. name)
end

test("last accepted ready command wins in the same frame, regardless of slot", function()
    setup()
    player(5, "a"); player(1, "b", 2)
    ready(5); now = now + 1; ready(1, "READYTOGGLE"); now = now + 3
    countdown(); et_RunFrame(101)
    assert(#posts == 1)
    local p = posts[1]
    assert(p.playerGuid == string.rep("B", 32) and p.serverId == "cup-1")
    assert(p.readyAtUnix == 1791048935 and p.countdownAtUnix == 1791048938)
    assert(p.round == 1 and p.mapName == "sw_goldrush_te" and #p.eventId <= 64)
end)

test("repeated ready and spectator commands do not win", function()
    setup()
    player(0, "a"); player(1, "b"); player(2, "c", 3)
    ready(0); ready(1); ready(0); ready(2)
    countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("B", 32))
end)

test("unready, disconnect, spectator changes and reused slots remove candidates", function()
    setup()
    for slot = 0, 4 do player(slot, string.char(97 + slot)); ready(slot) end
    et_ClientCommand(1, "unready"); clients[1]["pers.ready"] = 0
    et_ClientDisconnect(2); clients[2] = nil
    clients[3]["sess.sessionTeam"] = 3
    player(4, "f"); clients[4]["pers.ready"] = 1
    countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("A", 32))
end)

test("rejected commands, invalid GUIDs and forced countdowns produce no event", function()
    setup()
    player(0, "a"); assert(et_ClientCommand(0, "ready") == 0) -- Rejected by engine.
    player(1, "z"); ready(1)
    countdown()
    assert(#posts == 0)
    setup(); countdown(); assert(#posts == 0)
end)

test("unready then ready again becomes the last ready player", function()
    setup()
    player(0, "a"); player(1, "b"); ready(0); ready(1)
    et_ClientCommand(0, "readytoggle"); clients[0]["pers.ready"] = 0
    ready(0, "readytoggle"); countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("A", 32))
end)

test("cancelled countdown clears tracking and the second round can report", function()
    setup()
    player(0, "a"); ready(0); countdown()
    cvars.gamestate = "2"; et_RunFrame(100); countdown()
    assert(#posts == 1)
    cvars.gamestate = "2"; et_RunFrame(100)
    cvars.g_currentRound = "1"; clients[0]["pers.ready"] = 0
    ready(0); countdown()
    assert(#posts == 2 and posts[2].round == 2 and posts[1].eventId ~= posts[2].eventId)
end)

test("event IDs differ across VM restarts and servers at identical times", function()
    setup(); player(0, "a"); ready(0); countdown()
    local first = posts[1].eventId
    cvars.gamestate = "2"; load_ready(); et_InitGame(100, 0, 1)
    clients[0]["pers.ready"] = 0; ready(0); countdown()
    assert(posts[2].eventId ~= first)
    setup({ pappa_stats_server_id = "cup-2" }); player(0, "a"); ready(0); countdown()
    assert(posts[1].eventId ~= first)
end)

test("ready tracking does no player checks during play or intermission", function()
    setup(); player(0, "a"); ready(0)
    et.gentity_get = function() error("Unexpected player lookup") end
    et.trap_GetUserinfo = function() error("Unexpected GUID lookup") end
    for _, state in ipairs({ "0", "3", "-1" }) do
        cvars.gamestate = state; et_RunFrame(100)
        assert(et_ClientCommand(0, "ready") == 0)
    end
    assert(#posts == 0)
end)

test("relative key path and existing hostname fallback are supported", function()
    setup({ pappa_api_key_file = " keys/ingest.key ", fs_homepath = "/srv/et/",
        fs_game = "legacy", pappa_stats_server_id = "" })
    assert(files.keyPath == "/srv/et/legacy/keys/ingest.key")
    player(0, "a"); ready(0); countdown()
    assert(posts[1].serverId == "Cup")
    assert(commands[1]:find("Authorization: Bearer test-key'", 1, true))
end)

test("missing, empty and invalid keys disable sending", function()
    for _, value in ipairs({ false, " \n", "key\ninjection" }) do
        setup(); key = value ~= false and value or nil; et_InitGame(0, 0, 0)
        player(0, "a"); ready(0); countdown()
        assert(#posts == 0)
    end
end)

test("background curl quotes paths and cleans up after all retries", function()
    setup(); player(0, "a"); ready(0); countdown()
    local command = commands[1]
    assert(#commands == 1 and command:find("--retry 3", 1, true))
    assert(command:find("--data-binary '@/tmp/ready '\\''test-1'", 1, true))
    assert(command:find("; rm -f '/tmp/ready '\\''test-1' )", 1, true))
    assert(command:sub(-1) == "&" and #removed == 0)
end)

test("failed file writes, closes and shell launches clean up temporary files", function()
    for _, failure in ipairs({ "write", "close", "launch" }) do
        setup()
        writeFailure, closeFailure = failure == "write", failure == "close"
        if failure == "launch" then launchResult = false end
        player(0, "a"); ready(0); countdown()
        assert(#removed == 1)
        if failure ~= "launch" then assert(#commands == 0) end
    end
end)

print(string.format("%d PappaReady tests passed", count))
