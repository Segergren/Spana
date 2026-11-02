using System.Text.RegularExpressions;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

public sealed class SportEvent
{
    public ProgramView Program { get; set; } = new();
    public CatalogItem Channel { get; set; } = new();
    public bool Live { get; set; }
    public string Sport { get; set; } = "Sport";
    public string? League { get; set; }
    public List<string> Teams { get; set; } = new();
    public bool Mine { get; set; }
    public bool Followed { get; set; }
    public double Relevance { get; set; }
    public int Tier { get; set; }
    public int Prominence { get; set; }
    public string? Reason { get; set; }
    public MatchInfo? Info { get; set; }
    internal ProgramTitle? Parsed { get; set; }
    internal string? LeagueKey { get; set; }
    internal bool NorthAmerica { get; set; }
}

// Sport schedule built from the guide: matches and events on sport channels, live now and coming up.
public static partial class SportService
{
    // Sport nouns and names that only mean one sport, checked in every text before league names.
    private static readonly (string sport, string[] words)[] SportWords =
    [
        ("Snooker", ["snooker", "international championship", "uk championship", "masters snooker", "world snooker"]),
        ("Darts", ["darts", "pdc"]),
        ("Table tennis", ["table tennis", "bordtennis", "tischtennis", "tennis de table", "ping pong"]),
        ("Padel", ["padel"]),
        ("Badminton", ["badminton", "bwf"]),
        ("Gaelic games", ["gaelic football", "hurling", "camogie", "gaa"]),
        ("Australian football", ["afl", "aussie rules", "australian football"]),
        ("American football", ["nfl", "american football", "amerikansk fotboll", "football americano", "futbol americano", "futebol americano",
            "college football", "ncaa football", "high school football", "flag football", "cfl", "xfl", "ufl", "super bowl",
            "monday night football", "thursday night football", "sunday night football"]),
        ("Field hockey", ["field hockey", "landhockey", "hockey sur gazon"]),
        ("Ice hockey", ["ishockey", "ice hockey", "eishockey", "ijshockey", "jaakiekko", "hockey sur glace", "hockey su ghiaccio", "hockey sobre hielo",
            "hockey", "hokej", "shl", "nhl", "khl", "ahl", "echl", "ohl", "whl", "qmjhl", "liiga", "hockeyallsvenskan", "hockeyettan", "hockeylordag",
            "metal ligaen", "fjordkraft ligaen", "extraliga", "penny del", "stanley cup", "iihf"]),
        ("Football", ["fotboll", "fotball", "football", "soccer", "futbol", "fussball", "futebol", "calcio", "voetbal", "fodbold", "jalkapallo",
            "pilka nozna", "nogomet", "fotbal", "futsal"]),
        ("Handball", ["handboll", "handball", "handbold", "balonmano", "pallamano", "handebol", "pilka reczna", "ehf"]),
        ("Floorball", ["innebandy", "floorball", "salibandy", "unihockey"]),
        ("Bandy", ["bandy"]),
        ("Basketball", ["basket", "basketball", "baloncesto", "pallacanestro", "basquete", "basquetbol", "koripallo", "koszykowka", "nba", "wnba",
            "euroleague", "eurocup", "college basketball", "ncaa basketball", "march madness", "g league", "fiba"]),
        ("Baseball", ["baseball", "beisbol", "baseboll", "mlb", "milb", "npb", "kbo"]),
        ("Volleyball", ["volleyboll", "volleyball", "beachvolley", "beach volleyball", "voleibol", "pallavolo", "siatkowka", "lentopallo"]),
        ("Rugby", ["rugby", "rygbi", "super rugby", "six nations", "nrl", "premiership rugby", "top 14", "state of origin"]),
        ("Cricket", ["cricket", "ipl", "big bash", "bbl", "wbbl", "the ashes", "test match", "t20", "odi", "the hundred", "county championship", "sheffield shield"]),
        ("Tennis", ["tennis", "tenis", "atp", "wta", "wimbledon", "roland garros", "australian open", "davis cup", "billie jean king cup", "laver cup", "united cup"]),
        ("Golf", ["golf", "pga", "lpga", "dp world tour", "ryder cup", "liv golf", "solheim cup", "presidents cup"]),
        ("Motorsport", ["formel 1", "formula 1", "formula one", "f1", "formula e", "motorsport", "motorsports", "motor sports", "motor racing", "auto racing",
            "nascar", "indycar", "indy 500", "motogp", "moto2", "moto3", "superbike", "wsbk", "world rally", "rally championship", "rallye", "dakar rally", "wrc", "speedway", "dtm", "wec", "le mans",
            "rallycross", "supercross", "motocross", "drag racing", "nhra", "supercars", "btcc", "imsa", "karting", "motorcycle racing", "motori"]),
        ("Combat sports", ["boxning", "boxing", "boxeo", "boxe", "ufc", "mma", "bellator", "pfl", "kickboxing", "muay thai", "one championship", "wwe", "aew",
            "wrestling", "pro wrestling", "lucha libre", "sumo", "judo", "karate", "taekwondo", "fight", "bkfc", "bare knuckle", "mixed martial arts"]),
        ("Winter sports", ["skidor", "langdskidor", "skidskytte", "alpint", "biathlon", "skiing", "ski jumping", "snowboard", "skeleton", "bobsleigh", "luge",
            "curling", "backhoppning", "figure skating", "speed skating", "short track", "eiskunstlauf", "konstakning", "skating"]),
        ("Cycling", ["cykel", "cykling", "cycling", "ciclismo", "radsport", "wielrennen", "cyclisme", "tour de france", "giro", "la vuelta", "uci", "paris roubaix"]),
        ("Horse racing", ["trav", "galopp", "v75", "v86", "v64", "horse racing", "harness racing", "kentucky derby", "breeders cup", "royal ascot", "cheltenham"]),
        ("Equestrian", ["equestrian", "ridsport", "show jumping", "dressage", "reitsport"]),
        ("Athletics", ["friidrott", "athletics", "diamond league", "track and field", "leichtathletik", "atletismo"]),
        ("Swimming", ["simning", "swimming", "natation", "nuoto", "natacion", "aquatics"]),
        ("Sailing", ["segling", "sailing", "sailgp", "americas cup"]),
        ("Esports", ["esport", "esports", "e sport", "counter strike", "league of legends", "dota", "valorant"]),
    ];

