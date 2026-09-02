using PappaETStats.SkillRating;

namespace PappaETStats.Server.Domain;

/// <summary>Format-specific rating track kept alongside the overall rating.</summary>
public enum SkillRatingBucket
{
    /// <summary>3on3/4on4 (up to 4 players per team).</summary>
    Small = 3,

    /// <summary>5on5/6on6 (5 or more players per team).</summary>
    Large = 6,
}

/// <summary>
/// Shared match-to-rating logic used by both live ingest and the admin full replay.
/// </summary>
public static class SkillRatingUpdater
{
    public readonly record struct AggregatedPlayer(Guid PlayerId, Team Team, int DamageDealt);

    public static SkillRatingBucket BucketForTeamSize(int teamSize) =>
        teamSize <= 4 ? SkillRatingBucket.Small : SkillRatingBucket.Large;

    public static int MapToOverallTeam(int roundNumber, int teamId)
    {
        // In stopwatch, teams swap between rounds. Overall Team 1/2 are defined by round 1.
        if (roundNumber == 2)
        {
            return teamId switch
            {
                1 => 2,
                2 => 1,
                _ => teamId,
            };
        }

        return teamId;
    }

    public static Team? MapOverallTeamToSkillTeam(int overallTeamId) => overallTeamId switch
    {
        1 => Team.Axis,
        2 => Team.Allies,
        _ => null,
    };

    /// <summary>
    /// Aggregates per-player damage across both rounds and assigns each player their overall team.
    /// </summary>
    public static Dictionary<string, AggregatedPlayer> BuildAggregate(MatchRound? round1, MatchRound round2)
    {
        var aggregate = new Dictionary<string, AggregatedPlayer>(StringComparer.OrdinalIgnoreCase);

        void AddRound(MatchRound r)
        {
            foreach (var p in r.Sides.SelectMany(s => s.Players))
            {
                if (string.IsNullOrWhiteSpace(p.Guid))
                {
                    continue;
                }

                var guidString = p.Guid.Trim();
                if (!Guid.TryParseExact(guidString, "N", out var playerId))
                {
                    continue;
                }

                var overallTeamId = MapToOverallTeam(r.RoundNumber, p.Team);
                var skillTeam = MapOverallTeamToSkillTeam(overallTeamId);
                if (skillTeam is null)
                {
                    continue;
                }

                var damage = Math.Max(0, p.DamageGiven);

                if (aggregate.TryGetValue(guidString, out var existing))
                {
                    aggregate[guidString] = existing with
                    {
                        DamageDealt = checked(existing.DamageDealt + damage),
                    };
                }
                else
                {
                    aggregate[guidString] = new AggregatedPlayer(playerId, skillTeam.Value, damage);
                }
            }
        }

        if (round1 is not null)
        {
            AddRound(round1);
        }

        AddRound(round2);

        return aggregate;
    }

    /// <summary>
    /// Applies a completed match to the overall rating track and to the format-specific track
    /// matching <paramref name="teamSize"/>. Both tracks use identical calculator logic.
    /// </summary>
    public static void ApplyMatch(
        SkillRatingCalculator calculator,
        SkillRatingOptions options,
        IReadOnlyDictionary<string, AggregatedPlayer> aggregate,
        Team winner,
        int teamSize,
        IReadOnlyDictionary<string, Player> playerRows)
    {
        ApplyTrack(
            calculator,
            aggregate,
            playerRows,
            winner,
            read: p => new PappaETStats.SkillRating.SkillRating(p.Mu, p.Sigma),
            write: (p, r) =>
            {
                p.Mu = r.Mu;
                p.Sigma = r.Sigma;
            });

        if (BucketForTeamSize(teamSize) == SkillRatingBucket.Small)
        {
            ApplyTrack(
                calculator,
                aggregate,
                playerRows,
                winner,
                read: p => new PappaETStats.SkillRating.SkillRating(p.MuSmall ?? options.Mu, p.SigmaSmall ?? options.Sigma),
                write: (p, r) =>
                {
                    p.MuSmall = r.Mu;
                    p.SigmaSmall = r.Sigma;
                });
        }
        else
        {
            ApplyTrack(
                calculator,
                aggregate,
                playerRows,
                winner,
                read: p => new PappaETStats.SkillRating.SkillRating(p.MuLarge ?? options.Mu, p.SigmaLarge ?? options.Sigma),
                write: (p, r) =>
                {
                    p.MuLarge = r.Mu;
                    p.SigmaLarge = r.Sigma;
                });
        }
    }

    private static void ApplyTrack(
        SkillRatingCalculator calculator,
        IReadOnlyDictionary<string, AggregatedPlayer> aggregate,
        IReadOnlyDictionary<string, Player> playerRows,
        Team winner,
        Func<Player, PappaETStats.SkillRating.SkillRating> read,
        Action<Player, PappaETStats.SkillRating.SkillRating> write)
    {
        var states = new List<MatchPlayerState>(aggregate.Count);
        var idToGuidString = new Dictionary<Guid, string>(aggregate.Count);

        foreach (var (guidString, v) in aggregate)
        {
            if (!playerRows.TryGetValue(guidString, out var row))
            {
                continue;
            }

            idToGuidString[v.PlayerId] = guidString;

            states.Add(new MatchPlayerState(
                PlayerId: v.PlayerId,
                Rating: read(row),
                Team: v.Team,
                DamageDealt: v.DamageDealt));
        }

        if (states.Count == 0)
        {
            return;
        }

        var updated = calculator.UpdateRatings(states, winner: winner, damageFloor: 0);
        foreach (var (playerId, rating) in updated)
        {
            if (!idToGuidString.TryGetValue(playerId, out var guidString))
            {
                continue;
            }

            write(playerRows[guidString], rating);
        }
    }
}
