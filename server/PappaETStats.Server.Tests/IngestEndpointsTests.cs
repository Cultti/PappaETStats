using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.Cookies;
using MudBlazor.Services;
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

public sealed partial class IngestEndpointsTests
{
    [Fact]
    public async Task GzipCompressedMatchUploadsAreAccepted()
    {
        await using var host = await TestHost.Start();
        var json = JsonSerializer.Serialize(Payload("gzip", "cup-1", "Cup", 1));
        var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(json));
        }

        using var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        var response = await host.Client.PostAsync("/api/matches", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = host.CreateDb();
        Assert.Equal("gzip", (await db.Matches.SingleAsync()).ExternalMatchId);
    }

    [Fact]
    public async Task RoundRetriesPreserveRowsRatingsAndSendOnlyOneWebhook()
    {
        await using var host = await TestHost.Start(webhook: true);
        var r1 = Payload("retry", "cup-1", "Cup", 1);
        var r2 = Payload("retry", "cup-1", "Cup", 2);
        await host.Post(r1);
        await host.Post(r2);
        Guid[] roundIds;
        Dictionary<string, double> ratings;
        await using (var db = host.CreateDb())
        {
            roundIds = await db.MatchRounds.OrderBy(r => r.RoundNumber).Select(r => r.Id).ToArrayAsync();
            ratings = await db.Players.ToDictionaryAsync(p => p.Guid, p => p.Mu);
            Assert.Equal(6, ratings.Count);
        }
        await host.Post(r1);
        await host.Post(r2);
        await host.Post(r2);
        await using var after = host.CreateDb();
        Assert.Equal(roundIds, await after.MatchRounds.OrderBy(r => r.RoundNumber).Select(r => r.Id).ToArrayAsync());
        Assert.Equal(1, await after.Matches.CountAsync());
        Assert.Equal(12, await after.MatchPlayers.CountAsync());
        foreach (var player in await after.Players.ToListAsync()) Assert.Equal(ratings[player.Guid], player.Mu);
        Assert.Equal(1, host.WebhookHandler.Requests);
    }

    [Fact]
    public async Task FailedRoundReplacementRollsBackTheDeletion()
    {
        await using var host = await TestHost.Start();
        var dto = Payload("replacement", "cup-1", "Cup", 2);
        await host.Post(dto);
        Guid originalId;
        await using (var db = host.CreateDb())
        {
            originalId = (await db.MatchRounds.SingleAsync()).Id;
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_round BEFORE INSERT ON MatchRounds
                BEGIN SELECT RAISE(ABORT, 'Simulated storage failure'); END;
                """);
        }
        var json = JsonSerializer.Serialize(dto).Replace("\"damage_given\":0", "\"damage_given\":10");
        var response = await host.Client.PostAsync("/api/matches", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await using var after = host.CreateDb();
        Assert.Equal(originalId, (await after.MatchRounds.SingleAsync()).Id);
        Assert.Equal(6, await after.MatchPlayers.CountAsync());
    }

    [Fact]
    public async Task LastReadyUpsAreAuthenticatedNormalizedAndIdempotent()
    {
        await using var host = await TestHost.Start();
        var dto = new LastReadyUpDto("countdown-1", new string('a', 32), 100, 110, "cup-1", "supply", 1);
        host.Client.DefaultRequestHeaders.Remove("Authorization");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer secret");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("/api/ready-ups",
            dto with { PlayerGuid = new string('B', 32) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/ready-ups",
            dto with { EventId = "countdown-2", Round = 2 })).StatusCode);
        await using var db = host.CreateDb();
        var events = await db.LastReadyUps.OrderBy(r => r.EventId).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, r => Assert.Equal(new string('A', 32), r.PlayerGuid));
        Assert.Equal(100, events[0].ReadyAtUnix);
        Assert.Equal(110, events[0].CountdownAtUnix);
        Assert.Equal("cup-1", events[0].ServerId);
        Assert.Equal("supply", events[0].MapName);
    }

    [Fact]
    public async Task InvalidLastReadyUpsAreRejected()
    {
        await using var host = await TestHost.Start();
        var valid = new LastReadyUpDto("event", new string('A', 32), 100, 110, "cup-1", "supply", 1);
        LastReadyUpDto[] invalid =
        [
            valid with { PlayerGuid = "" }, valid with { PlayerGuid = new string('Z', 32) },
            valid with { PlayerGuid = null! }, valid with { EventId = "" },
            valid with { EventId = new string('X', 65) }, valid with { ServerId = " " },
            valid with { MapName = "" }, valid with { Round = 0 }, valid with { Round = 3 },
            valid with { ReadyAtUnix = 0 }, valid with { CountdownAtUnix = 99 },
        ];
        foreach (var dto in invalid)
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).StatusCode);
        await using var db = host.CreateDb();
        Assert.Empty(await db.LastReadyUps.ToListAsync());
    }

    [Fact]
    public async Task ReadyUpLeaderboardCountsGuidsAndInvalidatesCachedResults()
    {
        await using var host = await TestHost.Start();
        // Existing scoreboard eligibility: at least 20 matches and recent activity.
        for (var i = 0; i < 20; i++) await host.Post(Payload("match-" + i, "cup-1", "Cup", 1));
        // Use the endpoint's singleton cache to exercise invalidation.
        var cache = host.ScoreboardCache;
        Assert.Empty((await cache.GetAsync()).Single(b => b.Title == "Their body wasn't ready").Rows);
        var guid = new string('A', 31) + "0";
        var dto = new LastReadyUpDto("ready-1", guid.ToLowerInvariant(), 100, 110, "cup-1", "supply", 1);
        (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).EnsureSuccessStatusCode();
        (await host.Client.PostAsJsonAsync("/api/ready-ups", dto)).EnsureSuccessStatusCode();
        (await host.Client.PostAsJsonAsync("/api/ready-ups", dto with { EventId = "ready-2" })).EnsureSuccessStatusCode();
        var board = (await cache.GetAsync()).Single(b => b.Title == "Their body wasn't ready");
        var row = Assert.Single(board.Rows);
        Assert.Equal(guid, row.Guid);
        Assert.Equal("Player 0", row.Name);
        Assert.Equal(2, row.Value);
        Assert.Equal("2", board.Format(row.Value));
    }

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
        public CountingHttpHandler WebhookHandler => app.Services.GetRequiredService<CountingHttpHandler>();
        public ScoreboardCache ScoreboardCache => app.Services.GetRequiredService<ScoreboardCache>();
        public StatsDbContext CreateDb() => new(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options);

        public static async Task<TestHost> Start(bool webhook = false, bool renderPages = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new IngestOptions { Token = "secret" }));
            builder.Services.AddRequestDecompression();
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WebhookOptions
            { Url = webhook ? "https://webhook.invalid/completed" : null }));
            builder.Services.Configure<SkillRatingOptions>(_ => { });
            builder.Services.AddSingleton<CountingHttpHandler>();
            builder.Services.AddHttpClient("Webhook").ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<CountingHttpHandler>());
            builder.Services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<CountingHttpHandler>());
            builder.Services.AddDbContextFactory<StatsDbContext>(o => o.UseSqlite(connection));
            builder.Services.AddSingleton<ScoreboardCache>();
            if (renderPages)
            {
                builder.Services.AddRazorComponents().AddInteractiveServerComponents();
                builder.Services.AddMudServices();
                builder.Services.AddScoped<RegistrationTokenService>();
                builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
                builder.Services.AddAuthorization();
                builder.Services.AddCascadingAuthenticationState();
                builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            }
            var app = builder.Build();
            app.UseRequestDecompression();
            app.MapIngestEndpoints();
            if (renderPages)
            {
                app.UseDeveloperExceptionPage();
                app.UseAntiforgery();
                app.MapRazorComponents<PappaETStats.Server.Components.App>().AddInteractiveServerRenderMode();
                app.MapGet("/test/rounds/{id:guid}", (Guid id) =>
                    new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<PappaETStats.Server.Components.Shared.RoundTimeline>(
                        new { RoundId = id, RoundStartMs = 1000L }));
            }
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

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
