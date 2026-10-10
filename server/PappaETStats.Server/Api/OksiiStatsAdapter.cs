using System.Globalization;
using System.Text.Json;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Api;

public static class OksiiStatsAdapter
{
    public static JsonElement Field(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    public static string Text(JsonElement value, string key)
    {
        var field = Field(value, key);
        return field.ValueKind is JsonValueKind.String ? field.GetString()! :
            field.ValueKind is JsonValueKind.Number ? field.GetRawText() : "";
    }
    public static long Number(JsonElement value, string key)
        => ParseLong(Text(value, key), key);
    private static long ParseLong(string value, string name)
        => value.Length == 0 ? 0 : long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : throw new JsonException($"Invalid integer: {name}");
    public static double? OptionalNumber(JsonElement value, string key)
    {
        var text = Text(value, key);
        if (text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0)
            throw new JsonException($"Invalid nonnegative number: {key}");
        return n;
    }
    private static int Count(long n) => n is >= 0 and <= int.MaxValue ? (int)n : throw new JsonException("Counter out of range");

    public static MatchIngestDto Parse(JsonElement payload, string mapId)
    {
        var info = Field(payload, "round_info");
        var metadata = Field(payload, "metadata");
        string Meta(string key) => Text(metadata, key) is { Length: > 0 } text ? text : Text(info, key);
        if (info.ValueKind != JsonValueKind.Object || Field(payload, "player_stats").ValueKind != JsonValueKind.Object)
            throw new JsonException("round_info and player_stats objects are required");
        var players = new List<PlayerDto>();
        foreach (var entry in Field(payload, "player_stats").EnumerateObject())
        {
            var guid = entry.Name.Trim().ToUpperInvariant();
            if (guid.Length != 32 || !guid.All(Uri.IsHexDigit)) throw new JsonException("player_stats keys must be full 32-character GUIDs");
            var p = entry.Value;
            if (Text(p, "name").Length > 256) throw new JsonException("Player name is too long");
            var team = Count(Number(p, "team"));
            if (team is < 0 or > 3) throw new JsonException("Invalid player team");
            // A player may finish in spectators after playing. Retain their
            // counters, but only factions 1 and 2 participate in roster grouping.
            var tokensElement = Field(p, "weaponStats");
            if (tokensElement.ValueKind != JsonValueKind.Array) throw new JsonException("weaponStats must be a token array");
            var tokens = tokensElement.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString()! : t.GetRawText()).ToArray();
            if (tokens.Length < 11) throw new JsonException("weaponStats is missing mask or summary counters");
            var mask = ParseLong(tokens[0], "weapon mask");
            if (mask < 0 || mask > uint.MaxValue) throw new JsonException("Invalid weapon mask");
            var offset = 1;
            long Next() => offset < tokens.Length ? ParseLong(tokens[offset++], "weaponStats") : throw new JsonException("Truncated weaponStats");
            var weapons = new List<WeaponStatDto>();
            for (var weapon = 0; weapon < 32; weapon++)
            {
                if ((mask & (1L << weapon)) == 0) continue;
                weapons.Add(new WeaponStatDto { Weapon = weapon, Hits = Count(Next()), Atts = Next(), Kills = Count(Next()), Deaths = Count(Next()), Headshots = Count(Next()) });
            }
            if (tokens.Length != offset + 10) throw new JsonException("weaponStats length does not match its weapon mask");
            var damage = Count(Next()); var received = Count(Next()); var teamDamage = Count(Next()); var teamReceived = Count(Next());
            var gibs = Count(Next()); var selfKills = Count(Next()); var teamKills = Count(Next()); var teamGibs = Count(Next());
            if (!double.TryParse(tokens[offset++], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !double.IsFinite(percent) || percent is < 0 or > 100)
                throw new JsonException("Invalid time played percentage");
            var xp = Count(Next());
            // Validate optional values before opening a write transaction.
            foreach (var key in new[] { "assists", "spawn_count" }) Count(Number(p, key));
            OptionalNumber(p, "distance_travelled_meters");
            foreach (var key in new[] { "alive", "engaged" }) OptionalNumber(Field(p, "activity_stats_seconds"), key);
            OptionalNumber(Field(p, "stance_stats_seconds"), "is_downed");
            foreach (var section in new[] { "activity_stats_seconds", "stance_stats_seconds", "player_speed" })
            {
                var metrics = Field(p, section);
                if (metrics.ValueKind == JsonValueKind.Object)
                    foreach (var metric in metrics.EnumerateObject()) OptionalNumber(metrics, metric.Name);
            }
            players.Add(new PlayerDto { Guid = guid, Name = Text(p, "name"), Team = team, Rounds = Count(Number(p, "rounds")),
                ClientNum = Count(Number(p, "slot")), WeaponStats = weapons, DamageGiven = damage, DamageReceived = received,
                TeamDamageGiven = teamDamage, TeamDamageReceived = teamReceived, Gibs = gibs, SelfKills = selfKills,
                TeamKills = teamKills, TeamGibs = teamGibs, TimePlayedPercent = percent, Xp = xp });
        }
        if (players.Select(p => p.Guid).Distinct(StringComparer.OrdinalIgnoreCase).Count() != players.Count)
            throw new JsonException("Duplicate player GUIDs");
        var events = Events(payload);
        foreach (var ev in events)
        {
            if (ev.ValueKind != JsonValueKind.Object || Text(ev, "label").Length is 0 or > 128 || Text(ev, "group").Length > 64)
                throw new JsonException("Invalid event label or group");
            Number(ev, "leveltime"); Number(ev, "unixtime");
        }
        var obituaries = events.Where(e => Text(e, "label") is "kill" or "teamkill" or "suicide").Select(e =>
        {
            var suicide = Text(e, "label") == "suicide";
            return new ObituaryDto { Timestamp = Number(e, "leveltime"), Target = Text(e, suicide ? "player" : "victim"),
                Attacker = Text(e, suicide ? "player" : "killer"), MeansOfDeath = Count(Number(e, "weapon")),
                AttackerRespawnTime = checked((int)Math.Round(OptionalNumber(e, "killer_reinf") ?? 0)),
                VictimRespawnTime = checked((int)Math.Round(OptionalNumber(e, "victim_reinf") ?? 0)) };
        }).ToList();
        return new MatchIngestDto { MatchId = mapId, MapName = Text(info, "mapname"), Round = Count(Number(info, "round")),
            DefenderTeam = Count(Number(info, "defenderteam")), WinnerTeam = Count(Number(info, "winnerteam")),
            TimeLimit = Text(info, "timelimit"), NextTimeLimit = Text(info, "nextTimeLimit"),
            RoundStart = Number(info, "round_start"), RoundEnd = Number(info, "round_end"),
            RoundStartUnix = Number(info, "round_start_unix"), RoundEndUnix = Number(info, "round_end_unix"),
            ServerName = Meta("servername"), ServerIp = Meta("server_ip"), ServerPort = Meta("server_port"),
            ServerId = $"oksii:{Meta("server_ip")}:{Meta("server_port")}",
            Config = Meta("config"), Players = players, Obituaries = obituaries, OksiiPayload = payload.Clone() };
    }

    public static List<JsonElement> Events(JsonElement payload)
    {
        var events = Field(payload, "gamelog");
        // dkjson encodes an empty Lua table as {}, rather than [].
        if (events.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
            events.ValueKind == JsonValueKind.Object && !events.EnumerateObject().Any()) return [];
        if (events.ValueKind != JsonValueKind.Array) throw new JsonException("gamelog must be an array");
        return events.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static void ApplyPlayer(MatchPlayer player, JsonElement payload, MatchPlayer? previous)
    {
        var entries = Field(payload, "player_stats");
        var entry = entries.EnumerateObject().First(p => p.Name.Equals(player.Guid, StringComparison.OrdinalIgnoreCase)).Value;
        var assists = OptionalNumber(entry, "assists");
        player.Assists = assists.HasValue ? Math.Max(0, checked((int)assists.Value) - (previous?.Assists ?? 0)) : null;
        player.DistanceMeters = OptionalNumber(entry, "distance_travelled_meters");
        player.SpawnCount = OptionalNumber(entry, "spawn_count") is { } spawns ? checked((int)spawns) : null;
        var activity = Field(entry, "activity_stats_seconds");
        player.AliveSeconds = OptionalNumber(activity, "alive");
        player.EngagedSeconds = OptionalNumber(activity, "engaged");
        player.DownedSeconds = OptionalNumber(Field(entry, "stance_stats_seconds"), "is_downed");
        int? Objectives(string key) => Field(entry, key).ValueKind == JsonValueKind.Object ? Field(entry, key).EnumerateObject().Count() : null;
        player.ObjectivesPlanted = Objectives("obj_planted"); player.ObjectivesDefused = Objectives("obj_defused");
        player.ObjectivesSecured = Objectives("obj_secured"); player.ObjectivesReturned = Objectives("obj_returned");
        player.ObjectivesDestroyed = Objectives("obj_destroyed"); player.ObjectivesRepaired = Objectives("obj_repaired");
        player.DetailsJson = entry.GetRawText();
    }
}
