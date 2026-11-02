using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

// Kick-off, status, scores and team logos for North American leagues from a public scoreboard, polled only while the guide shows them.
public static class EspnService
{
    internal sealed record League(string Key, string Path, string Sport, string Name, bool College, long TypicalMs);

    private static readonly League[] All =
    [
        new("nhl", "hockey/nhl", "Ice hockey", "NHL", false, 150 * 60_000),
        new("nba", "basketball/nba", "Basketball", "NBA", false, 140 * 60_000),
        new("wnba", "basketball/wnba", "Basketball", "WNBA", false, 120 * 60_000),
        new("college basketball", "basketball/mens-college-basketball", "Basketball", "College basketball", true, 130 * 60_000),
        new("nfl", "football/nfl", "American football", "NFL", false, 190 * 60_000),
        new("college football", "football/college-football", "American football", "College football", true, 200 * 60_000),
        new("mlb", "baseball/mlb", "Baseball", "MLB", false, 180 * 60_000),
    ];

    internal sealed class Game
    {
        public ExternalMatch M = new();
        public League League = null!;
        public ProgramTitle.TeamKey[] HomeKeys = [];
        public ProgramTitle.TeamKey[] AwayKeys = [];
        public string? Venue;
    }

    private static readonly ConcurrentDictionary<string, List<Game>> Games = new();
    private static readonly ConcurrentDictionary<string, long> Wanted = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> NextPoll = new();
    private static readonly ConcurrentDictionary<string, long> FinishedSeen = new();
    private static readonly ConcurrentDictionary<string, byte> SeenLive = new();
    private static volatile HashSet<string> _teamNames = new();
    private static readonly string Dir = Path.Combine(Storage.CacheRoot, "matchdata", "espn");
    private static readonly TimeZoneInfo Eastern = FindZone();
    private static readonly long WantFor = (long)TimeSpan.FromHours(6).TotalMilliseconds;
    private static int _started;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        LoadSnapshots();
        _ = Task.Run(Loop);
    }

    // The scoreboard league for a guide event, when there is one.
    internal static string? LeagueFor(string? leagueKey, string? sport, bool hasTeams, bool northAmerica)
    {
        if (leagueKey != null) return All.Any(l => l.Key == leagueKey) ? leagueKey : null;
        if (!hasTeams) return null;
        return sport switch
        {
            "American football" => "nfl",
            "Baseball" => "mlb",
            "Ice hockey" when northAmerica => "nhl",
            "Basketball" when northAmerica => "nba",
            _ => null
        };
    }

    public static void Want(string leagueKey) => Wanted[leagueKey] = Now;

    internal static bool Covers(string? sport) => sport != null && All.Any(l => l.Sport == sport);

    public static string? NameOf(string league) =>
        league.StartsWith("espn:", StringComparison.Ordinal) ? All.FirstOrDefault(l => "espn:" + l.Key == league)?.Name : null;

    // Whether a name is a team the scoreboard lists today or tomorrow ("Bruins", "Boston Bruins").
    internal static bool IsTeam(ProgramTitle.TeamKey key) => key.Core.Count > 0 && _teamNames.Contains(Id(key));

    private static string Id(ProgramTitle.TeamKey key) => string.Join(' ', key.Core.OrderBy(x => x, StringComparer.Ordinal));

    // The game for a guide matchup; both teams must match one of the names the scoreboard uses (full, nickname, school).
    internal static (Game game, bool swapped)? Find(IReadOnlyList<ProgramTitle.TeamKey> teams, long start, string? sport)
    {
        if (teams.Count != 2) return null;
        Game? best = null;
        double bestScore = 0;
        bool bestSwapped = false;
        foreach (var list in Games.Values)
            foreach (var g in list)
            {
                if (sport != null && g.League.Sport != sport) continue;
                if (Math.Abs(g.M.Start - start) > 3 * 3_600_000) continue;
                double straight = Math.Min(Sim(g.HomeKeys, teams[0]), Sim(g.AwayKeys, teams[1]));
                double reversed = Math.Min(Sim(g.HomeKeys, teams[1]), Sim(g.AwayKeys, teams[0]));
                double s = Math.Max(straight, reversed);
                if (s > bestScore) { bestScore = s; best = g; bestSwapped = reversed > straight; }
            }
        // Without a known sport only a clear match counts.
        return best != null && bestScore >= (sport == null ? 0.5 : 0.3) ? (best, bestSwapped) : null;
    }

    // "From Amerant Bank Arena in Sunrise, Fla." names no teams, but only one game is played there that day.
    internal static Game? AtVenue(string? description, long start, string? sport)
    {
        if (string.IsNullOrEmpty(description)) return null;
        foreach (var list in Games.Values)
            foreach (var g in list)
                if (g.Venue != null && (sport == null || g.League.Sport == sport) && Math.Abs(g.M.Start - start) <= 3 * 3_600_000 &&
                    description.Contains(g.Venue, StringComparison.OrdinalIgnoreCase))
                    return g;
        return null;
    }

    private static double Sim(ProgramTitle.TeamKey[] keys, ProgramTitle.TeamKey t)
    {
        double best = 0;
        foreach (var k in keys) best = Math.Max(best, ProgramTitle.TeamSimilarity(k, t));
        return best;
    }

    private static string[] Dates()
    {
        var et = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Eastern);
        var days = new List<DateTimeOffset>();
        // Late games of the previous US evening are still on in the European morning.
        if (et.Hour < 6) days.Add(et.AddDays(-1));
        days.Add(et);
        days.Add(et.AddDays(1));
        return days.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).ToArray();
    }

    private static async Task Loop()
    {
        while (true)
        {
            try
            {
                if (MatchDataService.Enabled)
                {
                    bool changed = false;
                    long now = Now;
                    var dates = Dates();
                    foreach (var lg in All)
                    {
                        if (!Wanted.TryGetValue(lg.Key, out var at) || now - at > WantFor) continue;
                        foreach (var date in dates)
                        {
                            string id = lg.Key + "|" + date;
                            if (NextPoll.TryGetValue(id, out var due) && DateTimeOffset.UtcNow < due) continue;
                            changed |= await Fetch(lg, date);
                        }
                    }
                    foreach (var id in Games.Keys)
                        if (!dates.Contains(id[(id.IndexOf('|') + 1)..])) { Games.TryRemove(id, out _); changed = true; }
                    foreach (var id in NextPoll.Keys)
                        if (!dates.Contains(id[(id.IndexOf('|') + 1)..])) NextPoll.TryRemove(id, out _);
                    // Only today's games need these; a long-running app should not keep every id it ever saw.
                    if (FinishedSeen.Count > 5000) FinishedSeen.Clear();
                    if (SeenLive.Count > 5000) SeenLive.Clear();
                    if (changed)
                    {
                        RebuildNames();
                        _ = MessageHub.Send("matchDataUpdated", new { });
                        FeedCache.Invalidate();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Scoreboard loop: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task<bool> Fetch(League lg, string date)
    {
        string id = lg.Key + "|" + date;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // The API wants YYYY-MM-DD; our cache keys keep the compact form.
            string iso = date.Insert(4, "-").Insert(7, "-");
            var body = await MatchDataService.GetBytes($"{SpanaApi.Base}/scoreboard?league={Uri.EscapeDataString(lg.Path)}&date={iso}", cts.Token);
            var games = Parse(body, lg);
            Games[id] = games;
            Save(lg, date, body);
            long now = Now;
            bool active = games.Any(g => g.M.Live || (g.M.Start > now && g.M.Start - now < 15 * 60_000));
            NextPoll[id] = DateTimeOffset.UtcNow.Add(active ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
            Log.Information("Scoreboard {League} {Date}: {Count} games", lg.Name, date, games.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("Scoreboard {League} fetch failed: {Message}", lg.Name, ex.Message);
            NextPoll[id] = DateTimeOffset.UtcNow.AddMinutes(10);
            return false;
        }
    }

    internal static List<Game> Parse(byte[] body, League lg)
    {
        var list = new List<Game>();
        using var doc = JsonDocument.Parse(body);
        long now = Now;
        foreach (var e in (doc.RootElement.Prop("events") ?? default).Items())
        {
            var comp = (e.Prop("competitions") ?? default).Items().FirstOrDefault();
            if (comp.ValueKind != JsonValueKind.Object) continue;
            if (!DateTimeOffset.TryParse(e.Str("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)) continue;
            var type = e.Prop("status")?.Prop("type");
            string state = type?.Str("state") ?? "pre";
            if (type?.Str("name") is "STATUS_POSTPONED" or "STATUS_CANCELED" or "STATUS_SUSPENDED") continue;
            JsonElement home = default, away = default;
            foreach (var c in (comp.Prop("competitors") ?? default).Items())
                if (c.Str("homeAway") == "home") home = c; else away = c;
            var ht = home.Prop("team");
            var at = away.Prop("team");
            if (ht == null || at == null) continue;

            string? matchId = e.Str("id");
            long start = utc.ToUnixTimeMilliseconds();
            bool started = state is "in" or "post";
            long? finishedAt = null;
            if (matchId != null)
            {
                if (state == "in") SeenLive.TryAdd(matchId, 0);
                if (state == "post")
                    finishedAt = FinishedSeen.GetOrAdd(matchId, _ => SeenLive.ContainsKey(matchId) ? now : Math.Min(now, start + lg.TypicalMs));
            }
            string hn = ht.Value.Str("displayName") ?? "", an = at.Value.Str("displayName") ?? "";
            var m = new ExternalMatch
            {
                Source = "espn",
                MatchId = matchId,
                HomeName = hn,
                AwayName = an,
                HomeId = ht.Value.Str("id") is { } hid ? $"espn:{lg.Key}:{hid}" : null,
                AwayId = at.Value.Str("id") is { } aid ? $"espn:{lg.Key}:{aid}" : null,
                Home = ProgramTitle.TeamKey.From(hn),
                Away = ProgramTitle.TeamKey.From(an),
                Start = start,
                End = start + lg.TypicalMs,
                Kickoff = start,
                Sport = lg.Sport,
                Tournament = lg.Name,
                LeagueName = lg.Name,
                League = "espn:" + lg.Key,
                Country = "USA",
                HomeLogo = ht.Value.Str("logo"),
                AwayLogo = at.Value.Str("logo"),
                HomeScore = started && int.TryParse(home.Str("score"), out var hs) ? hs : null,
                AwayScore = started && int.TryParse(away.Str("score"), out var aws) ? aws : null,
                Live = state == "in",
                Finished = state == "post",
                FinishedAt = finishedAt,
                Status = started ? type?.Str("shortDetail") : null
            };
            list.Add(new Game
            {
                M = m,
                League = lg,
                HomeKeys = Keys(ht.Value, lg),
                AwayKeys = Keys(at.Value, lg),
                Venue = comp.Prop("venue")?.Str("fullName")
            });
        }
        return list;
    }

    // Pro nicknames are unique in their league ("Rangers"); college ones are not ("Wildcats"), so schools go by name.
    private static ProgramTitle.TeamKey[] Keys(JsonElement team, League lg)
    {
        var names = new List<string?> { team.Str("displayName") };
        names.Add(lg.College ? team.Str("location") ?? team.Str("shortDisplayName") : team.Str("name"));
        return names.Where(n => !string.IsNullOrEmpty(n)).Select(n => ProgramTitle.TeamKey.From(n!)).Where(k => k.Core.Count > 0).ToArray();
    }

    private static void RebuildNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in Games.Values)
            foreach (var g in list)
                foreach (var k in g.HomeKeys.Concat(g.AwayKeys))
                    set.Add(Id(k));
        _teamNames = set;
    }

    private static string FileFor(League lg, string date) => Path.Combine(Dir, $"{lg.Key.Replace(' ', '-')}-{date}.json");

    private static void Save(League lg, string date, byte[] body)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string file = FileFor(lg, date);
            File.WriteAllBytes(file + ".tmp", body);
            File.Move(file + ".tmp", file, true);
            var keep = All.SelectMany(l => Dates().Select(d => FileFor(l, d))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in Directory.EnumerateFiles(Dir, "*.json"))
                if (!keep.Contains(old)) File.Delete(old);
        }
        catch (Exception ex)
        {
            Log.Debug("Could not save scoreboard snapshot: {Message}", ex.Message);
        }
    }

    private static void LoadSnapshots()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            int count = 0;
            foreach (var lg in All)
                foreach (var date in Dates())
                {
                    string file = FileFor(lg, date);
                    if (!File.Exists(file)) continue;
                    var games = Parse(File.ReadAllBytes(file), lg);
                    Games[lg.Key + "|" + date] = games;
                    // Seen in the guide before the restart: keep it fresh for a while without waiting for the Sport page.
                    Wanted.TryAdd(lg.Key, Now - WantFor + (long)TimeSpan.FromMinutes(30).TotalMilliseconds);
                    count += games.Count;
                }
            RebuildNames();
            if (count > 0) Log.Information("Scoreboard games restored from disk: {Count}", count);
        }
        catch (Exception ex)
        {
            Log.Warning("Could not restore scoreboard data: {Message}", ex.Message);
        }
    }
}
