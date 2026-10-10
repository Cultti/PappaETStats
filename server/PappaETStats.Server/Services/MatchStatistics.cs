using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Api;
using PappaETStats.Server.Components.Shared;
using PappaETStats.Server.Data;
using PappaETStats.Server.Domain;

namespace PappaETStats.Server.Services;

public sealed class MatchStatsSample
{
    public required MatchPlayer Player { get; init; }
    public List<MatchPlayerWeaponStat> Weapons { get; init; } = [];
    public List<MatchPlayerClassStat> Classes { get; init; } = [];
    public Guid MapId { get; init; }
    public Guid? SeriesId { get; init; }
    public int SeriesTeam1Faction { get; init; } = 1;
    public Guid RoundId { get; init; }
    public int RoundNumber { get; init; }
    public int DefenderTeam { get; init; }
    public long StartUnix { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public long EndUnix { get; init; }
    public DateTime IngestedAtUtc { get; init; }
}

public sealed record MatchStatsPlayer(PlayerStatsGridRow Grid, int Team, Dictionary<string, double> Metrics);
public sealed record MatchStatsResult(List<MatchStatsPlayer> Players, List<MatchAward> Awards);
public sealed record MatchAwardEntry(string Guid, string Name, double Value, string DisplayValue);
public sealed record MatchAward(string Name, string Description, bool IsWeapon, List<MatchAwardEntry> Rankings)
{
    public IEnumerable<MatchAwardEntry> Winners => Rankings.Where(p => Math.Abs(p.Value - Rankings[0].Value) < 0.000001);
}

public static class MatchStatistics
{
    public static async Task<MatchStatsResult> LoadAsync(StatsDbContext db, IReadOnlyCollection<Guid> mapIds,
        Guid? roundId = null, CancellationToken ct = default)
    {
        var ids = mapIds.Distinct().ToList();
        if (ids.Count == 0) return new([], []);
        var query = db.MatchPlayers.AsNoTracking().AsSplitQuery()
            .Where(p => ids.Contains(p.MatchSide.MatchRound.MatchId) && p.Guid != "");
        if (roundId.HasValue) query = query.Where(p => p.MatchSide.MatchRoundId == roundId.Value);
        var samples = await query.Select(p => new MatchStatsSample
        {
            Player = p, Weapons = p.WeaponStats.ToList(), Classes = p.ClassStats.ToList(),
            MapId = p.MatchSide.MatchRound.MatchId, SeriesId = p.MatchSide.MatchRound.Match.SeriesId,
            SeriesTeam1Faction = p.MatchSide.MatchRound.Match.SeriesTeam1Faction,
            RoundId = p.MatchSide.MatchRoundId, RoundNumber = p.MatchSide.MatchRound.RoundNumber,
            DefenderTeam = p.MatchSide.MatchRound.DefenderTeam,
            StartUnix = p.MatchSide.MatchRound.RoundStartUnix, EndUnix = p.MatchSide.MatchRound.RoundEndUnix,
            StartMs = p.MatchSide.MatchRound.RoundStartMs, EndMs = p.MatchSide.MatchRound.RoundEndMs,
            IngestedAtUtc = p.MatchSide.MatchRound.IngestedAtUtc
        }).ToListAsync(ct);
        var rounds = samples.Select(s => s.RoundId).Distinct().ToList();
        List<string> labels = ["kill", "suicide", "teamkill", "spawn", "weapon_fire", "damage", "obj_damage",
            "vehicle_damage", "pause", "unpause"];
        var events = await db.RoundEvents.AsNoTracking()
            .Where(e => rounds.Contains(e.MatchRoundId) && labels.Contains(e.Label))
            .OrderBy(e => e.Sequence).ToListAsync(ct);
        return Calculate(samples, events);
    }

