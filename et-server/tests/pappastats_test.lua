-- Run from the repository root: lua et-server/tests/pappastats_test.lua
-- ET, HTTP and file operations are mocked; no real API requests are made.
local load_stats = assert(loadfile("et-server/pappastats.lua"))
local cvars, payload, request, response, now, writes
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
    }
    payload, request, response, now, writes = nil, nil, apiResponse, 100, {}
    et = {
        GS_INITIALIZE = -1, CS_SERVERINFO = 0, CS_MULTI_INFO = 1, CS_MULTI_MAPWINNER = 2,
        trap_Cvar_Get = function(key) return cvars[key] or "" end,
        trap_GetConfigstring = function(key) return key == 0 and cvars.mapname or "0" end,
        Info_ValueForKey = function(info) return info end,
        trap_GetUserinfo = function() return "" end,
        trap_Milliseconds = function() return now end,
        gentity_get = function() return 0 end,
        trap_SendConsoleCommand = function() end,
        RegisterModname = function() end,
        G_Print = function() end,
    }
    os.getenv = function() return nil end
    os.time = function() return 1791048934 end
    os.tmpname = function() return "/tmp/stats-test" end
    os.execute = function() return true, "exit", 0 end
    io.open = function(path, mode)
        if mode == "w" then
            return { write = function(_, value) writes[path] = value end, close = function() end }
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

print(string.format("%d PappaStats tests passed", count))
