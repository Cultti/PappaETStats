# Switching to Oksii stats

The API accepts the format documented in [Oksii's stats.lua README](https://github.com/Oksii/legacy-configs/tree/main/luascripts). The Lua files already installed on the ET server can be used unchanged. This integration consumes stats; it does not implement Oksii's gather scheduling, roster enforcement or map rotation API.

## Database and existing data

The `OksiiStatsAndMatchSeries` EF migration adds these relationships:

```text
MatchSeries (one match between two rosters)
  Matches (one map, retained table and existing map IDs)
    MatchRounds (one report per round)
      MatchSides / MatchPlayers / weapon and class stats
      RoundEvents (ordered gamelog, with complete event JSON)
```

`Matches.SeriesId`, `MapNumber` and `SeriesTeam1Faction` connect each map to its match and maintain team identity across faction swaps. Existing `/matches/{mapId}` links, demos, registrations and ratings remain attached to the same records. Match overviews use `/match-series/{seriesId}`.

Both ingestion formats write the same relational stats and an Oksii-shaped `MatchRounds.PayloadJson` containing `round_info`, GUID-keyed `player_stats`, `metadata` and `gamelog`. Packed counters in this stored report are **per round**, identified by `metadata.counters = "per_round"`. `SourcePayloadJson` preserves the original Oksii report with its cumulative counters, metadata, spectators and optional/future fields. `RawJson` remains the retry fingerprint. Original source `matchID` values are retained separately from the internal map ID.

Optional player metrics have nullable columns. `DetailsJson` retains complete player details for stance, activity, speed, objectives, shoves and vehicles. Missing historical stats remain unavailable rather than being invented. Assists are absent on older engine builds; this is distinct from zero assists.

On startup the app applies migrations, backfills existing rounds in batches and groups existing maps chronologically. Backfill uses already stored counters without subtracting again, preserves class stats, and converts historical obituaries to gamelog events. Ratings are not replayed by this migration.

## Match grouping

The API reconstructs matches in chronological play order, including delayed uploads:

- Consecutive maps on the same server join when both teams have exactly the same full player GUIDs. Axis/Allies swaps are allowed, including between maps.
- A substitution, player changing between the two rosters, unequal roster between a map's two rounds, or configuration change starts a new match. Spectators do not enter roster comparisons.
- A match may contain any number of maps, including repeated instances of the same map.
- A gap longer than `Pappa:Ingest:MatchGapHours` starts a new match; the default is six hours. This prevents separate sessions with identical teams from merging. Set `Pappa__Ingest__MatchGapHours` to adjust it.
- The previous match ends at its last played timestamp when another roster/session begins. An incoming `metadata.scores.match_finished = true` also closes it. The last session is displayed as completed after the gap has elapsed.

Oksii server identity uses IP and port; changing the display hostname does not change server identity. The legacy endpoint retains its existing `serverId`/hostname checks. Legacy server IDs and Oksii server identities are separate namespaces, so the transition itself starts a new match.

No custom Lua start/end notifier is needed for automatic grouping. With unchanged rosters there is no way to tell immediately whether a further map will follow using end-of-round reports alone. The gap rule or Oksii's explicit finished flag resolves that boundary.

Oksii `matchID` may span several maps or use a different Unix fallback each round. It is not used as a unique map key. Rounds are paired by server, map and their Unix play times, within six hours. Retries preserve row IDs and do not apply ratings or send completion webhooks twice. A late round one re-normalizes an already stored Oksii round two in the same transaction. A completed map applies the existing ratings and webhook behavior once both rounds are available.

One API process should own ingestion for a database. Round writes and grouping are serialized within that process; multiple independent writers would need a database-level coordination mechanism.

## Server configuration

Use the existing bearer token configured in `Pappa:Ingest:Token`. Point Oksii's startup environment at:

```text
STATS_API_URL_SUBMIT=https://et.aukko.net/api/v2/stats/etl/matches/stats/submit
STATS_API_URL_MATCHID=https://et.aukko.net/api/v2/stats/etl/matches/matchid
STATS_API_TOKEN=<same bearer token as Pappa:Ingest:Token>
STATS_SUBMIT=true
STATS_API_VERSION_CHECK=false
STATS_GATHER_FEATURES=false
STATS_AUTO_RENAME=false
STATS_AUTO_SORT=false
STATS_AUTO_START=false
STATS_AUTO_MAP=false
STATS_AUTO_CONFIG=false
STATS_AUTO_SCORES=false
STATS_API_GAMELOG=true
STATS_API_OBJSTATS=true
STATS_API_MOVEMENTSTATS=true
STATS_API_STANCESTATS=true
STATS_API_ACTIVITYSTATS=true
STATS_API_SHOVESTATS=true
STATS_API_VEHICLESTATS=true
```

The supported authenticated routes are:

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/v2/stats/etl/matches/stats/submit` | Oksii round report |
| GET | `/api/v2/stats/etl/matches/matchid/{ip}/{port}` | Oksii's `{ match_id }` response; informational source ID, grouping happens at ingestion |
| POST | `/api/v2/stats/etl/matches/players/notify` | Preserve countdown roster/spectator snapshots, idempotently |
| POST | `/api/matches` | Existing legacy payload, stored in the shared schema |
| GET | `/api/matches/matchid` | Existing legacy round pairing |
| POST | `/api/ready-ups` | Existing last-ready-up events |

Roster notifications are optional for grouping, and the first countdown can legitimately have none: Oksii skips them until it has cached an ID. Disable upstream version checks because this app does not maintain Oksii's version service. Keep gather automation disabled because those features expect a match-manager service beyond stats ingestion.

Stop `pappastats.lua` round submissions when enabling Oksii submissions for that server. The two producers describe the same rounds under different identities, so simultaneously sending both would double-count games. Existing clients on other servers can continue using the legacy endpoints. The independent registration, balancing and position scripts can remain enabled.

Load [`pappaready.lua`](../et-server/pappaready.lua) alongside Oksii's `stats.lua` for last-ready-up reporting. It replaces the ready-up part of `pappastats.lua`, uses the existing `pappa_api_key_file` token configuration and `pappa_stats_server_id`, and sends only to `/api/ready-ups`. Keep `dkjson` available and install `curl` on the ET server. Add `pappaready.lua` to the server's existing `lua_modules` list and remove `pappastats.lua` from that list. Retain the other enabled Lua modules.

Demo uploading is unused and needs no replacement sender. The existing demo API and any stored demos remain available.

## Rollout

1. Back up the production database and demo storage, and retain the current application build.
2. Stop ingestion while deploying the new application. On its first startup, the app applies the additive migration and completes historical backfill before serving requests. Backfill can take time on a large history.
3. Verify existing player history, map links and demos. Match list now has grouped matches and an individual-map list; map details, combined results and player pages include the new metrics. Scoreboards distinguish maps from matches and rank newly collected stats. Their existing eligibility rule is twenty maps and activity within a month.
4. Update the ET server's Oksii configuration above and switch its stats producer. Verify both reports of one test map, a subsequent map with the same teams, then a roster change.
5. Keep the backup until the new ingestion has been verified. Restoring a previous build requires restoring the matching database backup as well; merely removing the new migration would lose the new stats.

The implementation is verified with endpoint tests, SQLite upgrade/backfill tests and server-rendered page checks. A live MariaDB upgrade and browser interaction checks should be exercised in the deployment environment.
