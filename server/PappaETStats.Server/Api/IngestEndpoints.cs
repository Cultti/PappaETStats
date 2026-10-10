using System.Text.Json;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using PappaETStats.SkillRating;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Options;
using PappaETStats.Server.Services;
using PappaETStats.Server.Util;

namespace PappaETStats.Server.Api;

public static class IngestEndpoints
{
    // Serialize round replacements and chronological match grouping in this process.
    private static readonly SemaphoreSlim IngestLock = new(1, 1);
    private sealed record Round1WeaponSnapshot(int Hits, int Atts, int Kills, int Deaths, int Headshots);

    private static string ObituaryKey(long timestamp, string? target, string? attacker, int meansOfDeath)
        => $"{timestamp}|{target?.Trim().ToUpperInvariant()}|{attacker?.Trim().ToUpperInvariant()}|{meansOfDeath}";

    private static int NormalizeCountToInt(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value <= int.MaxValue)
        {
            return (int)value;
        }

        // Some sources can emit an unsigned 32-bit representation for counters.
        // If the sign bit is set (>= 2^31) but within uint32 range, interpret it by clearing the sign bit.
        // Example: 2147483660 (0x8000000C) -> 12.
        const long signBit = 2_147_483_648L;
        if (value >= signBit && value <= uint.MaxValue)
        {
            var normalized = value - signBit;
            return normalized <= int.MaxValue ? (int)normalized : int.MaxValue;
        }

