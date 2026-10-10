using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Services;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class ScoreboardCacheTests
{
    [Fact]
    public async Task MatchesCountSeriesOnceAndUngroupedMapsSeparatelyAcrossBothRounds()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options;
        await using var db = new StatsDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var series = new MatchSeries { Id = Guid.NewGuid(), ServerKey = "test" };
        db.MatchSeries.Add(series);
        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10000;
        for (var index = 0; index < 20; index++)
        {
            var map = new Match
            {
                Id = Guid.NewGuid(), Series = index < 18 ? series : null,
                ExternalMatchId = "map-" + index, MapName = "supply", Config = "test",
                ServerName = "Test", ServerIp = "127.0.0.1", ServerPort = "27960"
            };
            for (var round = 1; round <= 2; round++)
            {
                map.Rounds.Add(new MatchRound
                {
                    Id = Guid.NewGuid(), RoundNumber = round, TimeLimit = "12:00", NextTimeLimit = "12:00",
                    RoundStartUnix = start + index * 200 + round * 70, IngestedAtUtc = DateTime.UtcNow,
                    Sides = [new MatchSide
                    {
                        Id = Guid.NewGuid(), Team = round,
                        Players = [new MatchPlayer
                        {
                            Id = Guid.NewGuid(), Guid = new string('A', 32), Name = "Player", Team = round,
                            Assists = 2, ObjectivesPlanted = 1
                        }]
                    }]
                });
            }
            db.Matches.Add(map);
        }
        await db.SaveChangesAsync();

        var boards = await new ScoreboardCache(new Factory(options)).GetAsync();

        Assert.Equal(20, Assert.Single(boards.Single(b => b.Title == "Maps played").Rows).Value);
        Assert.Equal(3, Assert.Single(boards.Single(b => b.Title == "Matches played").Rows).Value);
        Assert.Equal(80, Assert.Single(boards.Single(b => b.Title == "Assists").Rows).Value);
        Assert.Equal(40, Assert.Single(boards.Single(b => b.Title == "Objectives planted").Rows).Value);
    }

    [Fact]
    public async Task MariaDbScoreboardQueriesAvoidCorrelatedDerivedTables()
    {
        // Run the real service through the MariaDB provider, capturing commands
        // before execution. Empty readers allow every query to be translated
        // without needing a database server or touching application data.
        var capture = new CaptureCommands();
        var options = new DbContextOptionsBuilder<StatsDbContext>()
            .UseMySql("Server=localhost;Database=unused;User=unused;Password=unused",
                new MariaDbServerVersion(new Version(11, 8, 3)))
            .AddInterceptors(new SkipConnection(), capture).Options;

        await new ScoreboardCache(new Factory(options)).GetAsync();

        Assert.Equal(4, capture.Commands.Count);
        Assert.Contains("`SeriesId`", capture.Commands[0]);
        var metrics = Assert.Single(capture.Commands, sql => sql.Contains("AS `Assists`"));
        // The old query nested SELECT DISTINCT inside SELECT COUNT(*) and
        // referenced the outer GUID from a derived table in that subquery.
        Assert.DoesNotContain("SELECT DISTINCT", metrics);
        Assert.DoesNotContain("SELECT COUNT(*)", metrics);
        Assert.Contains("GROUP BY", metrics);
    }

    [Fact]
    public async Task MatchStatisticsQueriesTranslateForMariaDb()
    {
        var capture = new CaptureCommands();
        var options = new DbContextOptionsBuilder<StatsDbContext>()
            .UseMySql("Server=localhost;Database=unused;User=unused;Password=unused",
                new MariaDbServerVersion(new Version(11, 8, 3)))
            .AddInterceptors(new SkipConnection(), capture).Options;
        await using var db = new StatsDbContext(options);
        var result = await MatchStatistics.LoadAsync(db, [Guid.NewGuid()]);
        Assert.Empty(result.Players);
        Assert.Contains(capture.Commands, sql => sql.Contains("`SeriesTeam1Faction`"));
        Assert.Contains(capture.Commands, sql => sql.Contains("`RoundEvents`"));
    }

    private sealed class Factory(DbContextOptions<StatsDbContext> options) : IDbContextFactory<StatsDbContext>
    {
        public StatsDbContext CreateDbContext() => new(options);
    }

    private sealed class SkipConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class CaptureCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            var table = new DataTable();
            // Split queries inspect their projected schema even with no rows.
            for (var i = 0; i < 128; i++) table.Columns.Add("column" + i, typeof(object));
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}