    // League names alone ("Allsvenskan", "Serie A"), so "Bundesliga Basketball" stays basketball.
    private static readonly (string sport, string[] words)[] LeagueWords =
    [
        ("Football", ["allsvenskan", "superettan", "damallsvenskan", "elitettan", "obos", "premier league", "engelska championship", "efl championship",
            "efl", "spfl", "scottish premiership", "super league", "womens super league", "serie a", "serie b", "la liga", "laliga", "bundesliga",
            "ligue 1", "ligue 2", "champions league", "europa league", "conference league", "eredivisie", "eliteserien", "superliga", "superligaen",
            "veikkausliiga", "mls", "nwsl", "liga mx", "fa cup", "carabao cup", "efl cup", "dfb pokal", "coppa italia", "copa del rey", "coupe de france",
            "libertadores", "liga de campeones", "copa sudamericana", "primeira liga", "liga portugal", "super lig", "ekstraklasa", "jupiler pro league", "a league",
            "j league", "j1 league", "k league", "saudi pro league", "brasileirao", "liga profesional", "primera division", "uefa", "fifa", "concacaf",
            "conmebol", "nations league"]),
        ("Baseball", ["world series"]),
    ];

    private static readonly string[] SportyChannels =
        ["sport", "event", "ppv", "viaplay", "tv4play", "tv4 play", "eurosport", "espn", "dazn", "expressen", "fight", "ufc", "nfl", "nhl", "nba", "mlb",
         "golf", "arena", "fotboll", "hockey", "c more", "v sport", "tv4 sport", "padel", "tsn", "fs1", "fs2", "bein", "setanta", "acc network",
         "sec network", "big ten", "msg", "nesn", "willow", "cricket", "rugby", "racing", "tennis", "wwe", "boxing", "redzone", "golazo", "goltv",
         "tudn", "tyc", "deporte", "esporte", "desporto", "spor", "calcio", "futbol", "football", "basket", "stadium", "olympic"];

    private static readonly string[] NotEvents =
        ["hojdpunkter", "highlights", "magazine", "magasin", "nyheter", "news", "sportnytt", "sportcenter", "sportscenter", "podcast", "klassiker",
         "classic", "dokumentar", "documentary", "best of", "basta", "repris"];

    // Studio, talk and recap shows, matched as whole words.
    private static readonly string[] NotEventWords =
        ["next game", "countdown", "pregame", "pre game", "postgame", "post game", "preview", "previews", "review", "recap", "replay", "replays",
         "rewind", "flashback", "playback", "full fight", "originals", "the story", "stories", "insider", "insiders", "fantasy", "betting", "picks",
         "odds", "talk", "tonight", "gameday", "game day", "goal zone", "goals", "goal rush", "greatest", "top 10", "plays of", "shot clock", "inside",
         "now", "morning", "daily", "weekly", "this week", "roundup", "round up", "wrap", "hlts", "reaction", "debate", "analysis", "awards",
         "hall of fame", "football final", "life", "legends", "resumen", "resumo", "compacto", "melhores momentos", "zusammenfassung", "sintesi",
         "skrot", "skroty", "przeglad", "ozet", "analys", "sammandrag", "reprise", "rediffusion", "diferido", "off the pitch", "talks", "post match",
         "specials", "tribute", "interview", "gamecenter", "today", "draft", "før", "efter", "etter", "infor"];

    // XMLTV categories: generic sport, studio shows and old games, and genres that are never a live event.
    private static readonly HashSet<string> GuideSport = new(StringComparer.Ordinal)
        { "sports", "sport", "deportes", "deporte", "esporte", "esportes", "desporto", "spor", "sportmusor", "urheilu", "sports event", "sports events" };

