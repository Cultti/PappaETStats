-- Run from the repository root: lua et-server/tests/pappastats_test.lua
-- ET, HTTP and file operations are mocked; no real API requests are made.
local load_stats = assert(loadfile("et-server/pappastats.lua"))
local cvars, payload, request, response, now, writes, clients, posts
local count = 0
package.config = "/\n;\n?\n!\n-\n"
package.loaded.dkjson = {
    encode = function(value) payload = value; return "payload" end,
    decode = function() return response end,
}

local function setup(serverId, hostname, apiResponse)
    cvars = {
        pappa_stats_server_id = serverId,
        sv_hostname = hostname,
        net_ip = "2.29.8.44",
        net_port = "27960",
        mapname = "sw_goldrush_te",
        g_currentRound = "1",
        sv_maxclients = "0",
        timelimit = "12",
        g_nextTimeLimit = "12",
        gamestate = "2",
    }
    payload, request, response, now, writes = nil, nil, apiResponse, 100, {}
    clients, posts = {}, {}
    et = {
        GS_INITIALIZE = -1, CS_SERVERINFO = 0, CS_MULTI_INFO = 1, CS_MULTI_MAPWINNER = 2,
        GS_WARMUP = 2, GS_WARMUP_COUNTDOWN = 1, GS_PLAYING = 0, GS_INTERMISSION = 3,
        trap_Cvar_Get = function(key) return cvars[key] or "" end,
        trap_GetConfigstring = function(key) return key == 0 and cvars.mapname or "0" end,
        Info_ValueForKey = function(info) return info end,
        trap_GetUserinfo = function(client) return clients[client] and clients[client].guid or "" end,
        trap_Milliseconds = function() return now end,
        gentity_get = function(client, field) return clients[client] and clients[client][field] or 0 end,
        trap_SendConsoleCommand = function() end,
        RegisterModname = function() end,
        G_Print = function() end,
    }
    os.getenv = function() return nil end
    os.time = function() return 1791048934 end
    os.tmpname = function() return "/tmp/stats-test" end
    os.execute = function(command)
        if command:find("/api/ready-ups", 1, true) then posts[#posts + 1] = payload end
        return true, "exit", 0
    end
    io.open = function(path, mode)
        if mode == "w" then
            return { write = function(_, value) writes[path] = value end, close = function() end }
        end
        if path == "/etc/pappaetstats/ingest.key" then
            return { read = function() return "test-key" end, close = function() end }
        end
        return nil
    end
    io.popen = function(command)
        request = command
        return { read = function() return "response" end, close = function() return true, "exit", 0 end }
    end
    load_stats()
    et_InitGame(0, 0, 0)
end

local function test(name, run)
    run()
    count = count + 1
    print("PASS " .. name)
end

test("explicit identity is encoded in ID lookup and included in the payload", function()
    setup(" cup #1&test ", "Cup #1", { matchId = "server-one-match" })
    assert(SendStats("test-key"))
    assert(request:find("serverId=cup%20%231%26test", 1, true))
    assert(request:find("servername=Cup%20%231", 1, true))
    assert(payload.serverId == "cup #1&test")
    assert(payload.matchID == "server-one-match")
    assert(payload.serverIp == "2.29.8.44" and payload.serverPort == "27960")
end)

test("hostname distinguishes unconfigured servers sharing IP and port", function()
    for _, hostname in ipairs({ "Cup #1", "Cup #2" }) do
        setup("", hostname, { matchId = hostname })
        assert(SendStats("test-key"))
        assert(payload.serverId == hostname)
        assert(request:find("serverId=Cup%20%23" .. hostname:sub(-1), 1, true))
    end
end)

test("round two recovers its match using the same server identity after Lua reload", function()
    setup("cup-1", "Cup #1", { matchId = "round-one-match" })
    assert(SendStats("test-key"))
    -- A new Lua context loses in-memory state; the backend must recover our ID.
    setup("cup-1", "Cup #1", { matchId = "round-one-match" })
    cvars.g_currentRound = "0"
    assert(SendStats("test-key"))
    assert(request:find("round=2&serverId=cup-1", 1, true))
    assert(payload.round == 2 and payload.matchID == "round-one-match")
end)

test("fallback IDs differ across servers even with equal timestamps and uptime", function()
    local ids = {}
    for _, serverId in ipairs({ "cup-1", "cup-2" }) do
        setup(serverId, "Same hostname", { error = "API unavailable" })
        assert(SendStats("test-key"))
        assert(payload.matchID:match("^fallback%-"))
        assert(#payload.matchID <= 64)
        ids[#ids + 1] = payload.matchID
    end
    assert(ids[1] ~= ids[2])
end)

test("one Lua context reuses the same match ID across rounds", function()
    setup("cup-1", "Cup #1", { matchId = "round-one-match" })
    assert(SendStats("test-key"))
    request = nil
    cvars.g_currentRound = "0"
    assert(SendStats("test-key"))
    assert(request == nil)
    assert(payload.round == 2 and payload.matchID == "round-one-match")
end)

local function player(client, guid, team)
    clients[client] = { guid = string.rep(guid, 32), ["pers.connected"] = 2,
        ["sess.sessionTeam"] = team or 1, ["pers.ready"] = 0 }
end

local function ready(client, command)
    assert(et_ClientCommand(client, command or "ready") == 0)
    clients[client]["pers.ready"] = 1
end

local function countdown()
    cvars.gamestate = tostring(et.GS_WARMUP_COUNTDOWN)
    et_RunFrame(now)
end

test("last accepted ready command wins even in the same frame, regardless of slot", function()
    setup("cup-1", "Cup", {})
    player(5, "a"); player(1, "b")
    ready(5); ready(1, "readytoggle")
    countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("B", 32))
    assert(posts[1].readyAtUnix == 1791048934 and posts[1].countdownAtUnix == 1791048934)
    assert(posts[1].round == 2 and posts[1].serverId == "cup-1")
    et_RunFrame(now)
    assert(#posts == 1)
end)

test("repeated ready commands and rejected spectator commands do not win", function()
    setup("cup-1", "Cup", {})
    player(0, "a"); player(1, "b"); player(2, "c", 3)
    ready(0); ready(1); ready(0); ready(2)
    countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("B", 32))
end)

test("unready and disconnected players are removed from ready order", function()
    setup("cup-1", "Cup", {})
    player(0, "a"); player(1, "b"); player(2, "c")
    ready(0); ready(1); ready(2)
    et_ClientCommand(1, "unready"); clients[1]["pers.ready"] = 0
    et_ClientDisconnect(2); clients[2] = nil
    countdown()
    assert(#posts == 1 and posts[1].playerGuid == string.rep("A", 32))
end)

test("rejected ready commands, invalid GUIDs and forced countdowns have no event", function()
    setup("cup-1", "Cup", {})
    player(0, "a")
    et_ClientCommand(0, "ready") -- Engine leaves the player unready.
    player(1, "z"); ready(1)
    countdown()
    assert(#posts == 0)
end)

test("cancelled countdown starts fresh and both rounds can report", function()
    setup("cup-1", "Cup", { matchId = "match" })
    cvars.g_currentRound = "0"
    player(0, "a"); ready(0); countdown()
    assert(#posts == 1 and posts[1].round == 1)
    cvars.gamestate = tostring(et.GS_WARMUP); et_RunFrame(now)
    countdown() -- No new ready command since cancellation.
    assert(#posts == 1)
    cvars.gamestate = tostring(et.GS_WARMUP); et_RunFrame(now)
    cvars.g_currentRound = "1"
    clients[0]["pers.ready"] = 0
    ready(0); countdown()
    assert(#posts == 2 and posts[2].round == 2)
    assert(posts[1].eventId ~= posts[2].eventId)
end)

test("ready tracking does no player checks outside warmup and countdown", function()
    setup("cup-1", "Cup", {})
    player(0, "a"); ready(0) -- Leave confirmation pending.
    et.gentity_get = function() error("Unexpected ready tracking player check") end
    et.trap_GetUserinfo = function() error("Unexpected ready tracking GUID lookup") end
    for _, gamestate in ipairs({ et.GS_PLAYING, et.GS_INTERMISSION, et.GS_INITIALIZE }) do
        cvars.gamestate = tostring(gamestate)
        et_RunFrame(now)
        assert(et_ClientCommand(0, "ready") == 0)
    end
    assert(#posts == 0)
end)

print(string.format("%d PappaStats tests passed", count))
