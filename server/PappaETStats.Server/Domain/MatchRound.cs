using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class MatchRound
{
    public Guid Id { get; set; }

    public Guid MatchId { get; set; }
    public Match Match { get; set; } = null!;

    public int RoundNumber { get; set; }

    public int DefenderTeam { get; set; }
    public int WinnerTeam { get; set; }

    [MaxLength(16)]
    public required string TimeLimit { get; set; }

    [MaxLength(16)]
    public required string NextTimeLimit { get; set; }

    public long RoundStartMs { get; set; }
    public long RoundEndMs { get; set; }

    public long RoundStartUnix { get; set; }
    public long RoundEndUnix { get; set; }

    public DateTime IngestedAtUtc { get; set; }

    public string? RawJson { get; set; }

    public List<MatchSide> Sides { get; set; } = new();
    public List<MatchObituary> Obituaries { get; set; } = new();
}
