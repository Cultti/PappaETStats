using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Api;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;
using System.Text.Json;

namespace PappaETStats.Server.Services;

public static class MatchSeriesBuilder
{
    public static string ServerKey(Match map) => !string.IsNullOrWhiteSpace(map.ServerId)
        ? $"id:{map.ServerId}" : $"address:{map.ServerIp}:{map.ServerPort}|{map.ServerName}";

    public static async Task RebuildAsync(StatsDbContext db, double gapHours, CancellationToken ct = default, string? serverKey = null)
    {
        // Chronological reconstruction makes delayed uploads deterministic. Existing
        // series IDs are reused for their first map, keeping links stable.
        var query = db.Matches.AsQueryable();
        if (serverKey != null)
            query = query.Where(m => (m.ServerId != null && m.ServerId != "" ? "id:" + m.ServerId
                : "address:" + m.ServerIp + ":" + m.ServerPort + "|" + m.ServerName) == serverKey);
        var maps = await query.Include(m => m.Series).Include(m => m.Rounds)
            .ThenInclude(r => r.Sides).ThenInclude(s => s.Players).AsSplitQuery().ToListAsync(ct);
        var usedSeries = new HashSet<Guid>();
        foreach (var server in maps.GroupBy(ServerKey))
        {
            MatchSeries? series = null;
            string? previousRoster = null;
            string? firstTeam = null;
            string? previousConfig = null;
            var number = 0;
            var closed = false;
            foreach (var map in server.OrderBy(Start).ThenBy(m => m.ExternalMatchId, StringComparer.Ordinal))
            {
                var start = Start(map);
                var end = map.Rounds.Select(r => r.RoundEndUnix > 0 ? r.RoundEndUnix : Start(map)).DefaultIfEmpty(start).Max();
                var (roster, team1) = Roster(map);
                var continueSeries = series != null && !closed && roster != null && roster == previousRoster
                    && map.Config == previousConfig && start >= series.LastPlayedAtUnix
                    && start - series.LastPlayedAtUnix <= Math.Max(0, gapHours) * 3600;
                if (!continueSeries)
                {
                    if (series != null) series.EndedAtUnix = series.LastPlayedAtUnix;
                    series = map.Series != null && !usedSeries.Contains(map.Series.Id) ? map.Series : new MatchSeries
                    { Id = Guid.NewGuid(), ServerKey = server.Key };
                    if (db.Entry(series).State == EntityState.Detached) db.MatchSeries.Add(series);
                    series.ServerKey = server.Key; series.StartedAtUnix = start; series.EndedAtUnix = null;
                    usedSeries.Add(series.Id);
                    firstTeam = team1;
                    number = 0;
                }
                map.Series = series;
                map.SeriesId = series!.Id;
                map.MapNumber = ++number;
                map.SeriesTeam1Faction = team1 == firstTeam ? 1 : 2;
                series.LastPlayedAtUnix = end;
                previousRoster = roster;
                previousConfig = map.Config;
                closed = EndsMatch(map);
                if (closed) series.EndedAtUnix = end;
            }
            if (series != null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - series.LastPlayedAtUnix > gapHours * 3600)
                series.EndedAtUnix = series.LastPlayedAtUnix;
        }
        await db.SaveChangesAsync(ct);
        var unused = await db.MatchSeries.Where(s => !s.Maps.Any()).ToListAsync(ct);
        db.MatchSeries.RemoveRange(unused);
        await db.SaveChangesAsync(ct);
    }

    public static long Start(Match map) => map.Rounds.Select(r => r.RoundStartUnix > 0 ? r.RoundStartUnix :
        r.RoundEndUnix > 0 ? r.RoundEndUnix : new DateTimeOffset(DateTime.SpecifyKind(r.IngestedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()).DefaultIfEmpty(0).Min();

    private static (string? Roster, string Team1) Roster(Match map)
    {
        var round = map.Rounds.OrderBy(r => r.RoundNumber).FirstOrDefault();
        if (round == null) return (null, "");
        string Team(MatchRound r, int team) => string.Join(",", r.Sides.Where(s => s.Team == team).SelectMany(s => s.Players)
            .Select(p => p.Guid.Trim().ToUpperInvariant()).Order(StringComparer.Ordinal));
        var a = Team(round, round.RoundNumber == 1 ? 1 : 2);
        var b = Team(round, round.RoundNumber == 1 ? 2 : 1);
        if (a.Length == 0 || b.Length == 0 || a.Split(',').Intersect(b.Split(',')).Any()) return (null, a);
        var r2 = map.Rounds.FirstOrDefault(r => r.RoundNumber == 2);
        if (round.RoundNumber == 1 && r2 != null && (a != Team(r2, 2) || b != Team(r2, 1))) return (null, a);
        return (string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a, a);
    }

    private static bool EndsMatch(Match map)
    {
        var source = map.Rounds.OrderByDescending(r => r.RoundNumber).FirstOrDefault()?.SourcePayloadJson;
        if (source == null) return false;
        using var json = JsonDocument.Parse(source);
        return OksiiStatsAdapter.Field(OksiiStatsAdapter.Field(OksiiStatsAdapter.Field(json.RootElement, "metadata"), "scores"), "match_finished").ValueKind == JsonValueKind.True;
    }
}
