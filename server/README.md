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

Example MariaDB connection string (typical):

- `Server=127.0.0.1;Port=3306;Database=pappaetstats;User=pappa;Password=changeme;`

## API contract

The Lua script in [et-server/pappastats.lua](../et-server/pappastats.lua) POSTs a single **match summary** JSON document at end-of-round.

- `POST /api/matches`
  - Headers:
    - `Authorization: Bearer <token>`
  - Body:
    - One JSON object (example below)

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
