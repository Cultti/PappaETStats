using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PappaETStats.Server.Data;
using PappaETStats.Server.Api;
using PappaETStats.Server.Services;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed partial class IngestEndpointsTests
{
    private const string OksiiSubmit = "/api/v2/stats/etl/matches/stats/submit";
    private static string OksiiGuid(int index) => new string('A', 31) + index.ToString("X");

    // Mirrors stats/stats.lua's bitmask, five counters per weapon and ten
    // trailing summary values. GUID prefixes intentionally collide.
    private static JsonElement OksiiPayload(string map, int round, long start, string sourceId = "shared",
        bool swapped = false, int multiplier = 1, int replacement = 1, string port = "27960")
    {
        var stats = new Dictionary<string, object>();
        for (var i = 0; i < 2; i++)
        {
            var guid = OksiiGuid(i == 1 ? replacement : 0);
            stats[guid] = new
            {
                guid = guid[..8], name = $"Player {i}", rounds = round.ToString(), team = ((i == 0) ^ (round == 2) ^ swapped ? 1 : 2).ToString(),
                slot = i,
                weaponStats = new[] { "134217744", (10 * multiplier).ToString(), (20 * multiplier).ToString(), (3 * multiplier).ToString(),
                    (2 * multiplier).ToString(), (2 * multiplier).ToString(), "2", "4", "0", "0", "0",
                    (100 * multiplier).ToString(), (90 * multiplier).ToString(), "0", "0", "1", "0", "0", "0", "75.0", (10 * multiplier).ToString() },
                assists = 2, distance_travelled_meters = 50.5, spawn_count = 2,
                activity_stats_seconds = new { alive = 60, engaged = 30, from_weapon = 20, from_dmg_dealt = 25, from_support = 40 },
                stance_stats_seconds = new { in_prone = 5, is_downed = 6 },
                obj_planted = new Dictionary<string, object> { ["1100"] = new { objective = "Gate", timestamp_unix = start } },
                future_stat = new { value = 42 }
            };
        }
        return JsonSerializer.SerializeToElement(new
        {
            round_info = new { mapname = map, round, defenderteam = 1, winnerteam = 1, timelimit = "12:00", nextTimeLimit = "1:00",
                round_start = 1000, round_end = 61000, round_start_unix = start, round_end_unix = start + 60,
                server_ip = "wrong-legacy-copy", server_port = "bad", matchID = "old-copy" },
            metadata = new { servername = "Oksii server", server_ip = "127.0.0.1", server_port = port, matchID = sourceId, config = "test", stats_version = "2.10.0" },
            player_stats = stats,
            spectators = new[] { new { guid = OksiiGuid(9), name = "Spectator" } },
            gamelog = new object[]
            {
                new { label = "kill", group = "player", leveltime = 2000, unixtime = start * 1000 + 1000,
                    killer = OksiiGuid(0), victim = OksiiGuid(replacement), weapon = 8, killer_reinf = 12.345, victim_reinf = 2.5 },
                new { label = "obj_planted", group = "objective", leveltime = 3000, unixtime = start * 1000 + 2000,
                    player = OksiiGuid(0), objective = "Gate", pos = "1 2 3", future_context = new { value = 1 } }
            }
        });
    }

    private static async Task PostOksii(TestHost host, JsonElement payload)
    {
        var response = await host.Client.PostAsJsonAsync(OksiiSubmit, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostWithoutContentTypeAsync(HttpClient client, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)),
        };
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task OksiiStoresFullGuidsMetadataOptionalStatsAndLosslessEvents()
    {
        await using var host = await TestHost.Start();
        var payload = OksiiPayload("supply", 1, 1790000000);
        await PostOksii(host, payload);
        await PostOksii(host, payload);
        await using var db = host.CreateDb();
        var map = await db.Matches.SingleAsync();
        Assert.Equal("127.0.0.1", map.ServerIp);
        Assert.Equal("shared", map.SourceMatchId);
        var players = await db.MatchPlayers.Include(p => p.WeaponStats).OrderBy(p => p.Guid).ToListAsync();
        Assert.Equal(2, players.Count);
        Assert.Equal(OksiiGuid(0), players[0].Guid);
        Assert.Equal(OksiiGuid(1), players[1].Guid);
        Assert.Equal(100, players[0].DamageGiven);
        Assert.Equal(2, players[0].Assists);
        Assert.Equal(50.5, players[0].DistanceMeters);
        Assert.Equal(1, players[0].ObjectivesPlanted);
        Assert.Equal(new[] { 4, 27 }, players[0].WeaponStats.OrderBy(w => w.Weapon).Select(w => w.Weapon));
        Assert.Equal(2, await db.RoundEvents.CountAsync());
        var round = await db.MatchRounds.SingleAsync();
        Assert.Equal("oksii", round.StatsSource);
        using var canonical = JsonDocument.Parse(round.PayloadJson!);
        Assert.Equal("per_round", canonical.RootElement.GetProperty("metadata").GetProperty("counters").GetString());
        Assert.Equal(42, canonical.RootElement.GetProperty("player_stats").GetProperty(OksiiGuid(0)).GetProperty("future_stat").GetProperty("value").GetInt32());
        Assert.Equal(1, canonical.RootElement.GetProperty("spectators").GetArrayLength());
        Assert.Contains("future_context", (await db.RoundEvents.SingleAsync(e => e.Label == "obj_planted")).DataJson);
        Assert.Equal(1, await db.MatchSeries.CountAsync());
    }

    [Fact]
    public async Task VersionEndpointReturnsOksiiExpectedVersionAndSampleRoundsIngest()
    {
        await using var host = await TestHost.Start();

        var versionResponse = await host.Client.GetAsync("/api/v2/stats/etl/matches/stats/version");
        versionResponse.EnsureSuccessStatusCode();
        Assert.Equal("2.10.0", (await versionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetString());

        var examplesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../data/examples"));
        foreach (var fileName in new[]
        {
            "stats-1791637580-2026-10-10-130620-etl_adlernest-round-1.json",
            "stats-1791637713-2026-10-10-130833-etl_adlernest-round-2.json",
        })
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(examplesPath, fileName)));
            var response = await host.Client.PostAsJsonAsync(OksiiSubmit, document.RootElement);
            Assert.True(response.IsSuccessStatusCode, $"{fileName}: {await response.Content.ReadAsStringAsync()}");
        }

        await using var db = host.CreateDb();
        Assert.Equal(1, await db.Matches.CountAsync());
        Assert.Equal(2, await db.MatchRounds.CountAsync());
    }

    [Fact]
    public async Task IngestEndpointsAcceptJsonBodiesWithoutContentType()
    {
        await using var host = await TestHost.Start();
        var readyUp = new LastReadyUpDto("no-content-type", new string('A', 32), 100, 110, "cup-1", "supply", 1);
        var roster = new
        {
            server_ip = "127.0.0.1",
            server_port = "27960",
            timestamp = 100L,
            connected_players = Array.Empty<object>(),
            spectators = Array.Empty<object>(),
        };
        var requests = new[]
        {
            ("/api/matches", (object)Payload("no-content-type-legacy", "cup-1", "Cup", 1)),
            (OksiiSubmit, (object)OksiiPayload("supply", 1, 1790000000, "no-content-type-oksii")),
            ("/api/v2/stats/etl/matches/players/notify", roster),
            ("/api/ready-ups", readyUp),
        };

        foreach (var (path, payload) in requests)
        {
            using var response = await PostWithoutContentTypeAsync(host.Client, path, payload);
            Assert.True(response.IsSuccessStatusCode,
                $"{path}: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task OksiiRoundTwoSubtractsEqualCumulativeCountersButKeepsRoundOnlyMetrics()
    {
        await using var host = await TestHost.Start();
        await PostOksii(host, OksiiPayload("supply", 1, 1790000000, "fallback-1"));
        await PostOksii(host, OksiiPayload("supply", 2, 1790000070, "fallback-2"));
        await using var db = host.CreateDb();
        Assert.Equal(1, await db.Matches.CountAsync());
        var r2 = await db.MatchPlayers.Include(p => p.WeaponStats).Where(p => p.MatchSide.MatchRound.RoundNumber == 2).ToListAsync();
        Assert.All(r2, p =>
        {
            Assert.Equal(0, p.DamageGiven); Assert.Equal(0, p.Assists);
            Assert.Equal(0, p.WeaponStats.Sum(w => w.Kills)); Assert.Equal(0, p.WeaponStats.Sum(w => w.Hits));
            Assert.Equal(50.5, p.DistanceMeters); Assert.Equal(30, p.EngagedSeconds); Assert.Equal(1, p.ObjectivesPlanted);
        });
        // Per-round level time resets; the same leveltime in round 2 is a new event.
        Assert.Equal(4, await db.RoundEvents.CountAsync());
        Assert.Equal(2, await db.MatchObituaries.CountAsync());
    }

    [Fact]
    public async Task MatchingRostersJoinAnyNumberOfMapsAcrossFactionSwapsAndRepeatedMapNames()
    {
        await using var host = await TestHost.Start();
        for (var i = 0; i < 3; i++)
        {
            await PostOksii(host, OksiiPayload(i == 1 ? "adlernest" : "supply", 1, 1790000000 + i * 200, swapped: i == 1));
            await PostOksii(host, OksiiPayload(i == 1 ? "adlernest" : "supply", 2, 1790000070 + i * 200, swapped: i == 1, multiplier: 2));
        }
        await using var db = host.CreateDb();
        var maps = await db.Matches.OrderBy(m => m.MapNumber).ToListAsync();
        Assert.Equal(3, maps.Count);
        Assert.Equal(1, await db.MatchSeries.CountAsync());
        Assert.Equal(new[] { 1, 2, 3 }, maps.Select(m => m.MapNumber));
        Assert.Equal(new[] { 1, 2, 1 }, maps.Select(m => m.SeriesTeam1Faction));
        Assert.Equal(3, maps.Select(m => m.ExternalMatchId).Distinct().Count());
        Assert.Equal(1200, await db.MatchPlayers.SumAsync(p => p.DamageGiven));
    }

    [Fact]
    public async Task RosterChangesServersAndLongGapsStartNewMatches()
    {
        await using var host = await TestHost.Start();
        await PostOksii(host, OksiiPayload("supply", 1, 1790000000));
        await PostOksii(host, OksiiPayload("supply", 2, 1790000070, multiplier: 2));
        await PostOksii(host, OksiiPayload("adlernest", 1, 1790000200, replacement: 2));
        await PostOksii(host, OksiiPayload("adlernest", 2, 1790000270, replacement: 2, multiplier: 2));
        await PostOksii(host, OksiiPayload("supply", 1, 1790100000, replacement: 2));
        await PostOksii(host, OksiiPayload("supply", 1, 1790100000, replacement: 2, port: "27961"));
        await using var db = host.CreateDb();
        Assert.Equal(4, await db.MatchSeries.CountAsync());
        Assert.Equal(4, await db.Matches.CountAsync());
    }

    [Fact]
    public async Task OksiiInvalidAndUnauthorizedReportsNeverChangeStoredRows()
    {
        await using var host = await TestHost.Start();
        host.Client.DefaultRequestHeaders.Remove("Authorization");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(OksiiSubmit, OksiiPayload("supply", 1, 1790000000))).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Authorization", "Bearer secret");
        var bad = JsonSerializer.Serialize(OksiiPayload("supply", 1, 1790000000)).Replace("134217744", "0");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync(OksiiSubmit, new StringContent(bad, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        await using var db = host.CreateDb();
        Assert.Empty(await db.MatchRounds.ToListAsync());
    }

    [Fact]
    public async Task LegacyIngestionAndBackfillUseTheSameCanonicalSchemaWithoutLosingDemosOrClasses()
    {
        await using var host = await TestHost.Start();
        await host.Post(Payload("legacy", "server", "Legacy server", 1));
        await using var db = host.CreateDb();
        var map = await db.Matches.SingleAsync();
        map.DemoFileName = "demo.dm_84";
        var round = await db.MatchRounds.Include(r => r.Sides).ThenInclude(s => s.Players).SingleAsync();
        var player = round.Sides[0].Players[0];
        db.MatchPlayerClassStats.Add(new() { Id = Guid.NewGuid(), MatchPlayerId = player.Id, ClassId = 1, Ms = 1234 });
        round.PayloadJson = null;
        await db.SaveChangesAsync();
        await CanonicalStats.BackfillAsync(db);
        var stored = await db.MatchRounds.SingleAsync();
        using var json = JsonDocument.Parse(stored.PayloadJson!);
        Assert.Equal("legacy", json.RootElement.GetProperty("metadata").GetProperty("source").GetString());
        Assert.Equal(6, json.RootElement.GetProperty("player_stats").EnumerateObject().Count());
        Assert.Equal(1234, json.RootElement.GetProperty("player_stats").GetProperty(player.Guid).GetProperty("class_stats")[0].GetProperty("ms").GetInt64());
        Assert.Equal("demo.dm_84", (await db.Matches.SingleAsync()).DemoFileName);
    }

    [Fact]
    public async Task RosterNotificationsAreAuthenticatedAndIdempotent()
    {
        await using var host = await TestHost.Start();
        var json = new { match_id = "snapshot", server_ip = "127.0.0.1", server_port = "27960", timestamp = 1790000000,
            connected_players = new[] { new { guid = OksiiGuid(0), team = 1 } }, spectators = Array.Empty<object>() };
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK,
            (await host.Client.PostAsJsonAsync("/api/v2/stats/etl/matches/players/notify", json)).StatusCode);
        await using var db = host.CreateDb();
        Assert.Equal(1, await db.RosterSnapshots.CountAsync());
        var idResponse = await host.Client.GetFromJsonAsync<JsonElement>("/api/v2/stats/etl/matches/matchid/127.0.0.1/27960");
        Assert.False(string.IsNullOrEmpty(idResponse.GetProperty("match_id").GetString()));
    }

    [Fact]
    public async Task LateRoundOneCorrectsStoredRoundTwoAndKeepsItsIdentityOnRetries()
    {
        await using var host = await TestHost.Start(webhook: true);
        var second = OksiiPayload("supply", 2, 1790000070, multiplier: 2);
        await PostOksii(host, second);
        Guid round2Id;
        await using (var db = host.CreateDb())
        {
            round2Id = (await db.MatchRounds.SingleAsync()).Id;
            Assert.Null((await db.Matches.SingleAsync()).Winner);
        }
        Assert.Equal(0, host.WebhookHandler.Requests);
        var first = OksiiPayload("supply", 1, 1790000000);
        await PostOksii(host, first);
        await PostOksii(host, first);
        await PostOksii(host, second);
        await using var after = host.CreateDb();
        Assert.Equal(1, await after.Matches.CountAsync());
        Assert.Equal(round2Id, (await after.MatchRounds.SingleAsync(r => r.RoundNumber == 2)).Id);
        Assert.All(await after.MatchPlayers.Where(p => p.MatchSide.MatchRound.RoundNumber == 2).ToListAsync(), p => Assert.Equal(100, p.DamageGiven));
        Assert.NotNull((await after.Matches.SingleAsync()).Winner);
        Assert.Equal(1, host.WebhookHandler.Requests);
    }

    [Fact]
    public async Task LateMapsAreGroupedChronologicallyAndRosterChangesRemainBoundaries()
    {
        await using var host = await TestHost.Start();
        foreach (var index in new[] { 2, 0, 1 })
        {
            await PostOksii(host, OksiiPayload("map" + index, 1, 1790000000 + index * 200, replacement: index == 1 ? 2 : 1));
            await PostOksii(host, OksiiPayload("map" + index, 2, 1790000070 + index * 200, replacement: index == 1 ? 2 : 1, multiplier: 2));
        }
        await using var db = host.CreateDb();
        Assert.Equal(3, await db.MatchSeries.CountAsync());
        Assert.All(await db.Matches.ToListAsync(), map => Assert.Equal(1, map.MapNumber));
    }

    [Fact]
    public async Task ScoreboardsSeparateMatchesFromMapsAndIncludeNewMetrics()
    {
        await using var host = await TestHost.Start();
        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10000;
        for (var index = 0; index < 20; index++)
        {
            await PostOksii(host, OksiiPayload("map" + index, 1, start + index * 200));
            await PostOksii(host, OksiiPayload("map" + index, 2, start + 70 + index * 200, multiplier: 2));
        }
        var boards = await host.ScoreboardCache.GetAsync();
        Assert.All(boards.Single(b => b.Title == "Maps played").Rows, p => Assert.Equal(20, p.Value));
        Assert.All(boards.Single(b => b.Title == "Matches played").Rows, p => Assert.Equal(1, p.Value));
        var assists = boards.Single(b => b.Title == "Assists");
        Assert.Equal(2, assists.Rows.Count);
        Assert.All(assists.Rows, p => Assert.Equal(40, p.Value));
        Assert.All(boards.Single(b => b.Title == "Activity").Rows, p => Assert.Equal(50, p.Value));
    }

    [Fact]
    public async Task AllStatsPagesRenderTheNewSchemaAndAggregateNormalizedRounds()
    {
        await using var host = await TestHost.Start(renderPages: true);
        await PostOksii(host, OksiiPayload("supply", 1, 1790000000));
        await PostOksii(host, OksiiPayload("supply", 2, 1790000070, multiplier: 2));
        await using var db = host.CreateDb();
        var map = await db.Matches.SingleAsync();
        foreach (var path in new[] { "/", $"/matches/{map.Id}", $"/match-series/{map.SeriesId}",
            $"/player/{OksiiGuid(0)}", $"/matches/combined?id={map.Id}", "/scoreboards" })
        {
            var response = await host.Client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, path + ": " + html);
            Assert.DoesNotContain("could not be translated", html);
            Assert.DoesNotContain("An exception was thrown", html);
            if (path != "/scoreboards") Assert.Contains("Player 0", html);
            if (path != "/" && path != "/scoreboards")
                Assert.True(html.Contains("Support, objectives"), path + ": advanced stats missing");
        }
        var roundId = await db.MatchRounds.Where(r => r.RoundNumber == 1).Select(r => r.Id).SingleAsync();
        var timeline = await host.Client.GetAsync($"/test/rounds/{roundId}");
        var timelineHtml = await timeline.Content.ReadAsStringAsync();
        Assert.True(timeline.IsSuccessStatusCode, timelineHtml);
        Assert.Contains("Round timeline", timelineHtml);
        Assert.Contains("Gate", timelineHtml);
        Assert.Contains("Player 0", timelineHtml);
    }

    [Fact]
    public async Task MatchPageDefaultsToTotalsAndMapAndRoundScopesRecalculateAwards()
    {
        await using var host = await TestHost.Start(renderPages: true);
        foreach (var map in new[] { "supply", "adlernest" })
        {
            var start = map == "supply" ? 1790000000 : 1790000200;
            await PostOksii(host, OksiiPayload(map, 1, start, swapped: map == "adlernest"));
            await PostOksii(host, OksiiPayload(map, 2, start + 70, multiplier: 2, swapped: map == "adlernest"));
        }
        await using var db = host.CreateDb();
        var maps = await db.Matches.OrderBy(m => m.MapNumber).ToListAsync();
        var series = maps[0].SeriesId;
        Assert.Equal(series, maps[1].SeriesId);
        var root = $"/match-series/{series}";
        foreach (var (path, title, kills) in new[]
        {
            (root, "Match awards", "12"),
            ($"{root}?map={maps[0].Id}", "Map awards", "6"),
            ($"{root}?map={maps[0].Id}&round=1", "Round awards", "3"),
            ($"/matches/{maps[0].Id}", "Map awards", "6"),
            ($"/matches/combined?id={maps[0].Id}&id={maps[1].Id}", "Match awards", "12")
        })
        {
            var response = await host.Client.GetAsync(path);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, path + ": " + html);
            Assert.Contains(title, html);
            Assert.Contains("data-award-name=\"Top Fragger\"", html);
            Assert.Contains($"data-award-value=\"{kills}\"", html);
            Assert.Contains("Weapon awards", html);
            if (path.StartsWith(root))
            {
                Assert.Contains("Match totals", html);
                Assert.Contains("supply", html);
                Assert.Contains("adlernest", html);
            }
        }
        var invalidMap = await host.Client.GetStringAsync($"{root}?map={Guid.NewGuid()}");
        Assert.Contains("This map is not part of this match", invalidMap);
    }

    [Fact]
    public void NewMigrationGeneratesMariaDbSqlWithIndexedVarcharsAndLargePayloadColumns()
    {
        using var db = new StatsDbContext(new DbContextOptionsBuilder<StatsDbContext>()
            .UseMySql("Server=localhost;Database=unused;User=unused;Password=unused",
                new MariaDbServerVersion(new Version(11, 8, 3))).Options);
        var sql = db.GetService<IMigrator>().GenerateScript("20261007042413_AddLastReadyUps");
        Assert.Contains("varchar(512)", sql);
        Assert.Contains("varchar(128)", sql);
        Assert.Contains("longtext", sql);
        Assert.Contains("`UnixTimeMs` bigint", sql);
        Assert.Contains("CREATE TABLE `MatchSeries`", sql);
        Assert.DoesNotContain("DROP TABLE `Matches`", sql);
    }
}
