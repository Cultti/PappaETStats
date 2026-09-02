namespace PappaETStats.Server.Domain;

public sealed class MatchSide
{
    public Guid Id { get; set; }

    public Guid MatchRoundId { get; set; }
    public MatchRound MatchRound { get; set; } = null!;

    public int Team { get; set; }
    public bool IsDefender { get; set; }
    public bool IsWinner { get; set; }

    public int PlayerCount { get; set; }
    public int TotalXp { get; set; }

    public long TotalDamageGiven { get; set; }
    public long TotalDamageReceived { get; set; }
    public long TotalTeamDamageGiven { get; set; }
    public long TotalTeamDamageReceived { get; set; }

    public long TotalGibs { get; set; }
    public long TotalSelfKills { get; set; }
    public long TotalTeamKills { get; set; }
    public long TotalTeamGibs { get; set; }

    public List<MatchPlayer> Players { get; set; } = new();
}
