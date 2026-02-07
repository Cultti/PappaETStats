using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Options;

namespace PappaETStats.Server.Api;

public static class IngestEndpoints
{
    private sealed record Round1WeaponSnapshot(int Hits, int Atts, int Kills, int Deaths, int Headshots);

    private sealed record Round1PlayerSnapshot(
        int Xp,
        int DamageGiven,
        int DamageReceived,
        int TeamDamageGiven,
        int TeamDamageReceived,
        int Gibs,
        int SelfKills,
        int TeamKills,
        int TeamGibs,
        IReadOnlyDictionary<int, Round1WeaponSnapshot> WeaponByWeapon);

    private static bool LooksCumulative(IEnumerable<(int r1, int r2)> pairs)
    {
        var total = 0;
        var nonNeg = 0;
        var positive = 0;

        foreach (var (r1, r2) in pairs)
        {
            total++;
            var diff = r2 - r1;
            if (diff >= 0)
            {
                nonNeg++;
            }
            if (diff > 0)
            {
                positive++;
            }
        }

        if (total == 0)
        {
            return false;
        }

        // Heuristic: if most diffs are non-negative and at least one increased, treat as cumulative.
        return positive > 0 && (double)nonNeg / total >= 0.80;
    }

    private static bool LooksCumulativeWeaponStats(
        IEnumerable<PlayerDto> round2Players,
        IReadOnlyDictionary<string, Round1PlayerSnapshot> round1ByGuid)
    {
        var total = 0;
        var nonNeg = 0;
        var positive = 0;

        foreach (var p2 in round2Players)
        {
            if (string.IsNullOrWhiteSpace(p2.Guid))
            {
                continue;
            }

            if (!round1ByGuid.TryGetValue(p2.Guid, out var p1))
            {
                continue;
            }

            foreach (var w2 in p2.WeaponStats ?? [])
            {
                if (!p1.WeaponByWeapon.TryGetValue(w2.Weapon, out var w1))
                {
                    continue;
                }

                total++;

                var hitsDiff = w2.Hits - w1.Hits;
                var attsDiff = w2.Atts - w1.Atts;
                var killsDiff = w2.Kills - w1.Kills;
                var deathsDiff = w2.Deaths - w1.Deaths;
                var hsDiff = w2.Headshots - w1.Headshots;

                if (hitsDiff >= 0 && attsDiff >= 0 && killsDiff >= 0 && deathsDiff >= 0 && hsDiff >= 0)
                {
                    nonNeg++;
                }

                if (hitsDiff > 0 || attsDiff > 0 || killsDiff > 0 || deathsDiff > 0 || hsDiff > 0)
                {
                    positive++;
                }
            }
        }

        if (total == 0)
        {
            return false;
        }

        return positive > 0 && (double)nonNeg / total >= 0.80;
    }

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api")
            .WithTags("Ingest");

        group.MapGet("/matches/matchid", GetOrCreateMatchIdAsync)
            .WithName("GetOrCreateMatchId")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/matches", IngestMatchAsync)
            .WithName("IngestMatch")
            .Accepts<MatchIngestDto>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    private static IResult? ValidateBearerToken(HttpRequest request, IOptions<IngestOptions> ingestOptions)
    {
        var configuredToken = ingestOptions.Value.Token;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return null;
        }

