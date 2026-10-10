using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

// A match is a sequence of maps played by the same two rosters. Match remains
// the map entity so existing map URLs, demo uploads and skill ratings stay stable.
public sealed class MatchSeries
{
    public Guid Id { get; set; }
    [MaxLength(512)] public required string ServerKey { get; set; }
    public long StartedAtUnix { get; set; }
    public long LastPlayedAtUnix { get; set; }
    public long? EndedAtUnix { get; set; }
    public List<Match> Maps { get; set; } = [];
}
