using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class RoundEvent
{
    public Guid Id { get; set; }
    public Guid MatchRoundId { get; set; }
    public MatchRound MatchRound { get; set; } = null!;
    public int Sequence { get; set; }
    public long LevelTime { get; set; }
    public long UnixTimeMs { get; set; }
    [MaxLength(128)] public required string Label { get; set; }
    [MaxLength(64)] public required string Group { get; set; }
    public required string DataJson { get; set; }
}
