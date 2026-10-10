using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class Match
{
    public Guid Id { get; set; }

    public Guid? SeriesId { get; set; }
    public MatchSeries? Series { get; set; }
    public int MapNumber { get; set; }
    // Which round-one faction represents series team 1 (teams swap in round 2).
    public int SeriesTeam1Faction { get; set; } = 1;
    [MaxLength(64)] public string? SourceMatchId { get; set; }

    [MaxLength(64)]
    public required string ExternalMatchId { get; set; }

    [MaxLength(64)]
    public required string MapName { get; set; }

    [MaxLength(64)]
    public required string Config { get; set; }

    [MaxLength(256)]
    public required string ServerName { get; set; }

    [MaxLength(256)]
    public string? ServerId { get; set; }

    [MaxLength(64)]
    public required string ServerIp { get; set; }

    [MaxLength(16)]
    public required string ServerPort { get; set; }

    public MatchWinner? Winner { get; set; }

    [MaxLength(256)]
    public string? DemoFileName { get; set; }

    [MaxLength(256)]
    public string? DemoZipFileName { get; set; }

    public DateTime? DemoUploadedAtUtc { get; set; }
    public DateTime? DemoZippedAtUtc { get; set; }

    public List<MatchRound> Rounds { get; set; } = new();
}
