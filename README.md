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

### MariaDB

For local development, there’s an optional compose file:

- [docker-compose.yml](docker-compose.yml)
- [.env.example](.env.example)

## Quickstart (local dev)

1) Start MariaDB (optional):

- Copy `.env.example` → `.env` and edit values
- Run: `docker compose up -d`

2) Start the .NET server:

- Follow [server/README.md](server/README.md)

3) Configure ET: Legacy Lua scripts:

- Follow [et-server/README.md](et-server/README.md)

## Notes

- The folders are currently empty scaffolding; these READMEs define the intended integration contract and deployment shape.
- If you already have an API route shape in mind (e.g. `/api/matches`, `/api/events`), add it to `server/README.md` and I can align the Lua docs/config with it.
