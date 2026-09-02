using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class Match
{
    public Guid Id { get; set; }

    [MaxLength(64)]
    public required string ExternalMatchId { get; set; }

    [MaxLength(64)]
    public required string MapName { get; set; }

    [MaxLength(64)]
    public required string Config { get; set; }

    [MaxLength(256)]
    public required string ServerName { get; set; }

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
