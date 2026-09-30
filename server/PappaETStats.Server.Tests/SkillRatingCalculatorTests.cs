using PappaETStats.SkillRating;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class SkillRatingCalculatorTests
{
    [Fact]
    public void FullMatchPlayersReceiveTheFullRatingChange()
    {
        var initial = new PappaETStats.SkillRating.SkillRating(25.0, 12.5);
        var winners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var losers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var players = winners.Select(id => new MatchPlayerState(id, initial, Team.Axis, 0))
            .Concat(losers.Select(id => new MatchPlayerState(id, initial, Team.Allies, 0)))
            .ToArray();

        var updated = new SkillRatingCalculator().UpdateRatings(players, Team.Axis);

        // ET: Legacy's full-participation formula gives each player the complete
        // team result update; splitting it between teammates would halve this delta.
        const double expectedDelta = 4.4607563214109662;
        foreach (var id in winners)
        {
            Assert.InRange(updated[id].Mu, initial.Mu + expectedDelta - 1e-5, initial.Mu + expectedDelta + 1e-5);
        }
        foreach (var id in losers)
        {
            Assert.InRange(updated[id].Mu, initial.Mu - expectedDelta - 1e-5, initial.Mu - expectedDelta + 1e-5);
        }
    }

    [Fact]
    public void DamageDoesNotAffectRatingsByDefault()
    {
        var playerIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var ratings = new[]
        {
            new PappaETStats.SkillRating.SkillRating(30.0, 7.0),
            new PappaETStats.SkillRating.SkillRating(20.0, 9.0),
            new PappaETStats.SkillRating.SkillRating(27.0, 8.0),
            new PappaETStats.SkillRating.SkillRating(23.0, 10.0),
        };
        var teams = new[] { Team.Axis, Team.Axis, Team.Allies, Team.Allies };
        var damage = new[] { 5000, 100, 4500, 200 };
        var playersWithDamage = playerIds
            .Select((id, i) => new MatchPlayerState(id, ratings[i], teams[i], damage[i]))
            .ToArray();
        var playersWithoutDamage = playerIds
            .Select((id, i) => new MatchPlayerState(id, ratings[i], teams[i], DamageDealt: 0))
            .ToArray();
        var calculator = new SkillRatingCalculator();

        var withDamage = calculator.UpdateRatings(playersWithDamage, Team.Axis);
        var withoutDamage = calculator.UpdateRatings(playersWithoutDamage, Team.Axis);

        foreach (var playerId in playerIds)
        {
            Assert.Equal(withoutDamage[playerId], withDamage[playerId]);
        }
    }

    [Fact]
    public void DamageCanBeEnabledExplicitly()
    {
        var highDamageWinner = Guid.NewGuid();
        var lowDamageWinner = Guid.NewGuid();
        var initial = new PappaETStats.SkillRating.SkillRating(25.0, 25.0 / 2.0);
        var players = new[]
        {
            new MatchPlayerState(highDamageWinner, initial, Team.Axis, DamageDealt: 5000),
            new MatchPlayerState(lowDamageWinner, initial, Team.Axis, DamageDealt: 100),
            new MatchPlayerState(Guid.NewGuid(), initial, Team.Allies, DamageDealt: 2500),
            new MatchPlayerState(Guid.NewGuid(), initial, Team.Allies, DamageDealt: 2500),
        };
        var options = new SkillRatingOptions { UseDamageContribution = true };

        var updated = new SkillRatingCalculator(options).UpdateRatings(players, Team.Axis);

        Assert.True(updated[highDamageWinner].Mu > updated[lowDamageWinner].Mu);
    }
}
