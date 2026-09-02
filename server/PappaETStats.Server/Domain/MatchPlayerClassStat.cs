namespace PappaETStats.Server.Domain;

public sealed class MatchPlayerClassStat
{
    public Guid Id { get; set; }

    public Guid MatchPlayerId { get; set; }
    public MatchPlayer MatchPlayer { get; set; } = null!;

    public int ClassId { get; set; }

    // Milliseconds played as this class during this round.
    public long Ms { get; set; }
}
