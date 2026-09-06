# server (.NET 10 Blazor + API)

This folder is for the **.NET 10** backend that receives match data from ET: Legacy Lua scripts in [et-server/](../et-server/README.md), persists it (SQLite or MariaDB), and provides a Blazor UI to browse collected stats.

## Expected responsibilities

- HTTP API endpoints for ingesting match/event payloads from the ET server
- Authentication for ingestion (API key / bearer token)
- Storage layer (SQLite or MariaDB)
- Blazor UI pages to browse matches, players, maps, etc.

## Prerequisites

- `.NET SDK 10`
- Optional: Docker Desktop (for MariaDB via compose)

## Local database (MariaDB)

The repo includes a MariaDB `docker compose` setup.

From repo root:

1) Copy [.env.example](../.env.example) → `.env` and adjust passwords if desired.
2) Start the database:

- `docker compose up -d`

The compose file exposes:

- MariaDB on `localhost:3306`

## Configuration (recommended)

Use environment variables for secrets.

Suggested environment variables:

- `Pappa__Db__Provider` – `sqlite` (default) or `mariadb`
- `Pappa__Db__ConnectionString` – provider connection string
- `Pappa__Ingest__Token` – token required by Lua/ingest callers
- `Pappa__Webhook__Url` – full webhook URL (optional)
- `Pappa__Webhook__Token` – webhook bearer token (optional)
- `Pappa__Webhook__FrontendBaseUrl` – public base URL used to build a link to MatchDetails (optional)
- `Pappa__Discord__ClientId` – Discord OAuth2 application client id (optional; leave empty to disable Discord login)
- `Pappa__Discord__ClientSecret` – Discord OAuth2 application client secret

Example MariaDB connection string (typical):

- `Server=127.0.0.1;Port=3306;Database=pappaetstats;User=pappa;Password=changeme;`

## API contract

The Lua script in [et-server/pappastats.lua](../et-server/pappastats.lua) POSTs a single **match summary** JSON document at end-of-round.

- `POST /api/matches`
  - Headers:
    - `Authorization: Bearer <token>`
  - Body:
    - One JSON object (example below)

### Team balancing

- `POST /api/skillratings/balance-teams`
  - Headers:
    - `Authorization: Bearer <token>` (same token as ingest)
  - Body:
    - `guids` – list of 32-hex-char player GUIDs (at least 2, no duplicates)
    - `sigmaMultiplier` – optional, defaults to `2.0`
    - `mode` – optional rating track: omit/`null` for overall, `3` for 3on3/4on4, `6` for 5on5/6on6

Every rated match updates the player's overall rating **and** the format-specific rating that
matches the team size (up to 4 players per team → 3on3/4on4, 5 or more → 5on5/6on6). All three
tracks use identical calculation logic. If a player has no history in the requested format, their
overall rating is used instead.

Existing history can be backfilled into the format-specific tracks with
`POST /api/admin/skillratings/recalculate` (admin token), which resets overall ratings to their
initial defaults, clears format-specific ratings, and replays completed matches chronologically.
Existing players, Discord links, and voice preferences are preserved, including players with no
eligible matches. Format-specific ratings remain unset until a match in that format is replayed.

### Discord login & account linking

Users can log in with Discord OAuth2 (`/auth/login/discord`). Login is enabled when
`Pappa__Discord__ClientId` and `Pappa__Discord__ClientSecret` are configured. The Discord
application must whitelist the redirect URI `{your-base-url}/auth/callback/discord`.

When a logged-in user is not yet linked to an ET player, the UI shows a prompt with an
in-game console command containing a one-time registration token:

```
/register 5f3e8a2b-1c4d-4e6f-9a7b-0c1d2e3f4a5b
```

The game server should capture that command (ET GUID of the player + the token) and forward
it to the registration endpoint below. The Lua module
[et-server/papparegister.lua](../et-server/papparegister.lua) implements this: it handles
`/register <token>` (console) and `!register <token>` (chat), posts the player's `cl_guid`
plus the token to the endpoint, and prints the backend's success/error message to the player.
Configure `REGISTER_API_URL` and `AUTH_TOKEN` at the top of the script.

Logged-in users who are linked can toggle **"Move automatically to a voice channel"** on the
`/settings` page. When disabled, their Discord id is excluded from the move-teams webhook
payload (see below).

### ET GUID registration

- `POST /api/players/register`
  - Headers:
    - `Authorization: Bearer <token>` (same token as ingest)
    - `Content-Type: application/json`
  - Body:
    - `etGuid` – the player's ET GUID (32 hex chars, no dashes) as seen by the game server
    - `token` – the registration token the user typed in the in-game `/register` command

Example:

```bash
curl -X POST http://localhost:5080/api/players/register \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-ingest-token" \
  -d '{"etGuid": "0123456789abcdef0123456789abcdef", "token": "5f3e8a2b-1c4d-4e6f-9a7b-0c1d2e3f4a5b"}'
```

Responses (all bodies are JSON; error bodies contain an `error` message suitable for showing to the user):

