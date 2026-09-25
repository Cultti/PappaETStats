namespace PappaETStats.Server.Domain;

public sealed record MultiKillEvent(long TimestampMs, string? AttackerGuid, string? TargetGuid, int MeansOfDeath);

public static class MultiKillCounter
{
    // ET means of death value for MOD_AIRSTRIKE.
    private const int AirstrikeMeansOfDeath = 23;
    private const long AirstrikeWindowMs = 1_000;

    public static Dictionary<string, int[]> Count(
        IEnumerable<MultiKillEvent> obituaryEvents,
        IReadOnlyDictionary<string, int> playerTeams,
        out int qualifyingGroups)
    {
        var countsByAttacker = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        qualifyingGroups = 0;

        var validKills = obituaryEvents
            .Where(k => k.TimestampMs >= 0 && IsEnemyKill(k, playerTeams))
            .DistinctBy(k => EventKey(k), StringComparer.OrdinalIgnoreCase)
            .GroupBy(k => k.AttackerGuid!.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var attackerKills in validKills)
        {
            var killGroups = new List<int>();

            // Preserve simultaneous-kill grouping for other damage types.
            killGroups.AddRange(attackerKills
                .Where(k => k.MeansOfDeath != AirstrikeMeansOfDeath)
                .GroupBy(k => (k.TimestampMs, k.MeansOfDeath))
                .Select(g => g.Count()));

            // Airstrike splash deaths can be reported over several frames. Cluster them
            // into non-overlapping windows anchored at the first kill in each cluster.
            var airstrikeKills = attackerKills
                .Where(k => k.MeansOfDeath == AirstrikeMeansOfDeath)
                .OrderBy(k => k.TimestampMs)
                .ToList();
            for (var i = 0; i < airstrikeKills.Count;)
            {
                var windowStart = airstrikeKills[i].TimestampMs;
                var end = i + 1;
                while (end < airstrikeKills.Count
                       && airstrikeKills[end].TimestampMs - windowStart <= AirstrikeWindowMs)
                {
                    end++;
                }

                killGroups.Add(end - i);
                i = end;
            }

            foreach (var killCount in killGroups.Where(count => count is >= 2 and <= 6))
            {
                if (!countsByAttacker.TryGetValue(attackerKills.Key, out var counts))
                {
                    counts = new int[5];
                    countsByAttacker[attackerKills.Key] = counts;
                }

                counts[killCount - 2]++;
                qualifyingGroups++;
            }
        }

        return countsByAttacker;
    }

    private static bool IsEnemyKill(MultiKillEvent kill, IReadOnlyDictionary<string, int> playerTeams)
    {
        if (string.IsNullOrWhiteSpace(kill.AttackerGuid) || string.IsNullOrWhiteSpace(kill.TargetGuid)
            || string.Equals(kill.AttackerGuid.Trim(), kill.TargetGuid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return playerTeams.TryGetValue(kill.AttackerGuid.Trim(), out var attackerTeam)
               && playerTeams.TryGetValue(kill.TargetGuid.Trim(), out var targetTeam)
               && attackerTeam != targetTeam;
    }

    private static string EventKey(MultiKillEvent kill)
        => $"{kill.TimestampMs}|{kill.TargetGuid?.Trim().ToUpperInvariant()}|{kill.AttackerGuid?.Trim().ToUpperInvariant()}|{kill.MeansOfDeath}";
}
