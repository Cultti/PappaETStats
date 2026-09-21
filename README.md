# PappaETStats

Monorepo for collecting **Enemy Territory: Legacy** match data and browsing it via a **.NET 10 Blazor** web UI.

## Folders

- `et-server/` – ET: Legacy **Lua** scripts that run on the game server and collect match events/stats.
- `server/` – **.NET 10** Blazor + API service that receives posts from `et-server` and exposes a frontend to browse data. Supports **SQLite** and **MariaDB**.

## High-level flow

1. ET: Legacy loads Lua modules from the `legacy/` directory.
2. Lua callbacks (e.g. match start/end, kills, player connect/disconnect) build a payload.
3. The payload is POSTed to the `.NET` API.
4. The `.NET` server persists to SQLite or MariaDB and renders pages in the Blazor UI.

## Data we collect (Stopwatch)

The primary game mode of interest is **Stopwatch**.

For each match:

- Map name
- Start timestamp
- End timestamp
- Winner (team with smallest time set; tie when no time is set for both teams)

Per side/team:

- Time set
- Players with per-side stats (kills/assists/deaths/gibs/self kills/team kills/team gibs/eff/damage given+received/team damage given+received/score)

## Prerequisites

### For `et-server` (ET: Legacy Lua)

- ET: Legacy dedicated server (Legacy mod) with Lua enabled.
- Lua in ET: Legacy is **Lua 5.4** (be mindful of incompatibilities vs 5.1/5.2/5.3).
- ET: Legacy Lua API docs: https://etlegacy-lua-docs.readthedocs.io/en/latest/

### For `server` (.NET)

- `.NET SDK 10` (or matching SDK version for this repo once code is added).
- Optional: Docker Desktop (for MariaDB via `docker-compose`).

## Configuration overview

### Lua → API

The Lua side typically needs:

- API base URL (example: `http://localhost:5080`)
- API auth (example: bearer token / API key)
- Server identifier (so multiple ET servers can post into one backend)

This repo intentionally keeps these as *config values* (cvars or a simple config file) so you can run multiple servers.

### Docker Compose deployment

The Compose file starts both the .NET app and MariaDB. It is compatible with
Docker Compose and nerdctl compose.

- [docker-compose.yml](docker-compose.yml)
- [.env.example](.env.example)

The app listens on port `5080` on the host and MariaDB is available to the host
on port `3307` by default. The app connects to MariaDB using the Compose service
name `mariadb`; do not use `localhost` in the app connection string. The
PappaGather bot is available to the app at `http://pappagather:8080` on the
internal Compose network.

 The single `.env` file contains the settings for all three services. Keep the
 service prefix when editing variables: `MARIADB__*` for MariaDB,
 `PAPPAETSTATS__*` for the .NET app, and `PAPPAGATHER__*` for the Discord bot.

## Quickstart

1) Prepare the environment:

- Copy `.env.example` → `.env` and edit values
- Keep `.env` private; it contains production credentials and is ignored by git.
- Stop and disable the old standalone bot unit before starting Compose:
	`sudo systemctl disable --now pappagather.service`

2) Pull and start the stack:

- nerdctl: `nerdctl pull ghcr.io/cultti/pappaetstats:latest && nerdctl compose up -d`
- Docker: `docker pull ghcr.io/cultti/pappaetstats:latest && docker compose up -d`

3) Check the app at `http://localhost:5080`.

The app applies pending EF Core migrations automatically when it starts.

### Run with systemd

Copy [pappaetstats.service](pappaetstats.service) to `/etc/systemd/system/`
on the server, then enable the stack:

```bash
sudo install -m 0644 pappaetstats.service /etc/systemd/system/pappaetstats.service
sudo systemctl disable --now pappagather.service 2>/dev/null || true
sudo nerdctl rm -f pappagather 2>/dev/null || true
sudo systemctl daemon-reload
sudo systemctl enable --now pappaetstats.service
sudo systemctl status pappaetstats.service
```

The unit runs Compose as `root`, so it uses root's system-wide nerdctl/containerd
instance. The service still reads the deployment files from
`/home/etlserver/etlstats`.

To update all images and recreate only containers whose image or configuration
changed, run:

```bash
sudo systemctl reload pappaetstats.service
```

The unit runs `nerdctl compose pull` before `nerdctl compose up -d`. A plain
`nerdctl restart` only restarts existing containers and does not download new
images. Use `systemctl restart` only when you intentionally want a full stop
and start.

View application logs with `nerdctl compose logs -f` from the deployment
directory, or inspect unit failures with `journalctl -u pappaetstats.service`.

## Import the existing MariaDB database

Create a dump from the old MariaDB instance before stopping it, then import it
into the new container. The target database and user are created from `.env` on
the first MariaDB start.

```bash
mysqldump --single-transaction --routines --triggers \
	-h 127.0.0.1 -u OLD_USER -p OLD_DATABASE > old-database.sql
set -a; . ./.env; set +a
nerdctl compose up -d mariadb
nerdctl exec -i pappaetstats-mariadb mariadb \
	-u"$MARIADB__USER" -p"$MARIADB__PASSWORD" "$MARIADB__DATABASE" < old-database.sql
nerdctl pull ghcr.io/cultti/pappaetstats:latest
nerdctl compose up -d
```

For a remote migration host, use the new container's mapped port (`3307` by
default) instead of the internal port `3306`. Do not delete the
`mariadb_data` volume after importing: it contains the migrated database.

4) Configure ET: Legacy Lua scripts:

- Follow [et-server/README.md](et-server/README.md)

## Notes

- The folders are currently empty scaffolding; these READMEs define the intended integration contract and deployment shape.
- If you already have an API route shape in mind (e.g. `/api/matches`, `/api/events`), add it to `server/README.md` and I can align the Lua docs/config with it.
