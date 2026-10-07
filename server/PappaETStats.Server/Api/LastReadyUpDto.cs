namespace PappaETStats.Server.Api;

public sealed record LastReadyUpDto(
    string EventId, string PlayerGuid, long ReadyAtUnix, long CountdownAtUnix,
    string ServerId, string MapName, int Round);
