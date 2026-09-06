-- ============================================================================
-- PappaRegister - Discord account linking command for ET: Legacy
--
-- Usage (in console):
--   /register <token>
--
-- Also works in chat:
--   !register <token>
--
-- The token is shown to the user on the stats website after logging in with
-- Discord. This module posts the player's ET GUID + token to the backend
-- registration endpoint, which links the ET player to the Discord account.
--
-- Backend endpoint: POST /api/players/register
--   Body: { "etGuid": "<32 hex chars>", "token": "<registration token>" }
--   Auth: "Authorization: Bearer <token>" header (same token as ingest).
-- ============================================================================

local modname = "PappaRegister"
local version = "1.0-dev"

-- Backend endpoint
local REGISTER_API_URL = "http://localhost:5080/api/players/register"

-- Token used by backend (same token as ingest endpoints).
-- Keep empty to omit Authorization header.
local AUTH_TOKEN = "1234567890"

local json_ok, json = pcall(require, "dkjson")
if not json_ok then
    json = nil
end

local trap_GetUserinfo = et.trap_GetUserinfo
local Info_ValueForKey = et.Info_ValueForKey

local function log(message)
    et.G_Print(string.format("^2[%s]^7 %s\n", modname, tostring(message)))
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

local function say_player(clientNum, message)
    local msg = tostring(message or "")
    if msg == "" then
        return
    end

    -- Escape double quotes for server command.
    msg = msg:gsub('\\', '\\\\'):gsub('"', '\\"')
    et.trap_SendServerCommand(clientNum, string.format('chat "%s"', msg))
end

local function guid_for_client(clientNum)
    local userinfo = trap_GetUserinfo(clientNum)
    if not userinfo or userinfo == "" then
        return ""
    end

    return string.upper(Info_ValueForKey(userinfo, "cl_guid") or "")
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

-- Executes curl, returns (httpCode, decodedBody, err).
-- decodedBody may be nil when the response body is empty or not JSON.
local function executeCurlJsonWithHttpStatus(curl_cmd, body_file)
    local p = io.popen(curl_cmd .. " 2>&1")
    if not p then
        return nil, nil, "Failed to start curl"
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
            return nil, nil, output
        end
        return nil, nil, string.format("curl failed (exit=%s)", tostring(code))
    end

    local status = output:match("HTTPSTATUS:(%d%d%d)")
    local httpCode = tonumber(status)
    if not httpCode then
        return nil, nil, "Empty response (no HTTP status)"
    end

    local body = body_file and read_file_all(body_file) or ""
    local decoded = nil
    if json and body ~= "" then
        decoded = json.decode(body)
    end

    return httpCode, decoded, nil
end

local function post_register_request(etGuid, registrationToken)
    if not json then
        return nil, nil, "dkjson not available"
    end

    local payload_json = json.encode({
        etGuid = etGuid,
        token = registrationToken
    })

    local temp_file = os.tmpname() .. ".json"
    local body_file = os.tmpname() .. ".out"
    local f = io.open(temp_file, "w")
    if not f then
        return nil, nil, "Failed to create temp file"
    end
    f:write(payload_json)
    f:close()

    local authToken = tostring(AUTH_TOKEN or "")

    -- Match pappabalance.lua Authorization header formatting.
    local curl_cmd = string.format(
        'curl -sS -o "%s" -w "HTTPSTATUS:%%{http_code}" -X POST -H "Authorization: Bearer %s" -H "Content-Type: application/json" --compressed --connect-timeout 2 --max-time 15 --data-binary @"%s" "%s"',
        tostring(body_file),
        tostring(authToken),
        tostring(temp_file),
        tostring(REGISTER_API_URL)
    )

    local httpCode, decoded, err = executeCurlJsonWithHttpStatus(curl_cmd, body_file)

    -- Best-effort cleanup.
    os.remove(temp_file)
    os.remove(body_file)

    return httpCode, decoded, err
end

local function handle_register_command(clientNum, registrationToken)
    registrationToken = trim(registrationToken)
    if registrationToken == "" then
        say_player(clientNum, "^3Register:^7 usage: /register <token> (get your token from the stats website)")
        return
    end

    local etGuid = guid_for_client(clientNum)
    if etGuid == "" then
        say_player(clientNum, "^1Register failed:^7 could not read your GUID (cl_guid)")
        return
    end

    local httpCode, response, err = post_register_request(etGuid, registrationToken)
    if not httpCode then
        say_player(clientNum, "^1Register failed:^7 " .. tostring(err))
        log(string.format("register request failed for client %d: %s", clientNum, tostring(err)))
        return
    end

    if httpCode == 200 then
        local message = (type(response) == "table" and response.message) or "Registration successful!"
        say_player(clientNum, "^2Register:^7 " .. tostring(message))
        return
    end

    if httpCode == 401 then
        say_player(clientNum, "^1Register failed:^7 server configuration error (unauthorized). Contact an admin.")
        log("register request unauthorized (401) - check AUTH_TOKEN / backend ingest token")
        return
    end

    -- Backend returns { "error": "..." } with a user-friendly message for 400/404/409.
    local errorMessage = (type(response) == "table" and response.error) or string.format("HTTP %d", httpCode)
    say_player(clientNum, "^1Register failed:^7 " .. tostring(errorMessage))
end

function et_InitGame(levelTime, randomSeed, restart)
    et.RegisterModname(modname .. " " .. version)
    log("loaded")
end

function et_ClientCommand(clientNum, command)
    local cmd = string.lower(tostring(et.trap_Argv(0) or ""))

    -- Console command: /register <token>
    if cmd == "register" then
        handle_register_command(clientNum, et.ConcatArgs(1) or "")
        return 1
    end

    -- Chat command: !register <token>
    if cmd == "say" or cmd == "say_team" or cmd == "say_buddy" then
        local msg = trim(et.ConcatArgs(1) or "")
        if starts_with(msg, "!register") then
            handle_register_command(clientNum, msg:sub(#"!register" + 1))
            -- Swallow command so the token doesn't appear in chat.
            return 1
        end
    end

    return 0
end
