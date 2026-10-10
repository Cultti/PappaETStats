using System.Text.Json;
using PappaETStats.Server.Domain;
using PappaETStats.Server.Services;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class MatchStatisticsTests
{
    private static readonly string Alice = new('A', 32);
    private static readonly string Bob = new('B', 32);

    private static MatchStatsSample Sample(string guid, Guid map, Guid round, int team = 1, int number = 1,
        Guid? series = null, int faction = 1, long duration = 60000, string? details = null, int kills = 3)
        => new()
        {
            Player = new MatchPlayer
            {
                Guid = guid, Name = guid == Alice ? "^1Alice" : "Bob", Team = team,
                TimePlayedPercent = team == 1 ? 80 : 40, Xp = 5, DamageGiven = 100, DamageReceived = 90,
                DetailsJson = details
            },
            MapId = map, RoundId = round, RoundNumber = number, SeriesId = series,
            SeriesTeam1Faction = faction, DefenderTeam = 1, StartUnix = number * 1000,
            StartMs = 1000, EndMs = 1000 + duration,
            Weapons = [new() { Weapon = 4, Kills = kills, Deaths = 2, Headshots = 1, Hits = 10, Atts = 20 },
                new() { Weapon = 27, Hits = 2, Kills = 10, Deaths = 10, Headshots = 10 }],
            Classes = [new() { ClassId = 1, Ms = duration / 2 }]
        };

    private static RoundEvent Event(Guid round, int order, string label, long time, object data)
        => new() { MatchRoundId = round, Sequence = order, Label = label, Group = "player", LevelTime = time,
            DataJson = JsonSerializer.Serialize(data) };

    private static MatchAward Award(MatchStatsResult result, string name) => Assert.Single(result.Awards, a => a.Name == name && !a.IsWeapon);

    [Fact]
    public void NormalizedRoundsAreSummedAndSeriesTeamsSurviveFactionSwaps()
    {
        var firstMap = Guid.NewGuid(); var secondMap = Guid.NewGuid(); var series = Guid.NewGuid();
        var rows = new[]
        {
            Sample(Alice, firstMap, Guid.NewGuid(), series: series),
            Sample(Alice, firstMap, Guid.NewGuid(), team: 2, number: 2, series: series),
            Sample(Alice, secondMap, Guid.NewGuid(), team: 2, series: series, faction: 2, duration: 120000),
            Sample(Bob, secondMap, Guid.NewGuid(), team: 1, series: series, faction: 2)
        };
        var result = MatchStatistics.Calculate(rows, []);
        var alice = Assert.Single(result.Players, p => p.Grid.Guid == Alice);
        var bob = Assert.Single(result.Players, p => p.Grid.Guid == Bob);
        Assert.Equal(1, alice.Team); Assert.Equal(2, bob.Team);
        Assert.Equal(2, alice.Grid.Games);
        Assert.Equal(300, alice.Grid.DamageGiven);
        Assert.Equal(50, alice.Grid.TimePlayedPercent); // Duration-weighted, not an average of round averages.
        Assert.Equal("2:00", Assert.Single(alice.Grid.ClassStats).TimeText);
        Assert.Equal(9, Award(result, "Top Fragger").Rankings[0].Value);
        Assert.Equal(3, Award(result, "Headhunter").Rankings[0].Value);
        Assert.Equal(6, Award(result, "Needler").Rankings[0].Value);
        Assert.Equal(1.5, Award(result, "Best KDR").Rankings[0].Value);
        Assert.DoesNotContain(result.Awards, a => a.Name == "Weapon 27");
    }

    [Fact]
    public void AwardsRecalculateForScopeShareTiesAndOmitUnavailableTelemetry()
    {
        var first = Sample(Alice, Guid.NewGuid(), Guid.NewGuid());
        var second = Sample(Bob, Guid.NewGuid(), Guid.NewGuid());
        var combined = MatchStatistics.Calculate([first, second], []);
        Assert.Equal(2, Award(combined, "Top Fragger").Winners.Count());
        var map = MatchStatistics.Calculate([second], []);
        Assert.Equal(Bob, Assert.Single(Award(map, "Top Fragger").Winners).Guid);
        foreach (var title in new[] { "Speed Demon", "Coma", "Pillow Fort", "Rocket Surgeon", "Queue Manager", "Permanent Limbo Resident" })
            Assert.DoesNotContain(combined.Awards, a => a.Name == title);
        Assert.Null(combined.Players[0].Grid.Assists);
    }

    [Fact]
    public void StreaksResetAtDeathsAndRoundBoundariesAndMultikillsExcludePausedTime()
    {
        var round = Guid.NewGuid(); var secondRound = Guid.NewGuid(); var map = Guid.NewGuid();
        var rows = new[] { Sample(Alice, map, round), Sample(Bob, map, round, team: 2),
            Sample(Alice, map, secondRound, number: 2), Sample(Bob, map, secondRound, team: 2, number: 2) };
        var events = new[]
        {
            Event(round, 0, "kill", 1000, new { killer = Alice, victim = Bob, victim_reinf = 18 }),
            Event(round, 1, "pause", 1500, new { }),
            Event(round, 2, "unpause", 101500, new { }),
            Event(round, 3, "kill", 102000, new { killer = Alice, victim = Bob, victim_reinf = 18 }),
            Event(round, 4, "kill", 103000, new { killer = Alice, victim = Bob, victim_reinf = 18 }),
            Event(round, 5, "suicide", 104000, new { player = Alice }),
            Event(round, 6, "kill", 105000, new { killer = Alice, victim = Bob, victim_reinf = 18 }),
            Event(secondRound, 0, "kill", 1000, new { killer = Alice, victim = Bob, victim_reinf = 18 })
        };
        var result = MatchStatistics.Calculate(rows, events);
        Assert.Equal(3, Award(result, "Killing Spree").Rankings[0].Value);
        Assert.Equal(3, Award(result, "Rampage").Rankings[0].Value);
        Assert.Equal(4, Award(result, "Are these pliers sharp?").Rankings[0].Value);
        Assert.Equal(5, Award(result, "Queue Manager").Rankings[0].Value);
    }

    [Fact]
    public void SupportPackAndRocketAwardsUseWeaponFireIdsAndCountEachShotOnce()
    {
        var round = Guid.NewGuid(); var rows = new[] { Sample(Alice, Guid.NewGuid(), round) };
        var events = new[]
        {
            Event(round, 0, "weapon_fire", 1000, new { player = Alice, weapon = 19 }),
            Event(round, 1, "weapon_fire", 1100, new { player = Alice, weapon = 12 }),
            Event(round, 2, "weapon_fire", 2000, new { player = Alice, weapon = 5 }),
            Event(round, 3, "damage", 3000, new { attacker = Alice, weapon = 15, damage = 100 }),
            Event(round, 4, "damage", 3001, new { attacker = Alice, weapon = 15, damage = 100 }),
            Event(round, 5, "weapon_fire", 14000, new { player = Alice, weapon = 53 })
        };
        var result = MatchStatistics.Calculate(rows, events);
        Assert.Equal(1, Award(result, "Pillow Fort").Rankings[0].Value);
        Assert.Equal(1, Award(result, "Walking Ammo Cabinet").Rankings[0].Value);
        Assert.Equal("1 of 2", Award(result, "Rocket Surgeon").Rankings[0].DisplayValue);
    }

    [Fact]
    public void ObjectivesVehiclesPeaksAndSpawnAveragesUseTheirOwnAggregationRules()
    {
        var map = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var rows = new[]
        {
            Sample(Alice, map, first, details: """
                {"spawn_count":2,"distance_travelled_spawn":40,"player_speed":{"kph_peak":60},
                 "stance_stats_seconds":{"in_objcarrier":10},"obj_planted":{"100":{}},"obj_carrierkilled":{"200":{}},
                 "obj_vehicle":{"escort":{"tank":{"time_s":12}},"damage":{"damage":80},"repairs":1}}
                """),
            Sample(Alice, map, second, number: 2, details: """
                {"spawn_count":8,"distance_travelled_spawn_avg":10,"player_speed":{"kph_peak":45},
                 "stance_stats_seconds":{"in_objcarrier":20},"obj_secured":{"100":{}},
                 "obj_vehicle":{"escort":{"tank":{"time_s":8}},"damage":{"damage":20},"repairs":2}}
                """)
        };
        var result = MatchStatistics.Calculate(rows, []);
        Assert.Equal(60, Award(result, "Speed Demon").Rankings[0].Value);
        Assert.Equal(12, Award(result, "Coma").Rankings[0].Value); // (40 + 8 * 10) / (2 + 8).
        Assert.Equal(30, Award(result, "ObjWhore").Rankings[0].Value);
        Assert.Equal(2, Award(result, "Objective Hero").Rankings[0].Value);
        Assert.Equal(1, Award(result, "Kill the Messenger").Rankings[0].Value);
        Assert.Equal(20, Award(result, "Payload Princess").Rankings[0].Value);
        Assert.Equal(100, Award(result, "Panzerschreck").Rankings[0].Value);
        Assert.Equal(3, Award(result, "Duct Tape & Prayers").Rankings[0].Value);
    }

    [Fact]
    public void FullSpawnAwardsRequireEvidenceOfTheActualReinforcementWave()
    {
        var round = Guid.NewGuid(); var map = Guid.NewGuid();
        var rows = new[] { Sample(Alice, map, round), Sample(Bob, map, round, team: 2) };
        var events = new[]
        {
            Event(round, 0, "spawn", 1000, new { player = Alice, team = 1 }),
            Event(round, 1, "spawn", 21000, new { player = Alice, team = 1 }),
            Event(round, 2, "spawn", 41000, new { player = Alice, team = 1 }),
            Event(round, 3, "kill", 42000, new { killer = Bob, victim = Alice, victim_reinf = 19 }),
            Event(round, 4, "suicide", 43000, new { player = Alice, victim_reinf = 18 }),
            Event(round, 5, "kill", 55000, new { killer = Bob, victim = Alice, victim_reinf = 5 })
        };
        var result = MatchStatistics.Calculate(rows, events);
        Assert.Equal(1, Award(result, "Permanent Limbo Resident").Rankings[0].Value);
        Assert.Equal(1, Award(result, "Fentanyl").Rankings[0].Value);
        var incomplete = MatchStatistics.Calculate(rows, events.Skip(2).ToList());
        Assert.DoesNotContain(incomplete.Awards, a => a.Name is "Permanent Limbo Resident" or "Fentanyl");
    }
}
