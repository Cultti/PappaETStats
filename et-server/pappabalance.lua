-- ============================================================================
-- PappaBalance - team balancer chat command for ET: Legacy
--
-- Usage (in chat):
--   !balance
--   !balance <sigmaMultiplier>
--   !balance 3on3|4on4        (use the 3on3/4on4 skill ratings)
--   !balance 5on5|6on6        (use the 5on5/6on6 skill ratings)
--   !balance 5on5 2.5         (tokens can be combined in any order)
--
-- Only allowed during et.GS_WARMUP. Any player can run it.
-- Posts active (Axis/Allies) player GUIDs to backend and prints suggested teams.
-- ============================================================================

local modname = "PappaBalance"
local version = "1.0-dev"

-- Backend endpoint
local BALANCE_API_URL = "http://localhost:5080/api/skillratings/balance-teams"

-- Token used by backend (same token as ingest endpoints).
-- Keep empty to omit Authorization header.
local AUTH_TOKEN = "1234567890"

-- Request logging (ET filesystem).
-- Files are written under: <fs_homepath>/<fs_game>/<REQUEST_LOG_DIR>/
-- Ensure the directory exists on the server.
-- Set empty to disable.
local REQUEST_LOG_DIR = "pappabalance"

local json_ok, json = pcall(require, "dkjson")
if not json_ok then
    json = nil
end

local trap_GetUserinfo = et.trap_GetUserinfo
local Info_ValueForKey = et.Info_ValueForKey
local trap_Cvar_Get = et.trap_Cvar_Get
local gentity_get = et.gentity_get

local CON_CONNECTED = 2
local maxClients = 64

local function log(message)
    et.G_Print(string.format("^2[%s]^7 %s\n", modname, tostring(message)))
end

-- File logging implementation copied from pappastats.lua (ET FS APIs).
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
    return s:gsub("[^%w%._%-]", "_")
end

local function writeTextFile(filename, content)
    if not et.trap_FS_FOpenFile or not et.trap_FS_Write or not et.trap_FS_FCloseFile then
        return false, "ET FS write API not available"
    end

    local fd = et.trap_FS_FOpenFile(filename, et.FS_WRITE)
    if type(fd) == "table" then
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

local function get_fs_log_hint(fileName)
    local home = tostring(trap_Cvar_Get("fs_homepath") or "")
    local game = tostring(trap_Cvar_Get("fs_game") or "")
    if home == "" or game == "" then
        return fileName
    end
    return path_join(path_join(home, game), fileName)
end

local function persist_debug_file(relativePath, content)
    local ok, err = writeTextFile(relativePath, content)
    if not ok then
        log(string.format("log write failed: %s", tostring(err)))
        return false
    end
    return true
end

local function now_utc_iso()
    -- os.date with ! prefix uses UTC in Lua.
    return os.date("!%Y-%m-%dT%H:%M:%SZ")
end

local function truncate_for_log(s, maxLen)
    s = tostring(s or "")
    maxLen = tonumber(maxLen) or 2000
    if #s <= maxLen then
        return s
    end
    return s:sub(1, maxLen) .. "...(truncated)"
end

local function say_all(message)
    local msg = tostring(message or "")
    if msg == "" then
        return
    end

    -- Escape double quotes for server command.
    msg = msg:gsub('\\', '\\\\'):gsub('"', '\\"')
    et.trap_SendServerCommand(-1, string.format('chat "%s"', msg))
end

local function safe_number(v)
    return tonumber(v) or 0
end

