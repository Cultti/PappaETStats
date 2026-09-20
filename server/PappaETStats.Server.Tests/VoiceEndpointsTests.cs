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
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class VoiceEndpointsTests
{
    private const string AxisGuid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string AlliesGuid = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string UnknownGuid = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    private const string FormerGuid = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

    [Fact]
    public async Task MergeGuidMovesHistoryAndRecalculatesRatings()
    {
        await using var host = await TestHost.Start();

        var response = await host.Client.PostAsJsonAsync("/api/admin/players/merge-guid", new
        {
            sourceGuid = FormerGuid.ToLowerInvariant(),
            targetGuid = AxisGuid.ToLowerInvariant(),
        });

        response.EnsureSuccessStatusCode();
        await using var db = new StatsDbContext(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(host.Connection).Options);
        Assert.Null(await db.Players.SingleOrDefaultAsync(p => p.Guid == FormerGuid));
        Assert.NotNull(await db.Players.SingleOrDefaultAsync(p => p.Guid == AxisGuid));
        Assert.Equal(AxisGuid, await db.MatchPlayers.Select(p => p.Guid).SingleAsync());
    }

    [Fact]
    public async Task MigrationRemovesObsoletePreferenceAndPreservesDiscordLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StatsDbContext(new DbContextOptionsBuilder<StatsDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260902212429_AddDiscordRegistration");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Players (Guid, DiscordId, Mu, Sigma, AutoMoveToVoice) VALUES ({AxisGuid}, {"111"}, {25}, {8}, {false})");
        await db.Database.MigrateAsync();
        var player = await db.Players.SingleAsync();
        Assert.Equal(AxisGuid, player.Guid);
        Assert.Equal("111", player.DiscordId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Players') WHERE name = 'AutoMoveToVoice'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BalanceDoesNotCallVoiceBot()
    {
        await using var host = await TestHost.Start();
        var response = await host.Client.PostAsJsonAsync("/api/skillratings/balance-teams", new { guids = new[] { AxisGuid, AlliesGuid } });
        response.EnsureSuccessStatusCode();
        Assert.Equal(0, host.Bot.Calls);
    }

    [Fact]
    public async Task SelfMoveOnlySendsRequestedPlayerAndConfirmsBotResult()
    {
        await using var host = await TestHost.Start();
        var response = await host.Move(new VoiceEndpoints.VoicePlayer(AxisGuid.ToLowerInvariant(), "allies"));
        var result = Assert.Single(response);
        Assert.True(result.Moved);
        Assert.Equal("Blue room", result.Channel);
        Assert.Equal("Moved to Blue room.", result.Message);
        Assert.Equal("bot-secret", host.Bot.Secret);
        Assert.Equal("/webhook/move-teams", host.Bot.Path);
        Assert.Empty(host.Bot.Payload.GetProperty("axis").EnumerateArray());
        Assert.Equal("111", Assert.Single(host.Bot.Payload.GetProperty("allies").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task UnregisteredPlayerGetsChannelAndRegistrationLinkWithoutBotCall()
    {
        await using var host = await TestHost.Start();
        var result = Assert.Single(await host.Move(new VoiceEndpoints.VoicePlayer(UnknownGuid, "axis")));
        Assert.False(result.Moved);
        Assert.Contains("Red room", result.Message);
        Assert.Contains("https://et.aukko.net", result.Message);
        Assert.Equal(0, host.Bot.Calls);
    }

    [Fact]
    public async Task BulkMoveReportsPartialFailureAndUnregisteredPlayersIndividually()
    {
        await using var host = await TestHost.Start();
        host.Bot.Status = HttpStatusCode.MultiStatus;
        host.Bot.Response = """{"ok":false,"results":[{"user_id":"111","team":"axis","moved":true},{"user_id":"222","team":"allies","moved":false,"error":"User is not connected to voice"}]}""";
        var results = await host.Move(new VoiceEndpoints.VoicePlayer(AxisGuid, "axis"), new(AlliesGuid, "allies"), new(UnknownGuid, "axis"));
        Assert.True(results[0].Moved);
        Assert.False(results[1].Moved);
        Assert.Contains("User is not connected to voice", results[1].Message);
        Assert.Contains("Blue room", results[1].Message);
        Assert.Contains("Not registered", results[2].Message);
        Assert.Equal(1, host.Bot.Calls);
        Assert.Single(host.Bot.Payload.GetProperty("axis").EnumerateArray());
    }

    [Theory]
    [InlineData("{}", 200)]
    [InlineData("{\"results\":[null]}", 200)]
    [InlineData("not JSON", 200)]
    [InlineData("{}", 502)]
    [InlineData("{\"results\":[{\"user_id\":\"111\",\"team\":\"allies\",\"moved\":true}]}", 200)]
    public async Task MissingInvalidOrFailedBotResponseNeverClaimsSuccess(string body, int status)
    {
        await using var host = await TestHost.Start();
        host.Bot.Response = body;
        host.Bot.Status = (HttpStatusCode)status;
        var result = Assert.Single(await host.Move(new VoiceEndpoints.VoicePlayer(AxisGuid, "axis")));
        Assert.False(result.Moved);
        Assert.Contains("Red room", result.Message);
    }

    [Fact]
    public async Task BotTimeoutReturnsAnUnconfirmedResult()
    {
        await using var host = await TestHost.Start();
        host.Bot.Timeout = true;
        var result = Assert.Single(await host.Move(new VoiceEndpoints.VoicePlayer(AxisGuid, "axis")));
        Assert.False(result.Moved);
        Assert.Contains("did not confirm", result.Message);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("Bearer wrong", "secret")]
    [InlineData("Bearer secret", "")]
    public async Task UnauthorizedRequestsNeverReachBot(string authorization, string configuredToken)
    {
        await using var host = await TestHost.Start(configuredToken);
        host.Client.DefaultRequestHeaders.Remove("Authorization");
        host.Client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        var response = await host.Client.PostAsJsonAsync("/api/voice/move", new { players = new[] { new VoiceEndpoints.VoicePlayer(AxisGuid, "axis") } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Bot.Calls);
    }

    [Theory]
    [InlineData("{\"players\":[]}")]
    [InlineData("{\"players\":[null]}")]
    [InlineData("{\"players\":[{\"guid\":\"invalid\",\"team\":\"axis\"}]}")]
    [InlineData("{\"players\":[{\"guid\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"team\":\"spectator\"}]}")]
    [InlineData("{\"players\":[{\"guid\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"team\":\"axis\"},{\"guid\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"team\":\"allies\"}]}")]
    public async Task InvalidInputNeverReachesBot(string body)
    {
        await using var host = await TestHost.Start();
        var response = await host.Client.PostAsync("/api/voice/move", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Bot.Calls);
    }

    private sealed class TestHost(WebApplication app, SqliteConnection connection, HttpClient client, BotHandler bot) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public BotHandler Bot => bot;
        public SqliteConnection Connection => connection;

        public static async Task<TestHost> Start(string token = "secret")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new IngestOptions { Token = token }));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AdminOptions { Token = token }));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new WebhookOptions
            {
                Url = "http://bot.invalid/webhook/game-completed", Token = "bot-secret",
                AxisVoiceChannelName = "Red room", AlliesVoiceChannelName = "Blue room"
            }));
            builder.Services.AddDbContextFactory<StatsDbContext>(o => o.UseSqlite(connection));
            var bot = new BotHandler();
            builder.Services.AddHttpClient("Webhook").ConfigurePrimaryHttpMessageHandler(() => bot);
            var app = builder.Build();
            app.MapVoiceEndpoints();
            app.MapTeamBalanceEndpoints();
            app.MapAdminEndpoints();
            await using (var db = await app.Services.GetRequiredService<IDbContextFactory<StatsDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Players.AddRange(new Player { Guid = AxisGuid, DiscordId = "111", Mu = 25, Sigma = 8 },
                    new Player { Guid = AlliesGuid, DiscordId = "222", Mu = 25, Sigma = 8 },
                    new Player { Guid = FormerGuid, Mu = 25, Sigma = 8 });
                var match = new Match
                {
                    Id = Guid.NewGuid(), ExternalMatchId = "merge-test", MapName = "test", Config = "test",
                    ServerName = "test", ServerIp = "127.0.0.1", ServerPort = "27960",
                };
                var round = new MatchRound
                {
                    Id = Guid.NewGuid(), Match = match, RoundNumber = 1, TimeLimit = "", NextTimeLimit = "",
                };
                var side = new MatchSide { Id = Guid.NewGuid(), MatchRound = round, Team = 1 };
                side.Players.Add(new MatchPlayer
                {
                    Id = Guid.NewGuid(), MatchSide = side, Guid = FormerGuid, Name = "former", Team = 1,
                });
                round.Sides.Add(side);
                match.Rounds.Add(round);
                db.Matches.Add(match);
                await db.SaveChangesAsync();
            }
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer secret");
            return new TestHost(app, connection, client, bot);
        }

        public async Task<List<VoiceEndpoints.VoiceResult>> Move(params VoiceEndpoints.VoicePlayer[] players)
        {
            var response = await client.PostAsJsonAsync("/api/voice/move", new VoiceEndpoints.VoiceRequest(players));
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("results").Deserialize<List<VoiceEndpoints.VoiceResult>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class BotHandler : HttpMessageHandler
    {
        public int Calls;
        public bool Timeout;
        public string? Response;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public JsonElement Payload;
        public string? Secret;
        public string? Path;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Timeout) throw new TaskCanceledException("simulated bot timeout");
            Payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Secret = request.Headers.GetValues("X-Webhook-Secret").Single();
            Path = request.RequestUri!.AbsolutePath;
            var response = Response ?? JsonSerializer.Serialize(new
            {
                ok = true,
                results = new[] { "axis", "allies" }.SelectMany(team => Payload.GetProperty(team).EnumerateArray()
                    .Select(id => new { user_id = id.GetString(), team, moved = true }))
            });
            return new HttpResponseMessage(Status) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
