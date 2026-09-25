using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.SkillRating;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Options;
using PappaETStats.Server.Services;

namespace PappaETStats.Server.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .WithTags("Admin");

        group.MapDelete("/matches/{matchId:guid}", DeleteMatchAsync)
            .WithName("DeleteMatch")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/matches/recalculate-winners", RecalculateMatchWinnersAsync)
            .WithName("RecalculateMatchWinners")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/skillratings/recalculate", RecalculateAllSkillRatingsAsync)
            .WithName("RecalculateAllSkillRatings")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/multikills/recalculate", RecalculateAllMultiKillsAsync)
            .WithName("RecalculateAllMultiKills")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/players/merge-guid", MergePlayerGuidAsync)
            .WithName("MergePlayerGuid")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private sealed record RecalculateMatchWinnersRequest(Guid? MatchId);
    private sealed record MergePlayerGuidRequest(string? SourceGuid, string? TargetGuid);

    private static IResult? ValidateBearerToken(HttpRequest request, IOptions<AdminOptions> adminOptions)
    {
        var configuredToken = adminOptions.Value.Token;
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

    private static async Task<IResult> DeleteMatchAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
        ScoreboardCache scoreboardCache,
        Guid matchId,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, adminOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Delete directly in the database for minimal memory usage.
        // This relies on DB-level ON DELETE CASCADE foreign keys to remove dependent rows.
        try
        {
            var deletedRows = await db.Matches
                .Where(m => m.Id == matchId)
                .ExecuteDeleteAsync(cancellationToken);

            if (deletedRows == 0)
            {
                return Results.NotFound(new { error = "match not found", matchId });
            }

            scoreboardCache.Invalidate();
            return Results.Ok(new { deleted = true, matchId });
        }
        catch (DbUpdateException ex)
        {
            return Results.Conflict(new
            {
                error = "delete failed (missing DB cascades or restricted foreign keys)",
                matchId,
                detail = ex.GetBaseException().Message
            });
        }
    }

    private static async Task<IResult> RecalculateMatchWinnersAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
        ScoreboardCache scoreboardCache,
        RecalculateMatchWinnersRequest? body,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, adminOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Matches.AsQueryable();
        if (body?.MatchId is Guid matchId)
        {
            query = query.Where(m => m.Id == matchId);
        }

        var matches = await query.ToListAsync(cancellationToken);
        if (matches.Count == 0)
        {
            return Results.Ok(new { processed = 0, updated = 0 });
        }

        var matchIds = matches.Select(m => m.Id).ToList();

        var roundRows = await db.MatchRounds
            .AsNoTracking()
            .Where(r => matchIds.Contains(r.MatchId) && (r.RoundNumber == 1 || r.RoundNumber == 2))
            .Select(r => new { r.MatchId, r.RoundNumber, r.WinnerTeam, r.TimeLimit, r.NextTimeLimit })
            .ToListAsync(cancellationToken);

        var roundsByMatch = roundRows
            .GroupBy(r => r.MatchId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var updated = 0;
        foreach (var match in matches)
        {
            roundsByMatch.TryGetValue(match.Id, out var rounds);

            MatchRound? r1 = null;
            MatchRound? r2 = null;
            if (rounds is not null)
            {
                foreach (var r in rounds)
                {
                    var mr = new MatchRound
                    {
                        Id = Guid.Empty,
                        MatchId = match.Id,
                        Match = null!,
                        RoundNumber = r.RoundNumber,
                        DefenderTeam = 0,
                        WinnerTeam = r.WinnerTeam,
                        TimeLimit = r.TimeLimit ?? string.Empty,
                        NextTimeLimit = r.NextTimeLimit ?? string.Empty,
                    };

                    if (r.RoundNumber == 1)
                    {
                        r1 = mr;
                    }
                    else if (r.RoundNumber == 2)
                    {
                        r2 = mr;
                    }
                }
            }

            var computed = MatchWinnerCalculator.DetermineWinner(r1, r2);
            if (match.Winner != computed)
            {
                match.Winner = computed;
                updated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (updated > 0) scoreboardCache.Invalidate();
        return Results.Ok(new { processed = matches.Count, updated });
    }

    private static async Task<IResult> RecalculateAllSkillRatingsAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
        ScoreboardCache scoreboardCache,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, adminOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        // MySQL retry execution strategy can't be combined with user transactions
        // unless the transaction is created inside the execution strategy callback.
        await using var strategyDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyDb.Database.CreateExecutionStrategy();

        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            var options = new SkillRatingOptions();
            var calculator = new SkillRatingCalculator(options);

            // Reset only ratings: account links and preferences must survive a replay,
            // including for players with no eligible matches remaining in history.
            var playerRows = await db.Players
                .ToDictionaryAsync(p => p.Guid, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var resetPlayers = playerRows.Count;
            foreach (var player in playerRows.Values)
            {
                player.Mu = options.Mu;
                player.Sigma = options.Sigma;
                // Null denotes no rated history in a format; ApplyMatch initializes
                // that track from the defaults when its first match is replayed.
                player.MuSmall = null;
                player.SigmaSmall = null;
                player.MuLarge = null;
                player.SigmaLarge = null;
            }

            // Replay completed matches in chronological order (round 2 ingest time).
            var completedMatchIdsInOrder = await db.MatchRounds
                .AsNoTracking()
                .Where(r => r.RoundNumber == 2 && (r.Match.Winner == MatchWinner.Team1 || r.Match.Winner == MatchWinner.Team2))
                .OrderBy(r => r.IngestedAtUtc)
                .Select(r => r.MatchId)
                .ToListAsync(cancellationToken);

            var matchIds = completedMatchIdsInOrder
                .Distinct()
                .ToList();

            var processedMatches = 0;
            var skippedMissingRounds = 0;
            var skippedDraws = 0;
            var skippedUnevenTeams = 0;
            var skippedEmpty = 0;

            foreach (var matchId in matchIds)
            {
                var rounds = await db.MatchRounds
                    .AsNoTracking()
                    .Include(r => r.Match)
                    .Include(r => r.Sides)
                        .ThenInclude(s => s.Players)
                    .Where(r => r.MatchId == matchId && (r.RoundNumber == 1 || r.RoundNumber == 2))
                    .ToListAsync(cancellationToken);

                if (rounds.Count == 0)
                {
                    skippedMissingRounds++;
                    continue;
                }

                var round1 = rounds.FirstOrDefault(r => r.RoundNumber == 1);
                var round2 = rounds.FirstOrDefault(r => r.RoundNumber == 2);
                if (round2 is null)
                {
                    skippedMissingRounds++;
                    continue;
                }

                var match = rounds[0].Match;
                var winner = match.Winner;
                if (winner is not MatchWinner.Team1 and not MatchWinner.Team2)
                {
                    winner = MatchWinnerCalculator.DetermineWinner(round1, round2);
                }

                if (winner is not MatchWinner.Team1 and not MatchWinner.Team2)
                {
                    skippedDraws++;
                    continue;
                }

                var winningOverallTeamId = winner == MatchWinner.Team1 ? 1 : 2;
                var winningSkillTeam = SkillRatingUpdater.MapOverallTeamToSkillTeam(winningOverallTeamId);
                if (winningSkillTeam is null)
                {
                    skippedEmpty++;
                    continue;
                }

                var aggregate = SkillRatingUpdater.BuildAggregate(round1, round2);

                if (aggregate.Count == 0)
                {
                    skippedEmpty++;
                    continue;
                }

                var axisCount = aggregate.Values.Count(v => v.Team == Team.Axis);
                var alliesCount = aggregate.Values.Count(v => v.Team == Team.Allies);
                if (axisCount == 0 || alliesCount == 0 || axisCount != alliesCount)
                {
                    skippedUnevenTeams++;
                    continue;
                }

                // Ensure players exist with default ratings.
                foreach (var guidString in aggregate.Keys)
                {
                    if (playerRows.ContainsKey(guidString))
                    {
                        continue;
                    }

                    var created = new Player
                    {
                        Guid = guidString,
                        Mu = options.Mu,
                        Sigma = options.Sigma,
                    };

                    db.Players.Add(created);
                    playerRows[guidString] = created;
                }

                SkillRatingUpdater.ApplyMatch(
                    calculator,
                    options,
                    aggregate,
                    winner: winningSkillTeam.Value,
                    teamSize: axisCount,
                    playerRows);

                processedMatches++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return new
            {
                deletedPlayers = 0,
                resetPlayers,
                processedMatches,
                skippedMissingRounds,
                skippedDraws,
                skippedUnevenTeams,
                skippedEmpty,
                players = playerRows.Count,
            };
        });

        scoreboardCache.Invalidate();
        return Results.Ok(result);
    }

    private static async Task<IResult> RecalculateAllMultiKillsAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
        ScoreboardCache scoreboardCache,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, adminOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var players = await db.MatchPlayers
            .Select(p => new
            {
                p.Id,
                p.Guid,
                MatchId = p.MatchSide.MatchRound.MatchId,
                p.MatchSide.MatchRound.RoundNumber,
                p.Team,
            })
            .ToListAsync(cancellationToken);
        var playerIdsByMatchRoundGuid = players
            .Where(p => !string.IsNullOrWhiteSpace(p.Guid))
            .GroupBy(p => PlayerRoundKey(p.MatchId, p.RoundNumber, p.Guid))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        var obituaryRows = await db.MatchObituaries
            .Where(o => o.AttackerGuid != null)
            .Select(o => new
            {
                o.TimestampMs, o.TargetGuid, o.AttackerGuid, o.MeansOfDeath,
                o.MatchRound.MatchId, o.MatchRound.RoundNumber
            })
            .ToListAsync(cancellationToken);

        var playerCounts = new Dictionary<Guid, int[]>();
        var qualifyingGroups = 0;
        foreach (var matchGroup in obituaryRows.GroupBy(o => o.MatchId))
        {
            var round1Events = matchGroup
                .Where(o => o.RoundNumber == 1)
                .Select(o => ObituaryKey(o.TimestampMs, o.TargetGuid, o.AttackerGuid, o.MeansOfDeath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var roundGroup in matchGroup.GroupBy(o => o.RoundNumber))
            {
                var roundEvents = roundGroup
                    .Where(o => roundGroup.Key != 2
                                || !round1Events.Contains(ObituaryKey(o.TimestampMs, o.TargetGuid, o.AttackerGuid, o.MeansOfDeath)))
                    .Select(o => new MultiKillEvent(o.TimestampMs, o.AttackerGuid, o.TargetGuid, o.MeansOfDeath));
                var teamsForRound = players
                    .Where(p => p.MatchId == matchGroup.Key && p.RoundNumber == roundGroup.Key && !string.IsNullOrWhiteSpace(p.Guid))
                    .GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Team, StringComparer.OrdinalIgnoreCase);
                var countsByAttacker = MultiKillCounter.Count(roundEvents, teamsForRound, out var roundQualifyingGroups);
                qualifyingGroups += roundQualifyingGroups;

                foreach (var (attackerGuid, counts) in countsByAttacker)
                {
                    var playerKey = PlayerRoundKey(matchGroup.Key, roundGroup.Key, attackerGuid);
                    if (!playerIdsByMatchRoundGuid.TryGetValue(playerKey, out var matchingPlayerIds))
                    {
                        continue;
                    }

                    foreach (var playerId in matchingPlayerIds)
                    {
                        playerCounts[playerId] = counts.ToArray();
                    }
                }
            }
        }

        // Update only these counters so recalculation preserves every other match-row value.
        foreach (var player in await db.MatchPlayers.ToListAsync(cancellationToken))
        {
            playerCounts.TryGetValue(player.Id, out var counts);
            player.MultiKills2 = counts?[0] ?? 0;
            player.MultiKills3 = counts?[1] ?? 0;
            player.MultiKills4 = counts?[2] ?? 0;
            player.MultiKills5 = counts?[3] ?? 0;
            player.MultiKills6 = counts?[4] ?? 0;
        }

        var updatedPlayers = await db.SaveChangesAsync(cancellationToken);
        if (updatedPlayers > 0) scoreboardCache.Invalidate();
        return Results.Ok(new { processedPlayers = players.Count, updatedPlayers, qualifyingGroups });
    }

    private static string ObituaryKey(long timestamp, string? target, string? attacker, int meansOfDeath)
        => $"{timestamp}|{target?.Trim().ToUpperInvariant()}|{attacker?.Trim().ToUpperInvariant()}|{meansOfDeath}";

    private static string PlayerRoundKey(Guid matchId, int roundNumber, string guid)
        => $"{matchId:N}|{roundNumber}|{guid.Trim().ToUpperInvariant()}";

    private static async Task<IResult> MergePlayerGuidAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
        ScoreboardCache scoreboardCache,
        MergePlayerGuidRequest? body,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, adminOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        var sourceGuid = body?.SourceGuid?.Trim().ToUpperInvariant();
        var targetGuid = body?.TargetGuid?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(sourceGuid) || string.IsNullOrWhiteSpace(targetGuid))
        {
            return Results.BadRequest(new { error = "sourceGuid and targetGuid are required" });
        }

        if (sourceGuid == targetGuid)
        {
            return Results.BadRequest(new { error = "sourceGuid and targetGuid must differ" });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var source = await db.Players.SingleOrDefaultAsync(p => p.Guid == sourceGuid, cancellationToken);
        if (source is null)
        {
            var targetExists = await db.Players.AnyAsync(p => p.Guid == targetGuid, cancellationToken);
            return targetExists
                ? Results.Ok(new { merged = false, sourceGuid, targetGuid, reason = "source already merged or not found" })
                : Results.NotFound(new { error = "source or target player not found", sourceGuid, targetGuid });
        }

        var target = await db.Players.SingleOrDefaultAsync(p => p.Guid == targetGuid, cancellationToken);
        if (target is null)
        {
            target = new Player
            {
                Guid = targetGuid,
                Mu = source.Mu,
                Sigma = source.Sigma,
                MuSmall = source.MuSmall,
                SigmaSmall = source.SigmaSmall,
                MuLarge = source.MuLarge,
                SigmaLarge = source.SigmaLarge,
                DiscordId = source.DiscordId,
            };
            db.Players.Add(target);
        }
        else if (!string.IsNullOrWhiteSpace(source.DiscordId) &&
                 !string.IsNullOrWhiteSpace(target.DiscordId) &&
                 !string.Equals(source.DiscordId, target.DiscordId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "source and target have different Discord links", sourceGuid, targetGuid });
        }
        else if (string.IsNullOrWhiteSpace(target.DiscordId))
        {
            target.DiscordId = source.DiscordId;
        }

        var duplicateMatchIds = await db.MatchPlayers
            .Where(p => p.Guid == sourceGuid)
            .Join(db.MatchPlayers.Where(p => p.Guid == targetGuid),
                sourcePlayer => sourcePlayer.MatchSideId,
                targetPlayer => targetPlayer.MatchSideId,
                (sourcePlayer, targetPlayer) => sourcePlayer.MatchSideId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (duplicateMatchIds.Count > 0)
        {
            return Results.Conflict(new
            {
                error = "source and target both occur in the same match side; merge was not performed",
                sourceGuid,
                targetGuid,
                conflictingMatchSides = duplicateMatchIds.Count,
            });
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            await db.MatchPlayers
                .Where(p => p.Guid == sourceGuid)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Guid, targetGuid), cancellationToken);
            await db.MatchObituaries
                .Where(o => o.TargetGuid == sourceGuid || o.AttackerGuid == sourceGuid)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(o => o.TargetGuid, o => o.TargetGuid == sourceGuid ? targetGuid : o.TargetGuid)
                    .SetProperty(o => o.AttackerGuid, o => o.AttackerGuid == sourceGuid ? targetGuid : o.AttackerGuid), cancellationToken);
            await db.PlayerRegistrationTokens
                .Where(t => t.UsedByEtGuid == sourceGuid)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UsedByEtGuid, targetGuid), cancellationToken);

            db.Players.Remove(source);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });

        scoreboardCache.Invalidate();

        // Replaying from the merged history is the only correct way to combine
        // ratings from two GUIDs, especially for format-specific tracks.
        return await RecalculateAllSkillRatingsAsync(request, dbFactory, adminOptions, scoreboardCache, cancellationToken);
    }
}
