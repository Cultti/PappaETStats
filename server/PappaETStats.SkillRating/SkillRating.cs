namespace PappaETStats.SkillRating;

public readonly record struct SkillRating(double Mu, double Sigma)
{
    public double Conservative => Mu - 3.0 * Sigma;
}
