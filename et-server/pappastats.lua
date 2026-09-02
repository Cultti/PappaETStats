-- ============================================================================
-- PappaStats - ET: Legacy Stopwatch Match Statistics Tracker
-- Based on Oksii's game-stats-web.lua that can be found here: 
-- https://github.com/Oksii/legacy-configs/blob/main/luascripts/game-stats-web.lua
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Locals / configuration / state
-- ----------------------------------------------------------------------------

local modname = "PappaStats"
local version = "2.0-dev"

-- Hardcoded endpoint for now (will be config later)
local WEBHOOK_URL = "http://localhost:5080/api/matches"
local WEBHOOK_MATCHID_URL = WEBHOOK_URL .. "/matchid"
local WEBHOOK_DEMO_URL_TEMPLATE = "http://localhost:5080/api/matches/%s/demo"

-- Where to persist outgoing webhook payloads (relative to fs_homepath/fs_game).
-- Ensure this directory exists on the server (e.g. <fs_homepath>/<fs_game>/stats/).
local STATS_OUTPUT_DIR = "stats/"

local json_ok, json = pcall(require, "dkjson")
if not json_ok then
    json = nil
end

-- Local aliases (faster + matches style in game-stats-web.lua)
local trap_GetUserinfo = et.trap_GetUserinfo
local Info_ValueForKey = et.Info_ValueForKey
local trap_Milliseconds = et.trap_Milliseconds
local trap_Cvar_Get = et.trap_Cvar_Get
local gentity_get = et.gentity_get
local table_insert = table.insert
local trap_SendConsoleCommand = et.trap_SendConsoleCommand

-- Connected client tracking
local CON_CONNECTED = 2
local maxClients = 64
local connectedClients = {}
local currentGameState = et.GS_INITIALIZE

-- Stats constants + match/round state
local WS_KNIFE = 0
local WS_MAX = 28
local PERS_SCORE = 0

local round_start_time = 0
local round_start_unix = 0
local round_end_time = 0
local round_end_unix = 0
local obituaries = {}

-- Delay sending stats after intermission to ensure ET has finalized round stats.
local SEND_DELAY_MS = 5000
local pending_send = false
local pending_send_at_ms = 0
local pending_send_token = nil

-- Demo recording state (start at warmup countdown, stop at warmup/intermission).
local demo_recording = false
local demo_filename = nil
local pending_demo_stop = false
local pending_demo_stop_at_ms = 0

-- Demo upload scheduling (upload after round 2 stats are sent).
local pending_demo_upload = false
local pending_demo_upload_at_ms = 0
local pending_demo_upload_attempts_left = 0
local DEMO_UPLOAD_RETRY_DELAY_MS = 1000
local DEMO_UPLOAD_MAX_ATTEMPTS = 15

-- Per-player class time tracking.
-- Totals are stored as classStats[guid][classId] = milliseconds.
-- Current in-flight segment is tracked in classState[guid].
local classStats = {}
local classState = {}

-- Current match id (shared across round 1 + round 2). Preserved across map_restart.
local current_match_id = nil

-- ----------------------------------------------------------------------------
-- Helpers
-- ----------------------------------------------------------------------------

local function log(message)
    et.G_Print(string.format("^2[%s]^7 %s\n", modname, tostring(message)))
end

local function guid_for_client(clientNum)
    if type(clientNum) ~= "number" or clientNum < 0 or clientNum >= maxClients then
        return ""
    end

    local userinfo = trap_GetUserinfo(clientNum)
    if not userinfo or userinfo == "" then
        return ""
    end

    return string.upper(Info_ValueForKey(userinfo, "cl_guid") or "")
end

local function classstats_add_time(guid, classId, deltaMs)
    if not guid or guid == "" then
        return
    end

    local cls = tonumber(classId)
    if cls == nil then
        return
    end

    local ms = tonumber(deltaMs) or 0
    if ms <= 0 then
        return
    end

    if not classStats[guid] then
        classStats[guid] = {}
    end

    classStats[guid][cls] = (classStats[guid][cls] or 0) + ms
end

