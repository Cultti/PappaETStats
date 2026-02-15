# PappaETStats.SkillRating

Standalone skill rating library (mu/sigma Bayesian update) ported from ET: Legacy `g_skillrating.c`.

- Player identity: `Guid`
- Persistence: none (pure calculations; you store ratings yourself)
- Update scaling is based on per-player damage contribution across the whole match.

## Quick usage

```csharp
using PappaETStats.SkillRating;

var calc = new SkillRatingCalculator(new SkillRatingOptions());

var players = new[]
{
    new MatchPlayerState(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new SkillRating(25, 25.0/3.0), Team.Axis, DamageDealt: 2200),
    new MatchPlayerState(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), new SkillRating(25, 25.0/3.0), Team.Allies, DamageDealt: 1800),
};

var updated = calc.UpdateRatings(players, winner: Team.Axis);
```