        return int.MaxValue;
    }

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
                var attsDiff = NormalizeCountToInt(w2.Atts) - w1.Atts;
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
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/v2/stats/etl/matches/stats/submit", IngestOksiiAsync)
            .WithName("IngestOksiiStats");
        group.MapPost("/v2/stats/etl/matches/players/notify", IngestRosterAsync);
        group.MapGet("/v2/stats/etl/matches/matchid/{serverIp}/{serverPort}",
            (HttpRequest request, IOptions<IngestOptions> options, string serverIp, string serverPort) =>
                ValidateBearerToken(request, options) ?? Results.Ok(new { match_id = Guid.NewGuid().ToString("N") }));

        group.MapPost("/ready-ups", IngestLastReadyUpAsync)
            .WithName("IngestLastReadyUp")
            .Accepts<LastReadyUpDto>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> IngestLastReadyUpAsync(
        HttpRequest request, IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions, ScoreboardCache scoreboardCache,
        LastReadyUpDto dto, CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null) return authResult;

        var playerGuid = dto.PlayerGuid?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(dto.EventId) || dto.EventId.Length > 64
            || playerGuid is null || playerGuid.Length != 32 || !playerGuid.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(dto.ServerId) || dto.ServerId.Length > 256
            || string.IsNullOrWhiteSpace(dto.MapName) || dto.MapName.Length > 64
            || dto.Round is < 1 or > 2 || dto.ReadyAtUnix <= 0
            || dto.CountdownAtUnix < dto.ReadyAtUnix || dto.CountdownAtUnix > 253402300799)
        {
            return Results.BadRequest(new { error = "Invalid last ready-up event" });
        }

        var readyUp = new LastReadyUp
        {
            EventId = dto.EventId, PlayerGuid = playerGuid, ReadyAtUnix = dto.ReadyAtUnix,
            CountdownAtUnix = dto.CountdownAtUnix, ServerId = dto.ServerId.Trim(),
            MapName = dto.MapName.Trim(), Round = dto.Round,
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LastReadyUps.AsNoTracking().SingleOrDefaultAsync(
            r => r.EventId == dto.EventId, cancellationToken);
        if (existing is not null) return ReadyUpReplayResult(existing, readyUp);
        db.LastReadyUps.Add(readyUp);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent curl retries may race on the event's primary key.
            existing = await db.LastReadyUps.AsNoTracking().SingleOrDefaultAsync(
                r => r.EventId == dto.EventId, cancellationToken);
            if (existing is null) throw;
            return ReadyUpReplayResult(existing, readyUp);
        }
        scoreboardCache.Invalidate();
        return Results.Ok();
    }

    private static IResult ReadyUpReplayResult(LastReadyUp existing, LastReadyUp incoming)
        => existing.PlayerGuid == incoming.PlayerGuid && existing.ReadyAtUnix == incoming.ReadyAtUnix
            && existing.CountdownAtUnix == incoming.CountdownAtUnix && existing.ServerId == incoming.ServerId
            && existing.MapName == incoming.MapName && existing.Round == incoming.Round
            ? Results.Ok()
            : Results.Conflict(new { error = "Event ID already belongs to a different ready-up" });

    private static IResult? ValidateBearerToken(HttpRequest request, IOptions<IngestOptions> ingestOptions)
    {
        var configuredToken = ingestOptions.Value.Token;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return Results.Unauthorized();
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
        string? serverId,
        string? servername,
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

        serverId = string.IsNullOrWhiteSpace(serverId) ? null : serverId.Trim();
        if (serverId is null && string.IsNullOrWhiteSpace(servername))
        {
            return Results.BadRequest(new { error = "serverId or servername is required to pair round 2" });
        }

        if (serverId?.Length > 256 || servername?.Length > 256)
        {
            return Results.BadRequest(new { error = "serverId and servername must be at most 256 characters" });
        }

        // Prefer reusing an "open" match (round 1 exists, round 2 missing) for the same server+map.
        // This allows the Lua script to reliably pair round 2 with the match created for round 1.
        var cutoff = DateTime.UtcNow.AddHours(-6);

        var candidates = db.Matches
            .Where(m => m.ServerIp == serverIp && m.ServerPort == serverPort && m.MapName == mapname)
            .Where(m => m.Rounds.Any(r => r.RoundNumber == 1 && r.IngestedAtUtc >= cutoff)
                && !m.Rounds.Any(r => r.RoundNumber == 2));

        if (serverId is not null)
        {
            // Older matches have no ServerId. The hostname allows a safe transition
            // when the Lua module is upgraded between rounds.
            candidates = candidates.Where(m => m.ServerId == serverId
                || (m.ServerId == null && servername != null && m.ServerName == servername));
        }
        else
        {
            candidates = candidates.Where(m => m.ServerId == null && m.ServerName == servername);
        }

        var openMatchId = await candidates
            // A stored identity takes precedence over legacy hostname matching.
            .OrderByDescending(m => m.ServerId != null)
            .ThenByDescending(m => m.Rounds.Where(r => r.RoundNumber == 1).Max(r => r.IngestedAtUtc))
            .Select(m => m.ExternalMatchId)
            .FirstOrDefaultAsync(cancellationToken);

        var matchId = openMatchId ?? Guid.NewGuid().ToString("N");
        return Results.Ok(new { matchId });
    }

    private static async Task<IResult> IngestMatchAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        IOptions<WebhookOptions> webhookOptions,
        IOptions<SkillRatingOptions> skillRatingOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ScoreboardCache scoreboardCache,
        MatchIngestDto dto,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        await IngestLock.WaitAsync(cancellationToken);
        try
        {
            await using var strategyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                return await IngestMatchCoreAsync(db, transaction, webhookOptions, skillRatingOptions,
                    httpClientFactory, loggerFactory, scoreboardCache, dto, ingestOptions.Value.MatchGapHours, cancellationToken);
            });
        }
        finally { IngestLock.Release(); }
    }

    private static async Task<IResult> IngestOksiiAsync(HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory, IOptions<IngestOptions> ingestOptions,
        IOptions<WebhookOptions> webhookOptions, IOptions<SkillRatingOptions> skillRatingOptions,
        IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ScoreboardCache scoreboardCache,
        JsonElement payload, CancellationToken cancellationToken)
    {
        var auth = ValidateBearerToken(request, ingestOptions);
        if (auth != null) return auth;
        MatchIngestDto parsed;
        try
        {
            parsed = OksiiStatsAdapter.Parse(payload, "pending");
            if (parsed.Round is not (1 or 2) || string.IsNullOrWhiteSpace(parsed.MapName) || parsed.MapName.Length > 64
                || string.IsNullOrWhiteSpace(parsed.ServerIp) || parsed.ServerIp.Length > 64
                || string.IsNullOrWhiteSpace(parsed.ServerPort) || parsed.ServerPort.Length > 16
                || parsed.ServerName?.Length > 256 || parsed.Config?.Length > 64
                || parsed.RoundStartUnix <= 0 || parsed.RoundEndUnix < parsed.RoundStartUnix
                || parsed.RoundEndUnix > 253402300799 || parsed.RoundEnd < parsed.RoundStart
                || parsed.Players?.Count == 0)
                return Results.BadRequest(new { error = "Invalid round identity, timing, server or players" });
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or InvalidOperationException)
        { return Results.BadRequest(new { error = ex.Message }); }
        await IngestLock.WaitAsync(cancellationToken);
        try
        {
            await using var strategyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                // Source match IDs can span many maps or change between rounds (the
                // script's Unix fallback). Pair by server/map/timing, never that ID alone.
                var candidates = await db.Matches.Include(m => m.Rounds)
                    .Where(m => m.ServerId == parsed.ServerId && m.ServerIp == parsed.ServerIp && m.ServerPort == parsed.ServerPort
                        && m.MapName == parsed.MapName && m.Rounds.Any(r => r.RoundStartUnix >= parsed.RoundStartUnix - 21600
                            && r.RoundStartUnix <= parsed.RoundStartUnix + 21600))
                    .ToListAsync(cancellationToken);
                var exact = candidates.FirstOrDefault(m => m.Rounds.Any(r => r.RoundNumber == parsed.Round
                    && r.RoundStartUnix == parsed.RoundStartUnix));
                var pair = exact ?? candidates.Where(m => !m.Rounds.Any(r => r.RoundNumber == parsed.Round)
                    && m.Rounds.Any(r => parsed.Round == 2
                        ? r.RoundNumber == 1 && r.RoundEndUnix <= parsed.RoundStartUnix && parsed.RoundStartUnix - r.RoundEndUnix <= 21600
                        : r.RoundNumber == 2 && parsed.RoundEndUnix <= r.RoundStartUnix && r.RoundStartUnix - parsed.RoundEndUnix <= 21600))
                    .OrderByDescending(m => m.Rounds.Max(r => r.RoundStartUnix)).FirstOrDefault();
                var key = $"{parsed.ServerIp}|{parsed.ServerPort}|{parsed.ServerName}|{parsed.MapName}|{parsed.Round}|{parsed.RoundStartUnix}";
                var mapId = pair?.ExternalMatchId ?? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32];
                var dto = OksiiStatsAdapter.Parse(payload, mapId);
                return await IngestMatchCoreAsync(db, transaction, webhookOptions, skillRatingOptions,
                    httpClientFactory, loggerFactory, scoreboardCache, dto, ingestOptions.Value.MatchGapHours, cancellationToken);
            });
        }
        finally { IngestLock.Release(); }
    }

    private static async Task<IResult> IngestRosterAsync(HttpRequest request, IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions, JsonElement payload, CancellationToken ct)
    {
        var auth = ValidateBearerToken(request, ingestOptions);
        if (auth != null) return auth;
        var ip = OksiiStatsAdapter.Text(payload, "server_ip"); var port = OksiiStatsAdapter.Text(payload, "server_port");
        long timestamp;
        try { timestamp = OksiiStatsAdapter.Number(payload, "timestamp"); }
        catch (JsonException ex) { return Results.BadRequest(new { error = ex.Message }); }
        if (ip.Length is 0 or > 64 || port.Length is 0 or > 16 || timestamp <= 0)
            return Results.BadRequest(new { error = "Server address and timestamp are required" });
        var json = payload.GetRawText();
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        await IngestLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.RosterSnapshots.AnyAsync(s => s.Id == id, ct))
            {
                db.RosterSnapshots.Add(new RosterSnapshot { Id = id, ServerIp = ip, ServerPort = port, TimestampUnix = timestamp, PayloadJson = json });
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok();
        }
        finally { IngestLock.Release(); }
    }

    private static async Task<IResult> IngestMatchCoreAsync(
        StatsDbContext db,
        IDbContextTransaction transaction,
        IOptions<WebhookOptions> webhookOptions,
        IOptions<SkillRatingOptions> skillRatingOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ScoreboardCache scoreboardCache,
        MatchIngestDto dto,
        double matchGapHours,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.MatchId))
        {
            return Results.BadRequest(new { error = "matchID is required" });
        }

        if (dto.Round is not (1 or 2))
        {
            return Results.BadRequest(new { error = "round must be 1 or 2" });
        }

        if (string.IsNullOrWhiteSpace(dto.MapName))
        {
            return Results.BadRequest(new { error = "mapname is required" });
        }

        var players = dto.Players ?? [];
        var obituaries = dto.Obituaries ?? [];
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

        var serverId = string.IsNullOrWhiteSpace(dto.ServerId) ? null : dto.ServerId.Trim();
        if (serverId?.Length > 256)
        {
            return Results.BadRequest(new { error = "serverId must be at most 256 characters" });
        }

        if (existingMatch is not null)
        {
            // Validate before updating metadata or deleting a previously ingested round.
            // A mistaken match ID must never overwrite another server's data.
            var sameServer = existingMatch.ServerId is not null
                ? string.Equals(existingMatch.ServerId, serverId, StringComparison.Ordinal)
                : string.Equals(existingMatch.ServerName, dto.ServerName ?? string.Empty, StringComparison.Ordinal);
            if (!sameServer
                || !string.Equals(existingMatch.MapName, dto.MapName, StringComparison.Ordinal)
                || !string.Equals(existingMatch.ServerIp, dto.ServerIp ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(existingMatch.ServerPort, dto.ServerPort ?? string.Empty, StringComparison.Ordinal))
            {
                return Results.Conflict(new { error = "matchID belongs to a different server or map" });
            }
        }

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
                ServerId = serverId,
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
            match.ServerId = serverId;
            match.ServerIp = dto.ServerIp ?? string.Empty;
            match.ServerPort = dto.ServerPort ?? string.Empty;
        }

        var existingRound = await db.MatchRounds
            .FirstOrDefaultAsync(r => r.MatchId == match.Id && r.RoundNumber == dto.Round, cancellationToken);

        // Keep a compact fingerprint of the original DTO, before normalization.
        // Retried requests must not replace rows or apply ratings/webhooks again.
        var fingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(dto.OksiiPayload is { } source
            ? JsonSerializer.SerializeToUtf8Bytes(source) : JsonSerializer.SerializeToUtf8Bytes(dto)));
        if (existingRound?.RawJson == fingerprint)
        {
            return Results.Ok(new { matchId = match.ExternalMatchId, round = existingRound.RoundNumber,
                matchDbId = match.Id, roundDbId = existingRound.Id });
        }

        if (existingRound is not null)
        {
            db.MatchRounds.Remove(existingRound);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Normalize round 2 stats to per-round values if the source provides cumulative match totals.
        // ET can keep some sess.* values across map_restart, which makes round 2 partially aggregated.
        // We detect cumulatives by comparing against ingested round 1 values for the same GUID.
        Dictionary<string, Round1PlayerSnapshot> round1ByGuid = new(StringComparer.OrdinalIgnoreCase);
        MatchRound? round1ForWinner = null;
        if (dto.Round == 2 && !isNewMatch)
        {
            var round1 = await db.MatchRounds
                .AsNoTracking()
                .Include(r => r.Sides)
                    .ThenInclude(s => s.Players)
                        .ThenInclude(p => p.WeaponStats)
                .FirstOrDefaultAsync(r => r.MatchId == match.Id && r.RoundNumber == 1, cancellationToken);

            round1ForWinner = round1;

            if (round1 is not null)
            {
                // The Lua module retains obituary events through map_restart. Round 2's
                // payload can therefore repeat every round 1 event with the same uptime
                // timestamp. Exclude those duplicates before storing or counting streaks.
                if (!dto.OksiiPayload.HasValue)
                {
                    var round1Obituaries = await db.MatchObituaries
                        .AsNoTracking()
                        .Where(o => o.MatchRoundId == round1.Id)
                        .Select(o => new { o.TimestampMs, o.TargetGuid, o.AttackerGuid, o.MeansOfDeath })
                        .ToListAsync(cancellationToken);
                    var round1Events = round1Obituaries
                        .Select(o => ObituaryKey(o.TimestampMs, o.TargetGuid, o.AttackerGuid, o.MeansOfDeath))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    obituaries = obituaries
                        .Where(o => !round1Events.Contains(ObituaryKey(o.Timestamp, o.Target, o.Attacker, o.MeansOfDeath)))
                        .ToList();
                }

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

        var playerTeamsByGuid = players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid))
            .GroupBy(p => p.Guid!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Team, StringComparer.OrdinalIgnoreCase);
        var multiKillsByAttacker = MultiKillCounter.Count(
            obituaries.Select(o => new MultiKillEvent(o.Timestamp, o.Attacker, o.Target, o.MeansOfDeath)),
            playerTeamsByGuid,
            out _);

        var canNormalizeRound2 = dto.Round == 2 && round1ByGuid.Count > 0;
        var oksii = dto.OksiiPayload.HasValue;
        var weaponStatsAreCumulative = canNormalizeRound2 && (oksii || LooksCumulativeWeaponStats(players, round1ByGuid));

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
            RawJson = fingerprint,
            StatsSource = oksii ? "oksii" : "legacy",
            SourcePayloadJson = dto.OksiiPayload?.GetRawText(),
        };

        if (oksii && canNormalizeRound2)
        {
            xpIsCumulative = dmgGivenIsCumulative = dmgReceivedIsCumulative = teamDmgGivenIsCumulative
                = teamDmgReceivedIsCumulative = gibsIsCumulative = selfKillsIsCumulative
                = teamKillsIsCumulative = teamGibsIsCumulative = true;
        }
        if (dto.OksiiPayload is { } oksiiSource)
        {
            var metadata = OksiiStatsAdapter.Field(oksiiSource, "metadata");
            match.SourceMatchId = OksiiStatsAdapter.Text(metadata, "matchID");
            if (string.IsNullOrEmpty(match.SourceMatchId)) match.SourceMatchId = OksiiStatsAdapter.Text(OksiiStatsAdapter.Field(oksiiSource, "round_info"), "matchID");
            if (match.SourceMatchId?.Length > 64) return Results.BadRequest(new { error = "matchID is too long" });
            var sequence = 0;
            foreach (var ev in OksiiStatsAdapter.Events(oksiiSource))
            {
                var label = OksiiStatsAdapter.Text(ev, "label"); var eventGroup = OksiiStatsAdapter.Text(ev, "group");
                if (label.Length > 128 || eventGroup.Length > 64) return Results.BadRequest(new { error = "Event label or group is too long" });
                round.Events.Add(new RoundEvent { Id = Guid.NewGuid(), Sequence = sequence++, Label = label, Group = eventGroup,
                    LevelTime = OksiiStatsAdapter.Number(ev, "leveltime"), UnixTimeMs = OksiiStatsAdapter.Number(ev, "unixtime"), DataJson = ev.GetRawText() });
            }
        }

        // Persist overall match winner once round 2 is ingested.
        // Keep it null for matches without round 2.
        if (dto.Round == 2)
        {
            match.Winner = oksii && round1ForWinner == null ? null : MatchWinnerCalculator.DetermineWinner(round1ForWinner, round);
        }

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

                if (dto.OksiiPayload is { } playerSource)
                    OksiiStatsAdapter.ApplyPlayer(player, playerSource,
                        round1ForWinner?.Sides.SelectMany(s => s.Players).FirstOrDefault(p => p.Guid == player.Guid));

                if (!string.IsNullOrWhiteSpace(player.Guid) && multiKillsByAttacker.TryGetValue(player.Guid, out var multiKills))
                {
                    player.MultiKills2 = multiKills[0];
                    player.MultiKills3 = multiKills[1];
                    player.MultiKills4 = multiKills[2];
                    player.MultiKills5 = multiKills[3];
                    player.MultiKills6 = multiKills[4];
                }

                foreach (var w in p.WeaponStats ?? [])
                {
                    var r1Weapon = (weaponStatsAreCumulative && p1 is not null && p1.WeaponByWeapon.TryGetValue(w.Weapon, out var w1))
                        ? w1
                        : null;

                    var atts2 = NormalizeCountToInt(w.Atts);

                    player.WeaponStats.Add(new MatchPlayerWeaponStat
                    {
                        Id = Guid.NewGuid(),
                        Weapon = w.Weapon,
                        Hits = r1Weapon is null ? w.Hits : Math.Max(0, w.Hits - r1Weapon.Hits),
                        Atts = r1Weapon is null ? atts2 : Math.Max(0, atts2 - r1Weapon.Atts),
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

        foreach (var o in obituaries)
        {
            static string? NormGuidOrNull(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.Trim().ToUpperInvariant();
            }

            round.Obituaries.Add(new MatchObituary
            {
                Id = Guid.NewGuid(),
                TimestampMs = o.Timestamp,
                TargetGuid = NormGuidOrNull(o.Target),
                AttackerGuid = NormGuidOrNull(o.Attacker),
                MeansOfDeath = o.MeansOfDeath,
                AttackerRespawnTime = o.AttackerRespawnTime,
                VictimRespawnTime = o.VictimRespawnTime,
            });
        }

        if (isNewMatch)
        {
            db.Matches.Add(match);
        }

        if (!oksii) CanonicalStats.AddLegacyEvents(round);
        db.MatchRounds.Add(round);
        await db.SaveChangesAsync(cancellationToken);
        round.PayloadJson = CanonicalStats.Write(match, round);
        var completedRoundTwo = dto.Round == 2 ? round : await RefreshOksiiRoundTwoAsync(db, match, round, cancellationToken);
        var completedRoundOne = dto.Round == 1 ? round : round1ForWinner;
        var firstCompletion = existingRound == null && completedRoundTwo != null
            && ((!oksii && dto.Round == 2) || completedRoundOne != null);
        await MatchSeriesBuilder.RebuildAsync(db, matchGapHours, cancellationToken, MatchSeriesBuilder.ServerKey(match));

        if (firstCompletion)
        {
            var logger = loggerFactory.CreateLogger("PappaETStats.Server.Api.IngestEndpoints");

            await TryUpdateSkillRatingsForCompletedMatchAsync(
                db,
                logger,
                match,
                completedRoundOne,
                completedRoundTwo!,
                skillRatingOptions.Value,
                cancellationToken);
        }

        // Replacement deletion, new stats and first-completion ratings commit
        // together. A failed replacement leaves the previous round intact.
        await transaction.CommitAsync(cancellationToken);
        scoreboardCache.Invalidate();

        if (firstCompletion)
        {
            var logger = loggerFactory.CreateLogger("PappaETStats.Server.Api.IngestEndpoints");
            await TrySendGameCompletedWebhookAsync(
                httpClientFactory,
                webhookOptions.Value,
                logger,
                match,
                completedRoundOne,
                completedRoundTwo!,
                CancellationToken.None);
        }

        return Results.Ok(new { matchId = match.ExternalMatchId, round = round.RoundNumber, matchDbId = match.Id, roundDbId = round.Id });
    }

    private static async Task<MatchRound?> RefreshOksiiRoundTwoAsync(StatsDbContext db, Match map, MatchRound round1, CancellationToken ct)
    {
        var round2 = await db.MatchRounds.Include(r => r.Events)
            .Include(r => r.Sides).ThenInclude(s => s.Players).ThenInclude(p => p.WeaponStats)
            .Include(r => r.Sides).ThenInclude(s => s.Players).ThenInclude(p => p.ClassStats)
            .AsSplitQuery().FirstOrDefaultAsync(r => r.MatchId == map.Id && r.RoundNumber == 2 && r.StatsSource == "oksii", ct);
        if (round2?.SourcePayloadJson == null) return null;
        using var json = JsonDocument.Parse(round2.SourcePayloadJson);
        var raw = OksiiStatsAdapter.Parse(json.RootElement, map.ExternalMatchId);
        var priorPlayers = round1.Sides.SelectMany(s => s.Players).ToDictionary(p => p.Guid, StringComparer.OrdinalIgnoreCase);
        foreach (var player in round2.Sides.SelectMany(s => s.Players))
        {
            var source = raw.Players!.Single(p => p.Guid == player.Guid);
            priorPlayers.TryGetValue(player.Guid, out var prior);
            player.Xp = Math.Max(0, source.Xp - (prior?.Xp ?? 0));
            player.DamageGiven = Math.Max(0, source.DamageGiven - (prior?.DamageGiven ?? 0));
            player.DamageReceived = Math.Max(0, source.DamageReceived - (prior?.DamageReceived ?? 0));
            player.TeamDamageGiven = Math.Max(0, source.TeamDamageGiven - (prior?.TeamDamageGiven ?? 0));
            player.TeamDamageReceived = Math.Max(0, source.TeamDamageReceived - (prior?.TeamDamageReceived ?? 0));
            player.Gibs = Math.Max(0, source.Gibs - (prior?.Gibs ?? 0));
            player.SelfKills = Math.Max(0, source.SelfKills - (prior?.SelfKills ?? 0));
            player.TeamKills = Math.Max(0, source.TeamKills - (prior?.TeamKills ?? 0));
            player.TeamGibs = Math.Max(0, source.TeamGibs - (prior?.TeamGibs ?? 0));
            foreach (var weapon in player.WeaponStats)
            {
                var sourceWeapon = source.WeaponStats!.Single(w => w.Weapon == weapon.Weapon);
                var previousWeapon = prior?.WeaponStats.SingleOrDefault(w => w.Weapon == weapon.Weapon);
                weapon.Hits = Math.Max(0, sourceWeapon.Hits - (previousWeapon?.Hits ?? 0));
                weapon.Atts = Math.Max(0, NormalizeCountToInt(sourceWeapon.Atts) - (previousWeapon?.Atts ?? 0));
                weapon.Kills = Math.Max(0, sourceWeapon.Kills - (previousWeapon?.Kills ?? 0));
                weapon.Deaths = Math.Max(0, sourceWeapon.Deaths - (previousWeapon?.Deaths ?? 0));
                weapon.Headshots = Math.Max(0, sourceWeapon.Headshots - (previousWeapon?.Headshots ?? 0));
            }
            OksiiStatsAdapter.ApplyPlayer(player, json.RootElement, prior);
        }
        foreach (var side in round2.Sides)
        {
            side.TotalXp = side.Players.Sum(p => p.Xp);
            side.TotalDamageGiven = side.Players.Sum(p => (long)p.DamageGiven);
            side.TotalDamageReceived = side.Players.Sum(p => (long)p.DamageReceived);
            side.TotalTeamDamageGiven = side.Players.Sum(p => (long)p.TeamDamageGiven);
            side.TotalTeamDamageReceived = side.Players.Sum(p => (long)p.TeamDamageReceived);
            side.TotalGibs = side.Players.Sum(p => (long)p.Gibs);
            side.TotalSelfKills = side.Players.Sum(p => (long)p.SelfKills);
            side.TotalTeamKills = side.Players.Sum(p => (long)p.TeamKills);
            side.TotalTeamGibs = side.Players.Sum(p => (long)p.TeamGibs);
        }
        map.Winner = MatchWinnerCalculator.DetermineWinner(round1, round2);
        round2.PayloadJson = CanonicalStats.Write(map, round2);
        return round2;
    }

    private static async Task TryUpdateSkillRatingsForCompletedMatchAsync(
        StatsDbContext db,
        ILogger logger,
        Match match,
        MatchRound? round1,
        MatchRound round2,
        SkillRatingOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            // Only update ratings for a decisive winner. (The current calculator does not support draws.)
            if (match.Winner is not MatchWinner.Team1 and not MatchWinner.Team2)
            {
                return;
            }

            var winningOverallTeamId = match.Winner == MatchWinner.Team1 ? 1 : 2;
            var winningSkillTeam = SkillRatingUpdater.MapOverallTeamToSkillTeam(winningOverallTeamId);
            if (winningSkillTeam is null)
            {
                return;
            }

            var aggregate = SkillRatingUpdater.BuildAggregate(round1, round2);

            if (aggregate.Count == 0)
            {
                return;
            }

            var axisCount = aggregate.Values.Count(v => v.Team == Team.Axis);
            var alliesCount = aggregate.Values.Count(v => v.Team == Team.Allies);
            if (axisCount == 0 || alliesCount == 0 || axisCount != alliesCount)
            {
                logger.LogInformation(
                    "Skipping skill rating update for match {MatchId} due to uneven teams (Axis={AxisCount}, Allies={AlliesCount})",
                    match.Id,
                    axisCount,
                    alliesCount);
                return;
            }

            var calculator = new SkillRatingCalculator(options);

            var guidStrings = aggregate.Keys.ToList();

            // Load existing player ratings; create missing ones.
            var playerRows = await db.Players
                .Where(p => guidStrings.Contains(p.Guid))
                .ToDictionaryAsync(p => p.Guid, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var g in guidStrings)
            {
                if (playerRows.ContainsKey(g))
                {
                    continue;
                }

                var created = new Player
                {
                    Guid = g,
                    Mu = options.Mu,
                    Sigma = options.Sigma,
                };

                db.Players.Add(created);
                playerRows[g] = created;
            }

            SkillRatingUpdater.ApplyMatch(
                calculator,
                options,
                aggregate,
                winner: winningSkillTeam.Value,
                teamSize: axisCount,
                playerRows);

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Do not fail ingest if ratings fail.
            logger.LogWarning(ex, "Skill rating update failed for match {MatchId}", match.Id);
        }
    }

    private static async Task TrySendGameCompletedWebhookAsync(
        IHttpClientFactory httpClientFactory,
        WebhookOptions options,
        ILogger logger,
        Match match,
        MatchRound? round1,
        MatchRound round2,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return;
        }

        static string? NormalizeBaseUrl(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return null;
            }

            return baseUrl.Trim().TrimEnd('/');
        }

        static string CombineUrl(string? baseUrl, string path)
        {
            var b = NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrWhiteSpace(b))
            {
                return path.StartsWith('/') ? path : "/" + path;
            }

            var p = path.StartsWith('/') ? path : "/" + path;
            return b + p;
        }

        static int MapRoundTeamToOverallTeam(int roundNumber, int teamId)
        {
            if (roundNumber == 2)
            {
                return teamId switch
                {
                    1 => 2,
                    2 => 1,
                    _ => teamId,
                };
            }

            return teamId;
        }

        var matchLink = CombineUrl(options.FrontendBaseUrl, $"/matches/{match.Id}");

        static string FormatSideTime(MatchRound? round)
        {
            if (round is null)
            {
                return "";
            }

            var v = (round.NextTimeLimit ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }

            return (round.TimeLimit ?? string.Empty).Trim();
        }

        var time = $"{FormatSideTime(round1)} / {FormatSideTime(round2)}".Trim();

        var winnerText = match.Winner switch
        {
            MatchWinner.Team1 => "Team 1",
            MatchWinner.Team2 => "Team 2",
            MatchWinner.Draw => "Draw",
            _ => $"Team {MapRoundTeamToOverallTeam(round2.RoundNumber, round2.WinnerTeam)}",
        };

        var teams = round2.Sides
            .Select(s =>
            {
                var overallTeam = MapRoundTeamToOverallTeam(round2.RoundNumber, s.Team);
                var players = s.Players
                    .OrderBy(p => EtColorCodes.Strip(p.Name))
                    .Select(p => EtColorCodes.Strip(p.Name))
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToArray();

                return new
                {
                    name = $"Team {overallTeam}",
                    overallTeam,
                    players
                };
            })
            .OrderBy(t => t.overallTeam)
            .Select(t => new { t.name, t.players })
            .ToArray();

        var payload = new
        {
            map = match.MapName,
            winner = winnerText,
            time,
            teams,
            link = matchLink,
        };

        try
        {
            var client = httpClientFactory.CreateClient("Webhook");

            using var message = new HttpRequestMessage(HttpMethod.Post, options.Url);
            message.Content = JsonContent.Create(payload);

            if (!string.IsNullOrWhiteSpace(options.Token))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token.Trim());
            }

            using var response = await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = string.Empty;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch
                {
                    // ignore
                }

                logger.LogWarning(
                    "Webhook POST to {Url} failed with {StatusCode}. Body: {Body}",
                    options.Url,
                    (int)response.StatusCode,
                    body);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook POST to {Url} failed.", options.Url);
        }
    }
}
