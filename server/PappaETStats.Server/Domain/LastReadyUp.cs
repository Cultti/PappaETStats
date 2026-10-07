using System.ComponentModel.DataAnnotations;

namespace PappaETStats.Server.Domain;

public sealed class LastReadyUp
{
    [MaxLength(64)]
    public required string EventId { get; set; }
    [MaxLength(64)]
    public required string PlayerGuid { get; set; }
    public long ReadyAtUnix { get; set; }
    public long CountdownAtUnix { get; set; }
    [MaxLength(256)]
    public required string ServerId { get; set; }
    [MaxLength(64)]
    public required string MapName { get; set; }
    public int Round { get; set; }
}
