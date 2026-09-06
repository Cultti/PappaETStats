using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.SkillRating;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Options;

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

        return endpoints;
    }

    private sealed record RecalculateMatchWinnersRequest(Guid? MatchId);

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
        return Results.Ok(new { processed = matches.Count, updated });
    }

    private static async Task<IResult> RecalculateAllSkillRatingsAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<AdminOptions> adminOptions,
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

        return Results.Ok(result);
    }
}
