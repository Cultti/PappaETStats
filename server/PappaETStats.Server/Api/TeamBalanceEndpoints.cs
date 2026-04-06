using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.SkillRating;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;

namespace PappaETStats.Server.Api;

public static class TeamBalanceEndpoints
{
    public static IEndpointRouteBuilder MapTeamBalanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api")
            .WithTags("SkillRating");

        group.MapPost("/skillratings/balance-teams", BalanceTeamsAsync)
            .WithName("BalanceTeams")
            .Accepts<BalanceTeamsRequest>("application/json")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    private sealed record BalanceTeamsRequest(IReadOnlyList<string>? Guids, double? SigmaMultiplier);

    private sealed record BalancedPlayer(string Guid, double Mu, double Sigma, double Conservative);

    private sealed record BalancedTeam(IReadOnlyList<BalancedPlayer> Players, double SumMu, double SumConservative);

    private static IResult? ValidateBearerToken(HttpRequest request, IOptions<IngestOptions> ingestOptions)
    {
        var configuredToken = ingestOptions.Value.Token;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return null;
        }

        var authHeader = request.Headers.Authorization.ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        return string.Equals(token, configuredToken, StringComparison.Ordinal)
            ? null
            : Results.Unauthorized();
    }

    private sealed record Candidate(string GuidString, Guid PlayerId, double Mu, double Sigma, double Conservative);

    private static async Task<IResult> BalanceTeamsAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        BalanceTeamsRequest body,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        var sigmaMultiplier = body.SigmaMultiplier ?? 2.0;
        if (sigmaMultiplier <= 0.0 || double.IsNaN(sigmaMultiplier) || double.IsInfinity(sigmaMultiplier))
        {
            return Results.BadRequest(new { error = "sigmaMultiplier must be a positive number" });
        }

        var guids = body.Guids ?? Array.Empty<string>();
        var normalized = guids
            .Select(g => (g ?? string.Empty).Trim())
            .Where(g => g.Length > 0)
            .ToList();

        if (normalized.Count < 2)
        {
            return Results.BadRequest(new { error = "guids must contain at least 2 GUIDs" });
        }

        var distinct = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count != normalized.Count)
        {
            return Results.BadRequest(new { error = "guids contains duplicates" });
        }

        // Validate format: we use the same canonical GUID storage as ingest (32 hex, no dashes).
        var guidPairs = new List<(string guidString, Guid playerId)>(distinct.Count);
        foreach (var guidString in distinct)
        {
            if (!Guid.TryParseExact(guidString, "N", out var playerId))
            {
                return Results.BadRequest(new { error = "invalid guid format (expected 32 hex chars)", guid = guidString });
            }

            guidPairs.Add((guidString, playerId));
        }

        var options = new SkillRatingOptions();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Players
            .AsNoTracking()
            .Where(p => distinct.Contains(p.Guid))
            .ToDictionaryAsync(p => p.Guid, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var candidates = new List<Candidate>(guidPairs.Count);
        foreach (var (guidString, playerId) in guidPairs)
        {
            if (existing.TryGetValue(guidString, out var row))
            {
                candidates.Add(new Candidate(
                    GuidString: guidString,
                    PlayerId: playerId,
                    Mu: row.Mu,
                    Sigma: row.Sigma,
                    Conservative: row.Mu - sigmaMultiplier * row.Sigma));
            }
            else
            {
                candidates.Add(new Candidate(
                    GuidString: guidString,
                    PlayerId: playerId,
                    Mu: options.Mu,
                    Sigma: options.Sigma,
                    Conservative: options.Mu - sigmaMultiplier * options.Sigma));
            }
        }

        var calculator = new SkillRatingCalculator(options);
        var n = candidates.Count;
        var floorSize = n / 2;
        var ceilSize = n - floorSize;

        var bestMask = 0UL;
        var bestObjective = double.MaxValue;
        var bestConservativeDiff = double.MaxValue;

        foreach (var team1Size in (floorSize == ceilSize ? new[] { floorSize } : new[] { floorSize, ceilSize }))
        {
            var (mask, objective, conservativeDiff) = FindBestSplitMask(candidates, calculator, team1Size);
            if (objective < bestObjective || (Math.Abs(objective - bestObjective) < 1e-12 && conservativeDiff < bestConservativeDiff))
            {
                bestObjective = objective;
                bestConservativeDiff = conservativeDiff;
                bestMask = mask;
            }
        }

        var team1 = new List<Candidate>(candidates.Count / 2);
        var team2 = new List<Candidate>(candidates.Count / 2);

        for (var i = 0; i < candidates.Count; i++)
        {
            if (((bestMask >> i) & 1UL) == 1UL)
            {
                team1.Add(candidates[i]);
            }
            else
            {
                team2.Add(candidates[i]);
            }
        }

        var states = new List<MatchPlayerState>(candidates.Count);
        foreach (var c in team1)
        {
            states.Add(new MatchPlayerState(
                PlayerId: c.PlayerId,
                Rating: new PappaETStats.SkillRating.SkillRating(c.Mu, c.Sigma),
                Team: Team.Axis,
                DamageDealt: 0));
        }
        foreach (var c in team2)
        {
            states.Add(new MatchPlayerState(
                PlayerId: c.PlayerId,
                Rating: new PappaETStats.SkillRating.SkillRating(c.Mu, c.Sigma),
                Team: Team.Allies,
                DamageDealt: 0));
        }

        // Use damageFloor=1 to get equal weights (since we don't have per-match damage here).
        var pTeam1 = calculator.CalculateWinProbability(states, Team.Axis, damageFloor: 1);

        BalancedTeam ToTeam(IEnumerable<Candidate> team) => new(
            Players: team
                .OrderByDescending(p => p.Conservative)
                .Select(p => new BalancedPlayer(p.GuidString, p.Mu, p.Sigma, p.Conservative))
                .ToList(),
            SumMu: team.Sum(p => p.Mu),
            SumConservative: team.Sum(p => p.Conservative));

        return Results.Ok(new
        {
            sigmaMultiplier,
            team1 = ToTeam(team1),
            team2 = ToTeam(team2),
            winProbabilityTeam1 = pTeam1,
            winProbabilityTeam2 = 1.0 - pTeam1,
        });
    }

    private static (ulong mask, double objective, double conservativeDiff) FindBestSplitMask(
        IReadOnlyList<Candidate> candidates,
        SkillRatingCalculator calculator,
        int team1Size)
    {
        // Exact search for small N, greedy otherwise.
        // ET servers rarely exceed 20 active players per match; keep endpoint responsive.
        const int maxExactN = 20;
        var n = candidates.Count;
        if (team1Size < 1 || team1Size >= n)
        {
            // Force at least 1 player in each team.
            team1Size = Math.Clamp(team1Size, 1, n - 1);
        }

        var k = team1Size;

        if (n > maxExactN)
        {
            var sorted = candidates
                .Select((c, idx) => (c, idx))
                .OrderByDescending(x => x.c.Conservative)
                .ToList();

            var mask = 0UL;
            var count1 = 0;
            var count2 = 0;
            var sum1 = 0.0;
            var sum2 = 0.0;

            foreach (var (c, idx) in sorted)
            {
                var canPut1 = count1 < k;
                var canPut2 = count2 < k;

                var put1 = canPut1 && (!canPut2 || sum1 <= sum2);
                if (put1)
                {
                    mask |= 1UL << idx;
                    count1++;
                    sum1 += c.Conservative;
                }
                else
                {
                    count2++;
                    sum2 += c.Conservative;
                }
            }

            var states = new List<MatchPlayerState>(n);
            for (var i = 0; i < n; i++)
            {
                var inTeam1 = ((mask >> i) & 1UL) == 1UL;
                var c = candidates[i];
                states.Add(new MatchPlayerState(
                    c.PlayerId,
                    new PappaETStats.SkillRating.SkillRating(c.Mu, c.Sigma),
                    inTeam1 ? Team.Axis : Team.Allies,
                    0));
            }

            var p = calculator.CalculateWinProbability(states, Team.Axis, damageFloor: 1);
            var objective = Math.Abs(p - 0.5);
            var diff = Math.Abs(sum1 - sum2);
            return (mask, objective, diff);
        }

        // Brute force combinations (fix player 0 in team1 to avoid mirrored duplicates).
        var bestMask = 1UL;
        var bestObjective = double.MaxValue;
        var bestDiff = double.MaxValue;

        void Evaluate(ulong mask)
        {
            var states = new List<MatchPlayerState>(n);
            var sum1 = 0.0;
            var sum2 = 0.0;

            for (var i = 0; i < n; i++)
            {
                var inTeam1 = ((mask >> i) & 1UL) == 1UL;
                var c = candidates[i];
                if (inTeam1)
                {
                    sum1 += c.Conservative;
                    states.Add(new MatchPlayerState(c.PlayerId, new PappaETStats.SkillRating.SkillRating(c.Mu, c.Sigma), Team.Axis, 0));
                }
                else
                {
                    sum2 += c.Conservative;
                    states.Add(new MatchPlayerState(c.PlayerId, new PappaETStats.SkillRating.SkillRating(c.Mu, c.Sigma), Team.Allies, 0));
                }
            }

            var p = calculator.CalculateWinProbability(states, Team.Axis, damageFloor: 1);
            var objective = Math.Abs(p - 0.5);
            var diff = Math.Abs(sum1 - sum2);

            if (objective < bestObjective || (Math.Abs(objective - bestObjective) < 1e-12 && diff < bestDiff))
            {
                bestObjective = objective;
                bestDiff = diff;
                bestMask = mask;
            }
        }

        void Recurse(int idx, int countInTeam1, ulong mask)
        {
            if (countInTeam1 == k)
            {
                Evaluate(mask);
                return;
            }

            if (idx >= n)
            {
                return;
            }

            // Prune: if not enough remaining players to fill team1.
            var remaining = n - idx;
            if (countInTeam1 + remaining < k)
            {
                return;
            }

            // Option 1: put idx in team1
            Recurse(idx + 1, countInTeam1 + 1, mask | (1UL << idx));

            // Option 2: put idx in team2 (skip)
            Recurse(idx + 1, countInTeam1, mask);
        }

        // Fix index 0 in team1
        Recurse(idx: 1, countInTeam1: 1, mask: 1UL);
        return (bestMask, bestObjective, bestDiff);
    }
}
