using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;
using PappaETStats.SkillRating;

namespace PappaETStats.Server.Api;

public static partial class TeamBalanceEndpoints
{
    private sealed record BalanceGroupsRequest(IReadOnlyList<string>? Guids, int TeamSize, double? SigmaMultiplier);

    private static async Task<IResult> BalanceGroupsAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        IOptions<SkillRatingOptions> skillRatingOptions,
        BalanceGroupsRequest body,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null) return authResult;

        if (body.TeamSize is < 3 or > 6)
            return Results.BadRequest(new { error = "teamSize must be between 3 and 6" });

        var multiplier = body.SigmaMultiplier ?? 2.0;
        if (!double.IsFinite(multiplier) || multiplier <= 0)
            return Results.BadRequest(new { error = "sigmaMultiplier must be a positive number" });

        if (body.Guids is null || body.Guids.Count < body.TeamSize || body.Guids.Count % body.TeamSize != 0)
            return Results.BadRequest(new { error = "guids must contain a positive multiple of teamSize players" });

        var guids = new List<string>(body.Guids.Count);
        foreach (var value in body.Guids)
        {
            if (!Guid.TryParseExact(value?.Trim(), "N", out var guid))
                return Results.BadRequest(new { error = "invalid guid format (expected 32 hex chars)", guid = value });
            guids.Add(guid.ToString("N").ToUpperInvariant());
        }
        if (guids.Distinct(StringComparer.Ordinal).Count() != guids.Count)
            return Results.BadRequest(new { error = "guids contains duplicates" });

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Players.AsNoTracking()
            .Where(p => guids.Contains(p.Guid.ToUpper()))
            .ToDictionaryAsync(p => p.Guid, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var defaults = skillRatingOptions.Value;
        var players = guids.Select(guid =>
        {
            var mu = existing.TryGetValue(guid, out var player) ? player.Mu : defaults.Mu;
            var sigma = player?.Sigma ?? defaults.Sigma;
            return new BalancedPlayer(guid, mu, sigma, mu - multiplier * sigma);
        }).OrderByDescending(p => p.Mu).ThenBy(p => p.Guid, StringComparer.Ordinal).ToArray();

        var teams = DivideGroups(players, body.TeamSize, cancellationToken);
        return Results.Ok(new
        {
            teamSize = body.TeamSize,
            sigmaMultiplier = multiplier,
            teams = teams.Select(team => new BalancedTeam(
                team.OrderByDescending(p => p.Conservative).ToArray(),
                team.Sum(p => p.Mu), team.Sum(p => p.Conservative))).ToArray(),
        });
    }

    // Minimize variance in total overall Mu: equal totals give equal pairwise win
    // probabilities. Multiple starts escape some local minima without an exponential
    // search. The result is a best-effort partition, not a guaranteed global optimum.
    private static List<BalancedPlayer>[] DivideGroups(
        BalancedPlayer[] players, int teamSize, CancellationToken cancellationToken)
    {
        var teamCount = players.Length / teamSize;
        var random = new Random(0);
        List<BalancedPlayer>[]? best = null;
        var bestScore = double.PositiveInfinity;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = players.ToArray();
            if (attempt > 0) random.Shuffle(order);
            var teams = Enumerable.Range(0, teamCount).Select(_ => new List<BalancedPlayer>(teamSize)).ToArray();
            var sums = new double[teamCount];
            foreach (var player in order)
            {
                var target = Enumerable.Range(0, teamCount).Where(t => teams[t].Count < teamSize)
                    .OrderBy(t => sums[t]).ThenBy(t => teams[t].Count).First();
                teams[target].Add(player);
                sums[target] += player.Mu;
            }

            // Every accepted swap strictly reduces the objective and keeps sizes fixed.
            for (var pass = 0; pass < 1000; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var improvement = 1e-10;
                var swap = (a: -1, b: -1, i: -1, j: -1);
                for (var a = 0; a < teamCount; a++)
                for (var b = a + 1; b < teamCount; b++)
                for (var i = 0; i < teamSize; i++)
                for (var j = 0; j < teamSize; j++)
                {
                    var delta = teams[b][j].Mu - teams[a][i].Mu;
                    var gain = -2 * delta * (sums[a] - sums[b] + delta);
                    if (gain > improvement)
                    {
                        improvement = gain;
                        swap = (a, b, i, j);
                    }
                }
                if (swap.a < 0) break;
                var (ta, tb, pi, pj) = swap;
                var change = teams[tb][pj].Mu - teams[ta][pi].Mu;
                (teams[ta][pi], teams[tb][pj]) = (teams[tb][pj], teams[ta][pi]);
                sums[ta] += change;
                sums[tb] -= change;
            }
            var average = sums.Average();
            var score = sums.Sum(sum => (sum - average) * (sum - average));
            if (score < bestScore)
            {
                best = teams;
                bestScore = score;
            }
            if (bestScore < 1e-10) break;
        }
        return best!;
    }
}
