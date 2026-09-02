namespace PappaETStats.Server.Components.Shared;

public sealed record PlayerWeaponStatRow(
    int Weapon,
    int Hits,
    int Atts,
    int Kills,
    int Deaths,
    int Headshots);

public sealed record PlayerClassStatRow(
    string Name,
    string TimeText);

public sealed class PlayerStatsGridRow
{
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Games { get; init; }

    public double TimePlayedPercent { get; init; }

    public int DamageGiven { get; init; }
    public int DamageReceived { get; init; }
    public int TeamDamageGiven { get; init; }
    public int TeamDamageReceived { get; init; }
    public int Gibs { get; init; }
    public int SelfKills { get; init; }
    public int TeamKills { get; init; }

    public IReadOnlyList<PlayerWeaponStatRow> WeaponStats { get; init; } = [];
    public IReadOnlyList<PlayerClassStatRow> ClassStats { get; init; } = [];
}
