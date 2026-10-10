using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Data;

namespace PappaETStats.Server.Services;

public sealed class ScoreboardCache(IDbContextFactory<StatsDbContext> dbFactory)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<Scoreboard> _scoreboards = [];
    private DateTime _expiresAtUtc;
    private bool _hasValue;

    public async Task<IReadOnlyList<Scoreboard>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_hasValue && DateTime.UtcNow < _expiresAtUtc)
        {
            return _scoreboards;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_hasValue && DateTime.UtcNow < _expiresAtUtc)
            {
                return _scoreboards;
            }

            var calculated = await CalculateAsync(cancellationToken);
            _scoreboards = calculated;
            _expiresAtUtc = DateTime.UtcNow.Add(CacheDuration);
            _hasValue = true;
            return calculated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _hasValue = false;
    }

    private async Task<IReadOnlyList<Scoreboard>> CalculateAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var playerRows = await (
            from p in db.MatchPlayers.AsNoTracking()
            join s in db.MatchSides.AsNoTracking() on p.MatchSideId equals s.Id
            join r in db.MatchRounds.AsNoTracking() on s.MatchRoundId equals r.Id
            where p.Guid != null && p.Guid != ""
            select new PlayerRoundRow(
                p.Guid!, p.Name, r.MatchId, r.Match.SeriesId, r.RoundStartMs, r.RoundEndMs,
                r.RoundStartUnix, r.RoundEndUnix, r.IngestedAtUtc, p.TimePlayedPercent,
                p.Xp, p.DamageGiven, p.DamageReceived, p.Gibs, p.SelfKills, p.TeamKills, p.TeamGibs,
                p.MultiKills2, p.MultiKills3, p.MultiKills4, p.MultiKills5, p.MultiKills6)
        ).ToListAsync(cancellationToken);

        var weaponRows = await (
            from ws in db.MatchPlayerWeaponStats.AsNoTracking()
            join p in db.MatchPlayers.AsNoTracking() on ws.MatchPlayerId equals p.Id
            where p.Guid != null && p.Guid != ""
            group ws by new { p.Guid, ws.Weapon } into g
            select new WeaponRow(g.Key.Guid!, g.Key.Weapon,
                g.Sum(x => x.Hits), g.Sum(x => x.Atts), g.Sum(x => x.Kills),
                g.Sum(x => x.Deaths), g.Sum(x => x.Headshots))
        ).ToListAsync(cancellationToken);

        var weaponsByGuid = weaponRows.GroupBy(x => x.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new WeaponTotals(
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Kills),
                g.Where(x => x.Weapon == 0).Sum(x => (long)x.Kills),
                g.Where(x => x.Weapon == 12).Sum(x => (long)x.Kills),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Deaths),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Headshots),
                g.Where(x => x.Weapon == 27).Sum(x => (long)x.Hits),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Hits),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Atts)), StringComparer.OrdinalIgnoreCase);

        var readyUpCounts = await db.LastReadyUps.AsNoTracking()
            .GroupBy(r => r.PlayerGuid)
            .Select(g => new { Guid = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Guid, x => x.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var extraTotals = await db.MatchPlayers.AsNoTracking().Where(p => p.Guid != "")
            .GroupBy(p => p.Guid).Select(g => new
            {
                Guid = g.Key,
                Assists = g.Sum(p => (long)(p.Assists ?? 0)),
                Plants = g.Sum(p => (long)(p.ObjectivesPlanted ?? 0)), Defuses = g.Sum(p => (long)(p.ObjectivesDefused ?? 0)),
                Secures = g.Sum(p => (long)(p.ObjectivesSecured ?? 0)), Returns = g.Sum(p => (long)(p.ObjectivesReturned ?? 0)),
                Repairs = g.Sum(p => (long)(p.ObjectivesRepaired ?? 0)), Destroyed = g.Sum(p => (long)(p.ObjectivesDestroyed ?? 0)),
                Distance = g.Sum(p => p.DistanceMeters ?? 0), Alive = g.Sum(p => p.AliveSeconds ?? 0), Engaged = g.Sum(p => p.EngagedSeconds ?? 0)
            }).ToDictionaryAsync(p => p.Guid, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var totals = playerRows.GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var rows = g.ToList();
            var latestName = g.Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .OrderByDescending(x => x.RoundStartUnix).ThenByDescending(x => x.IngestedAtUtc)
                .Select(x => x.Name).FirstOrDefault();
            var lastPlayedUtc = g.Select(x => x.RoundStartUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(x.RoundStartUnix).UtcDateTime
                : DateTime.SpecifyKind(x.IngestedAtUtc, DateTimeKind.Utc)).Max();
            weaponsByGuid.TryGetValue(g.Key, out var weapon);
            return new PlayerTotals(
                g.Key, string.IsNullOrWhiteSpace(latestName) ? g.Key : latestName,
                g.Select(x => x.MatchId).Distinct().Count(),
                // Count from the rows already loaded above. A grouped SQL
                // Distinct().Count() generates a correlated derived table
                // that MariaDB cannot resolve against the outer player GUID.
                g.Select(x => x.SeriesId ?? x.MatchId).Distinct().Count(),
                lastPlayedUtc, rows.Sum(x => x.Xp),
                rows.Sum(x => (long)x.DamageGiven), rows.Sum(x => (long)x.DamageReceived),
                rows.Sum(x => (long)x.Gibs), rows.Sum(x => (long)x.SelfKills),
                rows.Sum(x => (long)x.TeamKills), rows.Sum(x => (long)x.TeamGibs),
                weapon?.Kills ?? 0, weapon?.KnifeKills ?? 0, weapon?.MortarKills ?? 0, weapon?.Deaths ?? 0,
                weapon?.Headshots ?? 0, weapon?.Revives ?? 0,
                rows.Sum(x => (long)x.MultiKills2), rows.Sum(x => (long)x.MultiKills3),
                rows.Sum(x => (long)x.MultiKills4), rows.Sum(x => (long)x.MultiKills5),
                rows.Sum(x => (long)x.MultiKills6), weapon?.Hits ?? 0, weapon?.Atts ?? 0,
                rows.Sum(x => SpawnWaitTimeCalculator.CalculateSeconds(
                    x.RoundStartMs, x.RoundEndMs, x.RoundStartUnix, x.RoundEndUnix, x.TimePlayedPercent)));
        }).ToList();

        return
        [
            Board("Maps played", totals, p => p.Games),
            Board("Matches played", totals, p => p.Matches),
            Board("Assists", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Assists ?? 0),
            Board("Objectives planted", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Plants ?? 0),
            Board("Objectives defused", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Defuses ?? 0),
            Board("Objectives secured", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Secures ?? 0),
            Board("Objectives returned", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Returns ?? 0),
            Board("Objectives repaired", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Repairs ?? 0),
            Board("Objectives destroyed", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Destroyed ?? 0),
            Board("Distance travelled", totals, p => extraTotals.GetValueOrDefault(p.Guid)?.Distance ?? 0, "N0", " m"),
            Board("Activity", totals.Where(p => (extraTotals.GetValueOrDefault(p.Guid)?.Alive ?? 0) >= 60).ToList(),
                p => 100 * extraTotals[p.Guid].Engaged / extraTotals[p.Guid].Alive, "N1", "%"),
            Board("Their body wasn't ready", totals, p => readyUpCounts.GetValueOrDefault(p.Guid)),
            Board("XP", totals, p => p.Xp),
            Board("Kills", totals, p => p.Kills),
            Board("Knife kills", totals, p => p.KnifeKills),
            Board("Mortar kills", totals, p => p.MortarKills),
            Board("Deaths", totals, p => p.Deaths),
            Board("Waiting for spawn", totals, p => p.WaitingForSpawnSeconds, FormatDuration),
            Board("K/D", totals, p => p.Deaths == 0 ? p.Kills : (double)p.Kills / p.Deaths, "0.00"),
            Board("Headshots", totals, p => p.Headshots),
            Board("Revives", totals, p => p.Revives),
            Board("Damage given", totals, p => p.DamageGiven),
            Board("Damage received", totals, p => p.DamageReceived),
            Board("Gibs", totals, p => p.Gibs),
            Board("Self kills", totals, p => p.SelfKills),
            Board("Team kills", totals, p => p.TeamKills),
            Board("Team gibs", totals, p => p.TeamGibs),
            Board("Double kills", totals, p => p.MultiKills2),
            Board("Triple kills", totals, p => p.MultiKills3),
            Board("Quad kills", totals, p => p.MultiKills4),
            Board("5-kill streaks", totals, p => p.MultiKills5),
            Board("6-kill streaks", totals, p => p.MultiKills6),
            Board("Weapon accuracy", totals.Where(p => p.WeaponAtts >= 100).ToList(),
                p => p.WeaponAtts == 0 ? 0 : 100d * p.WeaponHits / p.WeaponAtts, "0.0", "%"),
        ];
    }

    private static Scoreboard Board(string title, List<PlayerTotals> players, Func<PlayerTotals, double> value,
        string format = "N0", string suffix = "")
        => Board(title, players, value,
            value => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix);

    private static Scoreboard Board(string title, List<PlayerTotals> players, Func<PlayerTotals, double> value,
        Func<double, string> format)
        => new(title, players.Where(p => p.Games >= 20 && p.LastPlayedUtc >= DateTime.UtcNow.AddMonths(-1))
            .Select(p => new ScoreRow(p.Guid, p.Name, value(p))).Where(row => row.Value > 0)
            .OrderByDescending(x => x.Value).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            format);

    private static string FormatDuration(double seconds)
    {
        var totalSeconds = Math.Max(0L, (long)Math.Round(seconds, MidpointRounding.AwayFromZero));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var remainingSeconds = totalSeconds % 60;
        return $"{hours}:{minutes:00}:{remainingSeconds:00}";
    }

    private sealed record PlayerRoundRow(string Guid, string Name, Guid MatchId, Guid? SeriesId, long RoundStartMs, long RoundEndMs,
        long RoundStartUnix, long RoundEndUnix, DateTime IngestedAtUtc, double TimePlayedPercent,
        int Xp, int DamageGiven, int DamageReceived, int Gibs, int SelfKills,
        int TeamKills, int TeamGibs, int MultiKills2, int MultiKills3, int MultiKills4, int MultiKills5, int MultiKills6);
    private sealed record WeaponRow(string Guid, int Weapon, int Hits, int Atts, int Kills, int Deaths, int Headshots);
    private sealed record WeaponTotals(long Kills, long KnifeKills, long MortarKills, long Deaths, long Headshots,
        long Revives, long Hits, long Atts);
    private sealed record PlayerTotals(string Guid, string Name, int Games, int Matches, DateTime LastPlayedUtc, int Xp,
        long DamageGiven, long DamageReceived, long Gibs, long SelfKills, long TeamKills, long TeamGibs,
        long Kills, long KnifeKills, long MortarKills, long Deaths, long Headshots, long Revives, long MultiKills2,
        long MultiKills3, long MultiKills4, long MultiKills5, long MultiKills6, long WeaponHits, long WeaponAtts,
        double WaitingForSpawnSeconds);
}

public static class SpawnWaitTimeCalculator
{
    public static double CalculateSeconds(long roundStartMs, long roundEndMs, long roundStartUnix,
        long roundEndUnix, double timePlayedPercent)
    {
        var durationMs = roundEndMs > roundStartMs
            ? roundEndMs - roundStartMs
            : roundEndUnix > roundStartUnix
                ? (roundEndUnix - roundStartUnix) * 1000d
                : 0d;

        var aliveFraction = Math.Clamp(timePlayedPercent, 0d, 100d) / 100d;
        return durationMs * (1d - aliveFraction) / 1000d;
    }
}

public sealed record ScoreRow(string Guid, string Name, double Value);

public sealed record Scoreboard(string Title, List<ScoreRow> Rows, Func<double, string> Format);
