using System.Collections.Immutable;

namespace PappaETStats.SkillRating;

public sealed class SkillRatingCalculator
{
    private const double Sqrt2 = 1.4142135623730950488016887242096980785696718753769;
    private const double TwoPi = 6.2831853071795864769252867665590057683943387987502;

    private readonly SkillRatingOptions _options;

    public SkillRatingCalculator(SkillRatingOptions? options = null)
    {
        _options = options ?? new SkillRatingOptions();

        // If caller overrides Mu/Sigma but not derived params, keep defaults intact.
        // We intentionally do NOT auto-derive Beta/Tau from Sigma here to preserve explicit config.
    }

    /// <summary>
    /// Calculates updated ratings for players based on a completed match.
    /// Port of ET: Legacy's G_UpdateSkillRating() core math (DB/persistence excluded).
    /// </summary>
    /// <param name="players">Per-player team + damage dealt plus pre-match rating.</param>
    /// <param name="winner">Winning team.</param>
    /// <param name="damageFloor">
    /// Added to each player's damage when computing contribution weights. Use 0 for pure proportional weighting.
    /// A small value (e.g. 1) prevents players with 0 damage from getting a strict 0 weight.
    /// </param>
    public ImmutableDictionary<Guid, SkillRating> UpdateRatings(
        IReadOnlyList<MatchPlayerState> players,
        Team winner,
        int damageFloor = 0)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));

        if (damageFloor < 0) throw new ArgumentOutOfRangeException(nameof(damageFloor));
        if (players.Count == 0) return ImmutableDictionary<Guid, SkillRating>.Empty;

        var (teamMuX, teamSigmaSqX, numPlayersX, teamMuL, teamSigmaSqL, numPlayersL, weights) =
            AggregateTeamsDamageWeighted(players, damageFloor);

        var c = CalculateC(teamSigmaSqX, teamSigmaSqL, numPlayersX, numPlayersL);
        if (c <= 0) return ImmutableDictionary<Guid, SkillRating>.Empty;

        var winningMu = winner == Team.Axis ? teamMuX : teamMuL;
        var losingMu = winner == Team.Axis ? teamMuL : teamMuX;

        var t = (winningMu - losingMu) / c;

        var v = V(t, _options.Epsilon / c);
        var w = W(t, _options.Epsilon / c);

        var builder = ImmutableDictionary.CreateBuilder<Guid, SkillRating>();

        foreach (var p in players)
        {
            var playerTeam = p.Team;
            var isWinner = playerTeam == winner;
            var rankFactor = isWinner ? 1.0 : -1.0;

            var sigmaSqPlusTauSq = (p.Rating.Sigma * p.Rating.Sigma) + (_options.Tau * _options.Tau);
            var muFactor = sigmaSqPlusTauSq / c;
            var sigmaFactor = sigmaSqPlusTauSq / (c * c);

            if (!weights.TryGetValue(p.PlayerId, out var contributionWeight))
                contributionWeight = 0.0;

            // Winner: higher contribution => bigger boost.
            // Loser: higher contribution => smaller decrease.
            var contributionFactor = isWinner ? contributionWeight : (1.0 - contributionWeight);
            if (contributionFactor <= 0.0)
                continue;

            var newMu = p.Rating.Mu + rankFactor * muFactor * v * contributionFactor;

            // Guard numerical drift: (1 - sigmaFactor*w) should be in [0,1].
            var varianceMultiplier = 1.0 - sigmaFactor * w;
            if (varianceMultiplier < 0.0) varianceMultiplier = 0.0;
            if (varianceMultiplier > 1.0) varianceMultiplier = 1.0;

            var newSigma = Math.Sqrt(sigmaSqPlusTauSq * varianceMultiplier);
            if (double.IsNaN(newSigma) || double.IsInfinity(newSigma))
                newSigma = p.Rating.Sigma;

            builder[p.PlayerId] = new SkillRating(newMu, newSigma);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Calculates a win probability (Axis win probability if team == Axis, else Allies win probability),
    /// using the same aggregation and normal CDF approach as ET: Legacy.
    /// </summary>
    public double CalculateWinProbability(
        IReadOnlyList<MatchPlayerState> players,
        Team team,
        int damageFloor = 0)
    {
        if (players is null) throw new ArgumentNullException(nameof(players));

        if (damageFloor < 0) throw new ArgumentOutOfRangeException(nameof(damageFloor));
        if (players.Count == 0) return 0.5;

        var (teamMuX, teamSigmaSqX, numPlayersX, teamMuL, teamSigmaSqL, numPlayersL, _) =
            AggregateTeamsDamageWeighted(players, damageFloor);

        var c = CalculateC(teamSigmaSqX, teamSigmaSqL, numPlayersX, numPlayersL);
        if (c <= 0) return 0.5;

        var winningMu = team == Team.Axis ? teamMuX : teamMuL;
        var losingMu = team == Team.Axis ? teamMuL : teamMuX;

        var t = (winningMu - losingMu - _options.Epsilon) / c;
        return Cdf(t);
    }

    private (double teamMuX, double teamSigmaSqX, int numPlayersX, double teamMuL, double teamSigmaSqL, int numPlayersL, Dictionary<Guid, double> weights)
        AggregateTeamsDamageWeighted(IReadOnlyList<MatchPlayerState> players, int damageFloor)
    {
        double teamMuX = 0.0;
        double teamMuL = 0.0;
        double teamSigmaSqX = 0.0;
        double teamSigmaSqL = 0.0;
        int numPlayersX = 0;
        int numPlayersL = 0;

        // Stopwatch-friendly: compute contribution weights against total damage dealt in the match,
        // not separately per team (players may have played both sides).
        long totalDamageAll = 0;
        foreach (var p in players)
        {
            totalDamageAll += Math.Max(0, p.DamageDealt) + damageFloor;
        }

        var weights = new Dictionary<Guid, double>(capacity: players.Count);
        var equalWeight = players.Count > 0 ? 1.0 / players.Count : 0.0;

        foreach (var p in players)
        {
            double w;
            if (totalDamageAll > 0)
                w = (Math.Max(0, p.DamageDealt) + damageFloor) / (double)totalDamageAll;
            else
                w = equalWeight;

            if (w < 0.0) w = 0.0;
            weights[p.PlayerId] = w;

            // Team additive terms.
            if (p.Team == Team.Axis)
            {
                teamMuX += p.Rating.Mu;
                teamSigmaSqX += p.Rating.Sigma * p.Rating.Sigma;
                numPlayersX++;
            }
            else if (p.Team == Team.Allies)
            {
                teamMuL += p.Rating.Mu;
                teamSigmaSqL += p.Rating.Sigma * p.Rating.Sigma;
                numPlayersL++;
            }
        }

        return (teamMuX, teamSigmaSqX, numPlayersX, teamMuL, teamSigmaSqL, numPlayersL, weights);
    }

    private double CalculateC(double teamSigmaSqX, double teamSigmaSqL, int numPlayersX, int numPlayersL)
    {
        var baseTerm = teamSigmaSqX + teamSigmaSqL + (numPlayersX + numPlayersL) * (_options.Beta * _options.Beta);
        return Math.Sqrt(Math.Max(0.0, baseTerm));
    }

    // ---- Math port (pdf/cdf/V/W) ----

    private static double Pdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(TwoPi);

    private static double Cdf(double x) => 0.5 * (1.0 + Erf(x / Sqrt2));

    // Error function approximation (Abramowitz & Stegun 7.1.26).
    // Max error ~1.5e-7; sufficient for rating updates.
    private static double Erf(double x)
    {
        // Save the sign of x
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t) * Math.Exp(-x * x);

        return sign * y;
    }

    private static double V(double t, double epsilon)
    {
        var denom = Cdf(t - epsilon);
        if (denom <= 1e-300)
        {
            // Extreme tail: fall back to a finite value.
            // This mirrors typical TrueSkill-style stabilizations.
            return -Math.Min(0.0, t - epsilon);
        }

        return Pdf(t - epsilon) / denom;
    }

    private static double W(double t, double epsilon)
    {
        var v = V(t, epsilon);
        return v * (v + t - epsilon);
    }
}
