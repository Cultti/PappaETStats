-- ============================================================================
-- PappaBalance - joukkueiden tasapainotuksen chat-komento ET: Legacylle
--
-- Käyttö (chatissa):
--   !balance
--   !balance <sigmaMultiplier>
--   !balance 3on3|4on4        (käytä 3on3/4on4-skill ratingeja)
--   !balance 5on5|6on6        (käytä 5on5/6on6-skill ratingeja)
--   !balance 5on5 2.5         (parametrit voi antaa missä järjestyksessä tahansa)
--   !voice                   (siirrä itsesi nykyisen joukkueesi voice-kanavalle)
--   !allutvittuun           (refereet: siirrä kaikki aktiiviset pelaajat)
--
-- Sallittu vain et.GS_WARMUP-tilassa. Kuka tahansa pelaaja voi ajaa komennon.
-- Lähettää aktiivisten (Axis/Allies) pelaajien GUIDit backendille ja tulostaa ehdotetut tiimit.
-- ============================================================================

local modname = "PappaBalance"
local version = "1.0-dev"

-- Backendin endpointit
local BALANCE_API_URL = "http://localhost:5080/api/skillratings/balance-teams"
local VOICE_API_URL = "http://localhost:5080/api/voice/move"
local VOICE_COOLDOWN_SECONDS = 10

-- Voice-HTTP-pyynnöt ajetaan taustalla POSIX-shellissa (Linux ET -serveri + curl).
-- Vastaukset pollataan et_RunFramesta; game thread ei odota verkkopyyntöjä.
local voiceRequests = {}
local voiceLastUsed = {}
local voiceAllLastUsed = nil
local nextVoicePoll = 0

-- Backendin käyttämä token (sama token kuin ingest-endpointeilla).
-- Jätä tyhjäksi, jos Authorization-headeria ei lähetetä.
local AUTH_TOKEN = "1234567890"

-- Pyyntöjen lokitus (ET:n tiedostojärjestelmä).
-- Tiedostot kirjoitetaan alle: <fs_homepath>/<fs_game>/<REQUEST_LOG_DIR>/
-- Varmista, että hakemisto on olemassa serverilla.
-- Aseta tyhjäksi poistaaksesi käytöstä.
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

-- Tiedostolokituksen toteutus kopioitu pappastats.lua:sta (ET FS API:t).
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
        return false, "ET FS write API ei ole saatavilla"
    end

    local fd = et.trap_FS_FOpenFile(filename, et.FS_WRITE)
    if type(fd) == "table" then
        fd = fd[1]
    end

    if not fd or fd < 0 then
        return false, string.format("Tiedostoa ei voitu avata kirjoitusta varten (koodi: %s)", tostring(fd))
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
        log(string.format("lokin kirjoitus epäonnistui: %s", tostring(err)))
        return false
    end
    return true
end

local function now_utc_iso()
    -- os.date käyttää UTC-aikaa, kun formaatissa on !-prefix.
    return os.date("!%Y-%m-%dT%H:%M:%SZ")
end

local function truncate_for_log(s, maxLen)
    s = tostring(s or "")
    maxLen = tonumber(maxLen) or 2000
    if #s <= maxLen then
        return s
    end
    return s:sub(1, maxLen) .. "...(katkaistu)"
end

local function say_all(message)
    local msg = tostring(message or "")
    if msg == "" then
        return
    end

    -- Escapeaa lainausmerkit serverikomennolle.
    msg = msg:gsub('\\', '\\\\'):gsub('"', '\\"')
    et.trap_SendServerCommand(-1, string.format('chat "%s"', msg))
end

local function say_client(clientNum, message)
    -- Botin virheet ovat epäluotettua tekstiä; pidetään ne yhdessä rajatussa chat-komennossa.
    local msg = tostring(message or ""):gsub("[%c]", " "):sub(1, 700)
    msg = msg:gsub('\\', '\\\\'):gsub('"', '\\"')
    et.trap_SendServerCommand(clientNum, string.format('chat "^3Voice:^7 %s"', msg))
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
    -- Ohjaa stderr mukaan, jotta curlin virheviestit eivät katoa.
    local p = io.popen(curl_cmd .. " 2>&1")
    if not p then
        return nil, "curlin käynnistys epäonnistui"
    end

    local output = p:read("*all")
    local ok, _, code = p:close()

    if type(ok) == "number" then
        code = ok
        ok = (code == 0)
    end

    if ok ~= true then
        return nil, string.format("curl epäonnistui (exit=%s)", tostring(code))
    end

    if not output or output == "" then
        return nil, "Tyhjä vastaus"
    end

    if not json then
        return nil, "dkjson ei ole saatavilla"
    end

    local decoded = json.decode(output)
    if not decoded then
        return nil, "JSONin purku epäonnistui"
    end

    return decoded
