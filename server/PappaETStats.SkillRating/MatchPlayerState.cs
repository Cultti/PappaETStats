namespace PappaETStats.SkillRating;

public readonly record struct MatchPlayerState(
    Guid PlayerId,
    SkillRating Rating,
    Team Team,
    int DamageDealt
);
