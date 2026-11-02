using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Serilog;
using Spana.Backend.App;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class MatchInfo
{
    public string? Image { get; set; }
    public string? Tournament { get; set; }
    public string? Description { get; set; }
    public string? HomeLogo { get; set; }
    public string? AwayLogo { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string? Status { get; set; }
    public bool Live { get; set; }
    public bool Finished { get; set; }
    public long? FinishedAt { get; set; }
    public long? Kickoff { get; set; }
    public string? Broadcaster { get; set; }
    public string? League { get; set; }
    public string? Country { get; set; }
    internal string? Sport { get; set; }
}

public sealed class TeamLeague
{
    public string League { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? Country { get; set; }
    public string? TeamId { get; set; }
    public string? Sport { get; set; }
    public DateTimeOffset SeenAt { get; set; }
}

// League and team identity for a programme. League is a canonical id such as "fm:67" (Allsvenskan).
// NotOnFotMob means the scores source covers that day and has no such match, so clubs with these names are not playing.
public readonly record struct MatchContext(string? League, string? LeagueName, string? Country, string? Sport, string? HomeId, string? AwayId,
    bool NotOnFotMob = false);

internal sealed class ExternalMatch
{
    public string Source = "";
    public ProgramTitle.TeamKey Home = new();
    public ProgramTitle.TeamKey Away = new();
    public string HomeName = "";
    public string AwayName = "";
    public string? HomeId;
    public string? AwayId;
    public long Start;
    public long End;
    public long? Kickoff;
    public string? Sport;
    public string? Image;
    public string? Tournament;
    public string? Description;
    public string? HomeLogo;
    public string? AwayLogo;
    public int? HomeScore;
    public int? AwayScore;
    public string? Status;
    public bool Live;
    public bool Finished;
    public long? FinishedAt;
    public string? MatchId;
    public bool Womens;
    public string? Channel;
    public string? ChannelLogo;
    public string? ChannelSlug;
    public string? Title;
    public string? League;
    public string? LeagueName;
    public string? Country;
}

// Matches guide programmes to the score and schedule records and uses their ids for teams and leagues.
// Names are only a fallback, so "Hammarby IF" and "Hammarby IF (W)", or Allsvenskan and Damallsvenskan, never mix.
public static class MatchDataService
{
    private static readonly TimeSpan MatchWindow = TimeSpan.FromHours(3);
    private static readonly TimeZoneInfo Stockholm = FindZone();

    private static volatile List<ExternalMatch> _tvnu = new();
    private static volatile List<ExternalMatch> _fotmob = new();
    private static volatile HashSet<string> _fotmobDates = new();

    // Raw responses for today and tomorrow are kept on disk so a restart starts with full match data.
    private static readonly string SnapshotDir = Path.Combine(Storage.CacheRoot, "matchdata");
    private static readonly TaskCompletionSource TvNuReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource FotMobReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Done when both sources have data (from disk or network) or have failed once.
    public static Task Ready => Enabled ? Task.WhenAll(TvNuReady.Task, FotMobReady.Task) : Task.CompletedTask;

    // When each match was first seen finished, so a final score stays relevant for a while after the whistle.
    private static readonly ConcurrentDictionary<string, long> FinishedSeen = new();
    private static readonly ConcurrentDictionary<string, byte> SeenLive = new();
    private const long TypicalMatchMs = 115 * 60_000;
    private static volatile Dictionary<string, string> _channelLogos = new();
    private static readonly ConcurrentDictionary<string, string> ChannelSlugs = new();

    // The source's own id for a channel ("tv4-fotboll"), learned from its channel list and sport broadcasts.
    public static string? TvNuChannelSlug(string channelKey) => ChannelSlugs.GetValueOrDefault(channelKey);
    private static int _started;
    private static DateTimeOffset _nextFotmob = DateTimeOffset.MinValue;

    private static readonly ConcurrentDictionary<string, string?> Badges = new();
    private static readonly ConcurrentQueue<string> BadgeQueue = new();
    private static readonly ConcurrentDictionary<string, byte> BadgeQueued = new();
    private static readonly string BadgeFile = Path.Combine(Storage.CacheRoot, "team-badges.json");

    private static readonly ConcurrentDictionary<string, TeamLeague> TeamLeagues = new();
    private static readonly ConcurrentDictionary<string, string> LeagueCountries = new();
    private static readonly ConcurrentDictionary<string, string> LeagueNames = new();
    private static readonly ConcurrentDictionary<string, string> LeagueAliases = new();
    private static readonly ConcurrentDictionary<string, string> LeaguesByName = new();
    private static readonly string LeagueFile = Path.Combine(Storage.CacheRoot, "leagues-v2.json");

    private sealed class LeagueCache
    {
        public Dictionary<string, TeamLeague> Teams { get; set; } = new();
        public Dictionary<string, string> Countries { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new();
        public Dictionary<string, string> Aliases { get; set; } = new();
        public Dictionary<string, string> ByName { get; set; } = new();
    }

    public static bool Enabled => SettingsService.Current.MatchData;

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); }
        catch { return TimeZoneInfo.Local; }
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        LoadBadges();
        LoadLeagues();
        LoadSnapshots();
        _ = Task.Run(TvNuLoop);
        _ = Task.Run(ChannelLogoLoop);
        _ = Task.Run(FotMobLoop);
        _ = Task.Run(BadgeLoop);
        EspnService.Start();
    }

    internal static IReadOnlyList<ExternalMatch> TvNuMatches => _tvnu;

    private static string NameId(ProgramTitle.TeamKey t, bool womens) =>
        string.Join(' ', t.Core.OrderBy(x => x, StringComparer.Ordinal)) + (womens ? "|w" : "");

    private static string NameKey(string? sport, string? league) =>
        $"{Leagues.Slug(sport)}:{Leagues.Key(league)}";

    private static bool LooksWomens(string? text) =>
        text != null && (text.Contains("(W)", StringComparison.Ordinal) || text.Contains("(D)", StringComparison.Ordinal) ||
                         CatalogService.Normalize(text) is var n && (n.StartsWith("dam") || n.Contains(" dam") || n.Contains("women") ||
                         n.Contains("kvinn") || n.Contains("frauen") || n.Contains("feminin") || n.Contains("toppserien") || n.Contains("dff")));

    private static string Today(int offsetDays) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Stockholm).AddDays(offsetDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await XtreamClient.Client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await res.Content.ReadAsByteArrayAsync(ct));
    }

    private static async Task TvNuLoop()
    {
        while (true)
        {
            if (Enabled)
            {
                try
                {
                    var list = new List<ExternalMatch>();
                    var pages = new List<(string date, int page, byte[] body)>();
                    foreach (var date in new[] { Today(0), Today(1) })
                    {
                        for (int page = 1; page <= 10; page++)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            var body = await GetBytes($"{SpanaApi.Base}/matches?date={date}&page={page}", cts.Token);
                            pages.Add((date, page, body));
                            if (!ParseTvNuPage(body, list)) break;
                        }
                    }
                    _tvnu = list;
                    foreach (var m in list)
                        if (m.Channel != null && m.ChannelSlug != null) ChannelSlugs[SportService.ChannelKey(m.Channel)] = m.ChannelSlug;
                    SaveSnapshot("tvnu", pages.Select(p => ($"{p.date}-{p.page}", p.body)));
                    var logos = new Dictionary<string, string>(_channelLogos, StringComparer.Ordinal);
                    foreach (var m in list)
                        if (m.Channel != null && m.ChannelLogo != null) logos.TryAdd(SportService.ChannelKey(m.Channel), SpanaApi.Image(m.ChannelLogo + "?width=200&quality=80"));
                    _channelLogos = logos;
                    Learn();
                    Log.Information("sport schedule: {Count} events", list.Count);
                    TvNuReady.TrySetResult();
                    _ = MessageHub.Send("matchDataUpdated", new { });
                    FeedCache.Invalidate();
                }
                catch (Exception ex)
                {
                    Log.Warning("schedule fetch failed: {Message}", ex.Message);
                    TvNuReady.TrySetResult();
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(30));
        }
    }

    // Clean logos for Swedish channels come from the guide source, used when the provider's own logo link is missing or dead.
    private static async Task ChannelLogoLoop()
    {
        while (true)
        {
            if (Enabled)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var doc = await GetJson($"{SpanaApi.Base}/channels", cts.Token);
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var c in (doc.RootElement.Prop("data") ?? default).Items())
                    {
                        string? name = c.Str("name");
                        var logo = c.Prop("themedLogo");
                        string? url = logo?.Prop("dark")?.Str("url") ?? logo?.Prop("light")?.Str("url");
                        if (name != null && url != null) map[SportService.ChannelKey(name)] = SpanaApi.Image(url + "?width=200&quality=80");
                        if (name != null && c.Str("slug") is { } slug) ChannelSlugs[SportService.ChannelKey(name)] = slug;
                    }
                    foreach (var (k, v) in _channelLogos) map.TryAdd(k, v);
                    _channelLogos = map;
                    Log.Information("channel logos from the guide: {Count}", map.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning("channel logos fetch failed: {Message}", ex.Message);
                }
            }
            await Task.Delay(TimeSpan.FromHours(24));
        }
    }

    // Attaches the guide logos to the channels it knows; they are made for dark backgrounds and never dead links.
    public static void ApplyLogos(IEnumerable<CatalogItem> items)
    {
        var logos = _channelLogos;
        if (logos.Count == 0) return;
        foreach (var item in items)
            if (item.IconAlt == null && OnTvNu(item) && logos.TryGetValue(SportService.ChannelKey(item.Name), out var url)) item.IconAlt = url;
    }

    // The channel list is Swedish; Danish "TV3" shares the name but not the schedule.
    public static bool OnTvNu(CatalogItem item) => item.Country is null or "se";

    internal static async Task<byte[]> GetBytes(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var res = await XtreamClient.Client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    // Returns whether the schedule has another page.
    private static bool ParseTvNuPage(byte[] body, List<ExternalMatch> into)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        foreach (var b in (root.Prop("data")?.Prop("broadcasts") ?? default).Items())
            if (ParseTvNu(b) is { } m) into.Add(m);
        return root.Prop("meta")?.Prop("pagination")?.Prop("hasNext")?.ValueKind == JsonValueKind.True;
    }

    private static void ParseFotMobDay(byte[] body, List<ExternalMatch> into)
    {
        using var doc = JsonDocument.Parse(body);
        foreach (var league in (doc.RootElement.Prop("leagues") ?? default).Items())
        {
            string? leagueName = league.Str("name");
            string? ccode = league.Str("ccode");
            string? leagueId = league.Str("primaryId") ?? league.Str("id");
            foreach (var match in (league.Prop("matches") ?? default).Items())
                if (ParseFotMob(match, leagueName, ccode, leagueId) is { } m) into.Add(m);
        }
    }

    private static void SaveSnapshot(string source, IEnumerable<(string name, byte[] body)> parts)
    {
        try
        {
            string dir = Path.Combine(SnapshotDir, source);
            Directory.CreateDirectory(dir);
            var keep = new HashSet<string>();
            foreach (var (name, body) in parts)
            {
                string file = Path.Combine(dir, name + ".json");
                File.WriteAllBytes(file + ".tmp", body);
                File.Move(file + ".tmp", file, true);
                keep.Add(file);
            }
            foreach (var old in Directory.EnumerateFiles(dir, "*.json"))
                if (!keep.Contains(old)) File.Delete(old);
        }
        catch (Exception ex)
        {
            Log.Debug("Could not save {Source} snapshot: {Message}", source, ex.Message);
        }
    }

    private static void LoadSnapshots()
    {
        var dates = new[] { Today(0), Today(1) };
        try
        {
            string tvDir = Path.Combine(SnapshotDir, "tvnu");
            if (Directory.Exists(tvDir))
            {
                var list = new List<ExternalMatch>();
                foreach (var file in Directory.EnumerateFiles(tvDir, "*.json").Order())
                    if (dates.Any(d => Path.GetFileName(file).StartsWith(d))) ParseTvNuPage(File.ReadAllBytes(file), list);
                if (list.Count > 0)
                {
                    _tvnu = list;
                    foreach (var m in list)
                        if (m.Channel != null && m.ChannelSlug != null) ChannelSlugs[SportService.ChannelKey(m.Channel)] = m.ChannelSlug;
                    foreach (var m in list)
                        if (m.Channel != null && m.ChannelLogo != null)
                            _channelLogos.TryAdd(SportService.ChannelKey(m.Channel), SpanaApi.Image(m.ChannelLogo + "?width=200&quality=80"));
                    TvNuReady.TrySetResult();
                }
            }
            string fmDir = Path.Combine(SnapshotDir, "fotmob");
            if (Directory.Exists(fmDir))
            {
                var list = new List<ExternalMatch>();
                var loaded = new HashSet<string>();
                foreach (var date in dates)
                {
                    string file = Path.Combine(fmDir, date + ".json");
                    if (!File.Exists(file)) continue;
                    ParseFotMobDay(File.ReadAllBytes(file), list);
                    loaded.Add(date);
                }
                if (list.Count > 0)
                {
                    _fotmob = list;
                    _fotmobDates = loaded;
                    FotMobReady.TrySetResult();
                }
            }
            if (_tvnu.Count > 0 || _fotmob.Count > 0)
            {
                Learn();
                Log.Information("Match data restored from disk: {TvNu} schedule events, {FotMob} scored events", _tvnu.Count, _fotmob.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Could not restore match data: {Message}", ex.Message);
        }
    }

    private static ExternalMatch? ParseTvNu(JsonElement b)
    {
        var bc = (b.Prop("broadcasts") ?? default).Items().FirstOrDefault();
        long start = bc.ValueKind == JsonValueKind.Object ? bc.Long("startTime") : b.Long("eventTime");
        long end = bc.ValueKind == JsonValueKind.Object ? bc.Long("endTime") : start + 2 * 3_600_000;
        if (start == 0) return null;
        string? t1 = b.Prop("team1")?.Str("name"), t2 = b.Prop("team2")?.Str("name");
        if (t1 != null) t1 = ProgramTitle.StripEvent(t1);
        if (t2 != null) t2 = ProgramTitle.StripEvent(t2);
        string? image = b.Str("imageLandscape") ?? b.Str("imagePortrait");
        string? sport = SportService.FromSwedish(b.Str("sport"));
        string? tournament = b.Str("tournament");
        string? slug = b.Str("tournamentSlug") ?? Leagues.Key(tournament);
        var m = new ExternalMatch
        {
            Source = "tvnu",
            Title = b.Str("title"),
            Start = start,
            End = end,
            Kickoff = b.Long("eventTime") is > 0 and var kick ? kick : null,
            Sport = sport,
            Image = image != null ? SpanaApi.Image(image + "?width=640&height=360&forceFit=1&quality=70") : null,
            Tournament = b.Str("subtitle") ?? tournament,
            Description = b.Str("description"),
            Channel = bc.ValueKind == JsonValueKind.Object ? bc.Prop("channel")?.Str("name") : null,
            ChannelSlug = bc.ValueKind == JsonValueKind.Object ? bc.Prop("channel")?.Str("slug") : null,
            ChannelLogo = bc.ValueKind == JsonValueKind.Object
                ? bc.Prop("channel")?.Prop("themedLogo")?.Prop("dark")?.Str("url") ?? bc.Prop("channel")?.Prop("themedLogo")?.Prop("light")?.Str("url")
                : null,
            LeagueName = tournament,
            League = slug != null ? $"tv:{Leagues.Slug(sport)}:{slug}" : null,
            Womens = LooksWomens(tournament) || LooksWomens(b.Str("subtitle"))
        };
        if (t1 != null && t2 != null)
        {
            m.HomeName = t1;
            m.AwayName = t2;
            m.Home = ProgramTitle.TeamKey.From(t1);
            m.Away = ProgramTitle.TeamKey.From(t2);
        }
        return m;
    }

    private static async Task FotMobLoop()
    {
        while (true)
        {
            if (Enabled && DateTimeOffset.UtcNow >= _nextFotmob)
            {
                try
                {
                    var list = new List<ExternalMatch>();
                    var days = new List<(string date, byte[] body)>();
                    foreach (var date in new[] { Today(0), Today(1) })
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var body = await GetBytes($"{SpanaApi.Base}/scores?date={date}", cts.Token);
                        days.Add((date, body));
                        ParseFotMobDay(body, list);
                    }
                    _fotmob = list;
                    _fotmobDates = new HashSet<string> { Today(0), Today(1) };
                    SaveSnapshot("fotmob", days.Select(d => (d.date, d.body)));
                    FotMobReady.TrySetResult();
                    Learn();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    bool active = list.Any(m => m.Live || (m.Start > now && m.Start - now < 15 * 60_000));
                    _nextFotmob = DateTimeOffset.UtcNow.Add(active ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10));
                    _ = MessageHub.Send("matchDataUpdated", new { });
                    FeedCache.Invalidate();
                }
                catch (Exception ex)
                {
                    Log.Warning("scores fetch failed: {Message}", ex.Message);
                    FotMobReady.TrySetResult();
                    _nextFotmob = DateTimeOffset.UtcNow.AddMinutes(5);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    private static ExternalMatch? ParseFotMob(JsonElement m, string? league, string? ccode, string? leagueId)
    {
        var home = m.Prop("home");
        var away = m.Prop("away");
        var status = m.Prop("status");
        if (home == null || away == null || status == null) return null;
        if (!DateTimeOffset.TryParse(status.Value.Str("utcTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc)) return null;
        bool started = status.Value.Bool("started");
        bool finished = status.Value.Bool("finished");
        if (status.Value.Bool("cancelled")) return null;
        string? minute = status.Value.Prop("liveTime")?.Str("short")?.Replace("‎", "");
        string? reason = status.Value.Prop("reason")?.Str("short");
        string hn = home.Value.Str("longName") ?? home.Value.Str("name") ?? "";
        string an = away.Value.Str("longName") ?? away.Value.Str("name") ?? "";
        string? hid = home.Value.Str("id"), aid = away.Value.Str("id");
        string? matchId = m.Str("id");
        long startMs = utc.ToUnixTimeMilliseconds();
        long? finishedAt = null;
        if (matchId != null)
        {
            if (started && !finished) SeenLive.TryAdd(matchId, 0);
            if (finished)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Seen live before: this poll is the end, give or take a minute. Otherwise estimate from kick-off.
                finishedAt = FinishedSeen.GetOrAdd(matchId, _ => SeenLive.ContainsKey(matchId) ? now : Math.Min(now, startMs + TypicalMatchMs));
            }
        }
        return new ExternalMatch
        {
            MatchId = matchId,
            FinishedAt = finishedAt,
            Source = "fotmob",
            HomeName = hn,
            AwayName = an,
            HomeId = hid != null ? "fm:" + hid : null,
            AwayId = aid != null ? "fm:" + aid : null,
            Home = ProgramTitle.TeamKey.From(hn),
            Away = ProgramTitle.TeamKey.From(an),
            Start = utc.ToUnixTimeMilliseconds(),
            End = utc.ToUnixTimeMilliseconds() + 2 * 3_600_000,
            Kickoff = utc.ToUnixTimeMilliseconds(),
            Sport = "Football",
            Tournament = league,
            LeagueName = league,
            League = leagueId != null ? "fm:" + leagueId : null,
            Country = ccode,
            Womens = LooksWomens(hn) || LooksWomens(league),
            HomeLogo = hid != null ? SpanaApi.Image($"https://images.fotmob.com/image_resources/logo/teamlogo/{hid}.png") : null,
            AwayLogo = aid != null ? SpanaApi.Image($"https://images.fotmob.com/image_resources/logo/teamlogo/{aid}.png") : null,
            HomeScore = started ? (int)home.Value.Long("score") : null,
            AwayScore = started ? (int)away.Value.Long("score") : null,
            Live = started && !finished,
            Finished = finished,
            Status = finished ? (reason ?? "FT") : started ? minute : null
        };
    }

    private static double PairScore(ExternalMatch m, ProgramTitle.TeamKey a, ProgramTitle.TeamKey b, out bool swapped)
    {
        double straight = Math.Min(ProgramTitle.TeamSimilarity(m.Home, a), ProgramTitle.TeamSimilarity(m.Away, b));
        double reversed = Math.Min(ProgramTitle.TeamSimilarity(m.Home, b), ProgramTitle.TeamSimilarity(m.Away, a));
        swapped = reversed > straight;
        return Math.Max(straight, reversed);
    }

    internal static ExternalMatch? FindIn(IReadOnlyList<ExternalMatch> list, IReadOnlyList<ProgramTitle.TeamKey> teams, long start) =>
        FindIn(list, teams, start, out _);

    internal static ExternalMatch? FindIn(IReadOnlyList<ExternalMatch> list, IReadOnlyList<ProgramTitle.TeamKey> teams, long start, out bool swapped)
    {
        swapped = false;
        if (teams.Count != 2) return null;
        ExternalMatch? best = null;
        double bestScore = 0;
        foreach (var m in list)
        {
            if (m.Home.Core.Count == 0 || Math.Abs(m.Start - start) > MatchWindow.TotalMilliseconds) continue;
            double s = PairScore(m, teams[0], teams[1], out bool sw);
            if (s > bestScore) { bestScore = s; best = m; swapped = sw; }
        }
        return bestScore >= 0.3 ? best : null;
    }

    // Schedule and title leagues are folded into the scores league id whenever they describe the same competition.
    public static string? Canonical(string? league)
    {
        if (league == null) return null;
        for (int i = 0; i < 3 && LeagueAliases.TryGetValue(league, out var next); i++) league = next;
        return league;
    }

    public static string? CountryOf(string? league) => league != null ? LeagueCountries.GetValueOrDefault(Canonical(league)!) : null;

    public static string? NameOf(string? league) =>
        league != null ? LeagueNames.GetValueOrDefault(Canonical(league)!) ?? EspnService.NameOf(league) : null;

    private static string? ResolveByName(string? sport, string? name)
    {
        if (Leagues.Key(name) == null) return null;
        return LeaguesByName.GetValueOrDefault(NameKey(sport, name));
    }

    private static void Learn()
    {
        var now = DateTimeOffset.UtcNow;
        var fotmob = _fotmob;
        var tvnu = _tvnu;

        foreach (var m in fotmob)
        {
            if (m.League == null) continue;
            if (m.Country != null && m.Country != "INT") LeagueCountries[m.League] = m.Country;
            if (m.LeagueName != null)
            {
                LeagueNames[m.League] = m.LeagueName;
                LeaguesByName.TryAdd(NameKey(m.Sport, m.LeagueName), m.League);
            }
            LearnTeam(m.Home, m.HomeId, m, now);
            LearnTeam(m.Away, m.AwayId, m, now);
        }

        foreach (var m in tvnu)
        {
            if (m.League == null) continue;
            if (m.LeagueName != null) LeagueNames.TryAdd(m.League, m.LeagueName);
            string? target = null;
            if (m.Home.Core.Count > 0 && m.Sport == "Football" && FindIn(fotmob, [m.Home, m.Away], m.Start) is { League: not null } fm)
                target = fm.League;
            target ??= ResolveByName(m.Sport, m.LeagueName);
            if (target != null && target != m.League) LeagueAliases[m.League] = target;
            LearnTeam(m.Home, null, m, now, onlyIfUnknown: true);
            LearnTeam(m.Away, null, m, now, onlyIfUnknown: true);
        }
        SaveLeagues();
    }

    private static void LearnTeam(ProgramTitle.TeamKey team, string? teamId, ExternalMatch m, DateTimeOffset now, bool onlyIfUnknown = false)
    {
        if (team.Core.Count == 0 || m.League == null) return;
        string key = NameId(team, m.Womens);
        string league = Canonical(m.League)!;
        string? leagueName = NameOf(league) ?? m.LeagueName;
        if (TeamLeagues.TryGetValue(key, out var known))
        {
            if (onlyIfUnknown) return;
            // Cup and European games should not replace a team's home league.
            if (known.League != league && Leagues.IsCup(Leagues.Key(leagueName) ?? "") && now - known.SeenAt < TimeSpan.FromDays(120))
            {
                known.TeamId ??= teamId;
                return;
            }
        }
        TeamLeagues[key] = new TeamLeague
        {
            League = league,
            LeagueName = leagueName,
            Country = m.Country ?? CountryOf(league),
            TeamId = teamId ?? known?.TeamId,
            Sport = m.Sport,
            SeenAt = now
        };
    }

    public static TeamLeague? LeagueOf(ProgramTitle.TeamKey team) =>
        team.Core.Count > 0 && TeamLeagues.TryGetValue(NameId(team, false), out var tl) ? tl : null;

    private static bool FotMobCovers(long start) =>
        _fotmobDates.Contains(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(start), Stockholm).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // Identity of a programme: the scored match first, then the schedule entry, then what is known about the teams, then the title.
    public static MatchContext Context(ProgramTitle parsed, string title, long start)
    {
        if (parsed.Teams.Count == 2)
        {
            var fm = FindIn(_fotmob, parsed.Teams, start, out bool swapped);
            if (fm != null)
            {
                string? league = Canonical(fm.League);
                return new MatchContext(league, NameOf(league) ?? fm.LeagueName, fm.Country ?? CountryOf(league), "Football",
                    swapped ? fm.AwayId : fm.HomeId, swapped ? fm.HomeId : fm.AwayId);
            }
            var tv = FindIn(_tvnu, parsed.Teams, start);
            if (tv?.League != null)
            {
                string? league = Canonical(tv.League);
                string? IdFor(ProgramTitle.TeamKey t) => LeagueOf(t) is { } k && k.Sport == tv.Sport ? k.TeamId : null;
                var ids = tv.Womens ? (null, null) : (IdFor(parsed.Teams[0]), IdFor(parsed.Teams[1]));
                return new MatchContext(league, NameOf(league) ?? tv.LeagueName, CountryOf(league), tv.Sport, ids.Item1, ids.Item2);
            }
            if (EspnService.Find(parsed.Teams, start, null) is { } es)
            {
                var g = es.game.M;
                return new MatchContext(g.League, g.LeagueName, g.Country, g.Sport, es.swapped ? g.AwayId : g.HomeId, es.swapped ? g.HomeId : g.AwayId);
            }
            bool notOnFotMob = FotMobCovers(start);
            if (notOnFotMob)
                return new MatchContext(null, null, null, null, null, null, true);
            var a = LeagueOf(parsed.Teams[0]);
            var b = LeagueOf(parsed.Teams[1]);
            var known = a != null && b != null && a.League == b.League ? a : a ?? b;
            if (known != null)
            {
                string? league = Canonical(known.League);
                return new MatchContext(league, NameOf(league) ?? known.LeagueName, known.Country ?? CountryOf(league), known.Sport, a?.TeamId, b?.TeamId);
            }
        }

        var (sport, key) = Leagues.FromTitle(title);
        if (key == null) return default;
        string? resolved = ResolveByName(sport, key) ?? $"t:{Leagues.Slug(sport)}:{key}";
        return new MatchContext(resolved, NameOf(resolved) ?? key, CountryOf(resolved), sport, null, null);
    }

    // Guide entries such as "Fotboll: Allsvenskan" on "TV4 Sport Live 2" are matched to the schedule broadcast on that channel.
    internal static ExternalMatch? OnChannel(string channelKey, long start)
    {
        ExternalMatch? best = null;
        long bestDiff = 40 * 60_000;
        foreach (var m in _tvnu)
        {
            if (m.Channel == null || m.Home.Core.Count == 0) continue;
            long diff = Math.Abs(m.Start - start);
            if (diff >= bestDiff || SportService.ChannelKey(m.Channel) != channelKey) continue;
            best = m;
            bestDiff = diff;
        }
        return best;
    }

    public static MatchInfo? Lookup(IReadOnlyList<ProgramTitle.TeamKey> teams, IReadOnlyList<string> teamNames, long start, string? sport)
    {
        if (!Enabled || teams.Count != 2) return null;
        Start();
        var tv = FindIn(_tvnu, teams, start);
        bool unknownSport = sport is null or "Other" or "Sport";
        bool swapped = false;
        // The scores source is football only and the scoreboard covers the North American leagues; neither is asked about other sports.
        var fm = unknownSport || sport == "Football" ? FindIn(_fotmob, teams, start, out swapped) : null;
        var es = fm == null && (unknownSport || EspnService.Covers(sport)) ? EspnService.Find(teams, start, unknownSport ? null : sport) : null;
        if (es != null) swapped = es.Value.swapped;
        var data = fm ?? es?.game.M;
        if (tv == null && data == null && teamNames.Count != 2) return null;

        var info = new MatchInfo
        {
            Image = tv?.Image,
            Tournament = tv?.Tournament ?? data?.Tournament,
            Description = tv?.Description,
            Broadcaster = tv?.Channel,
            HomeLogo = data?.HomeLogo,
            AwayLogo = data?.AwayLogo,
            HomeScore = data?.HomeScore,
            AwayScore = data?.AwayScore,
            Status = data?.Status,
            Live = data?.Live ?? false,
            Finished = data?.Finished ?? false,
            FinishedAt = data?.FinishedAt,
            Kickoff = data?.Kickoff ?? tv?.Kickoff,
            League = data?.LeagueName ?? tv?.LeagueName,
            Country = data?.Country ?? CountryOf(tv?.League),
            Sport = data?.Sport
        };
        if (swapped)
        {
            (info.HomeLogo, info.AwayLogo) = (info.AwayLogo, info.HomeLogo);
            (info.HomeScore, info.AwayScore) = (info.AwayScore, info.HomeScore);
        }
        // Without a known team sport, "Show - New York" would pick up some club's badge.
        string? teamSport = sport ?? tv?.Sport;
        if (data == null && teamNames.Count == 2 && SportsDbSport(teamSport) != null)
        {
            info.HomeLogo = Badge(teamNames[0], teamSport);
            info.AwayLogo = Badge(teamNames[1], teamSport);
        }

        bool empty = info.Image == null && info.HomeLogo == null && info.AwayLogo == null && info.Status == null && info.Tournament == null;
        return empty ? null : info;
    }

    // Matches with a broadcaster image or live data are the ones that matter; bare guide entries rank lower.
    public static double Quality(MatchInfo? info)
    {
        if (info == null) return 0.6;
        double q = 1;
        if (info.Image != null) q *= 1.25;
        if (info.Status != null || info.HomeScore != null || (info.HomeLogo != null && info.AwayLogo != null)) q *= 1.25;
        return q == 1 ? 0.6 : q;
    }

    private static string? SportsDbSport(string? sport) => sport switch
    {
        "Ice hockey" => "Ice Hockey",
        "Football" => "Soccer",
        "Handball" => "Handball",
        "Basketball" => "Basketball",
        "Floorball" => "Floorball",
        "American football" => "American Football",
        "Baseball" => "Baseball",
        "Rugby" => "Rugby",
        "Volleyball" => "Volleyball",
        _ => null
    };

    // The badge source is rate limited, so badges are fetched slowly in the background and kept forever.
    private static string? Badge(string team, string? sport)
    {
        string key = CatalogService.Normalize(team) + "|" + (sport ?? "");
        if (Badges.TryGetValue(key, out var url)) return url;
        if (BadgeQueued.TryAdd(key, 0)) BadgeQueue.Enqueue(key);
        return null;
    }

    private static async Task BadgeLoop()
    {
        int sinceSave = 0;
        while (true)
        {
            if (Enabled && BadgeQueue.TryDequeue(out var key))
            {
                var parts = key.Split('|');
                string? found = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var doc = await GetJson($"{SpanaApi.Base}/teams?q=" + Uri.EscapeDataString(parts[0]), cts.Token);
                    string? wanted = SportsDbSport(parts[1]);
                    var team = (doc.RootElement.Prop("teams") ?? default).Items().FirstOrDefault(t => t.Str("strSport") == wanted);
                    if (team.ValueKind == JsonValueKind.Object) found = team.Str("strBadge") is { } badge ? SpanaApi.Image(badge) : null;
                }
                catch (Exception ex)
                {
                    Log.Debug("Badge lookup failed for {Team}: {Message}", parts[0], ex.Message);
                }
                Badges[key] = found;
                if (++sinceSave >= 5 || BadgeQueue.IsEmpty)
                {
                    sinceSave = 0;
                    SaveBadges();
                    _ = MessageHub.Send("matchDataUpdated", new { });
                    FeedCache.Invalidate();
                }
                await Task.Delay(TimeSpan.FromSeconds(2.5));
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static void LoadBadges()
    {
        try
        {
            if (!File.Exists(BadgeFile)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(BadgeFile));
            if (map != null) foreach (var (k, v) in map) Badges[k] = v;
        }
        catch (Exception ex)
        {
            Log.Warning("Could not read badge cache: {Message}", ex.Message);
        }
    }

    private static void SaveBadges()
    {
        try
        {
            Storage.Save(BadgeFile, Badges.ToDictionary(k => k.Key, v => v.Value));
        }
        catch (Exception ex)
        {
            Log.Debug("Could not save badge cache: {Message}", ex.Message);
        }
    }

    private static void LoadLeagues()
    {
        try
        {
            try { File.Delete(Path.Combine(Storage.CacheRoot, "team-leagues.json")); } catch { }
            if (!File.Exists(LeagueFile)) return;
            var cache = JsonSerializer.Deserialize<LeagueCache>(File.ReadAllText(LeagueFile), Storage.Json);
            if (cache == null) return;
            foreach (var (k, v) in cache.Teams) TeamLeagues[k] = v;
            foreach (var (k, v) in cache.Countries) LeagueCountries[k] = v;
            foreach (var (k, v) in cache.Names) LeagueNames[k] = v;
            foreach (var (k, v) in cache.Aliases) LeagueAliases[k] = v;
            foreach (var (k, v) in cache.ByName) LeaguesByName[k] = v;
        }
        catch (Exception ex)
        {
            Log.Warning("Could not read league cache: {Message}", ex.Message);
        }
    }

    private static void SaveLeagues()
    {
        try
        {
            Storage.Save(LeagueFile, new LeagueCache
            {
                Teams = TeamLeagues.ToDictionary(k => k.Key, v => v.Value),
                Countries = LeagueCountries.ToDictionary(k => k.Key, v => v.Value),
                Names = LeagueNames.ToDictionary(k => k.Key, v => v.Value),
                Aliases = LeagueAliases.ToDictionary(k => k.Key, v => v.Value),
                ByName = LeaguesByName.ToDictionary(k => k.Key, v => v.Value)
            });
        }
        catch (Exception ex)
        {
            Log.Debug("Could not save league cache: {Message}", ex.Message);
        }
    }
}