    private static readonly HashSet<string> GuideEvent = new(StringComparer.Ordinal)
        { "sports event", "sports events", "live event", "event", "playoff sports", "live", "multi sport event" };

    private static readonly HashSet<string> GuideNotEvent = new(StringComparer.Ordinal)
    {
        "sports talk", "talk", "talk show", "news", "magazine", "magazin", "magazyn", "documentary", "documentaire", "documental", "documentario",
        "dokumentar", "dokumentation", "classic sport event", "sports non event", "sportmagazin", "magazine sportif", "fussballmagazin", "sporttalkshow",
        "informacao desportiva", "mag sport", "mag football", "jt sport", "interview", "debate", "highlights", "game show", "card games", "poker",
        "fishing", "hunting", "outdoors", "documentaire sportif", "documentario desportivo", "magazyn sportowy", "magazyn pilkarski", "sports talk show"
    };

    private static readonly HashSet<string> GuideNotSport = new(StringComparer.Ordinal)
    {
        "movie", "movies", "film", "series", "serie", "comedy", "drama", "sitcom", "children", "kids", "animated", "animation", "cooking", "reality",
        "music", "religious", "religion", "shopping", "educational", "soap", "thriller", "horror", "western", "romance", "crime", "fantasy",
        "science fiction", "adventure", "action", "mystery", "family", "anime", "crime drama"
    };

    private static readonly HashSet<string> TeamSports = new(StringComparer.Ordinal)
    {
        "Football", "Ice hockey", "Basketball", "American football", "Baseball", "Handball", "Volleyball", "Rugby", "Cricket", "Floorball", "Bandy",
        "Field hockey", "Australian football", "Gaelic games"
    };

    // Words in a description that make a bare "Football" the American game.
    private static readonly string[] GridironHints =
        ["nfl", "touchdown", "quarterback", "gridiron", "college", "ncaa", "high school", "national football league", "bowl", "cfl", "fbs", "fcs"];

    [GeneratedRegex(@"^(?:The\s+)?(?<a>[A-Z0-9][\w.'&-]*(?:\s+[A-Z0-9][\w.'&-]*){0,3})\s+(?<v>hosts?|welcomes?|visits?|takes?\s+on|faces?|plays?\s+host\s+to)\s+(?:the\s+)?(?<b>[A-Z0-9][\w.'&-]*(?:\s+[A-Z0-9][\w.'&-]*){0,3})")]
    private static partial Regex HostsRegex();

    [GeneratedRegex(@"^(?:The\s+)?(?<a>[A-Z0-9][\w.'&-]*(?:\s+[A-Z0-9][\w.'&-]*){0,3})\s+and\s+(?:the\s+)?(?<b>[A-Z0-9][\w.'&-]*(?:\s+[A-Z0-9][\w.'&-]*){0,3})\s+(?:meet|square off|clash|battle|face off)\b")]
    private static partial Regex MeetRegex();

    [GeneratedRegex(@"(?<!\d)(?:20)?\d{2}/(?<end>\d{2})(?!\d)")]
    private static partial Regex Season();

