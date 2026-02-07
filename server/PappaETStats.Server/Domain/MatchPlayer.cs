using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class MatchPlayer
{
    public Guid Id { get; set; }

    public Guid MatchSideId { get; set; }
    public MatchSide MatchSide { get; set; } = null!;

    public int ClientNum { get; set; }

    [MaxLength(64)]
    public required string Guid { get; set; }

    [MaxLength(256)]
    public required string Name { get; set; }

    public int Team { get; set; }
    public int Rounds { get; set; }

    public int Xp { get; set; }
    public double TimePlayedPercent { get; set; }

    public int DamageGiven { get; set; }
    public int DamageReceived { get; set; }
    public int TeamDamageGiven { get; set; }
    public int TeamDamageReceived { get; set; }

    public int Gibs { get; set; }
    public int SelfKills { get; set; }
    public int TeamKills { get; set; }
    public int TeamGibs { get; set; }

    public List<MatchPlayerWeaponStat> WeaponStats { get; set; } = new();
}