local function classstats_rollover(guid, nowMs, newClassId)
    if not guid or guid == "" then
        return
    end

    local state = classState[guid]
    if state and state.classId ~= nil and state.startMs and state.startMs > 0 then
        local elapsed = (tonumber(nowMs) or 0) - state.startMs
        if elapsed > 0 then
            classstats_add_time(guid, state.classId, elapsed)
        end
    end

    classState[guid] = {
        classId = tonumber(newClassId) or 0,
        startMs = tonumber(nowMs) or trap_Milliseconds()
    }
end

local function classstats_finalize(guid, nowMs)
    if not guid or guid == "" then
        return
    end

    local state = classState[guid]
    if state and state.classId ~= nil and state.startMs and state.startMs > 0 then
        local elapsed = (tonumber(nowMs) or 0) - state.startMs
        if elapsed > 0 then
            classstats_add_time(guid, state.classId, elapsed)
        end
    end

    classState[guid] = nil
end

local function classstats_snapshot(guid, nowMs)
    -- Build a stable, JSON-friendly structure:
    --   [ { classId = <number>, ms = <number> }, ... ]
    -- (keeps internal storage as classStats[guid][classId] = ms)
    local totalsMs = {}

    local totals = classStats[guid]
    if totals then
        for classId, ms in pairs(totals) do
            totalsMs[tonumber(classId) or 0] = tonumber(ms) or 0
        end
    end

    local state = classState[guid]
    if state and state.classId ~= nil and state.startMs and state.startMs > 0 then
        local elapsed = (tonumber(nowMs) or 0) - state.startMs
        if elapsed > 0 then
            local cls = tonumber(state.classId) or 0
            totalsMs[cls] = (totalsMs[cls] or 0) + elapsed
        end
    end

    local classIds = {}
    for classId, ms in pairs(totalsMs) do
        if (tonumber(ms) or 0) > 0 then
            table_insert(classIds, classId)
        end
    end

    table.sort(classIds)

    local snapshot = {}
    for _, classId in ipairs(classIds) do
        table_insert(snapshot, { classId = classId, ms = totalsMs[classId] })
    end

    return snapshot
end

local function fs_file_exists(path)
    if not et.trap_FS_FOpenFile then
        return false
    end

    local ok, fd, len = pcall(et.trap_FS_FOpenFile, path, et.FS_READ)
    if not ok then
        return false
    end

    if fd and fd >= 0 then
        pcall(et.trap_FS_FCloseFile, fd)
        return true
    end

    return false
end

local function writeTextFile(filename, content)
    if not et.trap_FS_FOpenFile or not et.trap_FS_Write or not et.trap_FS_FCloseFile then
        return false, "ET FS write API not available"
    end

    local fd = et.trap_FS_FOpenFile(filename, et.FS_WRITE)
    if type(fd) == "table" then
        -- Some ET Lua bindings return (fd, len)
        fd = fd[1]
    end

    if not fd or fd < 0 then
        return false, string.format("Could not open file for writing (code: %s)", tostring(fd))
    end

    local bytes = tostring(content or "")
    et.trap_FS_Write(bytes, string.len(bytes), fd)
    et.trap_FS_FCloseFile(fd)
    return true
end

local function os_file_exists(path)
    local p = tostring(path or "")
    if p == "" then
        return false
    end

    local f = io.open(p, "rb")
    if f then
        f:close()
        return true
    end
    return false
end

local function path_join(a, b)
    local sep = "/"
    if package and package.config and type(package.config) == "string" and #package.config >= 1 then
        sep = package.config:sub(1, 1)
    end

    a = tostring(a or "")
    b = tostring(b or "")
    if a == "" then
        return b
    end
    if b == "" then
        return a
    end

    local aEnds = a:sub(-1) == sep
    local bStarts = b:sub(1, 1) == sep
    if aEnds and bStarts then
        return a .. b:sub(2)
    elseif (not aEnds) and (not bStarts) then
        return a .. sep .. b
    end
    return a .. b
end

local function sanitize_filename_component(s)
    s = tostring(s or "")
    -- Replace anything that could be problematic in filenames or console parsing.
    s = s:gsub("[^%w%._%-]", "_")
    s = s:gsub("_+", "_")
    s = s:gsub("^_+", "")
    s = s:gsub("_+$", "")
    if s == "" then
        return "na"
    end
    return s
end

