using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

/// <summary>
/// A one-time token shown to a logged-in Discord user so they can link their
/// Discord account to an ET player by running "/register {token}" in-game.
/// </summary>
public sealed class PlayerRegistrationToken
{
    /// <summary>The random token the user types in the in-game /register command.</summary>
    [Key]
    public Guid Token { get; set; }

    /// <summary>Discord user id (snowflake) that requested the token.</summary>
    [MaxLength(64)]
    public required string DiscordId { get; set; }

    /// <summary>Discord username at the time the token was created (for display/debugging).</summary>
    [MaxLength(128)]
    public string? DiscordUsername { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Set when the token has been consumed by the registration endpoint.</summary>
    public DateTime? UsedAtUtc { get; set; }

    /// <summary>The ET GUID the token was used for, if consumed.</summary>
    [MaxLength(64)]
    public string? UsedByEtGuid { get; set; }
}
