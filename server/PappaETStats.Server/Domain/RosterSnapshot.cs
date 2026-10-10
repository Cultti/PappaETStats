using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class RosterSnapshot
{
    [MaxLength(64)] public required string Id { get; set; }
    [MaxLength(64)] public required string ServerIp { get; set; }
    [MaxLength(16)] public required string ServerPort { get; set; }
    public long TimestampUnix { get; set; }
    public required string PayloadJson { get; set; }
}