local function send_server_console(cmd)
    if not trap_SendConsoleCommand then
        return false
    end

    local command = tostring(cmd or "")
    if command == "" then
        return false
    end

    -- Append ensures ordering with other server-side commands.
    trap_SendConsoleCommand(et.EXEC_APPEND, command .. "\n")
    return true
end

local function stop_demo_recording()
    -- Always issue demo_stop; we want recording to stop on command.
    send_server_console("demo_stop")
    demo_recording = false
end

local function savePostedJsonPayload(payload_str)
    local unix = os.time()
    local baseName = string.format("posted_%d.json", unix)
    local relPath = STATS_OUTPUT_DIR .. baseName

    -- Avoid overwriting if multiple payloads are sent within the same second.
    if fs_file_exists(relPath) then
        relPath = STATS_OUTPUT_DIR .. string.format("posted_%d_%d.json", unix, trap_Milliseconds())
    end

    local ok, err = writeTextFile(relPath, payload_str)
    if ok then
        log("Saved webhook payload to " .. relPath)
    else
        log("Failed to save webhook payload to " .. relPath .. ": " .. tostring(err))
    end
end

-- Async curl execution function (extracted from game-stats-web.lua)
local function os_execute_ok(r1, r2, r3)
    -- Lua return values differ by version/build:
    --   - Lua 5.1 often returns a status code number (0 == success)
    --   - Lua 5.2+ commonly returns: true, "exit", 0 on success
    if type(r1) == "number" then
        return r1 == 0
    end

    if type(r1) == "boolean" then
        if r1 == true then
            return true
        end
        return (r2 == "exit" and tonumber(r3) == 0)
    end

    return false
end

local function executeCurlCommandAsync(curl_cmd, payload)
    local temp_file
    if payload then
        temp_file = os.tmpname() .. ".json"
        local f = io.open(temp_file, "w")
        if not f then
            return false, "Failed to create temp file"
        end
        f:write(payload)
        f:close()

        curl_cmd = string.format("%s --data-binary @%s", curl_cmd, temp_file)
    end

    if not curl_cmd:find("--retry") then
        curl_cmd = curl_cmd .. " -H 'Content-Type: application/json'"
        curl_cmd = curl_cmd .. " --compressed --connect-timeout 2 --max-time 10"
        curl_cmd = curl_cmd .. " --retry 3 --retry-delay 1 --retry-max-time 15"
        curl_cmd = curl_cmd .. " --silent --output /dev/null"
    end

    curl_cmd = curl_cmd .. " &"
    local r1, r2, r3 = os.execute(curl_cmd)
    local success = os_execute_ok(r1, r2, r3)

    if temp_file then
        os.execute(string.format("sleep 15 && rm -f %s &", temp_file))
    end

    if success then
        return true, "Request sent asynchronously"
    end

    return false, string.format("Failed to start async request (os.execute=%s,%s,%s)", tostring(r1), tostring(r2), tostring(r3))
end

local function executeCurlCommandAsyncRaw(curl_cmd)
    if not curl_cmd or curl_cmd == "" then
        return false, "Empty curl command"
    end

    local cmd = curl_cmd .. " &"
    local r1, r2, r3 = os.execute(cmd)
    local success = os_execute_ok(r1, r2, r3)

    if success then
        return true, "Request sent asynchronously"
    end

    return false, string.format("Failed to start async request (os.execute=%s,%s,%s)", tostring(r1), tostring(r2), tostring(r3))
end

local function executeCurlCommandSync(curl_cmd)
    local p = io.popen(curl_cmd)
    if not p then
        return nil, "Failed to start curl"
    end

    local output = p:read("*all")
    local ok, _, code = p:close()

    if type(ok) == "number" then
        -- Some Lua builds return status code directly
        code = ok
        ok = (code == 0)
    end

    if ok ~= true then
        return nil, string.format("curl failed (exit=%s)", tostring(code))
    end

    if not output or output == "" then
        return nil, "Empty response"
    end

    if not json then
        return nil, "dkjson not available"
    end

    local decoded = json.decode(output)
    if not decoded then
        return nil, "Failed to decode JSON"
    end

    return decoded
end

local function getPublicIP()
    local curl_cmd = 'curl -s --connect-timeout 2 --max-time 5 https://api.ipify.org?format=json'
    local result, err = executeCurlCommandSync(curl_cmd)

    if result and result.ip then
        return result.ip
    end

    log("Failed to fetch public IP: " .. tostring(err or "unknown error"))
    return "0.0.0.0"
