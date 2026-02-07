using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
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

        return endpoints;
    }

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
}
