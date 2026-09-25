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
                p.Guid!, p.Name, r.MatchId, r.RoundStartUnix, r.IngestedAtUtc,
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
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Deaths),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Headshots),
                g.Where(x => x.Weapon == 27).Sum(x => (long)x.Hits),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Hits),
                g.Where(x => x.Weapon != 27).Sum(x => (long)x.Atts)), StringComparer.OrdinalIgnoreCase);

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
                g.Select(x => x.MatchId).Distinct().Count(), lastPlayedUtc, rows.Sum(x => x.Xp),
                rows.Sum(x => (long)x.DamageGiven), rows.Sum(x => (long)x.DamageReceived),
                rows.Sum(x => (long)x.Gibs), rows.Sum(x => (long)x.SelfKills),
                rows.Sum(x => (long)x.TeamKills), rows.Sum(x => (long)x.TeamGibs),
                weapon?.Kills ?? 0, weapon?.KnifeKills ?? 0, weapon?.Deaths ?? 0,
                weapon?.Headshots ?? 0, weapon?.Revives ?? 0,
                rows.Sum(x => (long)x.MultiKills2), rows.Sum(x => (long)x.MultiKills3),
                rows.Sum(x => (long)x.MultiKills4), rows.Sum(x => (long)x.MultiKills5),
                rows.Sum(x => (long)x.MultiKills6), weapon?.Hits ?? 0, weapon?.Atts ?? 0);
        }).ToList();

        return
        [
            Board("Games", totals, p => p.Games),
            Board("XP", totals, p => p.Xp),
            Board("Kills", totals, p => p.Kills),
            Board("Knife kills", totals, p => p.KnifeKills),
            Board("Deaths", totals, p => p.Deaths),
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
        => new(title, players.Where(p => p.Games >= 20 && p.LastPlayedUtc >= DateTime.UtcNow.AddMonths(-1))
            .Select(p => new ScoreRow(p.Guid, p.Name, value(p))).Where(row => row.Value > 0)
            .OrderByDescending(x => x.Value).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
            value => value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix);

    private sealed record PlayerRoundRow(string Guid, string Name, Guid MatchId, long RoundStartUnix,
        DateTime IngestedAtUtc, int Xp, int DamageGiven, int DamageReceived, int Gibs, int SelfKills,
        int TeamKills, int TeamGibs, int MultiKills2, int MultiKills3, int MultiKills4, int MultiKills5, int MultiKills6);
    private sealed record WeaponRow(string Guid, int Weapon, int Hits, int Atts, int Kills, int Deaths, int Headshots);
    private sealed record WeaponTotals(long Kills, long KnifeKills, long Deaths, long Headshots, long Revives, long Hits, long Atts);
    private sealed record PlayerTotals(string Guid, string Name, int Games, DateTime LastPlayedUtc, int Xp,
        long DamageGiven, long DamageReceived, long Gibs, long SelfKills, long TeamKills, long TeamGibs,
        long Kills, long KnifeKills, long Deaths, long Headshots, long Revives, long MultiKills2,
        long MultiKills3, long MultiKills4, long MultiKills5, long MultiKills6, long WeaponHits, long WeaponAtts);
}

public sealed record ScoreRow(string Guid, string Name, double Value);

public sealed record Scoreboard(string Title, List<ScoreRow> Rows, Func<double, string> Format);