        var authHeader = request.Headers.Authorization.ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        return string.Equals(token, configuredToken, StringComparison.Ordinal)
            ? null
            : Results.Unauthorized();
    }

    private static async Task<IResult> GetOrCreateMatchIdAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        string? serverIp,
        string? serverPort,
        string? mapname,
        int? round,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        if (string.IsNullOrWhiteSpace(serverIp))
        {
            return Results.BadRequest(new { error = "serverIp is required" });
        }

        if (string.IsNullOrWhiteSpace(serverPort))
        {
            return Results.BadRequest(new { error = "serverPort is required" });
        }

        if (string.IsNullOrWhiteSpace(mapname))
        {
            return Results.BadRequest(new { error = "mapname is required" });
        }

        var resolvedRound = round ?? 0;
        if (resolvedRound is not (1 or 2))
        {
            return Results.BadRequest(new { error = "round is required (1 or 2)" });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (resolvedRound == 1)
        {
            return Results.Ok(new { matchId = Guid.NewGuid().ToString("N") });
        }

        // Prefer reusing an "open" match (round 1 exists, round 2 missing) for the same server+map.
        // This allows the Lua script to reliably pair round 2 with the match created for round 1.
        var cutoff = DateTime.UtcNow.AddHours(-6);

        var candidates = await db.MatchRounds
            .Where(r => r.IngestedAtUtc >= cutoff && r.Match.ServerIp == serverIp && r.Match.ServerPort == serverPort && r.Match.MapName == mapname)
            .OrderByDescending(r => r.IngestedAtUtc)
            .Take(200)
            .Select(r => new { r.Match.ExternalMatchId, r.RoundNumber, r.IngestedAtUtc })
            .ToListAsync(cancellationToken);

        var openMatchId = candidates
            .GroupBy(c => c.ExternalMatchId)
            .Select(g => new
            {
                ExternalMatchId = g.Key,
                Latest = g.Max(x => x.IngestedAtUtc),
                HasRound1 = g.Any(x => x.RoundNumber == 1),
                HasRound2 = g.Any(x => x.RoundNumber == 2)
            })
            // Round 2 should match a match that has round 1 ingested but not round 2 yet.
            .Where(x => x.HasRound1 && !x.HasRound2)
            .OrderByDescending(x => x.Latest)
            .Select(x => x.ExternalMatchId)
            .FirstOrDefault();

        var matchId = openMatchId ?? Guid.NewGuid().ToString("N");
        return Results.Ok(new { matchId });
    }

    private static async Task<IResult> IngestMatchAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        MatchIngestDto dto,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        if (string.IsNullOrWhiteSpace(dto.MatchId))
        {
            return Results.BadRequest(new { error = "matchID is required" });
        }

        if (dto.Round <= 0)
        {
            return Results.BadRequest(new { error = "round must be > 0" });
        }

        if (string.IsNullOrWhiteSpace(dto.MapName))
        {
            return Results.BadRequest(new { error = "mapname is required" });
        }

        var players = dto.Players ?? [];
        var groupedByTeam = players
            .GroupBy(p => p.Team)
            .OrderBy(g => g.Key)
            .ToList();

        if (groupedByTeam.Count == 0)
        {
            return Results.BadRequest(new { error = "players[] is required" });
        }

        var existingMatch = await db.Matches
            .FirstOrDefaultAsync(m => m.ExternalMatchId == dto.MatchId, cancellationToken);

        Match match;
        var isNewMatch = existingMatch is null;
        if (existingMatch is null)
        {
            match = new Match
            {
                Id = Guid.NewGuid(),
                ExternalMatchId = dto.MatchId,
                MapName = dto.MapName,
                Config = dto.Config ?? string.Empty,
                ServerName = dto.ServerName ?? string.Empty,
                ServerIp = dto.ServerIp ?? string.Empty,
                ServerPort = dto.ServerPort ?? string.Empty,
            };
        }
        else
        {
            match = existingMatch;
            // Keep match-level info current.
            match.MapName = dto.MapName;
            match.Config = dto.Config ?? string.Empty;
            match.ServerName = dto.ServerName ?? string.Empty;
            match.ServerIp = dto.ServerIp ?? string.Empty;
            match.ServerPort = dto.ServerPort ?? string.Empty;
        }

        var existingRound = await db.MatchRounds
            .Include(r => r.Sides)
                .ThenInclude(s => s.Players)
                    .ThenInclude(p => p.WeaponStats)
            .Include(r => r.Sides)
                .ThenInclude(s => s.Players)
                    .ThenInclude(p => p.ClassStats)
            .Include(r => r.Obituaries)
            .FirstOrDefaultAsync(r => r.MatchId == match.Id && r.RoundNumber == dto.Round, cancellationToken);

        if (existingRound is not null)
        {
            db.MatchRounds.Remove(existingRound);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Normalize round 2 stats to per-round values if the source provides cumulative match totals.
        // ET can keep some sess.* values across map_restart, which makes round 2 partially aggregated.
        // We detect cumulatives by comparing against ingested round 1 values for the same GUID.
        Dictionary<string, Round1PlayerSnapshot> round1ByGuid = new(StringComparer.OrdinalIgnoreCase);
        if (dto.Round == 2 && !isNewMatch)
        {
            var round1 = await db.MatchRounds
                .AsNoTracking()
                .Include(r => r.Sides)
                    .ThenInclude(s => s.Players)
                        .ThenInclude(p => p.WeaponStats)
                .FirstOrDefaultAsync(r => r.MatchId == match.Id && r.RoundNumber == 1, cancellationToken);

            if (round1 is not null)
            {
                foreach (var p1 in round1.Sides.SelectMany(s => s.Players))
                {
                    if (string.IsNullOrWhiteSpace(p1.Guid))
                    {
                        continue;
                    }

                    var byWeapon = p1.WeaponStats
                        .GroupBy(w => w.Weapon)
                        .ToDictionary(
                            g => g.Key,
                            g =>
                            {
                                var hits = g.Sum(x => x.Hits);
                                var atts = g.Sum(x => x.Atts);
                                var kills = g.Sum(x => x.Kills);
                                var deaths = g.Sum(x => x.Deaths);
                                var headshots = g.Sum(x => x.Headshots);
                                return new Round1WeaponSnapshot(hits, atts, kills, deaths, headshots);
                            });

                    round1ByGuid[p1.Guid] = new Round1PlayerSnapshot(
                        Xp: p1.Xp,
                        DamageGiven: p1.DamageGiven,
                        DamageReceived: p1.DamageReceived,
                        TeamDamageGiven: p1.TeamDamageGiven,
                        TeamDamageReceived: p1.TeamDamageReceived,
                        Gibs: p1.Gibs,
                        SelfKills: p1.SelfKills,
                        TeamKills: p1.TeamKills,
                        TeamGibs: p1.TeamGibs,
                        WeaponByWeapon: byWeapon);
                }
            }
        }

        var canNormalizeRound2 = dto.Round == 2 && round1ByGuid.Count > 0;
        var weaponStatsAreCumulative = canNormalizeRound2 && LooksCumulativeWeaponStats(players, round1ByGuid);

        var xpIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].Xp, p.Xp)));
        var dmgGivenIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].DamageGiven, p.DamageGiven)));
        var dmgReceivedIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].DamageReceived, p.DamageReceived)));
        var teamDmgGivenIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].TeamDamageGiven, p.TeamDamageGiven)));
        var teamDmgReceivedIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].TeamDamageReceived, p.TeamDamageReceived)));
        var gibsIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].Gibs, p.Gibs)));
        var selfKillsIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].SelfKills, p.SelfKills)));
        var teamKillsIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].TeamKills, p.TeamKills)));
        var teamGibsIsCumulative = canNormalizeRound2 && LooksCumulative(players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid) && round1ByGuid.ContainsKey(p.Guid!))
            .Select(p => (round1ByGuid[p.Guid!].TeamGibs, p.TeamGibs)));

        var round = new MatchRound
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            RoundNumber = dto.Round,
            DefenderTeam = dto.DefenderTeam,
            WinnerTeam = dto.WinnerTeam,
            TimeLimit = dto.TimeLimit ?? string.Empty,
            NextTimeLimit = dto.NextTimeLimit ?? string.Empty,
            RoundStartMs = dto.RoundStart,
            RoundEndMs = dto.RoundEnd,
            RoundStartUnix = dto.RoundStartUnix,
            RoundEndUnix = dto.RoundEndUnix,
            IngestedAtUtc = DateTime.UtcNow,
            RawJson = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = false })
        };

        foreach (var teamGroup in groupedByTeam)
        {
            var sidePlayers = teamGroup.ToList();

            var side = new MatchSide
            {
                Id = Guid.NewGuid(),
                Team = teamGroup.Key,
                IsDefender = teamGroup.Key == dto.DefenderTeam,
                IsWinner = teamGroup.Key == dto.WinnerTeam,
                PlayerCount = sidePlayers.Count,
                // Totals are filled after players are normalized and added.
                TotalXp = 0,
                TotalDamageGiven = 0,
                TotalDamageReceived = 0,
                TotalTeamDamageGiven = 0,
                TotalTeamDamageReceived = 0,
                TotalGibs = 0,
                TotalSelfKills = 0,
                TotalTeamKills = 0,
                TotalTeamGibs = 0,
            };

            foreach (var p in sidePlayers)
            {
                round1ByGuid.TryGetValue(p.Guid ?? string.Empty, out var p1);

                static int Delta(bool shouldDelta, int r2, int r1)
                    => shouldDelta ? Math.Max(0, r2 - r1) : r2;

                var player = new MatchPlayer
                {
                    Id = Guid.NewGuid(),
                    ClientNum = p.ClientNum,
                    Guid = p.Guid ?? string.Empty,
                    Name = p.Name ?? string.Empty,
                    Team = p.Team,
                    Rounds = p.Rounds,
                    Xp = p1 is null ? p.Xp : Delta(xpIsCumulative, p.Xp, p1.Xp),
                    TimePlayedPercent = p.TimePlayedPercent,
                    DamageGiven = p1 is null ? p.DamageGiven : Delta(dmgGivenIsCumulative, p.DamageGiven, p1.DamageGiven),
                    DamageReceived = p1 is null ? p.DamageReceived : Delta(dmgReceivedIsCumulative, p.DamageReceived, p1.DamageReceived),
                    TeamDamageGiven = p1 is null ? p.TeamDamageGiven : Delta(teamDmgGivenIsCumulative, p.TeamDamageGiven, p1.TeamDamageGiven),
                    TeamDamageReceived = p1 is null ? p.TeamDamageReceived : Delta(teamDmgReceivedIsCumulative, p.TeamDamageReceived, p1.TeamDamageReceived),
                    Gibs = p1 is null ? p.Gibs : Delta(gibsIsCumulative, p.Gibs, p1.Gibs),
                    SelfKills = p1 is null ? p.SelfKills : Delta(selfKillsIsCumulative, p.SelfKills, p1.SelfKills),
                    TeamKills = p1 is null ? p.TeamKills : Delta(teamKillsIsCumulative, p.TeamKills, p1.TeamKills),
                    TeamGibs = p1 is null ? p.TeamGibs : Delta(teamGibsIsCumulative, p.TeamGibs, p1.TeamGibs),
                };

                foreach (var w in p.WeaponStats ?? [])
                {
                    var r1Weapon = (weaponStatsAreCumulative && p1 is not null && p1.WeaponByWeapon.TryGetValue(w.Weapon, out var w1))
                        ? w1
                        : null;

                    player.WeaponStats.Add(new MatchPlayerWeaponStat
                    {
                        Id = Guid.NewGuid(),
                        Weapon = w.Weapon,
                        Hits = r1Weapon is null ? w.Hits : Math.Max(0, w.Hits - r1Weapon.Hits),
                        Atts = r1Weapon is null ? w.Atts : Math.Max(0, w.Atts - r1Weapon.Atts),
                        Kills = r1Weapon is null ? w.Kills : Math.Max(0, w.Kills - r1Weapon.Kills),
                        Deaths = r1Weapon is null ? w.Deaths : Math.Max(0, w.Deaths - r1Weapon.Deaths),
                        Headshots = r1Weapon is null ? w.Headshots : Math.Max(0, w.Headshots - r1Weapon.Headshots)
                    });
                }

                foreach (var c in p.ClassStats ?? [])
                {
                    if (c.Ms <= 0)
                    {
                        continue;
                    }

                    player.ClassStats.Add(new MatchPlayerClassStat
                    {
                        Id = Guid.NewGuid(),
                        ClassId = c.ClassId,
                        Ms = c.Ms
                    });
                }

                side.Players.Add(player);
            }

            side.TotalXp = side.Players.Sum(p => p.Xp);
            side.TotalDamageGiven = side.Players.Sum(p => (long)p.DamageGiven);
            side.TotalDamageReceived = side.Players.Sum(p => (long)p.DamageReceived);
            side.TotalTeamDamageGiven = side.Players.Sum(p => (long)p.TeamDamageGiven);
            side.TotalTeamDamageReceived = side.Players.Sum(p => (long)p.TeamDamageReceived);
            side.TotalGibs = side.Players.Sum(p => (long)p.Gibs);
            side.TotalSelfKills = side.Players.Sum(p => (long)p.SelfKills);
            side.TotalTeamKills = side.Players.Sum(p => (long)p.TeamKills);
            side.TotalTeamGibs = side.Players.Sum(p => (long)p.TeamGibs);

            round.Sides.Add(side);
        }

        foreach (var o in dto.Obituaries ?? [])
        {
            round.Obituaries.Add(new MatchObituary
            {
                Id = Guid.NewGuid(),
                TimestampMs = o.Timestamp,
                TargetGuid = o.Target,
                AttackerGuid = o.Attacker,
                MeansOfDeath = o.MeansOfDeath,
                AttackerRespawnTime = o.AttackerRespawnTime,
                VictimRespawnTime = o.VictimRespawnTime,
            });
        }

        if (isNewMatch)
        {
            db.Matches.Add(match);
        }

        db.MatchRounds.Add(round);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { matchId = match.ExternalMatchId, round = round.RoundNumber, matchDbId = match.Id, roundDbId = round.Id });
    }
}
