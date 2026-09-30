using PappaETStats.Server.Services;
using Xunit;

namespace PappaETStats.Server.Tests;

public sealed class SpawnWaitTimeCalculatorTests
{
    [Fact]
    public void CalculatesWaitTimeFromRoundDurationAndAlivePercentage()
    {
        var result = SpawnWaitTimeCalculator.CalculateSeconds(
            roundStartMs: 10_000,
            roundEndMs: 610_000,
            roundStartUnix: 0,
            roundEndUnix: 0,
            timePlayedPercent: 75);

        Assert.Equal(150, result);
    }

    [Fact]
    public void UsesUnixDurationWhenMillisecondTimestampsAreUnavailable()
    {
        var result = SpawnWaitTimeCalculator.CalculateSeconds(
            roundStartMs: 0,
            roundEndMs: 0,
            roundStartUnix: 1_000,
            roundEndUnix: 1_600,
            timePlayedPercent: 80);

        Assert.Equal(120, result, precision: 6);
    }

    [Theory]
    [InlineData(-10, 600)]
    [InlineData(110, 0)]
    public void ClampsInvalidPercentages(double percentage, double expectedSeconds)
    {
        var result = SpawnWaitTimeCalculator.CalculateSeconds(0, 600_000, 0, 0, percentage);

        Assert.Equal(expectedSeconds, result, precision: 6);
    }
}
