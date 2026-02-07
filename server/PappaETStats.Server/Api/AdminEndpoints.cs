using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

        // Load full graph to ensure deletes succeed even if DB-level cascades are disabled.
        var match = await db.Matches
            .Include(m => m.Rounds)
                .ThenInclude(r => r.Sides)
                    .ThenInclude(s => s.Players)
                        .ThenInclude(p => p.WeaponStats)
            .Include(m => m.Rounds)
                .ThenInclude(r => r.Sides)
                    .ThenInclude(s => s.Players)
                        .ThenInclude(p => p.ClassStats)
            .Include(m => m.Rounds)
                .ThenInclude(r => r.Obituaries)
            .FirstOrDefaultAsync(m => m.Id == matchId, cancellationToken);

        if (match is null)
        {
            return Results.NotFound(new { error = "match not found", matchId });
        }

        db.Matches.Remove(match);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { deleted = true, matchId });
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
}