end

local function executeCurlJsonWithHttpStatus(curl_cmd, body_file)
    local p = io.popen(curl_cmd .. " 2>&1")
    if not p then
        return nil, "curlin käynnistys epäonnistui"
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
        return nil, string.format("curl epäonnistui (exit=%s)", tostring(code))
    end

    local status = output:match("HTTPSTATUS:(%d%d%d)")
    local httpCode = tonumber(status)
    local body = body_file and read_file_all(body_file) or ""

    if not httpCode then
        output = trim(output)
        if output == "" then
            return nil, "Tyhjä vastaus (HTTP-status puuttuu)"
        end
        return nil, output
    end

    if httpCode ~= 200 then
        if httpCode == 401 then
            return nil, "Ei oikeutta (401) - tarkista backendin token-asetukset"
        end
        if httpCode == 404 then
            return nil, "Ei löytynyt (404) - tarkista BALANCE_API_URL"
        end

        body = trim(body)
        if body ~= "" then
            return nil, string.format("HTTP %d - %s", httpCode, body)
        end
        return nil, string.format("HTTP %d (tyhjä body)", httpCode)
    end

    if body == "" then
        return nil, "Tyhjä vastausbody (HTTP 200)"
    end

    if not json then
        return nil, "dkjson ei ole saatavilla"
    end

    local decoded = json.decode(body)
    if not decoded then
        return nil, "JSONin purku epäonnistui"
    end

    return decoded
end

local function is_warmup_only()
    local gamestate = tonumber(trap_Cvar_Get("gamestate"))
    local roundNumber = tonumber(trap_Cvar_Get("g_currentRound"))
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
    -- Palauttaa:
    --   guids = { "...", ... } (uniikit, vakaa järjestys)
    --   nameByGuid = { [guid] = name }
    --   clientNumByGuid = { [guid] = clientNum }
    local guids = {}
    local seen = {}
    local nameByGuid = {}
    local clientNumByGuid = {}

    for clientNum = 0, maxClients - 1 do
        if gentity_get(clientNum, "pers.connected") == CON_CONNECTED then
            local team = safe_number(gentity_get(clientNum, "sess.sessionTeam"))
            -- 1=Axis, 2=Allies; ohita spectatorit/tuntemattomat.
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

                    -- Pidä uusin clientNum-mappaus (pitäisi pysyä vakaana yhteyden aikana).
                    clientNumByGuid[guid] = clientNum
                end
            end
        end
    end

    return guids, nameByGuid, clientNumByGuid
end

local function shell_quote(value)
    return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function cleanup_voice_request(request)
    for _, path in ipairs(request.files) do
        os.remove(path)
    end
end

local function start_voice_request(players, recipients, requester)
    if not json then
        return nil, "dkjson puuttuu"
    end
    if package.config:sub(1, 1) == "\\" then
        return nil, "Taustalla ajettavat voice-pyynnöt vaativat Linux/POSIX-peliserverin."
    end

    local base = os.tmpname()
    local request = {
        payload = base, body = base .. ".body", status = base .. ".status",
        done = base .. ".done", recipients = recipients, requester = requester,
        started = os.time()
    }
    request.files = { request.payload, request.body, request.status, request.done }
    local file = io.open(request.payload, "w")
    if not file then
        return nil, "Voice-pyyntötiedostoa ei voitu luoda."
    end
    file:write(json.encode({ players = players }))
    file:close()

    local curl = "curl -sS --connect-timeout 2 --max-time 15 --compressed -X POST"
        .. " -H " .. shell_quote("Authorization: Bearer " .. AUTH_TOKEN)
        .. " -H 'Content-Type: application/json' --data-binary " .. shell_quote("@" .. request.payload)
        .. " -o " .. shell_quote(request.body) .. " -w '%{http_code}' " .. shell_quote(VOICE_API_URL)
        .. " > " .. shell_quote(request.status)
    local cleanup = {}
    for _, path in ipairs(request.files) do
        table.insert(cleanup, shell_quote(path))
    end
    -- Merkitse valmiiksi vasta, kun curl sulkee vastauksen. Worker siivoaa myös,
    -- jos mapin vaihto purkaa tämän Lua VM:n ennen vastauksen käsittelyä.
    local command = "( " .. curl .. "; printf '%s' \"$?\" > " .. shell_quote(request.done)
        .. "; sleep 30; rm -f -- " .. table.concat(cleanup, " ")
        .. " ) </dev/null >/dev/null 2>&1 &"
    local r1, r2, r3 = os.execute(command)
    if not os_execute_ok(r1, r2, r3) then
        cleanup_voice_request(request)
        return nil, "Voice-pyyntöä ei voitu käynnistää."
    end
    table.insert(voiceRequests, request)
    return true
