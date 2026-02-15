using System.Text.Json.Serialization;

namespace PappaETStats.Server.Api;

public sealed class MatchIngestDto
{
    [JsonPropertyName("roundStart")]
    public long RoundStart { get; init; }

    [JsonPropertyName("config")]
    public string? Config { get; init; }

    [JsonPropertyName("roundStartUnix")]
    public long RoundStartUnix { get; init; }

    [JsonPropertyName("mapname")]
    public string? MapName { get; init; }

    [JsonPropertyName("defenderteam")]
    public int DefenderTeam { get; init; }

    [JsonPropertyName("serverPort")]
    public string? ServerPort { get; init; }

    [JsonPropertyName("winnerteam")]
    public int WinnerTeam { get; init; }

    [JsonPropertyName("nextTimeLimit")]
    public string? NextTimeLimit { get; init; }

    [JsonPropertyName("obituaries")]
    public List<ObituaryDto>? Obituaries { get; init; }

    [JsonPropertyName("timelimit")]
    public string? TimeLimit { get; init; }

    [JsonPropertyName("roundEnd")]
    public long RoundEnd { get; init; }

    [JsonPropertyName("players")]
    public List<PlayerDto>? Players { get; init; }

    [JsonPropertyName("servername")]
    public string? ServerName { get; init; }

    [JsonPropertyName("serverIp")]
    public string? ServerIp { get; init; }

    [JsonPropertyName("matchID")]
    public string? MatchId { get; init; }

    [JsonPropertyName("roundEndUnix")]
    public long RoundEndUnix { get; init; }

    [JsonPropertyName("round")]
    public int Round { get; init; }
}

public sealed class PlayerDto
{
    [JsonPropertyName("team_kills")]
    public int TeamKills { get; init; }

    [JsonPropertyName("xp")]
    public int Xp { get; init; }

    [JsonPropertyName("time_played_percent")]
    public double TimePlayedPercent { get; init; }

    [JsonPropertyName("clientNum")]
    public int ClientNum { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("team_gibs")]
    public int TeamGibs { get; init; }

    [JsonPropertyName("gibs")]
    public int Gibs { get; init; }

    [JsonPropertyName("team")]
    public int Team { get; init; }

    [JsonPropertyName("rounds")]
    public int Rounds { get; init; }

    [JsonPropertyName("damage_received")]
    public int DamageReceived { get; init; }

    [JsonPropertyName("team_damage_received")]
    public int TeamDamageReceived { get; init; }

    [JsonPropertyName("team_damage_given")]
    public int TeamDamageGiven { get; init; }

    [JsonPropertyName("self_kills")]
    public int SelfKills { get; init; }

    [JsonPropertyName("damage_given")]
    public int DamageGiven { get; init; }

    [JsonPropertyName("weapon_stats")]
    public List<WeaponStatDto>? WeaponStats { get; init; }

    [JsonPropertyName("class_stats")]
    public List<ClassStatDto>? ClassStats { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ClassStatDto
{
    [JsonPropertyName("classId")]
    public int ClassId { get; init; }

    [JsonPropertyName("ms")]
    public long Ms { get; init; }
}

public sealed class WeaponStatDto
{
    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("atts")]
    public long Atts { get; init; }

    [JsonPropertyName("weapon")]
    public int Weapon { get; init; }

    [JsonPropertyName("deaths")]
    public int Deaths { get; init; }

    [JsonPropertyName("headshots")]
    public int Headshots { get; init; }

    [JsonPropertyName("kills")]
    public int Kills { get; init; }
}

public sealed class ObituaryDto
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("attacker")]
    public string? Attacker { get; init; }

    [JsonPropertyName("meansOfDeath")]
    public int MeansOfDeath { get; init; }

    [JsonPropertyName("attackerRespawnTime")]
    public int AttackerRespawnTime { get; init; }

    [JsonPropertyName("victimRespawnTime")]
    public int VictimRespawnTime { get; init; }
}
