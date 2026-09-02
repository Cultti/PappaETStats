using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;

namespace PappaETStats.Server.Api;

public static class RegistrationEndpoints
{
    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api")
            .WithTags("Registration");

        group.MapPost("/players/register", RegisterEtGuidAsync)
            .WithName("RegisterEtGuid")
            .Accepts<RegisterEtGuidRequest>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <param name="EtGuid">The player's ET GUID (32 hex chars, no dashes) captured on the game server.</param>
    /// <param name="Token">The registration token the user typed in the in-game /register command.</param>
    private sealed record RegisterEtGuidRequest(string? EtGuid, string? Token);

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

    private static async Task<IResult> RegisterEtGuidAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        ILoggerFactory loggerFactory,
        RegisterEtGuidRequest body,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        var etGuid = (body.EtGuid ?? string.Empty).Trim();
        if (etGuid.Length == 0)
        {
            return Results.BadRequest(new { error = "etGuid is required" });
        }

        // Same canonical GUID storage as ingest (32 hex chars, no dashes).
        if (!Guid.TryParseExact(etGuid, "N", out _))
        {
            return Results.BadRequest(new { error = "Invalid etGuid format (expected 32 hex chars)" });
        }

        var tokenString = (body.Token ?? string.Empty).Trim();
        if (tokenString.Length == 0)
        {
            return Results.BadRequest(new { error = "token is required" });
        }

        if (!Guid.TryParse(tokenString, out var token))
        {
            return Results.BadRequest(new { error = "Invalid token format. Copy the exact /register command shown on the website." });
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var registrationToken = await db.PlayerRegistrationTokens
            .FirstOrDefaultAsync(t => t.Token == token, cancellationToken);

        if (registrationToken is null)
        {
            return Results.NotFound(new { error = "Unknown registration token. Log in on the website to get a new /register command." });
        }

        if (registrationToken.UsedAtUtc is not null)
        {
            return Results.Conflict(new { error = "This registration token has already been used. Log in on the website to get a new /register command." });
        }

        var player = await db.Players.FirstOrDefaultAsync(p => p.Guid == etGuid, cancellationToken);
        if (player is null)
        {
            return Results.NotFound(new { error = "No player found with this ET GUID. Play at least one match on the server first, then try again." });
        }

        if (!string.IsNullOrEmpty(player.DiscordId))
        {
            if (string.Equals(player.DiscordId, registrationToken.DiscordId, StringComparison.Ordinal))
            {
                return Results.Ok(new { message = "This player is already linked to your Discord account.", etGuid, discordId = player.DiscordId });
            }

            return Results.Conflict(new { error = "This player is already linked to a different Discord account." });
        }

        var existingLink = await db.Players
            .AsNoTracking()
            .Where(p => p.DiscordId == registrationToken.DiscordId)
            .Select(p => p.Guid)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingLink is not null)
        {
            return Results.Conflict(new { error = "Your Discord account is already linked to another player." });
        }

        player.DiscordId = registrationToken.DiscordId;
        registrationToken.UsedAtUtc = DateTime.UtcNow;
        registrationToken.UsedByEtGuid = etGuid;

        await db.SaveChangesAsync(cancellationToken);

        var logger = loggerFactory.CreateLogger("Registration");
        logger.LogInformation(
            "Linked ET GUID {EtGuid} to Discord user {DiscordId} ({DiscordUsername})",
            etGuid,
            registrationToken.DiscordId,
            registrationToken.DiscordUsername);

        return Results.Ok(new
        {
            message = "Registration successful! Your Discord account is now linked to this player.",
            etGuid,
            discordId = registrationToken.DiscordId,
        });
    }
}
