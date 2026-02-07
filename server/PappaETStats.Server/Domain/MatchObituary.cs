using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class MatchObituary
{
    public Guid Id { get; set; }

    public Guid MatchRoundId { get; set; }
    public MatchRound MatchRound { get; set; } = null!;

    public long TimestampMs { get; set; }

    [MaxLength(64)]
    public string? TargetGuid { get; set; }

    [MaxLength(64)]
    public string? AttackerGuid { get; set; }

    public int MeansOfDeath { get; set; }

    public int AttackerRespawnTime { get; set; }
    public int VictimRespawnTime { get; set; }
}