| Status | Body | Meaning |
| --- | --- | --- |
| `200 OK` | `{ "message", "etGuid", "discordId" }` | Linked successfully (or was already linked to the same Discord account). |
| `400 Bad Request` | `{ "error": "etGuid is required" }` | Missing `etGuid`. |
| `400 Bad Request` | `{ "error": "Invalid etGuid format (expected 32 hex chars)" }` | Malformed ET GUID. |
| `400 Bad Request` | `{ "error": "token is required" }` | Missing `token`. |
| `400 Bad Request` | `{ "error": "Invalid token format. ..." }` | Token is not a valid GUID. |
| `401 Unauthorized` | – | Missing/wrong ingest bearer token. |
| `404 Not Found` | `{ "error": "Unknown registration token. ..." }` | Token does not exist. |
| `404 Not Found` | `{ "error": "No player found with this ET GUID. ..." }` | The ET GUID has no stats yet; play a match first. |
| `409 Conflict` | `{ "error": "This registration token has already been used. ..." }` | Token was already consumed. |
| `409 Conflict` | `{ "error": "This player is already linked to a different Discord account." }` | ET GUID taken by another Discord user. |
| `409 Conflict` | `{ "error": "Your Discord account is already linked to another player." }` | Discord user already linked to a different ET GUID. |

### Move-teams webhook (bot integration)

After `POST /api/skillratings/balance-teams` computes the balanced teams, the server POSTs the
Discord ids of the linked players to the bot so it can move them to their team voice channels.
The URL is derived from `Pappa__Webhook__Url` (same host, path `/webhook/move-teams`), and the
`Pappa__Webhook__Token` is sent as the `X-Webhook-Secret` header:

```bash
POST {webhook-host}/webhook/move-teams
Content-Type: application/json
X-Webhook-Secret: your-secret

{"axis": ["123", "321"], "allies": ["567", "542"]}
```

`axis` contains the Discord ids of `team1` and `allies` those of `team2` from the balance
response. Players without a linked Discord account, or with the "Move automatically to a
voice channel" setting disabled, are omitted. The call is skipped when no webhook URL is
configured or when no player in the request has a Discord id to send.

## What gets saved

This backend stores the incoming payload in a relational form.

Per match (1 row):

- `matchID`, `round`, `mapname`, `config`
- `servername`, `serverIp`, `serverPort`
- `defenderteam`, `winnerteam`
- `timelimit`, `nextTimeLimit`
- `roundStart`, `roundEnd`, `roundStartUnix`, `roundEndUnix`
- `rawJson` (canonical re-serialization for debugging/replay)

Per side/team (derived from `players[].team`):

- `team`
- `isDefender` (derived from `defenderteam`)
- `isWinner` (derived from `winnerteam`)
- Aggregates (computed): player count, total xp, total damage/gibs/etc

Per player (one row per `players[]` item, linked to match+team):

- `clientNum`, `guid`, `name`, `team`, `rounds`
- `xp`, `time_played_percent`
- `damage_given`, `damage_received`, `team_damage_given`, `team_damage_received`
- `gibs`, `self_kills`, `team_kills`, `team_gibs`
- `weapon_stats[]` (one row per weapon entry)

Per obituary (one row per `obituaries[]` item):

- `timestamp`, `target`, `attacker`, `meansOfDeath`, `attackerRespawnTime`, `victimRespawnTime`

### JSON example (what the API accepts)

```json
{"roundStart":0,"config":"defaultpublic","roundStartUnix":0,"mapname":"radar","defenderteam":1,"serverPort":"27962","winnerteam":1,"nextTimeLimit":"2:04","obituaries":[],"timelimit":"2:04","roundEnd":0,"players":[{"team_kills":0,"xp":3,"time_played_percent":97.338709677419,"clientNum":0,"guid":"3D0593E5921E0626A2A5DD3D518B1766","team_gibs":0,"gibs":0,"team":1,"rounds":2,"damage_received":0,"team_damage_received":0,"team_damage_given":0,"self_kills":2,"damage_given":0,"weapon_stats":[{"hits":0,"atts":5,"weapon":5,"deaths":0,"headshots":0,"kills":0},{"hits":0,"atts":1,"weapon":11,"deaths":0,"headshots":0,"kills":0},{"hits":0,"atts":1,"weapon":14,"deaths":0,"headshots":0,"kills":0}],"name":"^1.^0Cultti"}],"servername":"^1Pappaliiga.fi ^2Private ^0COMP","serverIp":"0.0.0.0","matchID":"1770194680","roundEndUnix":0,"round":2}
```

### Future: incremental events

If we later want near-real-time stats, we can add an events endpoint (NDJSON or batched JSON).

## Running

Once the project exists in this folder, typical commands will be:

- `dotnet restore`
- `dotnet run`

You’ll also want to ensure CORS is configured if you host UI and API separately.

## Production notes

- Put the API behind HTTPS (reverse proxy like nginx/Caddy) if receiving data over the internet.
- Rotate ingest tokens; log and reject unauthorized posts.
- Consider batching on the Lua side and idempotency on the API side (dedupe repeated events).

## Webhook (game completed)

If `Pappa:Webhook:Url` is configured, the server will `POST` a JSON payload after **round 2** is successfully ingested.

Payload shape:

- `map` (string)
- `winner` (string; `Team 1`, `Team 2`, or `Draw`)
- `teams` (array of `{ name, players[] }`)
- `link` (string; points directly to `/matches/{matchDbId}`)
