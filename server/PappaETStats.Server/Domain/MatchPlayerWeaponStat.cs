namespace PappaETStats.Server.Domain;

public sealed class MatchPlayerWeaponStat
{
    public Guid Id { get; set; }

    public Guid MatchPlayerId { get; set; }
    public MatchPlayer MatchPlayer { get; set; } = null!;

    public int Weapon { get; set; }
    public int Hits { get; set; }
    public int Atts { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Headshots { get; set; }
}