end

local function url_encode(s)
    if s == nil then
        return ""
    end
    s = tostring(s)
    s = s:gsub("\n", "\r\n")
    s = s:gsub("([^%w _%%%-%.~])", function(c)
        return string.format("%%%02X", string.byte(c))
    end)
    s = s:gsub(" ", "%%20")
    return s
end

local function fetchMatchIDFromAPI(authToken, server_ip, server_port, mapname, round)
    if not authToken or authToken == "" then
        return nil, "No auth token"
    end

    local resolvedRound = tonumber(round) or 0
    if resolvedRound <= 0 then
        resolvedRound = 1
    end

    local url = string.format(
        '%s?serverIp=%s&serverPort=%s&mapname=%s&round=%d',
        WEBHOOK_MATCHID_URL,
        url_encode(server_ip),
        url_encode(server_port),
        url_encode(mapname),
        resolvedRound
    )

    local curl_cmd = string.format('curl -s -H "Authorization: Bearer %s" "%s"', authToken, url)
    local result, err = executeCurlCommandSync(curl_cmd)
    if not result then
        return nil, err
    end

    local matchId = result.matchId or result.matchID or result.id
    if not matchId or tostring(matchId) == "" then
        return nil, "matchId missing in response"
    end

    return tostring(matchId)
end

local function get_current_mapname()
    local mapname = Info_ValueForKey(et.trap_GetConfigstring(et.CS_SERVERINFO), "mapname")
    if not mapname or mapname == "" then
        mapname = trap_Cvar_Get("mapname")
    end
    return mapname or ""
end

local function safe_number(v)
    return tonumber(v) or 0
end

local function build_demo_filename()
    local mapname = sanitize_filename_component(get_current_mapname())
    local matchPart = sanitize_filename_component(current_match_id or "nomatch")
    -- Keep the filename stable and easy to correlate later.
    return string.format("pappastats_%s_%s", mapname, matchPart)
end

local function start_demo_recording()
    local filename = build_demo_filename()
    demo_filename = filename
    demo_recording = true
    send_server_console(string.format("demo_record %s", filename))
    return filename
end

local function resolve_demo_file_path()
    if not demo_filename or demo_filename == "" then
        return nil
    end

    local base = tostring(demo_filename)

    -- Demos are always stored at:
    --   <fs_homepath>/<fs_game>/svdemos/<demoname>.sv_84
    local home = tostring(trap_Cvar_Get("fs_homepath") or "")
    local game = tostring(trap_Cvar_Get("fs_game") or "")
    local demoDir = path_join(path_join(home, game), "svdemos")

    local fileName = base
    if not fileName:find("%.sv_%d%d$") then
        fileName = fileName .. ".sv_84"
    end

    local candidate = path_join(demoDir, fileName)
    if os_file_exists(candidate) then
        return candidate
    end

    return nil
end

local function upload_demo_to_backend(authToken)
    if not current_match_id or current_match_id == "" then
        return false, "current_match_id missing"
    end
    if not demo_filename or demo_filename == "" then
        local filename = build_demo_filename()
        demo_filename = filename
    end

    local demoPath = resolve_demo_file_path()
    if not demoPath then
        return false, "demo file not found yet"
    end

    local url = string.format(WEBHOOK_DEMO_URL_TEMPLATE, url_encode(current_match_id))

    -- Include --retry to avoid executeCurlCommandAsync auto-adding JSON content-type.
    local curl_cmd = string.format(
        'curl -X POST -H "Authorization: Bearer %s" --compressed --connect-timeout 2 --max-time 180 --retry 3 --retry-delay 1 --retry-max-time 180 --silent --output /dev/null -F "file=@%s" -F "demoFilename=%s" "%s"',
        tostring(authToken or ""),
        tostring(demoPath),
        tostring(demo_filename),
        tostring(url)
    )

    return executeCurlCommandAsyncRaw(curl_cmd)
end

local function isEmpty(v)
    if v == nil or v == "" then
        return "0"
    end
    return v
end