end

local function recipient_is_connected(recipient)
    return gentity_get(recipient.clientNum, "pers.connected") == CON_CONNECTED
        and guid_for_client(recipient.clientNum) == recipient.guid
end

local function handle_voice_command(clientNum, moveAll)
    if not is_warmup_only() then
        say_client(clientNum, "!voice ja !allutvittuun ovat sallittuja vain warmupissa ennen matsin alkua.")
        return
    end
    if moveAll and safe_number(gentity_get(clientNum, "sess.referee")) <= 0 then
        say_client(clientNum, "Vain refereet voivat käyttää komentoa !allutvittuun.")
        return
    end

    local now = os.time()
    local callerGuid = guid_for_client(clientNum)
    local lastUsed = moveAll and voiceAllLastUsed or voiceLastUsed[callerGuid]
    if lastUsed and now - lastUsed < VOICE_COOLDOWN_SECONDS then
        say_client(clientNum, "Odota muutama sekunti ennen seuraavaa voice-siirtoa.")
        return
    end

    local players, recipients, selected = {}, {}, {}
    for slot = 0, maxClients - 1 do
        if (moveAll or slot == clientNum) and gentity_get(slot, "pers.connected") == CON_CONNECTED then
            local team = safe_number(gentity_get(slot, "sess.sessionTeam"))
            local guid = guid_for_client(slot)
            if (team == 1 or team == 2) and guid ~= "" and not selected[guid] then
                local teamName = team == 1 and "axis" or "allies"
                table.insert(players, { guid = guid, team = teamName })
                table.insert(recipients, { guid = guid, clientNum = slot, team = teamName, teamNumber = team })
                selected[guid] = true
            end
        end
    end
    if #players == 0 then
        say_client(clientNum, "Liity ensin Axis- tai Allies-tiimiin; siirrettäviä pelaajia ei löytynyt.")
        return
    end
    for _, pending in ipairs(voiceRequests) do
        for _, recipient in ipairs(pending.recipients) do
            if selected[recipient.guid] then
                say_client(clientNum, "Näille pelaajille on jo voice-siirto käynnissä.")
                return
            end
        end
    end

    local requester = { clientNum = clientNum, guid = callerGuid, moveAll = moveAll }
    local ok, err = start_voice_request(players, recipients, requester)
    if not ok then
        say_client(clientNum, err)
        return
    end
    voiceLastUsed[callerGuid] = now
    if moveAll then
        voiceAllLastUsed = now
        say_all("^3Voice:^7 " .. name_for_client(clientNum) .. "^7 käytti komentoa !allutvittuun siirtääkseen kaikki tiimiensä voice-kanaville.")
    else
        say_client(clientNum, "Pyydetään siirtoa tiimisi voice-kanavalle...")
    end
end

function et_RunFrame(levelTime)
    if levelTime < nextVoicePoll then return end
    nextVoicePoll = levelTime + 200
    for i = #voiceRequests, 1, -1 do
        local request = voiceRequests[i]
        local exitCode = tonumber(read_file_all(request.done))
        if exitCode or os.time() - request.started > 20 then
            local response, failure
            local status = tonumber(read_file_all(request.status))
            if exitCode ~= 0 then
                failure = "Voice-pyyntö aikakatkaistiin tai epäonnistui. Tarkista Discord ennen uutta yritystä."
            elseif status ~= 200 then
                failure = "Voice API -pyyntö epäonnistui (HTTP " .. tostring(status or "tuntematon") .. ")."
            else
                response = json.decode(read_file_all(request.body))
                if type(response) ~= "table" or type(response.results) ~= "table" then
                    failure = "Voice API palautti virheellisen vastauksen."
                end
            end

            local moved = 0
            for _, recipient in ipairs(request.recipients) do
                local result
                if not failure then
                    for _, candidate in ipairs(response.results) do
                        if type(candidate) == "table" and tostring(candidate.guid):upper() == recipient.guid
                            and candidate.team == recipient.team then
                            result = candidate
                            break
                        end
                    end
                end
                if result and result.moved == true then moved = moved + 1 end
                if recipient_is_connected(recipient) then
                    if safe_number(gentity_get(recipient.clientNum, "sess.sessionTeam")) ~= recipient.teamNumber then
                        say_client(recipient.clientNum, "Tiimisi vaihtui voice-pyynnön aikana. Tarkista kanavasi Discordissa.")
                    else
                        say_client(recipient.clientNum, failure or (result and result.message) or "Voice API ei raportoinut siirtoasi. Tarkista Discord.")
                    end
                end
            end
            if request.requester.moveAll and recipient_is_connected(request.requester) then
                say_client(request.requester.clientNum, string.format("!allutvittuun: %d/%d siirtoa vahvistettu. Jokainen pelaaja sai oman tuloksensa.", moved, #request.recipients))
            end
            -- Käynnissä olevat workerit hoitavat siivouksen timeoutissa; älä poista tiedostoja, joita ne voivat vielä käyttää.
            if exitCode then cleanup_voice_request(request) end
            table.remove(voiceRequests, i)
        end
    end
end

local function build_payload_json(guids, sigmaMultiplier, mode)
    if not json then
        return nil, "dkjson ei ole saatavilla"
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
        return nil, "Temp-tiedoston luonti epäonnistui"
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
            "aikaUtc=%s\nurl=%s\nauth=%s\npyyntoTiedosto=%s\n",
            now_utc_iso(),
            BALANCE_API_URL,
            (authToken ~= "") and "yes" or "no",
            reqName))

        -- Kerro minne tiedostot päätyvät levyllä (best effort).
        say_all(string.format("^3Balance:^7 lokitus: %s", get_fs_log_hint(REQUEST_LOG_DIR)))
    end

    -- Vastaa pappastats.lua:n Authorization-headerin muotoilua.
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

    -- Best-effort-siivous.
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
    return "kokonaisrating"
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

    -- Käytäntö: Team1 -> Axis, Team2 -> Allies.
    force_team(t1Players, "axis")
    force_team(t2Players, "allies")
