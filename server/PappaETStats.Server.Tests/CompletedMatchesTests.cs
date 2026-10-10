using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Services;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class CompletedMatchesTests
{
    [Fact]
    public async Task MissingStartsDoNotHideRecentGamesBehindTheFiftyMatchLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StatsDbContext(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var recent = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (var i = 0; i < 51; i++) AddMatch(db, "old-" + i, recent - 86400 - i, recent - 86000 - i);
        var missingStart = AddMatch(db, "missing-start", 0, recent);
        var missingRound1 = AddMatch(db, "missing-round1", 0, recent + 100, round1: false);
        var missingBothTimes = AddMatch(db, "missing-times", 0, 0, ingestedAt: DateTime.UnixEpoch.AddSeconds(recent + 200));
        // This match must remain excluded because the second round hasn't arrived.
        AddMatch(db, "unfinished", recent + 300, recent + 400, round2: false);
        await db.SaveChangesAsync();

        var rows = await CompletedMatches.Query(db).Take(50).ToListAsync();
        Assert.Equal(50, rows.Count);
        Assert.Equal(new[] { missingBothTimes, missingRound1, missingStart }, rows.Take(3).Select(r => r.MatchId));
        Assert.Equal(recent, rows[2].PlayedAtUnix);
        Assert.Equal(recent + 100, rows[1].PlayedAtUnix);
        Assert.Equal(50, rows.Select(r => r.MatchId).Distinct().Count());
    }

    private static Guid AddMatch(StatsDbContext db, string externalId, long start, long end,
        bool round1 = true, bool round2 = true, DateTime? ingestedAt = null)
    {
        var match = new Match { Id = Guid.NewGuid(), ExternalMatchId = externalId, MapName = "radar",
            ServerName = "Cup", Config = "test", ServerIp = "0.0.0.0", ServerPort = "27960" };
        foreach (var number in new[] { 1, 2 })
        {
            if (number == 1 && !round1 || number == 2 && !round2) continue;
            match.Rounds.Add(new MatchRound { Id = Guid.NewGuid(), RoundNumber = number,
                RoundStartUnix = start, RoundEndUnix = end, TimeLimit = "12:00", NextTimeLimit = "7:48",
                IngestedAtUtc = ingestedAt ?? DateTime.UnixEpoch.AddSeconds(end) });
        }
        db.Matches.Add(match);
        return match.Id;
    }
}