local function ConvertTimelimit(timelimit)
    local msec = math.floor(safe_number(timelimit) * 60000)
    local seconds = math.floor(msec / 1000)
    local mins = math.floor(seconds / 60)
    seconds = math.floor(seconds - (mins * 60))
    local tens = math.floor(seconds / 10)
    seconds = math.floor(seconds - (tens * 10))

    return string.format("%i:%i%i", mins, tens, seconds)
end

local function getServerIpPort()
    local env_ip = os.getenv("MAP_IP")
    local net_ip = et.trap_Cvar_Get("net_ip")
    local net_port = et.trap_Cvar_Get("net_port")

    local server_ip
    if env_ip and env_ip ~= "" then
        server_ip = env_ip
    elseif net_ip and net_ip ~= "" and net_ip ~= "0.0.0.0" and net_ip ~= "::0" then
        server_ip = net_ip
    else
        server_ip = getPublicIP()
    end

    return server_ip or "0.0.0.0", net_port
end

-- ---------------------------------------------------------------------------
-- Connected client tracking
-- ---------------------------------------------------------------------------

local function initMaxClients()
    local cvar = tonumber(et.trap_Cvar_Get("sv_maxclients"))
    if cvar and cvar > 0 then
        maxClients = cvar
    end
end

local function rebuild_connectedClients()
    for k in pairs(connectedClients) do
        connectedClients[k] = nil
    end

    for clientNum = 0, maxClients - 1 do
        if et.gentity_get(clientNum, "pers.connected") == CON_CONNECTED then
            connectedClients[clientNum] = true
        end
    end
end

local function add_connected_client(clientNum)
    if type(clientNum) ~= "number" then
        return
    end

    if clientNum >= 0 and clientNum < maxClients then
        connectedClients[clientNum] = true
    end
end

local function remove_connected_client(clientNum)
    connectedClients[clientNum] = nil
end

-- ---------------------------------------------------------------------------
-- Handle gamestate changes
-- ---------------------------------------------------------------------------
local function handle_gamestate_change()
    local newGamestate = tonumber(et.trap_Cvar_Get("gamestate"))
    local currentRound = safe_number(trap_Cvar_Get("g_currentRound"))

    -- Verify that gamestate has changed. If not, do nothing.
    if newGamestate == nil or newGamestate == currentGameState then
        return
    end

    log(string.format("Gamestate changed: %d -> %d", currentGameState, newGamestate))

    if newGamestate == et.GS_WARMUP and currentRound == 0 then
        -- Round 1 warmup: ensure no recording continues.
        pending_demo_stop = false
        pending_demo_stop_at_ms = 0
        stop_demo_recording()
    elseif newGamestate == et.GS_WARMUP_COUNTDOWN and currentRound == 0 then
        -- Round 1 countdown: stop any existing demo, then start fresh.
        pending_demo_stop = false
        pending_demo_stop_at_ms = 0
        stop_demo_recording()

        -- Ensure we have a backend match id before starting the demo so the filename can include it.
        if not current_match_id or current_match_id == "" then
            local server_ip, server_port = getServerIpPort()
            local mapname = get_current_mapname()

            -- Reuse the same token we use for stats sending (currently hardcoded for testing).
            local authToken = pending_send_token or "1234567890"
            local fetched, err = fetchMatchIDFromAPI(authToken, server_ip, server_port, mapname, 1)
            if fetched then
                current_match_id = fetched
                log("Fetched matchID from API for demo: " .. tostring(current_match_id))
            else
                log("Failed to fetch matchID from API for demo: " .. tostring(err))
            end
        end

        local filename = start_demo_recording()
        log("Demo recording started: " .. tostring(filename))
    elseif newGamestate == et.GS_PLAYING then -- Game has started
        round_start_time = trap_Milliseconds()
        round_start_unix = os.time()    
        -- New round started; cancel any pending send from a previous intermission.
        pending_send = false
    elseif newGamestate == et.GS_INTERMISSION then -- Game has ended
        round_end_time = trap_Milliseconds()
        round_end_unix = os.time()

        -- Schedule sending stats a few seconds after intermission starts.
        -- (et_RunFrame will perform the actual send once.)
        pending_send_token = "1234567890" -- Temporary hardcoded token for testing
        pending_send_at_ms = round_end_time + SEND_DELAY_MS
        pending_send = true

        -- Round 1 intermission: stop demo recording after the same 5s delay as stats.
        if currentRound == 0 then
            pending_demo_stop = true
            pending_demo_stop_at_ms = round_end_time + SEND_DELAY_MS - 1000 -- Stop 1s before stats send
        end
    end

    currentGameState = newGamestate
