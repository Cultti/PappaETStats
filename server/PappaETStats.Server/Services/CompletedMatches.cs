using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Data;

namespace PappaETStats.Server.Services;

public sealed record MatchListRow(
    Guid MatchId, DateTime PlayedAtUtc, DateTime IngestedAtUtc,
    string? MapName, string? ServerName, int? Team1PlayerCount, int? Team2PlayerCount,
    string? Round2NextTimeLimit, int Round2WinnerTeam,
    string? Round1TimeLimit, string? Round1NextTimeLimit, int? Round1WinnerTeam)
{
    public long PlayedAtUnix => new DateTimeOffset(DateTime.SpecifyKind(PlayedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

public static class CompletedMatches
{
    public static IQueryable<MatchListRow> Query(StatsDbContext db) =>
        from r2 in db.MatchRounds.AsNoTracking()
        where r2.RoundNumber == 2
        join m in db.Matches.AsNoTracking() on r2.MatchId equals m.Id
        join s1j in db.MatchSides.AsNoTracking().Where(x => x.Team == 1) on r2.Id equals s1j.MatchRoundId into s1g
        from s1 in s1g.DefaultIfEmpty()
        join s2j in db.MatchSides.AsNoTracking().Where(x => x.Team == 2) on r2.Id equals s2j.MatchRoundId into s2g
        from s2 in s2g.DefaultIfEmpty()
        join r1j in db.MatchRounds.AsNoTracking().Where(x => x.RoundNumber == 1) on r2.MatchId equals r1j.MatchId into r1g
        from r1 in r1g.DefaultIfEmpty()
        // A Lua VM initialized in GS_PLAYING used to omit the start. Use a
        // recorded event time so those completed games still enter the top 50.
        let playedAtUnix = r1 != null && r1.RoundStartUnix > 0 ? r1.RoundStartUnix
            : r1 != null && r1.RoundEndUnix > 0 ? r1.RoundEndUnix
            : r2.RoundStartUnix > 0 ? r2.RoundStartUnix : r2.RoundEndUnix
        let playedAtUtc = playedAtUnix > 0 ? DateTime.UnixEpoch.AddSeconds(playedAtUnix) : r2.IngestedAtUtc
        orderby playedAtUtc descending, r2.IngestedAtUtc descending, r2.MatchId
        select new MatchListRow(
            r2.MatchId, playedAtUtc, r2.IngestedAtUtc, m.MapName, m.ServerName,
            s1 != null ? s1.PlayerCount : (int?)null, s2 != null ? s2.PlayerCount : (int?)null,
            r2.NextTimeLimit, r2.WinnerTeam, r1 != null ? r1.TimeLimit : null,
            r1 != null ? r1.NextTimeLimit : null, r1 != null ? r1.WinnerTeam : (int?)null);
}