end

local function print_balance_result(response, nameByGuid, clientNumByGuid)
    if type(response) ~= "table" then
        say_all("^1Balance epäonnistui:^7 virheellinen vastaus")
        return
    end

    local p1 = response.winProbabilityTeam1
    local p2 = response.winProbabilityTeam2

    local team1 = response.team1 or {}
    local team2 = response.team2 or {}

    local t1Players = (team1.players or team1.Players or {})
    local t2Players = (team2.players or team2.Players or {})

    say_all(string.format("^3Balance:^7 Tiimi 1 %s vs Tiimi 2 %s ^3[%s]", format_pct(p1), format_pct(p2), format_mode(response.mode)))

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
            joined = "(ei ketään)"
        end

        say_all(string.format("^2%s:^7 %s", label, joined))
    end

    team_line("Tiimi 1", t1Players)
    team_line("Tiimi 2", t2Players)

    apply_team_assignments(t1Players, t2Players, clientNumByGuid)
    say_all("^3Balance:^7 tiimit asetettu (Team1->Axis, Team2->Allies)")
    say_all("^3Voice:^7 Kirjoita !voice chattiin warmupissa siirtyäksesi tiimisi voice-kanavalle.")
end

local function handle_balance_command(rawMessage)
    if #voiceRequests > 0 then
        say_all("^3Balance:^7 Odota keskeneräiset voice-siirrot ennen uutta balansointia.")
        return
    end
    if not is_warmup_only() then
        say_all("^1!balance^7 on sallittu vain warmupissa")
        return
    end

    local msg = trim(rawMessage)
    if msg == "" then
        return
    end

    local sigmaMultiplier = 3
    local mode = nil

    -- Parsitaan: !balance [3on3|4on4|5on5|6on6] [multiplier]
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
        say_all("^1Balance:^7 tiimeissä pitää olla vähintään 2 pelaajaa")
        return
    end

    if not json then
        say_all("^1Balance epäonnistui:^7 dkjson puuttuu")
        return
    end

    local payload_json, err = build_payload_json(guids, sigmaMultiplier, mode)
    if not payload_json then
        say_all("^1Balance epäonnistui:^7 " .. tostring(err))
        return
    end

    local response, postErr = post_balance_request(payload_json)
    if not response then
        say_all("^1Balance epäonnistui:^7 " .. tostring(postErr))
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
    log("ladattu")
end

function et_ClientCommand(clientNum, command)
    local cmd = et.trap_Argv(0)
    if cmd ~= "say" and cmd ~= "say_team" and cmd ~= "say_buddy" then
        return 0
    end

    local msg = et.ConcatArgs(1) or ""
    msg = trim(msg)

    local voiceCommand = msg:lower()
    if voiceCommand == "!voice" or voiceCommand == "!allutvittuun" then
        handle_voice_command(clientNum, voiceCommand == "!allutvittuun")
        -- Säilytä normaali chat-viesti, mukaan lukien lähettäjän oma chat-echo.
        return 0
    end

    if starts_with(msg, "!balance") then
        handle_balance_command(msg)
        -- Piilota komento, jotta se ei näy chatissa.
        return 0
    end

    return 0
end
