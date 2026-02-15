namespace PappaETStats.SkillRating;

/// <summary>
/// Tuning constants ported from ET: Legacy g_skillrating.c defaults.
/// </summary>
public sealed record SkillRatingOptions
{
    public double Mu { get; init; } = 25.0;

    /// <summary>Default sigma is Mu / 3.</summary>
    public double Sigma { get; init; } = 25.0 / 3.0;

    /// <summary>Skill chain length (default Sigma / 2).</summary>
    public double Beta { get; init; } = (25.0 / 3.0) / 2.0;

    /// <summary>Dynamics factor (default Sigma / 100).</summary>
    public double Tau { get; init; } = (25.0 / 3.0) / 100.0;

    /// <summary>Draw margin (ET: Legacy assumes 0).</summary>
    public double Epsilon { get; init; } = 0.0;

    /// <summary>
    /// Controls how strongly damage contribution affects rating deltas.
    /// 1.0 = linear weighting, &gt;1.0 emphasizes top contributors more (and shields them more on losses).
    /// </summary>
    public double ContributionExponent { get; init; } = 1.0;

    public SkillRating DefaultRating => new(Mu, Sigma);
}