end

local function process_pending_demo_stop()
    if not pending_demo_stop then
        return
    end

    local now = trap_Milliseconds()
    if now < (pending_demo_stop_at_ms or 0) then
        return
    end

    pending_demo_stop = false
    pending_demo_stop_at_ms = 0
    stop_demo_recording()
    if demo_filename and demo_filename ~= "" then
        log("Demo recording stopped: " .. tostring(demo_filename))
    else
        log("Demo recording stopped")
    end
end

local function process_pending_demo_upload()
    if not pending_demo_upload then
        return
    end

    local now = trap_Milliseconds()
    if now < (pending_demo_upload_at_ms or 0) then
        return
    end

    if (pending_demo_upload_attempts_left or 0) <= 0 then
        pending_demo_upload = false
        pending_demo_upload_at_ms = 0
        log("Demo upload aborted (out of attempts)")
        return
    end

    pending_demo_upload_attempts_left = pending_demo_upload_attempts_left - 1

    local authToken = pending_send_token or "1234567890"
    local ok, msg = upload_demo_to_backend(authToken)
    if ok then
        pending_demo_upload = false
        pending_demo_upload_at_ms = 0
        log("Demo upload started")
        return
    end

    pending_demo_upload_at_ms = now + DEMO_UPLOAD_RETRY_DELAY_MS
    log("Demo upload retry: " .. tostring(msg))
end

local function process_pending_send()
    if not pending_send then
        return
    end

    local now = trap_Milliseconds()
    if now < (pending_send_at_ms or 0) then
        return
    end

    pending_send = false
    SendStats(pending_send_token)

    -- After second round stats have been sent, upload the demo.
    local resolvedRound = (safe_number(trap_Cvar_Get("g_currentRound")) == 0) and 2 or 1
    if resolvedRound == 2 then
        pending_demo_upload = true
        pending_demo_upload_attempts_left = DEMO_UPLOAD_MAX_ATTEMPTS
        pending_demo_upload_at_ms = trap_Milliseconds() + 250
    end
end

-- ---------------------------------------------------------------------------
-- Stats extraction helpers
-- ---------------------------------------------------------------------------

-- Stats constants declared at top.

