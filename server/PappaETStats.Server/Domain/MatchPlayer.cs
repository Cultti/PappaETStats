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

    public int? Assists { get; set; }
    public double? DistanceMeters { get; set; }
    public int? SpawnCount { get; set; }
    public double? AliveSeconds { get; set; }
    public double? EngagedSeconds { get; set; }
    public double? DownedSeconds { get; set; }
    public int? ObjectivesPlanted { get; set; }
    public int? ObjectivesDefused { get; set; }
    public int? ObjectivesSecured { get; set; }
    public int? ObjectivesReturned { get; set; }
    public int? ObjectivesDestroyed { get; set; }
    public int? ObjectivesRepaired { get; set; }
    // Retain all optional and future stats, including stance, speed and vehicles.
    public string? DetailsJson { get; set; }

    public int MultiKills2 { get; set; }
    public int MultiKills3 { get; set; }
    public int MultiKills4 { get; set; }
    public int MultiKills5 { get; set; }
    public int MultiKills6 { get; set; }

    public List<MatchPlayerWeaponStat> WeaponStats { get; set; } = new();

    public List<MatchPlayerClassStat> ClassStats { get; set; } = new();
}