    public static MatchStatsResult Calculate(IReadOnlyList<MatchStatsSample> samples, IReadOnlyList<RoundEvent> events)
    {
        var players = new List<MatchStatsPlayer>();
        foreach (var group in samples.Where(s => !string.IsNullOrWhiteSpace(s.Player.Guid))
                     .GroupBy(s => s.Player.Guid, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(s => s.StartUnix).ThenBy(s => s.IngestedAtUtc).ThenBy(s => s.RoundNumber).ToList();
            var first = rows.FirstOrDefault(s => s.Player.Team is 1 or 2);
            var faction = first == null ? 0 : first.RoundNumber == 2 ? 3 - first.Player.Team : first.Player.Team;
            var team = first?.SeriesId != null ? (faction == first.SeriesTeam1Faction ? 1 : 2) : faction;
            var weapons = rows.SelectMany(s => s.Weapons).GroupBy(w => w.Weapon)
                .Select(g => new PlayerWeaponStatRow(g.Key, g.Sum(w => w.Hits), g.Sum(w => w.Atts),
                    g.Sum(w => w.Kills), g.Sum(w => w.Deaths), g.Sum(w => w.Headshots))).OrderBy(w => w.Weapon).ToList();
            var duration = rows.Sum(DurationMs);
            var playtime = duration > 0 ? rows.Sum(s => s.Player.TimePlayedPercent * DurationMs(s)) / duration
                : rows.Average(s => s.Player.TimePlayedPercent);
            var stats = rows.Select(s => s.Player).ToList();
            static double? OptionalSum(IEnumerable<double?> values)
            {
                var present = values.Where(v => v.HasValue).ToList();
                return present.Count == 0 ? null : present.Sum(v => v!.Value);
            }
            var assists = OptionalSum(stats.Select(p => (double?)p.Assists));
            var alive = OptionalSum(stats.Select(p => p.AliveSeconds));
            var engaged = OptionalSum(stats.Select(p => p.EngagedSeconds));
            var grid = new PlayerStatsGridRow
            {
                Guid = group.Key.ToUpperInvariant(), Name = rows.Last().Player.Name,
                Games = rows.Select(s => s.MapId).Distinct().Count(), TimePlayedPercent = playtime,
                DamageGiven = stats.Sum(p => p.DamageGiven), DamageReceived = stats.Sum(p => p.DamageReceived),
                TeamDamageGiven = stats.Sum(p => p.TeamDamageGiven), TeamDamageReceived = stats.Sum(p => p.TeamDamageReceived),
                TeamKills = stats.Sum(p => p.TeamKills), Gibs = stats.Sum(p => p.Gibs), SelfKills = stats.Sum(p => p.SelfKills),
                MultiKills2 = stats.Sum(p => p.MultiKills2), MultiKills3 = stats.Sum(p => p.MultiKills3),
                MultiKills4 = stats.Sum(p => p.MultiKills4), MultiKills5 = stats.Sum(p => p.MultiKills5), MultiKills6 = stats.Sum(p => p.MultiKills6),
                Assists = assists.HasValue ? (int)assists.Value : null, AliveSeconds = alive, EngagedSeconds = engaged,
                WeaponStats = weapons,
                ClassStats = rows.SelectMany(s => s.Classes).GroupBy(c => c.ClassId).OrderBy(g => g.Key)
                    .Select(g => new PlayerClassStatRow(ClassName(g.Key), FormatDuration(g.Sum(c => c.Ms) / 1000d))).ToList()
            };
            var kills = weapons.Where(w => w.Weapon != 27).Sum(w => w.Kills);
            var deaths = weapons.Where(w => w.Weapon != 27).Sum(w => w.Deaths);
            var metrics = new Dictionary<string, double>
            {
                ["kills"] = kills, ["deaths"] = deaths, ["kdr"] = deaths == 0 ? kills : (double)kills / deaths,
                ["headshots"] = weapons.Where(w => w.Weapon != 27).Sum(w => w.Headshots),
                ["revives"] = weapons.Where(w => w.Weapon == 27).Sum(w => w.Hits),
                ["xp"] = stats.Sum(p => p.Xp), ["damage"] = grid.DamageGiven, ["received"] = grid.DamageReceived,
                ["gibs"] = grid.Gibs, ["selfkills"] = grid.SelfKills, ["teamkills"] = grid.TeamKills,
                ["teamdamage"] = grid.TeamDamageGiven, ["playtime"] = playtime,
                ["spam"] = weapons.Where(w => w.Weapon is >= 8 and <= 19).Sum(w => w.Kills)
            };
            if (alive.HasValue) metrics["alive"] = alive.Value;
            if (engaged.HasValue) metrics["engaged"] = engaged.Value;
            var distance = OptionalSum(stats.Select(p => p.DistanceMeters));
            if (distance.HasValue) metrics["distance"] = distance.Value;
            var rifle = weapons.FirstOrDefault(w => w.Weapon == 18);
            if (rifle?.Atts > 0) metrics["rifleaccuracy"] = 100d * rifle.Hits / rifle.Atts;
            foreach (var weapon in weapons.Where(w => w.Weapon != 27)) metrics["weapon:" + weapon.Weapon] = weapon.Kills;
            AddDetails(metrics, stats);
            players.Add(new(grid, team, metrics));
        }
        AddEventMetrics(players, samples, events);
        players = players.OrderByDescending(p => p.Grid.DamageGiven).ThenBy(p => p.Grid.Guid, StringComparer.Ordinal).ToList();
        return new(players, MatchAwards.Calculate(players));
    }

    private static double DurationMs(MatchStatsSample s) => s.EndMs > s.StartMs ? s.EndMs - s.StartMs
        : s.EndUnix > s.StartUnix ? (s.EndUnix - s.StartUnix) * 1000d : 0;

    private static void AddDetails(Dictionary<string, double> metrics, List<MatchPlayer> rows)
    {
        var spawnDistance = 0d;
        var spawns = 0d;
        foreach (var player in rows.Where(p => p.DetailsJson != null))
        {
            using var doc = JsonDocument.Parse(player.DetailsJson!);
            var root = doc.RootElement;
            void Add(string key, double? value)
            {
                if (value.HasValue) metrics[key] = metrics.GetValueOrDefault(key) + value.Value;
            }
            var peak = OksiiStatsAdapter.OptionalNumber(OksiiStatsAdapter.Field(root, "player_speed"), "kph_peak");
            if (peak.HasValue) metrics["speed"] = Math.Max(metrics.GetValueOrDefault("speed"), peak.Value);
            Add("carry", OksiiStatsAdapter.OptionalNumber(OksiiStatsAdapter.Field(root, "stance_stats_seconds"), "in_objcarrier"));
            foreach (var key in new[] { "obj_planted", "obj_defused", "obj_secured", "obj_returned", "obj_destroyed",
                         "obj_repaired", "obj_taken", "obj_repickup", "obj_flagcaptured", "obj_flag_captured", "obj_misc" })
            {
                var entries = OksiiStatsAdapter.Field(root, key);
                if (entries.ValueKind == JsonValueKind.Object) Add("objectives", entries.EnumerateObject().Count());
            }
            var carriers = OksiiStatsAdapter.Field(root, "obj_carrierkilled");
            if (carriers.ValueKind == JsonValueKind.Object) Add("carriers", carriers.EnumerateObject().Count());
            var vehicle = OksiiStatsAdapter.Field(root, "obj_vehicle");
            Add("vehicledamage", OksiiStatsAdapter.OptionalNumber(OksiiStatsAdapter.Field(vehicle, "damage"), "damage"));
            Add("vehiclerepairs", OksiiStatsAdapter.OptionalNumber(vehicle, "repairs"));
            var escort = OksiiStatsAdapter.Field(vehicle, "escort");
            if (escort.ValueKind == JsonValueKind.Object)
                foreach (var item in escort.EnumerateObject()) Add("escort", OksiiStatsAdapter.OptionalNumber(item.Value, "time_s"));
            var count = OksiiStatsAdapter.OptionalNumber(root, "spawn_count");
            var total = OksiiStatsAdapter.OptionalNumber(root, "distance_travelled_spawn");
            var average = OksiiStatsAdapter.OptionalNumber(root, "distance_travelled_spawn_avg");
            if (count > 0 && (total.HasValue || average.HasValue))
            {
                spawnDistance += total ?? average!.Value * count.Value;
                spawns += count.Value;
            }
        }
        if (spawns > 0) metrics["spawndistance"] = spawnDistance / spawns;
    }

    private static void AddEventMetrics(List<MatchStatsPlayer> players, IReadOnlyList<MatchStatsSample> samples,
        IReadOnlyList<RoundEvent> events)
    {
        var byGuid = players.ToDictionary(p => p.Grid.Guid, StringComparer.OrdinalIgnoreCase);
        foreach (var round in samples.GroupBy(s => s.RoundId))
        {
            var roundEvents = events.Where(e => e.MatchRoundId == round.Key).OrderBy(e => e.Sequence).ToList();
            var teams = round.GroupBy(s => s.Player.Guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Player.Team, StringComparer.OrdinalIgnoreCase);
            var kills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var deaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var windows = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
            var shots = new List<(string Guid, long Time, int Weapon)>();
            var damage = new List<(string Guid, long Time, int Weapon)>();
            var spawnWaves = new Dictionary<int, List<long>>();
            var fatalEvents = new List<(string Guid, double Wait, bool Suicide, int Team)>();
            long pausedMs = 0;
            long? pausedAt = null;
            void Increment(string guid, string metric, double amount = 1)
            {
                if (byGuid.TryGetValue(guid, out var p)) p.Metrics[metric] = p.Metrics.GetValueOrDefault(metric) + amount;
            }
            void Peak(string guid, string metric, double value)
            {
                if (byGuid.TryGetValue(guid, out var p)) p.Metrics[metric] = Math.Max(p.Metrics.GetValueOrDefault(metric), value);
            }
            foreach (var ev in roundEvents)
            {
                if (ev.Label == "pause") { pausedAt ??= ev.LevelTime; continue; }
                if (ev.Label == "unpause")
                {
                    if (pausedAt.HasValue) pausedMs += Math.Max(0, ev.LevelTime - pausedAt.Value);
                    pausedAt = null;
                    continue;
                }
                var time = ev.LevelTime - pausedMs - (pausedAt.HasValue ? Math.Max(0, ev.LevelTime - pausedAt.Value) : 0);
                using var doc = JsonDocument.Parse(ev.DataJson);
                var root = doc.RootElement;
                string Text(string key) => OksiiStatsAdapter.Text(root, key);
                var actor = Text("player");
                var weapon = (int)OksiiStatsAdapter.Number(root, "weapon");
                if (ev.Label == "spawn")
                {
                    var team = (int)OksiiStatsAdapter.Number(root, "team");
                    if (team is 1 or 2)
                    {
                        teams[actor] = team;
                        if (!spawnWaves.TryGetValue(team, out var times)) spawnWaves[team] = times = [];
                        times.Add(time);
                    }
                }
                if (ev.Label == "weapon_fire")
                {
                    // weapon_fire uses WP_* constants; kills/damage use MOD_*.
                    if (weapon == 19) Increment(actor, "medpacks");
                    if (weapon == 12) Increment(actor, "ammopacks");
                    if (weapon is 5 or 53) shots.Add((actor, time, weapon));
                }
                if (ev.Label is "damage" or "obj_damage" or "vehicle_damage")
                {
                    var attacker = Text("attacker");
                    if (attacker.Length == 0) attacker = actor;
                    if (OksiiStatsAdapter.OptionalNumber(root, "damage") > 0)
                        damage.Add((attacker, time, weapon));
                }
                if (ev.Label is not ("kill" or "teamkill" or "suicide")) continue;
                var killer = Text("killer");
                var victim = ev.Label == "suicide" ? actor : Text("victim");
                if (ev.Label == "kill")
                {
                    kills[killer] = kills.GetValueOrDefault(killer) + 1;
                    deaths[killer] = 0;
                    Peak(killer, "killspree", kills[killer]);
                    if (!windows.TryGetValue(killer, out var window)) windows[killer] = window = new();
                    while (window.Count > 0 && time - window.Peek() > 3000) window.Dequeue();
                    window.Enqueue(time);
                    Peak(killer, "multikill", window.Count);
                    var wait = OksiiStatsAdapter.OptionalNumber(root, "victim_reinf");
                    if (teams.TryGetValue(killer, out var team) && team is 1 or 2
                        && wait >= (team == round.First().DefenderTeam ? 15 : 10)) Increment(killer, "impact");
                }
                kills[victim] = 0;
                if (windows.TryGetValue(victim, out var victimWindow)) victimWindow.Clear();
                deaths[victim] = deaths.GetValueOrDefault(victim) + 1;
                Peak(victim, "deathstreak", deaths[victim]);
                var remaining = OksiiStatsAdapter.OptionalNumber(root, "victim_reinf");
                if (remaining.HasValue && teams.TryGetValue(victim, out var victimTeam))
                    fatalEvents.Add((victim, remaining.Value, ev.Label == "suicide", victimTeam));
            }
            foreach (var shot in shots)
            {
                Increment(shot.Guid, "rocketshots");
                var next = shots.Where(s => s.Guid.Equals(shot.Guid, StringComparison.OrdinalIgnoreCase) && s.Time > shot.Time)
                    .Select(s => s.Time).DefaultIfEmpty(long.MaxValue).Min();
                if (!damage.Any(d => d.Guid.Equals(shot.Guid, StringComparison.OrdinalIgnoreCase)
                        && d.Time >= shot.Time && d.Time < next && d.Time - shot.Time <= 10000
                        && (d.Weapon == 0 || d.Weapon == (shot.Weapon == 5 ? 15 : 64))))
                    Increment(shot.Guid, "rocketmisses");
            }
            // Infer the actual wave from repeated spawn intervals instead of
            // assuming competitive defaults on servers with custom spawn times.
            foreach (var team in spawnWaves)
            {
                var times = team.Value.Distinct().Order().ToList();
                var intervals = times.Zip(times.Skip(1), (a, b) => Math.Round((b - a) / 1000d))
                    .Where(s => s >= 5).GroupBy(s => s).Where(g => g.Count() >= 2)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
                if (intervals.Count == 0) continue;
                var wave = intervals[0].Key;
                foreach (var ev in fatalEvents.Where(e => e.Team == team.Key && e.Wait >= wave - 2 && e.Wait <= wave))
                    Increment(ev.Guid, ev.Suicide ? "fullselfkills" : "fulldeaths");
            }
        }
    }

    public static string FormatDuration(double seconds)
    {
        var total = Math.Max(0, (long)Math.Round(seconds));
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }

    public static string WeaponName(int id) => id switch
    {
        0 => "Knife", 1 => "Ka-Bar", 2 => "Luger", 3 => "Colt", 4 => "MP 40", 5 => "Thompson", 6 => "Sten",
        7 => "FG 42", 8 => "Panzer", 9 => "Bazooka", 10 => "F.Thrower", 11 => "Grenade", 12 => "Mortar",
        13 => "Mortar (Axis)", 14 => "Dynamite", 15 => "Airstrike", 16 => "Artillery", 17 => "Satchel",
        18 => "G.Launchr", 19 => "Landmine", 20 => "MG 42 Gun", 21 => "Browning", 22 => "Garand",
        23 => "K43 Rifle", 24 => "Garand Scope", 25 => "K43 Scope", 26 => "MP 34", _ => $"Weapon {id}"
    };
    private static string ClassName(int id) => id switch
    { 0 => "Soldier", 1 => "Medic", 2 => "Engineer", 3 => "Fieldop", 4 => "Covert", _ => $"Class {id}" };
}

public static class MatchAwards
{
    public static List<MatchAward> Calculate(IReadOnlyList<MatchStatsPlayer> players)
    {
        var result = new List<MatchAward>();
        void Award(string name, string description, string metric, bool lowest = false, double minimum = double.Epsilon,
            double maximum = double.MaxValue, Func<MatchStatsPlayer, double, string>? format = null, bool weapon = false)
        {
            var candidates = players.Where(p => p.Metrics.TryGetValue(metric, out var v) && v >= minimum && v <= maximum);
            var rankings = (lowest ? candidates.OrderBy(p => p.Metrics[metric]) : candidates.OrderByDescending(p => p.Metrics[metric]))
                .ThenBy(p => p.Grid.Guid, StringComparer.Ordinal)
                .Select(p => new MatchAwardEntry(p.Grid.Guid, p.Grid.Name, p.Metrics[metric],
                    format?.Invoke(p, p.Metrics[metric]) ?? p.Metrics[metric].ToString("N0", CultureInfo.InvariantCulture))).ToList();
            if (rankings.Count > 0) result.Add(new(name, description, weapon, rankings));
        }
        static string Percent(MatchStatsPlayer _, double value) => value.ToString("N1", CultureInfo.InvariantCulture) + "%";
        static string Time(MatchStatsPlayer _, double value) => MatchStatistics.FormatDuration(value);
        Award("Top Fragger", "Most kills", "kills");
        Award("Killing Spree", "Most kills without dying", "killspree", minimum: 2);
        Award("Rampage", "Most kills within three seconds", "multikill", minimum: 2);
        Award("Queue Manager", "Long-spawn kills: 15s+ against attackers, 10s+ against defenders", "impact");
        Award("Are these pliers sharp?", "Most deaths without a kill", "deathstreak", minimum: 2);
        Award("Headhunter", "Most headshots", "headshots");
        Award("Needler", "Most revives", "revives");
        Award("XP King", "Most XP earned", "xp");
        Award("Pillow Fort", "Most medpacks thrown", "medpacks");
        Award("Walking Ammo Cabinet", "Most ammo packs thrown", "ammopacks");
        Award("Objective Hero", "Most objective contributions", "objectives");
        Award("Payload Princess", "Longest vehicle escort time", "escort", format: Time);
        Award("Panzerschreck", "Most damage to vehicles", "vehicledamage");
        Award("Duct Tape & Prayers", "Most vehicle repairs", "vehiclerepairs");
        Award("ObjWhore", "Longest time carrying an objective", "carry", format: Time);
        Award("Kill the Messenger", "Most objective carriers killed", "carriers");
        Award("Damage Dealer", "Most damage given", "damage");
        Award("Tank", "Most damage absorbed", "received");
        Award("Marathon Runner", "Longest distance travelled", "distance", format: (_, v) => v.ToString("N0", CultureInfo.InvariantCulture) + " m");
        Award("Speed Demon", "Highest peak speed", "speed", format: (_, v) => v.ToString("N1", CultureInfo.InvariantCulture) + " km/h");
        Award("Best KDR", "Best kill/death ratio", "kdr", format: (_, v) => v.ToString("0.00", CultureInfo.InvariantCulture));
        Award("iPod", "Fewest deaths", "deaths", lowest: true, minimum: 0);
        Award("Gibber", "Most gibs", "gibs");
        Award("Spammer", "Most kills with explosive or flame weapons", "spam");
        Award("Mayan", "Lowest rifle grenade accuracy below 50%", "rifleaccuracy", lowest: true, minimum: 0, maximum: 49.999999, format: Percent);
        Award("Kamikaze", "Most self kills", "selfkills");
        Award("Rocket Surgeon", "Most rockets with no recorded damage before the next shot (up to 10s)", "rocketmisses",
            format: (p, v) => $"{v:N0} of {p.Metrics.GetValueOrDefault("rocketshots"):N0}");
        Award("Permanent Limbo Resident", "Most deaths within two seconds of a spawn wave", "fulldeaths");
        Award("Fentanyl", "Most self kills within two seconds of a spawn wave", "fullselfkills");
        Award("Teamkiller", "Most team kills", "teamkills");
        Award("Friendly Fire", "Most team damage", "teamdamage");
        Award("Cannon Fodder", "Most deaths", "deaths");
        Award("Frontliner", "Most time engaged in combat or objectives", "engaged", format: (p, v) =>
            p.Metrics.GetValueOrDefault("alive") > 0 ? $"{MatchStatistics.FormatDuration(v)} ({100 * v / p.Metrics["alive"]:N0}%)" : MatchStatistics.FormatDuration(v));
        Award("Playtime", "Highest time played", "playtime", minimum: 0, format: Percent);
        Award("Spectator", "Lowest time played (70% or less)", "playtime", lowest: true, minimum: 0, maximum: 70, format: Percent);
        Award("Coma", "Least average movement after spawning", "spawndistance", lowest: true, minimum: 0,
            format: (_, v) => v.ToString("N1", CultureInfo.InvariantCulture) + " m");
        foreach (var id in players.SelectMany(p => p.Grid.WeaponStats).Where(w => w.Weapon != 27).Select(w => w.Weapon).Distinct().Order())
            Award(MatchStatistics.WeaponName(id), "Most kills", "weapon:" + id, weapon: true);
        return result;
    }
}
