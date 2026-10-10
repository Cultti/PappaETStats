using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Services;

public static class CanonicalStats
{
    public static string Write(Match map, MatchRound round)
    {
        var root = round.SourcePayloadJson != null ? JsonNode.Parse(round.SourcePayloadJson)!.AsObject() : new JsonObject();
        root["round_info"] = JsonSerializer.SerializeToNode(new { mapname = map.MapName, round = round.RoundNumber,
            defenderteam = round.DefenderTeam, winnerteam = round.WinnerTeam, timelimit = round.TimeLimit,
            nextTimeLimit = round.NextTimeLimit, round_start = round.RoundStartMs, round_end = round.RoundEndMs,
            round_start_unix = round.RoundStartUnix, round_end_unix = round.RoundEndUnix });
        if (root["metadata"] is not JsonObject) root["metadata"] = new JsonObject();
        var metadata = root["metadata"]!.AsObject();
        metadata["servername"] = map.ServerName; metadata["config"] = map.Config;
        metadata["server_ip"] = map.ServerIp; metadata["server_port"] = map.ServerPort;
        metadata["matchID"] = map.SourceMatchId ?? map.ExternalMatchId;
        metadata["source"] = round.StatsSource;
        metadata["counters"] = "per_round";
        var players = new JsonObject();
        foreach (var player in round.Sides.SelectMany(s => s.Players))
        {
            var details = player.DetailsJson != null ? JsonNode.Parse(player.DetailsJson)!.AsObject() : new JsonObject();
            details["guid"] = player.Guid.Length >= 8 ? player.Guid[..8] : player.Guid;
            details["name"] = player.Name; details["team"] = player.Team.ToString(CultureInfo.InvariantCulture);
            details["rounds"] = player.Rounds.ToString(CultureInfo.InvariantCulture);
            details["slot"] = player.ClientNum;
            long mask = 0;
            var tokens = new List<string>();
            foreach (var w in player.WeaponStats.OrderBy(w => w.Weapon))
            {
                mask |= 1L << w.Weapon;
                tokens.AddRange(new[] { w.Hits, w.Atts, w.Kills, w.Deaths, w.Headshots }.Select(n => n.ToString(CultureInfo.InvariantCulture)));
            }
            tokens.Insert(0, mask.ToString(CultureInfo.InvariantCulture));
            tokens.AddRange(new[] { player.DamageGiven, player.DamageReceived, player.TeamDamageGiven, player.TeamDamageReceived,
                player.Gibs, player.SelfKills, player.TeamKills, player.TeamGibs }.Select(n => n.ToString(CultureInfo.InvariantCulture)));
            tokens.Add(player.TimePlayedPercent.ToString(CultureInfo.InvariantCulture));
            tokens.Add(player.Xp.ToString(CultureInfo.InvariantCulture));
            details["weaponStats"] = JsonSerializer.SerializeToNode(tokens);
            if (player.Assists.HasValue) details["assists"] = player.Assists.Value;
            details["class_stats"] = JsonSerializer.SerializeToNode(player.ClassStats.Select(c => new { classId = c.ClassId, ms = c.Ms }));
            players[player.Guid] = details;
        }
        root["player_stats"] = players;
        root["gamelog"] = new JsonArray(round.Events.OrderBy(e => e.Sequence).Select(e => JsonNode.Parse(e.DataJson)).ToArray());
        return root.ToJsonString();
    }

    public static async Task BackfillAsync(Data.StatsDbContext db, CancellationToken ct = default)
    {
        // Historical rows already contain normalized counters; never subtract again.
        var ids = await db.MatchRounds.Where(r => r.PayloadJson == null).Select(r => r.Id).ToListAsync(ct);
        foreach (var batch in ids.Chunk(100))
        {
            var batchIds = batch.ToList();
            var rounds = await db.MatchRounds.AsSplitQuery().Include(r => r.Match)
                .Include(r => r.Sides).ThenInclude(s => s.Players).ThenInclude(p => p.WeaponStats)
                .Include(r => r.Sides).ThenInclude(s => s.Players).ThenInclude(p => p.ClassStats)
                .Include(r => r.Obituaries).Where(r => batchIds.Contains(r.Id)).ToListAsync(ct);
            foreach (var round in rounds)
            {
                AddLegacyEvents(round);
                db.RoundEvents.AddRange(round.Events);
                round.PayloadJson = Write(round.Match, round);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    public static void AddLegacyEvents(MatchRound round)
    {
        var teams = round.Sides.SelectMany(s => s.Players).GroupBy(p => p.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Team, StringComparer.OrdinalIgnoreCase);
        foreach (var obituary in round.Obituaries)
        {
            var suicide = obituary.AttackerGuid == obituary.TargetGuid || string.IsNullOrEmpty(obituary.AttackerGuid);
            var label = suicide ? "suicide" : teams.TryGetValue(obituary.AttackerGuid!, out var a)
                && teams.TryGetValue(obituary.TargetGuid ?? "", out var b) && a == b ? "teamkill" : "kill";
            var unix = round.RoundStartUnix * 1000 + Math.Max(0, obituary.TimestampMs - round.RoundStartMs);
            var data = new Dictionary<string, object?> { ["label"] = label, ["group"] = "player",
                ["leveltime"] = obituary.TimestampMs, ["unixtime"] = unix, ["weapon"] = obituary.MeansOfDeath,
                ["killer_reinf"] = obituary.AttackerRespawnTime, ["victim_reinf"] = obituary.VictimRespawnTime };
            if (suicide) data["player"] = obituary.TargetGuid;
            else { data["killer"] = obituary.AttackerGuid; data["victim"] = obituary.TargetGuid; }
            round.Events.Add(new RoundEvent { Id = Guid.NewGuid(), Sequence = round.Events.Count, Label = label,
                Group = "player", LevelTime = obituary.TimestampMs, UnixTimeMs = unix, DataJson = JsonSerializer.Serialize(data) });
        }
    }
}