    [GeneratedRegex(@"\.(?:us|ca)(?:@|\d|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NorthAmericanId();

    internal static bool IsHome(CatalogItem c) => c.Country is null || c.Country == ChannelNames.HomeCode;

    // A match that just ended still matters for a while: final score, reactions, the studio after.
    private const long AfterMatch = 20 * 60_000;

    public static List<SportEvent> Get(Account a)
    {
        var guide = GuideService.Get(a);
        if (guide == null) return new();
        var streams = SuggestionService.StreamsByEpg(a);
        var groups = CatalogService.CategoryNames(a, MediaKind.Live);
        var profile = SuggestionService.BuildProfile(a.Id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long horizon = now + TimeSpan.FromHours(40).Ticks / TimeSpan.TicksPerMillisecond;
        long recent = now - AfterMatch;

        var seen = new Dictionary<string, SportEvent>();
        foreach (var p in guide.All)
        {
            if (p.End <= recent || p.Start > horizon) continue;
            if (!streams.TryGetValue(p.EpgId, out var channels)) continue;
            string title = ResolveTitle(p, channels);
            var look = Inspect(p, title, now);
            if (look.Skip || look.GuideOnly) continue;
            var parsed = look.Parsed;
            string? sport = look.Sport;

            string channelText = CatalogService.Normalize(string.Join(' ', channels.Take(3).Select(c => c.Name + " " + groups.GetValueOrDefault(c.CategoryId, ""))));
            bool sportyChannel = SportyChannels.Any(w => channelText.Contains(w, StringComparison.Ordinal));
            bool matchup = parsed.Teams.Count == 2;

            // Plain programmes on sport channels are usually studio shows or reruns; keep matchups and labelled sport.
            if (sport == null && !(matchup && sportyChannel && LooksLikeTeams(parsed))) continue;
            if (sport != null && !matchup && !sportyChannel && !parsed.Live && !look.GuideSport) continue;

            var channel = SuggestionService.PickChannel(channels, profile, null);
            if (channel == null) continue;

            bool mine = parsed.Teams.Any(t => profile.Teams.Any(i => ProgramTitle.TeamSimilarity(t, i.Team) >= 0.5));
            string key = parsed.Show + "|" + p.Start / 60000;
            // The same show on a foreign channel gives way to one from the viewer's country.
            if (seen.TryGetValue(key, out var have) && (IsHome(have.Channel) || !IsHome(channel))) continue;
            var league = Leagues.Find(parsed.Category, title, look.GuideCategory);
            seen[key] = new SportEvent
            {
                Program = new ProgramView { Title = CleanTitle(title), Description = p.Description, Start = p.Start, End = p.End, Image = p.Image },
                Channel = channel,
                Live = p.Start <= now,
                Sport = sport ?? GuessFromTeams(parsed) ?? "Other",
                League = league?.name ?? League(title),
                LeagueKey = league?.key,
                NorthAmerica = look.NorthAmerica,
                Teams = parsed.TeamNames,
                Parsed = parsed,
                Mine = mine
            };
        }
        var events = seen.Values.ToList();
        AddTvNuEvents(a, events, profile, now, horizon);
        AddNamedEvents(a, events, groups, now, horizon);
        foreach (var e in events)
        {
            var parsed = e.Parsed ?? new ProgramTitle(e.Program.Title, e.Teams);
            if (EspnService.LeagueFor(e.LeagueKey, e.Sport, parsed.Teams.Count == 2, e.NorthAmerica) is { } espn) EspnService.Want(espn);
            e.Info ??= MatchDataService.Lookup(parsed.Teams, parsed.TeamNames, e.Program.Start, e.Sport);
            if (e.Sport is "Other" or "Sport" && e.Info?.Sport != null) e.Sport = e.Info.Sport;
            var ctx = MatchDataService.Context(parsed, e.Program.Title, e.Program.Start);
            var rel = SuggestionService.Score(profile, parsed, ctx, e.Sport is "Other" or "Sport" ? ctx.Sport : e.Sport);
            double quality = MatchDataService.Quality(e.Info);
            // Without any viewing history the quality alone orders the list, as a small value below every real interest.
            e.Relevance = Math.Round(rel.Score > 0 ? rel.Score * quality : (quality - 0.6) * 0.1, 3);
            e.Reason = rel.Score > 0 ? rel.Reason : null;
            if (rel.Score <= 0) e.Relevance = Math.Min(e.Relevance, 0.09);
            e.Mine = rel.Kind == "team";
            e.Tier = rel.Score <= 0 ? 0 : rel.Kind switch { "team" => 5, "show" => 4, "league" => 4, "top" => 3, "country" => 2, _ => 1 };
            e.Followed = rel.Kind is "team" or "league" || (ctx.League != null && profile.Leagues.ContainsKey(ctx.League));
            if (e.Info?.Sport != null && e.Info.League != null) e.League = e.Info.League;
            if (e.League == null && ctx.LeagueName != null) e.League = ctx.LeagueName;
            bool covered = e.Info?.Image != null || e.Info?.Status != null || (e.Info?.HomeLogo != null && e.Info?.AwayLogo != null);
            e.Prominence = Leagues.Prominence(ctx.LeagueName ?? e.Info?.League ?? e.League, ctx.Country ?? e.Info?.Country, covered);
        }
        return Merge(events).OrderBy(e => e.Program.Start).ToList();
    }

    // The same match often appears on several channels under slightly different names; keep the best one.
    // A broadcast that starts after a match would normally be over is a repeat.
    private const long RerunAfter = 100 * 60_000;

    private static bool SamePair(ProgramTitle.TeamKey a1, ProgramTitle.TeamKey b1, ProgramTitle.TeamKey a2, ProgramTitle.TeamKey b2) =>
        Math.Max(Math.Min(ProgramTitle.TeamSimilarity(a1, a2), ProgramTitle.TeamSimilarity(b1, b2)),
                 Math.Min(ProgramTitle.TeamSimilarity(a1, b2), ProgramTitle.TeamSimilarity(b1, a2))) >= 0.5;

    // Guides do not mark repeats, so a matchup shown again well after its real kick-off is treated as a rerun.
    // The original is the known kick-off from the match data or else the earliest showing of the same matchup.
    private static List<SportEvent> DropReruns(List<SportEvent> events)
    {
        var matchups = events.Where(e => e.Teams.Count == 2)
            .Select(e => (e, a: ProgramTitle.TeamKey.From(e.Teams[0]), b: ProgramTitle.TeamKey.From(e.Teams[1])))
            .ToList();
        var reruns = new HashSet<SportEvent>();
        foreach (var x in matchups)
        {
            // A known kick-off is the reference; studio shows before it must not count as the original.
            long? kickoff = x.e.Info?.Kickoff;
            long earliest = x.e.Program.Start;
            foreach (var y in matchups)
            {
                if (ReferenceEquals(x.e, y.e) || !SamePair(x.a, x.b, y.a, y.b)) continue;
                if (y.e.Info?.Kickoff is long k) kickoff = kickoff is long have ? Math.Min(have, k) : k;
                earliest = Math.Min(earliest, y.e.Program.Start);
            }
            long original = kickoff ?? earliest;
            if (x.e.Program.Start - original > RerunAfter) reruns.Add(x.e);
        }
        return reruns.Count == 0 ? events : events.Where(e => !reruns.Contains(e)).ToList();
    }

    private static List<SportEvent> Merge(List<SportEvent> events)
    {
        events = DropReruns(events);
        var kept = new List<(SportEvent e, ProgramTitle.TeamKey a, ProgramTitle.TeamKey b)>();
        var result = new List<SportEvent>();
        foreach (var e in events.OrderByDescending(e => e.Relevance).ThenByDescending(e => IsHome(e.Channel))
                     .ThenByDescending(e => e.Info?.Image != null).ThenByDescending(e => e.Info?.HomeLogo != null))
        {
            if (e.Teams.Count != 2)
            {
                result.Add(e);
                continue;
            }
            var a = ProgramTitle.TeamKey.From(e.Teams[0]);
            var b = ProgramTitle.TeamKey.From(e.Teams[1]);
            // Same matchup on several channels, studio shows included, within three hours is one match.
            bool duplicate = kept.Any(k => Math.Abs(k.e.Program.Start - e.Program.Start) < 180 * 60_000 && SamePair(k.a, k.b, a, b));
            if (duplicate) continue;
            kept.Add((e, a, b));
            result.Add(e);
        }
        return result;
    }

    private static readonly Dictionary<string, string> SwedishSports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fotboll"] = "Football", ["Ishockey"] = "Ice hockey", ["Handboll"] = "Handball", ["Innebandy"] = "Floorball",
        ["Basket"] = "Basketball", ["Tennis"] = "Tennis", ["Golf"] = "Golf", ["Motorsport"] = "Motorsport",
        ["Boxning"] = "Combat sports", ["MMA"] = "Combat sports", ["Kampsport"] = "Combat sports", ["Cykel"] = "Cycling",
        ["Längdskidor"] = "Winter sports", ["Skidskytte"] = "Winter sports", ["Alpint"] = "Winter sports",
        ["Trav"] = "Horse racing", ["Galopp"] = "Horse racing", ["Friidrott"] = "Athletics", ["Dart"] = "Darts",
        ["Snooker"] = "Snooker", ["Hästsport"] = "Equestrian", ["Ridsport"] = "Equestrian", ["Volleyboll"] = "Volleyball", ["Beachvolley"] = "Volleyball", ["Bandy"] = "Bandy", ["Rugby"] = "Rugby", ["Simning"] = "Swimming", ["Segling"] = "Sailing", ["Padel"] = "Padel", ["Amerikansk fotboll"] = "American football", ["E-sport"] = "Esports",
        ["Baseboll"] = "Baseball", ["Cricket"] = "Cricket", ["Bordtennis"] = "Table tennis"
    };

    public static string? FromSwedish(string? name) =>
        name == null ? null : SwedishSports.TryGetValue(name, out var en) ? en : name;

    private static readonly HashSet<string> VariantWords = new(StringComparer.Ordinal)
        { "hd", "fhd", "sd", "uhd", "4k", "se", "swe", "sweden", "sverige", "50", "fps", "50fps", "hevc", "h265", "beta", "plus" };

    private sealed record NameMap(object Key, Dictionary<string, List<CatalogItem>> Map);
    private static volatile NameMap? _channelNames;

    public static string ChannelKey(string name)
    {
        var words = CatalogService.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && VariantWords.Contains(words[0])) words.RemoveAt(0);
        while (words.Count > 1 && VariantWords.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    // A generic guide title gets the real matchup when the match data knows what is on that channel.
    internal static string ResolveTitle(Programme p, List<CatalogItem> channels)
    {
        var parsed = p.Parsed;
        if (parsed.Teams.Count == 2 || (parsed.Category == null && Leagues.FromTitle(p.Title).key == null)) return p.Title;
        foreach (var c in channels.Take(4))
        {
            if (MatchDataService.OnTvNu(c) && MatchDataService.OnChannel(ChannelKey(c.Name), p.Start) is { } m && m.Title != null)
                return m.Title;
        }
        return p.Title;
    }

    // Channel names from the guide ("TV4 Fotboll") matched against provider names ("TV4 Fotboll HD", "SE: TV4 Fotboll").
    private static Dictionary<string, List<CatalogItem>> ChannelsByName(Account a)
    {
        var index = CatalogService.GetIndex(a, MediaKind.Live);
        if (index == null) return new();
        if (_channelNames is { } c && ReferenceEquals(c.Key, index)) return c.Map;
        var map = new Dictionary<string, List<CatalogItem>>(StringComparer.Ordinal);
        foreach (var item in index.All)
        {
            if (!MatchDataService.OnTvNu(item)) continue;
            string key = ChannelKey(item.Name);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new();
            list.Add(item);
        }
        _channelNames = new NameMap(index, map);
        return map;
    }

    private static void AddTvNuEvents(Account a, List<SportEvent> events, SuggestionService.Profile profile, long now, long horizon)
    {
        if (!MatchDataService.Enabled) return;
        var byName = ChannelsByName(a);
        var existing = events.Select(e => (teams: (e.Parsed ?? new ProgramTitle(e.Program.Title)).Teams, start: e.Program.Start)).ToList();
        foreach (var m in MatchDataService.TvNuMatches)
        {
            if (m.End <= now - AfterMatch || m.Start > horizon || m.Channel == null) continue;
            if (m.Home.Core.Count > 0 && existing.Any(x => x.teams.Count == 2 &&
                    Math.Abs(x.start - m.Start) < 3 * 3_600_000 &&
                    MatchDataService.FindIn([m], x.teams, x.start) != null))
                continue;
            if (!byName.TryGetValue(CatalogService.Normalize(m.Channel), out var channels)) continue;
            var channel = SuggestionService.PickChannel(channels, profile, null);
            if (channel == null) continue;

            bool hasTeams = m.Home.Core.Count > 0;
            string title = m.Title ?? (hasTeams ? $"{m.HomeName} - {m.AwayName}" : "Sport");
            var teams = hasTeams ? new List<ProgramTitle.TeamKey> { m.Home, m.Away } : new();
            events.Add(new SportEvent
            {
                Program = new ProgramView { Title = title, Description = m.Description, Start = m.Start, End = m.End },
                Channel = channel,
                Live = m.Start <= now,
                Sport = m.Sport ?? "Other",
                League = m.Tournament,
                Teams = hasTeams ? [m.HomeName, m.AwayName] : new(),
                Mine = teams.Any(t => profile.Teams.Any(i => ProgramTitle.TeamSimilarity(t, i.Team) >= 0.5)),
                Info = MatchDataService.Lookup(teams, hasTeams ? [m.HomeName, m.AwayName] : [], m.Start, null)
                       ?? new MatchInfo { Image = m.Image, Tournament = m.Tournament, Description = m.Description, Broadcaster = m.Channel, Kickoff = m.Kickoff }
            });
        }
    }

    // The guide's own categories say sport ("Sports", "Ice Hockey").
    internal static bool GuideSaysSport(string? category) =>
        GuideKind(category) is var k && (k.sport || (k.norm != null && !k.notSport && SportIndex.Match(k.norm) != null));

    internal readonly record struct Inspection(ProgramTitle Parsed, string? Sport, bool Skip, bool GuideSport, string? GuideCategory, bool NorthAmerica, bool GuideOnly = false);

    // A guide programme as the sport schedule sees it; Skip marks studio shows, old games and genres that are never sport.
    internal static Inspection Inspect(Programme p, string title, long now)
    {
        var parsed = title == p.Title ? p.Parsed : new ProgramTitle(title);
        string norm = title == p.Title ? p.NormalizedTitle : CatalogService.Normalize(title);
        var kind = GuideKind(p.Category);
        bool na = NorthAmericanId().IsMatch(p.EpgId);
        if (kind.notEvent || (kind.notSport && !kind.sport && !kind.live) || IsNotEvent(norm) || OldSeason(title, now))
            return new Inspection(parsed, null, true, kind.sport, kind.norm, na);
        string? sport = Classify(parsed.Category, norm, kind.norm, p.Description, na);
        if (parsed.Teams.Count == 0 && sport != null && TeamSports.Contains(sport))
            parsed = new ProgramTitle(title, DescriptionTeams(p.Description, p.Start, sport), sportContext: true);
        // "Moto3 - Malaysia Qualifying" is a session, not two teams.
        if (parsed.Teams.Count == 2 && !parsed.IsVersus && sport != null && Individual.Contains(sport))
        {
            parsed = new ProgramTitle(title);
            parsed.Teams.Clear();
            parsed.TeamNames.Clear();
        }
        // A guide category alone ("Sport, Calcio" on a club channel's magazine, "Hockey" on "On the Fly") is no evidence of a match.
        bool fromTitle = sport != null && (Find(SportIndex, [parsed.Category, norm]) ?? Find(LeagueIndex, [parsed.Category, norm])) != null;
        bool evidence = kind.live || (kind.sport && fromTitle);
        return new Inspection(parsed, sport, false, evidence, kind.norm, na, sport != null && !fromTitle && !kind.live && !parsed.Live && parsed.Teams.Count != 2);
    }

    private static readonly HashSet<string> Individual = new(StringComparer.Ordinal)
        { "Motorsport", "Cycling", "Golf", "Winter sports", "Athletics", "Horse racing", "Sailing", "Swimming", "Equestrian" };

    private sealed record NamedCache(object Key, DateTime Day, List<(CatalogItem item, EventChannels.Parsed ev)> List);
    private static volatile NamedCache? _named;

    // Event channels without a guide that name a current event ("NFL 01ⓧ: Vikings vs Steelers (Sunday 9/28) 9:30AM ET").
    private static void AddNamedEvents(Account a, List<SportEvent> events, Dictionary<string, string> groups, long now, long horizon)
    {
        var index = CatalogService.GetIndex(a, MediaKind.Live);
        if (index == null) return;
        var local = DateTimeOffset.Now;
        var cache = _named;
        if (cache == null || !ReferenceEquals(cache.Key, index) || cache.Day != local.Date)
        {
            var list = new List<(CatalogItem, EventChannels.Parsed)>();
            foreach (var item in index.All)
            {
                if (item.Name.Length <= 16) continue;
                // One odd name must not take the whole sport page down.
                try
                {
                    if (EventChannels.Parse(item.Name, local) is { } ev) list.Add((item, ev));
                }
                catch (Exception) { }
            }
            _named = cache = new NamedCache(index, local.Date, list);
        }
        foreach (var (item, ev) in cache.List)
        {
            long end = ev.Start + 3 * 3_600_000;
            if (end <= now - AfterMatch || ev.Start > horizon || ChannelFilter.Hidden(item)) continue;
            var parsed = new ProgramTitle(ev.Title);
            string context = CatalogService.Normalize(ev.Label + " " + groups.GetValueOrDefault(item.CategoryId, ""));
            bool na = item.Country is "us" or "ca" || context.Contains("usa");
            string? sport = Classify(parsed.Category, CatalogService.Normalize(ev.Title), context, null, na);
            var league = Leagues.Find(parsed.Category, ev.Title, context);
            events.Add(new SportEvent
            {
                Program = new ProgramView { Title = ev.Title, Start = ev.Start, End = end },
                Channel = item,
                Live = ev.Start <= now,
                Sport = sport ?? GuessFromTeams(parsed) ?? "Other",
                League = league?.name ?? League(ev.Title),
                LeagueKey = league?.key,
                NorthAmerica = na,
                Teams = parsed.TeamNames,
                Parsed = parsed
            });
        }
    }

    public static string? Classify(string? category, string title) => Classify(category, title, null, null, false);

    // Sport nouns win over league names in any text: the title's prefix, the title, then the guide's categories.
    internal static string? Classify(string? category, string title, string? guideCategory, string? description, bool northAmerica)
    {
        string?[] texts = [category, title, guideCategory];
        string? sport = Find(SportIndex, texts) ?? Find(LeagueIndex, texts);
        if (sport != "Football" || !texts.Any(t => t != null && ContainsWord(t, "football"))) return sport;
        // "Football" alone is the American game in US and Canadian guides, which call the other one soccer.
        if (texts.Any(t => t != null && (ContainsWord(t, "soccer") || LeagueIndex.Match(t) == "Football"))) return sport;
        string desc = CatalogService.Normalize(description is { Length: > 300 } ? description[..300] : description ?? "");
        return northAmerica || GridironHints.Any(w => ContainsWord(desc, w)) ? "American football" : sport;
    }

    private static string? Find(WordIndex index, string?[] texts)
    {
        foreach (var text in texts)
            if (!string.IsNullOrEmpty(text) && index.Match(text) is { } sport) return sport;
        return null;
    }

    private static readonly WordIndex SportIndex = new(SportWords);
    private static readonly WordIndex LeagueIndex = new(LeagueWords);
    private static readonly WordIndex NotEventIndex = new([("x", NotEventWords)]);

    // Phrases looked up by their first word, so a title costs a few lookups instead of a scan of every list.
    private sealed class WordIndex
    {
        private readonly Dictionary<string, List<(string phrase, int order, string value)>> _byFirst = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string phrase, int order, string value)>>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        public WordIndex((string value, string[] words)[] lists)
        {
            for (int order = 0; order < lists.Length; order++)
                foreach (var w in lists[order].words)
                {
                    string first = w.Split(' ')[0];
                    if (!_byFirst.TryGetValue(first, out var list)) _byFirst[first] = list = new();
                    list.Add((w, order, lists[order].value));
                }
            _lookup = _byFirst.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        // The value of the earliest list with a phrase in the text, as whole words.
        public string? Match(string text)
        {
            int best = int.MaxValue;
            string? value = null;
            for (int i = 0; i < text.Length;)
            {
                int end = text.IndexOf(' ', i);
                if (end < 0) end = text.Length;
                if (_lookup.TryGetValue(text.AsSpan(i, end - i), out var list))
                    foreach (var (phrase, order, v) in list)
                        if (order < best && string.CompareOrdinal(text, i, phrase, 0, phrase.Length) == 0 &&
                            (i + phrase.Length == text.Length || text[i + phrase.Length] == ' '))
                        {
                            best = order;
                            value = v;
                        }
                i = end + 1;
            }
            return value;
        }
    }

    private static bool IsNotEvent(string norm) =>
        NotEvents.Any(w => norm.Contains(w, StringComparison.Ordinal)) || NotEventIndex.Match(norm) != null || norm.EndsWith(" show", StringComparison.Ordinal);

    // "SPFL 23/24: Hibernian v Hearts" is a match from an earlier season.
    private static bool OldSeason(string title, long now)
    {
        string t = ProgramTitle.WithoutLive(title);
        int colon = t.IndexOf(':');
        if (colon <= 0) return false;
        var m = Season().Match(t[..colon]);
        if (!m.Success) return false;
        var today = DateTimeOffset.FromUnixTimeMilliseconds(now);
        int seasonEnd = (today.Month >= 7 ? today.Year + 1 : today.Year) % 100;
        return int.Parse(m.Groups["end"].Value) < seasonEnd;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool sport, bool live, bool notEvent, bool notSport, string? norm)> Kinds = new();

    // Guides repeat a few hundred category strings, so each is read once.
    private static (bool sport, bool live, bool notEvent, bool notSport, string? norm) GuideKind(string? category) =>
        string.IsNullOrEmpty(category) ? default : Kinds.Count < 20_000 ? Kinds.GetOrAdd(category, ReadKind) : ReadKind(category);

    private static (bool sport, bool live, bool notEvent, bool notSport, string? norm) ReadKind(string category)
    {
        bool sport = false, live = false, notEvent = false, notSport = false;
        foreach (var raw in category.Split(", "))
        {
            string c = CatalogService.Normalize(raw);
            sport |= GuideSport.Contains(c);
            live |= GuideEvent.Contains(c);
            notEvent |= GuideNotEvent.Contains(c);
            notSport |= GuideNotSport.Contains(c);
        }
        return (sport, live, notEvent, notSport, CatalogService.Normalize(category));
    }

    // US guides often title games "NHL Hockey" and name the teams in the description.
    internal static List<string>? DescriptionTeams(string? description, long start, string? sport)
    {
        if (string.IsNullOrEmpty(description)) return null;
        string first = description.Length > 240 ? description[..240] : description;
        var m = HostsRegex().Match(first);
        bool homeFirst = true;
        if (m.Success) homeFirst = !m.Groups["v"].Value.StartsWith("visit", StringComparison.OrdinalIgnoreCase);
        else m = MeetRegex().Match(first);
        if (m.Success)
        {
            string a = Possessor(m.Groups["a"].Value), b = Possessor(m.Groups["b"].Value);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return homeFirst ? [a, b] : [b, a];
        }
        return EspnService.AtVenue(description, start, sport) is { } g ? [g.M.HomeName, g.M.AwayName] : null;
    }

    // "the Central Division's Utah Mammoth" is the Utah Mammoth, "No. 17 Florida Gators. Both" the Florida Gators.
    private static string Possessor(string name)
    {
        int i = name.LastIndexOf("'s ", StringComparison.Ordinal);
        string n = Ranking().Replace(i > 0 ? name[(i + 3)..] : name, "");
        int dot = n.IndexOf(". ", StringComparison.Ordinal);
        return (dot > 0 ? n[..dot] : n).TrimEnd('.', ',');
    }

    [GeneratedRegex(@"^No\.\s*\d+\s+")]
    private static partial Regex Ranking();

    // Both sides of an unlabelled "A - B" should read like names, not "Men's Health - All-Natural Secret for Prostate Health".
    private static bool LooksLikeTeams(ProgramTitle parsed) =>
        parsed.TeamNames.All(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: <= 4 } words &&
                                  words.All(w => !char.IsLower(w[0]) || w is "de" or "del" or "da" or "do" or "van" or "von" or "of" or "y"));

    private static string CleanTitle(string title)
    {
        string t = ProgramTitle.WithoutUpcoming(title);
        return ProgramTitle.WithoutLive(t);
    }

    private static string? GuessFromTeams(ProgramTitle parsed)
    {
        var suffixes = parsed.Teams.SelectMany(t => t.Suffix).ToHashSet();
        if (suffixes.Contains("hc")) return "Ice hockey";
        if (suffixes.Contains("ibk") || suffixes.Contains("ibf")) return "Floorball";
        if (suffixes.Contains("hk")) return "Handball";
        if (suffixes.Contains("ff") || suffixes.Contains("fk") || suffixes.Contains("fc")) return "Football";
        return null;
    }

    // The title's own label ("Handboll: Handbollsligan" or "X | Premier League"), unless it only says the sport.
    private static string? League(string title)
    {
        string t = ProgramTitle.WithoutLive(title);
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and <= 24) return t[..colon];
        int pipe = t.LastIndexOf(" | ", StringComparison.Ordinal);
        return pipe > 0 ? t[(pipe + 3)..] : null;
    }

    private static bool ContainsWord(string text, string word)
    {
        int i = 0;
        while ((i = text.IndexOf(word, i, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = i == 0 || text[i - 1] == ' ';
            int end = i + word.Length;
            bool endOk = end == text.Length || text[end] == ' ';
            if (startOk && endOk) return true;
            i++;
        }
        return false;
    }
}
