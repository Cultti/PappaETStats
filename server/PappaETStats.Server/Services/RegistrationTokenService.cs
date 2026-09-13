using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Services;

/// <summary>
/// Creates and looks up one-time registration tokens used to link a Discord
/// account to an ET player GUID via the in-game /register command.
/// </summary>
public sealed class RegistrationTokenService(IDbContextFactory<StatsDbContext> dbFactory)
{
    /// <summary>
    /// Returns the ET GUID linked to the given Discord id, or null when not linked yet.
    /// </summary>
    public async Task<string?> GetLinkedEtGuidAsync(string discordId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Players
            .AsNoTracking()
            .Where(p => p.DiscordId == discordId)
            .Select(p => p.Guid)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Returns an unused registration token for the Discord user, creating one when needed.
    /// </summary>
    public async Task<Guid> GetOrCreateTokenAsync(string discordId, string? discordUsername, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.PlayerRegistrationTokens
            .Where(t => t.DiscordId == discordId && t.UsedAtUtc == null)
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Token;
        }

        var token = new PlayerRegistrationToken
        {
            Token = Guid.NewGuid(),
            DiscordId = discordId,
            DiscordUsername = discordUsername,
            CreatedAtUtc = DateTime.UtcNow,
        };

        db.PlayerRegistrationTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return token.Token;
    }
}
