using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PappaETStats.Server.Api;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Options;
using PappaETStats.Server.Services;
using PappaETStats.SkillRating;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class IngestEndpointsTests
{
    [Theory]
    [InlineData("Cup #1", "Cup #2")]
    [InlineData("Same hostname", "Same hostname")]
    public async Task TwoServersOnSameAddressAndMapKeepTheirOwnRounds(string name1, string name2)
    {
        await using var host = await TestHost.Start();
        var id1 = await host.MatchId("cup-1", name1, 1);
        await host.Post(Payload(id1, "cup-1", name1, 1, 'A'));
        var id2 = await host.MatchId("cup-2", name2, 1);
        await host.Post(Payload(id2, "cup-2", name2, 1, 'C'));

        // Server 2's round 1 was ingested last: the old query returned id2 here.
        Assert.Equal(id1, await host.MatchId("cup-1", name1, 2));
        Assert.Equal(id2, await host.MatchId("cup-2", name2, 2));
        await host.Post(Payload(id1, "cup-1", name1, 2, 'A'));
        Assert.Equal(id2, await host.MatchId("cup-2", name2, 2));
        await host.Post(Payload(id2, "cup-2", name2, 2, 'C'));

        await using var db = host.CreateDb();
        var matches = await db.Matches.Include(m => m.Rounds).ThenInclude(r => r.Sides)
            .ThenInclude(s => s.Players).ToListAsync();
        Assert.Equal(2, matches.Count);
        foreach (var match in matches)
        {
            Assert.Equal(2, match.Rounds.Count);
            var round1Guids = match.Rounds.Single(r => r.RoundNumber == 1).Sides.SelectMany(s => s.Players).Select(p => p.Guid).Order();
            var round2Guids = match.Rounds.Single(r => r.RoundNumber == 2).Sides.SelectMany(s => s.Players).Select(p => p.Guid).Order();
            Assert.Equal(round1Guids, round2Guids);
        }
        Assert.NotEqual(id1, await host.MatchId("cup-1", name1, 2)); // A completed match cannot be reused.
    }

    [Fact]
    public async Task ExactServerIdTakesPrecedenceOverANewerLegacyHostnameMatch()
    {
        await using var host = await TestHost.Start();
        await host.Post(Payload("identified", "cup-1", "Cup", 1));
        await host.Post(Payload("legacy", null, "Cup", 1, 'C'));
        Assert.Equal("identified", await host.MatchId("cup-1", "Cup", 2));
    }

    [Fact]
    public async Task StableServerIdAllowsHostnameChangesAndPlayerSubstitutions()
    {
        await using var host = await TestHost.Start();
        await host.Post(Payload("match-1", "cup-1", "Old name", 1));
        Assert.Equal("match-1", await host.MatchId("cup-1", "New name", 2));
        // Players may change between rounds; server identity determines the pairing.
        await host.Post(Payload("match-1", "cup-1", "New name", 2, 'C'));
        await using var db = host.CreateDb();
        Assert.Equal("New name", (await db.Matches.SingleAsync()).ServerName);
        Assert.Equal(2, await db.MatchRounds.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cup-1")]
    public async Task LegacyMatchesUseTheirHostnameAndCanAcquireAStableId(string? newServerId)
    {
        await using var host = await TestHost.Start();
        await host.Post(Payload("legacy-1", null, "Cup #1", 1));
        await host.Post(Payload("legacy-2", null, "Cup #2", 1, 'C'));
        Assert.Equal("legacy-1", await host.MatchId(newServerId, "Cup #1", 2));
        await host.Post(Payload("legacy-1", newServerId, "Cup #1", 2));
        await using var db = host.CreateDb();
        Assert.Equal(newServerId, (await db.Matches.SingleAsync(m => m.ExternalMatchId == "legacy-1")).ServerId);
    }

    [Fact]
    public async Task Round2LookupRequiresIdentityAndNeverBorrowsAnotherServersMatch()
    {
        await using var host = await TestHost.Start();
        await host.Post(Payload("other-match", "cup-2", "Cup #2", 1));
        var response = await host.Client.GetAsync("/api/matches/matchid?serverIp=2.29.8.44&serverPort=27960&mapname=sw_goldrush_te&round=2");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("other-match", await host.MatchId("cup-1", "Cup #1", 2));
        Assert.NotEqual("other-match", await host.MatchId(null, "Cup #2", 2)); // Cannot downgrade a stored stable ID.
    }

    [Theory]
    [InlineData("serverId")]
    [InlineData("missingServerId")]
    [InlineData("legacyHostname")]
    [InlineData("map")]
    [InlineData("ip")]
    [InlineData("port")]
    public async Task MismatchedUploadsCannotAddOrOverwriteRounds(string mismatch)
    {
        await using var host = await TestHost.Start();
        var legacy = mismatch == "legacyHostname";
        var original = Payload("match-1", legacy ? null : "cup-1", "Cup #1", 1);
        await host.Post(original);
        var wrong = new MatchIngestDto
        {
            MatchId = original.MatchId,
            ServerId = mismatch == "serverId" ? "cup-2" : mismatch == "missingServerId" ? null : original.ServerId,
            ServerName = legacy ? "Cup #2" : original.ServerName,
            MapName = mismatch == "map" ? "supply" : original.MapName,
            ServerIp = mismatch == "ip" ? "127.0.0.2" : original.ServerIp,
            ServerPort = mismatch == "port" ? "27961" : original.ServerPort,
            Round = 2,
            Players = original.Players,
        };
        var response = await host.Client.PostAsJsonAsync("/api/matches", wrong);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Also exercise the replacement path against an already stored round 2.
        await host.Post(Payload("match-1", original.ServerId, "Cup #1", 2));
        response = await host.Client.PostAsJsonAsync("/api/matches", wrong);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = host.CreateDb();
        var match = await db.Matches.SingleAsync();
        Assert.Equal(original.ServerName, match.ServerName);
        Assert.Equal(original.ServerId, match.ServerId);
        Assert.Equal(original.MapName, match.MapName);
        Assert.Equal(original.ServerIp, match.ServerIp);
        Assert.Equal(original.ServerPort, match.ServerPort);
        Assert.Equal(2, await db.MatchRounds.CountAsync());
        Assert.All(await db.MatchRounds.ToListAsync(), r => Assert.Equal("12:00", r.TimeLimit));
        Assert.Equal(12, await db.MatchPlayers.CountAsync());
    }

    [Fact]
    public async Task ServerIdMigrationPreservesExistingMatchesAndMatchesCurrentModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StatsDbContext(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260924200829_AddMatchPlayerMultiKills");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Matches (Id, ExternalMatchId, MapName, Config, ServerName, ServerIp, ServerPort)
            VALUES ({id}, {"legacy"}, {"supply"}, {"test"}, {"Cup #1"}, {"2.29.8.44"}, {"27960"})
            """);
        await db.Database.MigrateAsync();
        var match = await db.Matches.SingleAsync();
        Assert.Equal(id, match.Id);
        Assert.Equal("legacy", match.ExternalMatchId);
        Assert.Null(match.ServerId);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static MatchIngestDto Payload(string matchId, string? serverId, string name, int round, char firstGuid = 'A') => new()
    {
        MatchId = matchId,
        ServerId = serverId,
        ServerName = name,
        ServerIp = "2.29.8.44",
        ServerPort = "27960",
        MapName = "sw_goldrush_te",
        Config = "test",
        Round = round,
        WinnerTeam = 1,
        TimeLimit = "12:00",
        NextTimeLimit = "1:00",
        RoundStartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Players = Enumerable.Range(0, 6).Select(i => new PlayerDto
        {
            Guid = new string(firstGuid, 31) + i.ToString("X"),
            Name = "Player " + i,
            Team = round == 1 ? (i < 3 ? 1 : 2) : (i < 3 ? 2 : 1),
        }).ToList(),
    };

    private sealed class TestHost(WebApplication app, SqliteConnection connection, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public StatsDbContext CreateDb() => new(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options);

        public static async Task<TestHost> Start()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new IngestOptions { Token = "secret" }));
            builder.Services.Configure<WebhookOptions>(_ => { });
            builder.Services.Configure<SkillRatingOptions>(_ => { });
            builder.Services.AddHttpClient();
            builder.Services.AddDbContextFactory<StatsDbContext>(o => o.UseSqlite(connection));
            builder.Services.AddSingleton<ScoreboardCache>();
            var app = builder.Build();
            app.MapIngestEndpoints();
            await using (var db = await app.Services.GetRequiredService<IDbContextFactory<StatsDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer secret");
            return new TestHost(app, connection, client);
        }

        public async Task<string> MatchId(string? serverId, string name, int round)
        {
            var query = $"/api/matches/matchid?serverIp=2.29.8.44&serverPort=27960&mapname=sw_goldrush_te&round={round}&servername={Uri.EscapeDataString(name)}";
            if (serverId is not null) query += "&serverId=" + Uri.EscapeDataString(serverId);
            var response = await client.GetAsync(query);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matchId").GetString()!;
        }

        public async Task Post(MatchIngestDto dto)
        {
            var response = await client.PostAsJsonAsync("/api/matches", dto);
            response.EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