-- local function gather_player_stats
-- Returns: array of player tables
--
-- Shape:
--   players[i] = {
--     clientNum = <number>,
--     guid = <string>,
--     guid_short = <string>,
--     name = <string>,
--     rounds = <number>,
--     team = <number>,
--     weapon_mask = <number>,
--     weapon_stats = { { weapon=<id>, hits=..., atts=..., kills=..., deaths=..., headshots=... }, ... },
--     damage_given = <number>,
--     damage_received = <number>,
--     team_damage_given = <number>,
--     team_damage_received = <number>,
--     gibs = <number>,
--     self_kills = <number>,
--     team_kills = <number>,
--     team_gibs = <number>,
--     time_played_percent = <number>,
--     xp = <number>
--   }
local function gather_player_stats()
    local players = {}

    local nowMs = trap_Milliseconds()

    for clientNum in pairs(connectedClients) do
        if gentity_get(clientNum, "pers.connected") == CON_CONNECTED then
            local userinfo = trap_GetUserinfo(clientNum)
            if userinfo and userinfo ~= "" then
                local guid = string.upper(Info_ValueForKey(userinfo, "cl_guid") or "")
                if guid ~= "" then
                    local weaponMask = 0
                    local weaponStats = {}

                    for weaponId = WS_KNIFE, WS_MAX - 1 do
                        local ws = gentity_get(clientNum, "sess.aWeaponStats", weaponId)
                        if type(ws) == "table" then
                            local atts = safe_number(ws[1])
                            local deaths = safe_number(ws[2])
                            local headshots = safe_number(ws[3])
                            local hits = safe_number(ws[4])
                            local kills = safe_number(ws[5])

                            if atts ~= 0 or hits ~= 0 or deaths ~= 0 or kills ~= 0 then
                                table_insert(weaponStats, {
                                    weapon = weaponId,
                                    hits = hits,
                                    atts = atts,
                                    kills = kills,
                                    deaths = deaths,
                                    headshots = headshots
                                })
                                weaponMask = weaponMask | (1 << weaponId)
                            end
                        end
                    end

                    -- Match StoreStats behavior: only store if at least one weapon has activity
                    if weaponMask ~= 0 then
                        local name = gentity_get(clientNum, "pers.netname") or ""
                        local rounds = safe_number(gentity_get(clientNum, "sess.rounds"))
                        local team = safe_number(gentity_get(clientNum, "sess.sessionTeam"))

                        local damageGiven = safe_number(gentity_get(clientNum, "sess.damage_given"))
                        local damageReceived = safe_number(gentity_get(clientNum, "sess.damage_received"))
                        local teamDamageGiven = safe_number(gentity_get(clientNum, "sess.team_damage_given"))
                        local teamDamageReceived = safe_number(gentity_get(clientNum, "sess.team_damage_received"))
                        local gibs = safe_number(gentity_get(clientNum, "sess.gibs"))
                        local selfkills = safe_number(gentity_get(clientNum, "sess.self_kills"))
                        local teamkills = safe_number(gentity_get(clientNum, "sess.team_kills"))
                        local teamgibs = safe_number(gentity_get(clientNum, "sess.team_gibs"))
                        local timeAxis = safe_number(gentity_get(clientNum, "sess.time_axis"))
                        local timeAllies = safe_number(gentity_get(clientNum, "sess.time_allies"))
                        local timePlayed = safe_number(gentity_get(clientNum, "sess.time_played"))
                        local xp = safe_number(gentity_get(clientNum, "ps.persistant", PERS_SCORE))

                        local totalTeamTime = timeAxis + timeAllies
                        local timePlayedPercent = (totalTeamTime == 0) and 0 or (100.0 * timePlayed / totalTeamTime)

                        local classStatsForGuid = classstats_snapshot(guid, nowMs)

                        table_insert(players, {
                            clientNum = clientNum,
                            guid = guid,
                            name = name,
                            rounds = rounds,
                            team = team,
                            class_stats = classStatsForGuid,
                            weapon_stats = weaponStats,
                            damage_given = damageGiven,
                            damage_received = damageReceived,
                            team_damage_given = teamDamageGiven,
                            team_damage_received = teamDamageReceived,
                            gibs = gibs,
                            self_kills = selfkills,
                            team_kills = teamkills,
                            team_gibs = teamgibs,
                            time_played_percent = timePlayedPercent,
                            xp = xp
                        })
                    end
                end
            end
        end
    end

    return players
end

-- Gather basic server/round header data
-- and attach players array to it.
local function gather_match_stats(matchID)
    local server_ip, server_port = getServerIpPort()

    local mapname = Info_ValueForKey(et.trap_GetConfigstring(et.CS_SERVERINFO), "mapname")
    if not mapname or mapname == "" then
        mapname = et.trap_Cvar_Get("mapname")
    end

    local round = (safe_number(et.trap_Cvar_Get("g_currentRound")) == 0) and 2 or 1
    local resolved_matchID = tostring(matchID or os.time())

    local stats_json = {
        servername = et.trap_Cvar_Get("sv_hostname"),
        config = et.trap_Cvar_Get("g_customConfig"),
        defenderteam = safe_number(isEmpty(Info_ValueForKey(et.trap_GetConfigstring(et.CS_MULTI_INFO), "d"))) + 1,
        winnerteam = safe_number(isEmpty(Info_ValueForKey(et.trap_GetConfigstring(et.CS_MULTI_MAPWINNER), "w"))) + 1,
        timelimit = ConvertTimelimit(et.trap_Cvar_Get("timelimit")),
        nextTimeLimit = ConvertTimelimit(et.trap_Cvar_Get("g_nextTimeLimit")),
        mapname = mapname,
        round = round,
        matchID = resolved_matchID,
        serverIp = server_ip,
        serverPort = server_port,
        roundStart = round_start_time,
        roundEnd = round_end_time,
        roundStartUnix = round_start_unix,
        roundEndUnix = round_end_unix,
        players = gather_player_stats(),
        obituaries = obituaries
    }

    return stats_json
