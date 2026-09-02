namespace PappaETStats.Server.Domain;

public static class MatchWinnerCalculator
{
    public static MatchWinner? DetermineWinner(Match match)
        => DetermineWinner(
            match.Rounds.FirstOrDefault(r => r.RoundNumber == 1),
            match.Rounds.FirstOrDefault(r => r.RoundNumber == 2));

    public static MatchWinner? DetermineWinner(MatchRound? round1, MatchRound? round2)
    {
        if (round2 is null)
        {
            return null;
        }

        if (round1 is null)
        {
            return OverallTeamToWinner(MapToOverallTeam(roundNumber: 2, teamId: round2.WinnerTeam));
        }

        // Stopwatch draw rule: if round 2 next time equals the ORIGINAL first-round time limit,
        // neither side set a time.
        var baseLimit = ParseStopwatchTimeSeconds(round1.TimeLimit);
        var t2Next = ParseStopwatchTimeSeconds(round2.NextTimeLimit);
        if (baseLimit is not null && t2Next is not null)
        {
            if (t2Next.Value == baseLimit.Value)
            {
                return MatchWinner.Draw;
            }
        }
        else if (string.Equals(Norm(round2.NextTimeLimit), Norm(round1.TimeLimit), StringComparison.Ordinal))
        {
            return MatchWinner.Draw;
        }

        var t1 = ParseStopwatchTimeSeconds(round1.NextTimeLimit);
        var t2 = t2Next;

        if (t1 is null || t2 is null)
        {
            return OverallTeamToWinner(MapToOverallTeam(roundNumber: 2, teamId: round2.WinnerTeam));
        }

        // Smaller time is better (faster). If round 2 beats the time, round 2 winner wins.
        // If round 2 matches or is slower, round 1 winner wins.
        if (t2.Value < t1.Value)
        {
            return OverallTeamToWinner(MapToOverallTeam(roundNumber: 2, teamId: round2.WinnerTeam));
        }

        return OverallTeamToWinner(MapToOverallTeam(roundNumber: 1, teamId: round1.WinnerTeam));
    }

    private static MatchWinner? OverallTeamToWinner(int overallTeamId) => overallTeamId switch
    {
        1 => MatchWinner.Team1,
        2 => MatchWinner.Team2,
        _ => null,
    };

    private static int MapToOverallTeam(int roundNumber, int teamId)
    {
        // In stopwatch, teams swap between rounds.
        // Overall Team 1/2 are defined by round 1.
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

    private static string Norm(string? s) => (s ?? string.Empty).Trim();

    private static int? ParseStopwatchTimeSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        var dot = v.IndexOf('.');
        if (dot >= 0)
        {
            v = v[..dot];
        }

        var parts = v.Split(':');
        if (parts.Length is < 1 or > 3)
        {
            return null;
        }

        static bool TryParsePart(string s, out int n)
            => int.TryParse(s.Trim(), out n) && n >= 0;

        if (parts.Length == 1)
        {
            return TryParsePart(parts[0], out var seconds) ? seconds : null;
        }

        if (parts.Length == 2)
        {
            if (!TryParsePart(parts[0], out var minutes) || !TryParsePart(parts[1], out var seconds))
            {
                return null;
            }

            return checked(minutes * 60 + seconds);
        }

        if (!TryParsePart(parts[0], out var hours) || !TryParsePart(parts[1], out var minutes2) || !TryParsePart(parts[2], out var seconds2))
        {
            return null;
        }

        return checked(hours * 3600 + minutes2 * 60 + seconds2);
    }
}