local function trim(s)
    s = tostring(s or "")
    return (s:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function starts_with(s, prefix)
    s = tostring(s or "")
    prefix = tostring(prefix or "")
    return s:sub(1, #prefix) == prefix
end

local function os_execute_ok(r1, r2, r3)
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

local function read_file_all(path)
    local f = io.open(path, "r")
    if not f then
        return ""
    end
    local s = f:read("*all")
    f:close()
    return s or ""
end

local function executeCurlCommandSync(curl_cmd)
    -- Redirect stderr so we don't lose curl error messages.
    local p = io.popen(curl_cmd .. " 2>&1")
    if not p then
        return nil, "Failed to start curl"
    end

    local output = p:read("*all")
    local ok, _, code = p:close()

    if type(ok) == "number" then
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

local function executeCurlJsonWithHttpStatus(curl_cmd, body_file)
    local p = io.popen(curl_cmd .. " 2>&1")
    if not p then
        return nil, "Failed to start curl"
    end

    local output = p:read("*all") or ""
    local ok, _, code = p:close()

    if type(ok) == "number" then
        code = ok
        ok = (code == 0)
    end

    if ok ~= true then
        output = trim(output)
        if output ~= "" then
            return nil, output
        end
        return nil, string.format("curl failed (exit=%s)", tostring(code))
    end

    local status = output:match("HTTPSTATUS:(%d%d%d)")
    local httpCode = tonumber(status)
    local body = body_file and read_file_all(body_file) or ""

    if not httpCode then
        output = trim(output)
        if output == "" then
            return nil, "Empty response (no HTTP status)"
        end
        return nil, output
    end

    if httpCode ~= 200 then
        if httpCode == 401 then
            return nil, "Unauthorized (401) - check backend token settings"
        end
        if httpCode == 404 then
            return nil, "Not found (404) - check BALANCE_API_URL"
        end

        body = trim(body)
        if body ~= "" then
            return nil, string.format("HTTP %d - %s", httpCode, body)
        end
        return nil, string.format("HTTP %d (empty body)", httpCode)
    end

    if body == "" then
        return nil, "Empty response body (HTTP 200)"
    end

    if not json then
        return nil, "dkjson not available"
    end

    local decoded = json.decode(body)
    if not decoded then
        return nil, "Failed to decode JSON"
    end

    return decoded
end

local function is_warmup_only()
    local gamestate = safe_number(trap_Cvar_Get("gamestate"))
    local roundNumber = safe_number(trap_Cvar_Get("g_currentRound"))
    return gamestate == et.GS_WARMUP and roundNumber == 0
end

local function guid_for_client(clientNum)
    local userinfo = trap_GetUserinfo(clientNum)
    if not userinfo or userinfo == "" then
        return ""
    end

    return string.upper(Info_ValueForKey(userinfo, "cl_guid") or "")
end

local function name_for_client(clientNum)
    local name = gentity_get(clientNum, "pers.netname")
    return tostring(name or "")
end

local function collect_active_players()
    -- Returns:
    --   guids = { "...", ... } (distinct, stable order)
    --   nameByGuid = { [guid] = name }
    --   clientNumByGuid = { [guid] = clientNum }
    local guids = {}
    local seen = {}
    local nameByGuid = {}
    local clientNumByGuid = {}

    for clientNum = 0, maxClients - 1 do
        if gentity_get(clientNum, "pers.connected") == CON_CONNECTED then
            local team = safe_number(gentity_get(clientNum, "sess.sessionTeam"))
            -- 1=Axis, 2=Allies; ignore spectators/unknown.
            if team == 1 or team == 2 then
                local guid = guid_for_client(clientNum)
                if guid ~= "" and not seen[guid] then
                    seen[guid] = true
                    table.insert(guids, guid)
                end

                if guid ~= "" then
                    local name = name_for_client(clientNum)
                    if name ~= "" then
                        nameByGuid[guid] = name
                    end

                    -- Keep latest clientNum mapping (should be stable while connected).
                    clientNumByGuid[guid] = clientNum
                end
            end
        end
    end

    return guids, nameByGuid, clientNumByGuid
end

local function build_payload_json(guids, sigmaMultiplier, mode)
    if not json then
        return nil, "dkjson not available"
    end

    local payload = {
        guids = guids,
        sigmaMultiplier = sigmaMultiplier,
        mode = mode
    }

    return json.encode(payload)
end

local function post_balance_request(payload_json)
    local temp_file = os.tmpname() .. ".json"
    local body_file = os.tmpname() .. ".out"
    local f = io.open(temp_file, "w")
    if not f then
        return nil, "Failed to create temp file"
    end
    f:write(payload_json)
    f:close()

    local authToken = tostring(AUTH_TOKEN or "")

    if REQUEST_LOG_DIR and REQUEST_LOG_DIR ~= "" then
        local ts = sanitize_filename_component(now_utc_iso())
        local reqName = path_join(REQUEST_LOG_DIR, string.format("balance_request_%s.json", ts))
        local metaName = path_join(REQUEST_LOG_DIR, string.format("balance_request_%s.meta.txt", ts))

        persist_debug_file(reqName, payload_json)
        persist_debug_file(metaName, string.format(
            "timeUtc=%s\nurl=%s\nauth=%s\nrequestFile=%s\n",
            now_utc_iso(),
            BALANCE_API_URL,
            (authToken ~= "") and "yes" or "no",
            reqName))

        -- Tell where the files land on disk (best effort).
        say_all(string.format("^3Balance:^7 logging to %s", get_fs_log_hint(REQUEST_LOG_DIR)))
    end

    -- Match pappastats.lua Authorization header formatting.
    local curl_cmd = string.format(
        'curl -sS -o "%s" -w "HTTPSTATUS:%%{http_code}" -X POST -H "Authorization: Bearer %s" -H "Content-Type: application/json" --compressed --connect-timeout 2 --max-time 15 --data-binary @"%s" "%s"',
        tostring(body_file),
        tostring(authToken),
        tostring(temp_file),
        tostring(BALANCE_API_URL)
    )

    local result, err = executeCurlJsonWithHttpStatus(curl_cmd, body_file)

    if REQUEST_LOG_DIR and REQUEST_LOG_DIR ~= "" then
        local ts = sanitize_filename_component(now_utc_iso())
        local resName = path_join(REQUEST_LOG_DIR, string.format("balance_response_%s.txt", ts))
        local body = read_file_all(body_file)
        local header = result and "OK" or ("ERROR: " .. tostring(err))
        persist_debug_file(resName, header .. "\n\n" .. tostring(body or ""))
    end

    -- Best-effort cleanup.
    os.remove(temp_file)
    os.remove(body_file)

    return result, err
end

local function format_pct(p)
    local v = tonumber(p) or 0
    if v < 0 then v = 0 end
    if v > 1 then v = 1 end
    return string.format("%.0f%%", v * 100.0)
end

local function format_mode(mode)
    local v = tonumber(mode)
    if v == 3 then
        return "3on3/4on4"
    elseif v == 6 then
        return "5on5/6on6"
    end
    return "overall"
end

local function apply_team_assignments(t1Players, t2Players, clientNumByGuid)
    if type(clientNumByGuid) ~= "table" then
        return
    end

    local function force_team(players, teamName)
        for _, p in ipairs(players) do
            local guid = tostring(p.guid or p.Guid or "")
            local clientNum = clientNumByGuid[guid]
            if type(clientNum) == "number" then
                et.trap_SendConsoleCommand(et.EXEC_NOW, string.format("forceteam %d %s\n", clientNum, teamName))
            end
        end
    end

    -- By convention: Team1 -> Axis, Team2 -> Allies.
    force_team(t1Players, "axis")
    force_team(t2Players, "allies")
end

local function print_balance_result(response, nameByGuid, clientNumByGuid)
    if type(response) ~= "table" then
        say_all("^1Balance failed:^7 invalid response")
        return
    end

    local p1 = response.winProbabilityTeam1
    local p2 = response.winProbabilityTeam2

    local team1 = response.team1 or {}
    local team2 = response.team2 or {}

    local t1Players = (team1.players or team1.Players or {})
    local t2Players = (team2.players or team2.Players or {})

    say_all(string.format("^3Balance:^7 Team1 %s vs Team2 %s ^3[%s]", format_pct(p1), format_pct(p2), format_mode(response.mode)))

    local function team_line(label, players)
        local names = {}
        for _, p in ipairs(players) do
            local guid = tostring(p.guid or p.Guid or "")
            local name = nameByGuid[guid] or guid
            if name ~= "" then
                table.insert(names, name)
            end
        end

        local joined = table.concat(names, ", ")
        if joined == "" then
            joined = "(none)"
        end

        say_all(string.format("^2%s:^7 %s", label, joined))
    end

    team_line("Team1", t1Players)
    team_line("Team2", t2Players)

    apply_team_assignments(t1Players, t2Players, clientNumByGuid)
    say_all("^3Balance:^7 teams applied (Team1->Axis, Team2->Allies)")
end

local function handle_balance_command(rawMessage)
    if not is_warmup_only() then
        say_all("^1!balance^7 is only allowed in warmup")
        return
    end

    local msg = trim(rawMessage)
    if msg == "" then
        return
    end

    local sigmaMultiplier = 3
    local mode = nil

    -- Parse: !balance [3on3|4on4|5on5|6on6] [multiplier]
    local parts = {}
    for token in msg:gmatch("%S+") do
        table.insert(parts, token)
    end

    for i = 2, #parts do
        local token = parts[i]:lower()
        if token == "3on3" or token == "4on4" then
            mode = 3
        elseif token == "5on5" or token == "6on6" then
            mode = 6
        else
            local parsed = tonumber(token)
            if parsed and parsed > 0 then
                sigmaMultiplier = parsed
            end
        end
    end

    local guids, nameByGuid, clientNumByGuid = collect_active_players()
    if #guids < 2 then
        say_all("^1Balance:^7 need at least 2 players on teams")
        return
    end

    if not json then
        say_all("^1Balance failed:^7 dkjson missing")
        return
    end

    local payload_json, err = build_payload_json(guids, sigmaMultiplier, mode)
    if not payload_json then
        say_all("^1Balance failed:^7 " .. tostring(err))
        return
    end

    local response, postErr = post_balance_request(payload_json)
    if not response then
        say_all("^1Balance failed:^7 " .. tostring(postErr))
        return
    end

    print_balance_result(response, nameByGuid, clientNumByGuid)
    et.G_globalSound("sound/osp/goat.wav")
end

function et_InitGame(levelTime, randomSeed, restart)
    et.RegisterModname(modname .. " " .. version)
    local cvar = tonumber(et.trap_Cvar_Get("sv_maxclients"))
    if cvar and cvar > 0 then
        maxClients = cvar
    end
    log("loaded")
end

function et_ClientCommand(clientNum, command)
    local cmd = et.trap_Argv(0)
    if cmd ~= "say" and cmd ~= "say_team" and cmd ~= "say_buddy" then
        return 0
    end

    local msg = et.ConcatArgs(1) or ""
    msg = trim(msg)

    if starts_with(msg, "!balance") then
        handle_balance_command(msg)
        -- Swallow command so it doesn't appear in chat.
        return 1
    end

    return 0
end