end

-- Build the current match stats payload and POST it to the webhook.
-- authToken: optional; if provided, sends header "Authorization: Bearer <token>".
-- matchID: optional; passed through to gather_match_stats.
function SendStats(authToken)
    if not json then
        return false, "dkjson not available (require('dkjson') failed)"
    end

    local mapname = get_current_mapname()

    local resolvedRound = (safe_number(trap_Cvar_Get("g_currentRound")) == 0) and 2 or 1

    -- Solely trust backend match id generator. Always fetch matchID based on server+map+round.
    if not current_match_id or current_match_id == "" then
        local server_ip, server_port = getServerIpPort()
        local fetched, err = fetchMatchIDFromAPI(authToken, server_ip, server_port, mapname, resolvedRound)
        if fetched then
            current_match_id = fetched
            log("Using matchID from API: " .. current_match_id)
        else
            -- Backend contract: for round=2 it will still create a new one if no open match exists.
            -- If backend is unreachable, fall back to unix time to avoid losing data.
            current_match_id = tostring(os.time())
            log("Failed to fetch matchID from API, falling back to unix time: " .. current_match_id .. " (" .. tostring(err) .. ")")
        end
    end

    local payload_tbl = gather_match_stats(current_match_id)
    local payload_str = json.encode(payload_tbl)
    if not payload_str then
        return false, "Failed to encode JSON"
    end

    -- Persist the exact JSON we are about to POST, for later inspection/debugging.
    savePostedJsonPayload(payload_str)

    local curl_cmd = string.format(
        'curl -X POST -H "Authorization: Bearer %s" %s',
        authToken or "noavail",
        WEBHOOK_URL)

    local ok, msg = executeCurlCommandAsync(curl_cmd, payload_str)
    if ok then
        log("Stats webhook POST started")
    else
        log("Stats webhook POST failed to start: " .. tostring(msg))
    end
    return ok, msg
end

-- ---------------------------------------------------------------------------
-- ET: Legacy Lua callbacks
-- ---------------------------------------------------------------------------

function et_RunFrame(gameFrameLevelTime)
    handle_gamestate_change()
    process_pending_send()
    process_pending_demo_stop()
    process_pending_demo_upload()
end

function et_InitGame(levelTime, randomSeed, restart)
    et.RegisterModname(modname .. " " .. version)

    -- Keep only in-memory state; backend match-id endpoint provides continuity across rounds.
    if tonumber(restart) == 0 then
        current_match_id = nil
        obituaries = {}
        classStats = {}
        classState = {}
    end

    pending_send = false
    pending_send_at_ms = 0
    pending_send_token = nil

    pending_demo_stop = false
    pending_demo_stop_at_ms = 0
    demo_recording = false
    demo_filename = nil

    pending_demo_upload = false
    pending_demo_upload_at_ms = 0
    pending_demo_upload_attempts_left = 0

    initMaxClients()
    rebuild_connectedClients()

    local activeCount = 0
    for _ in pairs(connectedClients) do
        activeCount = activeCount + 1
    end

    et.G_Print(string.format(
        "^2[%s]^7 loaded (%s). Active clients seeded: %d (restart=%s)\n",
        modname,
        version,
        activeCount,
        tostring(restart)
    ))
end

function et_ClientBegin(clientNum)
    add_connected_client(clientNum)
end

function et_ClientDisconnect(clientNum)
    local guid = guid_for_client(clientNum)
    if guid ~= "" then
        classstats_finalize(guid, trap_Milliseconds())
    end
    remove_connected_client(clientNum)
end

function et_ClientSpawn(clientNum, revived, teamChange, restoreHealth)
    local guid = guid_for_client(clientNum)
    if guid == "" then
        return
    end

    local classId = safe_number(gentity_get(clientNum, "sess.playerType"))
    classstats_rollover(guid, trap_Milliseconds(), classId)
end

function et_Obituary(target, attacker, meansOfDeath)

    local targetGuid = guid_for_client(target)
    local attackerGuid = (attacker == 1022) and "WORLD" or guid_for_client(attacker)

    table_insert(obituaries, {
        timestamp = trap_Milliseconds(),
        target = targetGuid,
        attacker = attackerGuid,
        meansOfDeath = meansOfDeath
    })
end