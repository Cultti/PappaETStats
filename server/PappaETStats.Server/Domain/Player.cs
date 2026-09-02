using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class Player
{
    [Key]
    [MaxLength(64)]
    public required string Guid { get; set; }

    /// <summary>Discord user id (snowflake) linked to this player, if registered.</summary>
    [MaxLength(64)]
    public string? DiscordId { get; set; }

    /// <summary>
    /// When true (default), the player's Discord id is included in the move-teams
    /// webhook so the bot moves them to their team's voice channel automatically.
    /// </summary>
    public bool AutoMoveToVoice { get; set; } = true;

    public double Mu { get; set; }

    public double Sigma { get; set; }

    // Format-specific rating tracks. Null means the player has no rated match in that format yet.

    /// <summary>Rating for small formats (up to 4 players per team).</summary>
    public double? MuSmall { get; set; }

    public double? SigmaSmall { get; set; }

    /// <summary>Rating for large formats (5 or more players per team).</summary>
    public double? MuLarge { get; set; }

    public double? SigmaLarge { get; set; }
}
