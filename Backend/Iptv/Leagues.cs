using System.Text.RegularExpressions;

namespace Spana.Backend.Iptv;

// League names arrive in many shapes ("Allsvenskan (H)", "Fotboll: Engelska Premier League", "OBOS Damallsvenskan").
public static partial class Leagues
{
    private static readonly string[] Nationalities =
    [
        "engelska", "italienska", "tyska", "spanska", "franska", "svenska", "norska", "danska", "finska", "nederlandska",
        "portugisiska", "skotska", "belgiska", "turkiska", "amerikanska", "english", "italian", "german", "spanish", "french",
        "dutch", "portuguese", "scottish", "belgian", "turkish", "brazilian", "argentine", "mexican", "polish", "swiss", "austrian"
    ];

    private static readonly string[] Sponsors = ["obos", "unibet", "betsson", "svenska spel", "allsvenskan presented by"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["laliga"] = "la liga",
        ["primera division"] = "la liga",
        ["epl"] = "premier league",
        ["uefa champions league"] = "champions league",
        ["uefa europa league"] = "europa league",
        ["uefa conference league"] = "conference league",
        ["uefa europa conference league"] = "conference league",
        ["fotbolls vm"] = "world cup",
        ["vm"] = "world cup",
        ["fifa world cup"] = "world cup",
        ["em"] = "euro",
        ["uefa euro"] = "euro",
        ["formel 1"] = "formula 1",
        ["f1"] = "formula 1",
        ["formula one"] = "formula 1",
        ["svenska hockeyligan"] = "shl",
        ["national hockey league"] = "nhl",
        ["national basketball association"] = "nba",
        ["national football league"] = "nfl",
        ["major league baseball"] = "mlb",
        ["major league soccer"] = "mls",
        ["ncaa football"] = "college football",
        ["ncaaf"] = "college football",
        ["ncaa basketball"] = "college basketball",
        ["ncaam"] = "college basketball",
        ["ncaa men s basketball"] = "college basketball",
        ["mens college basketball"] = "college basketball",
        ["liga bbva mx"] = "liga mx",
        ["serie a tim"] = "serie a",
        ["serie a enilive"] = "serie a",
        ["laliga ea sports"] = "la liga",
        ["1 bundesliga"] = "bundesliga",
        ["uefa nations league"] = "nations league",
        ["penny del"] = "del",
        ["deutsche eishockey liga"] = "del",
        ["kontinental hockey league"] = "khl",
        ["american hockey league"] = "ahl",
        ["sm liiga"] = "liiga",
        ["tipsport extraliga"] = "extraliga",
        ["czech extraliga"] = "extraliga",
        ["turkish airlines euroleague"] = "euroleague",
        ["euroleague basketball"] = "euroleague",
        ["liga endesa"] = "acb",
        ["liga acb"] = "acb",
        ["nba g league"] = "g league",
    };

    // Leagues with a broad audience regardless of where you live.
    private static readonly HashSet<string> TopTier = new(StringComparer.Ordinal)
    {
        "premier league", "la liga", "serie a", "bundesliga", "ligue 1", "champions league", "europa league",
        "conference league", "world cup", "euro", "nations league", "nhl", "nba", "nfl", "mlb", "formula 1", "motogp",
        "atp", "wta", "wimbledon", "pga tour", "ryder cup", "ufc", "euroleague", "six nations", "super bowl"
    };

    private static readonly string[] SportWords =
        ["fotboll", "fotball", "football", "soccer", "futbol", "ishockey", "hockey", "ice hockey", "handboll", "innebandy", "basket",
         "basketball", "baseball", "tennis", "golf", "motorsport", "rugby", "cricket"];

    // League names found anywhere in a title or guide category, with the sport they belong to and how to show them.
    private static readonly (string phrase, string key, string sport, string name)[] Known =
    [
        ("nhl", "nhl", "Ice hockey", "NHL"), ("national hockey league", "nhl", "Ice hockey", "NHL"), ("stanley cup", "nhl", "Ice hockey", "NHL"),
        ("ahl", "ahl", "Ice hockey", "AHL"), ("khl", "khl", "Ice hockey", "KHL"), ("shl", "shl", "Ice hockey", "SHL"),
        ("hockeyallsvenskan", "hockeyallsvenskan", "Ice hockey", "HockeyAllsvenskan"), ("liiga", "liiga", "Ice hockey", "Liiga"),
        ("penny del", "del", "Ice hockey", "DEL"), ("deutsche eishockey liga", "del", "Ice hockey", "DEL"), ("extraliga", "extraliga", "Ice hockey", "Extraliga"),
        ("champions hockey league", "champions hockey league", "Ice hockey", "Champions Hockey League"),
        ("nba", "nba", "Basketball", "NBA"), ("wnba", "wnba", "Basketball", "WNBA"), ("g league", "g league", "Basketball", "NBA G League"),
        ("euroleague", "euroleague", "Basketball", "EuroLeague"), ("eurocup", "eurocup", "Basketball", "EuroCup"),
        ("college basketball", "college basketball", "Basketball", "College basketball"), ("ncaa basketball", "college basketball", "Basketball", "College basketball"),
        ("nfl", "nfl", "American football", "NFL"), ("national football league", "nfl", "American football", "NFL"), ("super bowl", "nfl", "American football", "NFL"),
        ("college football", "college football", "American football", "College football"), ("ncaa football", "college football", "American football", "College football"),
        ("cfl", "cfl", "American football", "CFL"), ("ufl", "ufl", "American football", "UFL"),
        ("mlb", "mlb", "Baseball", "MLB"), ("major league baseball", "mlb", "Baseball", "MLB"), ("world series", "mlb", "Baseball", "MLB"),
        ("milb", "milb", "Baseball", "Minor League Baseball"),
        ("mls", "mls", "Football", "MLS"), ("major league soccer", "mls", "Football", "MLS"), ("nwsl", "nwsl", "Football", "NWSL"),
        ("liga mx", "liga mx", "Football", "Liga MX"), ("premier league", "premier league", "Football", "Premier League"),
        ("la liga", "la liga", "Football", "LaLiga"), ("laliga", "la liga", "Football", "LaLiga"), ("serie a", "serie a", "Football", "Serie A"),
        ("bundesliga", "bundesliga", "Football", "Bundesliga"), ("ligue 1", "ligue 1", "Football", "Ligue 1"),
        ("champions league", "champions league", "Football", "Champions League"), ("europa league", "europa league", "Football", "Europa League"),
        ("conference league", "conference league", "Football", "Conference League"), ("eredivisie", "eredivisie", "Football", "Eredivisie"),
        ("allsvenskan", "allsvenskan", "Football", "Allsvenskan"), ("damallsvenskan", "damallsvenskan", "Football", "Damallsvenskan"),
        ("formula 1", "formula 1", "Motorsport", "Formula 1"), ("formel 1", "formula 1", "Motorsport", "Formula 1"), ("f1", "formula 1", "Motorsport", "Formula 1"),
        ("motogp", "motogp", "Motorsport", "MotoGP"), ("nascar", "nascar", "Motorsport", "NASCAR"), ("indycar", "indycar", "Motorsport", "IndyCar"),
        ("atp", "atp", "Tennis", "ATP"), ("wta", "wta", "Tennis", "WTA"), ("pga tour", "pga tour", "Golf", "PGA Tour"), ("ufc", "ufc", "Combat sports", "UFC"),
        ("six nations", "six nations", "Rugby", "Six Nations"), ("ipl", "ipl", "Cricket", "IPL"), ("big bash", "big bash", "Cricket", "Big Bash"),
    ];

    // The league named in a title or category ("Live: NHL Hockey", "Sports, NBA"), as (key, sport, display name).
    public static (string key, string sport, string name)? Find(params string?[] texts)
    {
        foreach (var raw in texts)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string text = CatalogService.Normalize(raw);
            (string key, string sport, string name)? best = null;
            int bestLength = 0;
            foreach (var (phrase, key, sport, name) in Known)
                if (phrase.Length > bestLength && HasWord(text, phrase, true)) { best = (key, sport, name); bestLength = phrase.Length; }
            if (best != null) return best;
        }
        return null;
    }

    // Words that make a league name another competition: "AFC Champions League", "Canadian Premier League", "2. Bundesliga".
    private static readonly HashSet<string> OtherCompetition = new(StringComparer.Ordinal)
    {
        "afc", "caf", "concacaf", "ofc", "asian", "african", "global", "youth", "women", "womens", "ehf", "cev", "fiba",
        "basketball", "volleyball", "handball", "canadian", "russian", "ukrainian", "egyptian", "welsh", "irish", "indian",
        "israeli", "maltese", "kenyan", "ghana", "nigerian", "jamaican", "bangladesh", "lebanese", "armenian", "2", "3"
    };

    private static bool HasWord(string text, string word, bool sameCompetition = false)
    {
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            if ((i != 0 && text[i - 1] != ' ') || (i + word.Length != text.Length && text[i + word.Length] != ' ')) continue;
            if (sameCompetition && i > 1 && OtherCompetition.Contains(text[(text.LastIndexOf(' ', i - 2) + 1)..(i - 1)])) continue;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parens();

    [GeneratedRegex(@"\b(omgang|round|matchday|speltag|group|grupp|final|semifinal|kvartsfinal)\b.*$")]
    private static partial Regex RoundSuffix();

    public static string? Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string s = Parens().Replace(name, " ");
        int comma = s.IndexOf(',');
        if (comma > 0)
        {
            // "Fotboll, Allsvenskan (H), Omgång 27" keeps the part after the sport.
            var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            s = parts.Length > 1 && SportWords.Contains(CatalogService.Normalize(parts[0])) ? parts[1] : parts[0];
        }
        s = CatalogService.Normalize(s);
        s = RoundSuffix().Replace(s, "").Trim();
        foreach (var w in Sponsors)
            if (s.StartsWith(w + " ", StringComparison.Ordinal)) s = s[(w.Length + 1)..];
        foreach (var w in Nationalities)
            if (s.StartsWith(w + " ", StringComparison.Ordinal)) s = s[(w.Length + 1)..];
        foreach (var w in SportWords)
            if (s.StartsWith(w + " ", StringComparison.Ordinal)) s = s[(w.Length + 1)..];
        s = s.Trim();
        // "NHL Hockey" and "Premier League Soccer" are the league itself, "Bundesliga Basketball" is not the football one.
        foreach (var (w, sports) in SportSuffixes)
        {
            if (!s.EndsWith(" " + w, StringComparison.Ordinal)) continue;
            string rest = s[..^(w.Length + 1)];
            var known = Known.FirstOrDefault(k => k.phrase == rest);
            if (known.phrase != null && sports.Contains(known.sport)) s = rest;
            break;
        }
        if (s.Length < 2 || SportWords.Contains(s)) return null;
        return Aliases.GetValueOrDefault(s, s);
    }

    private static readonly (string word, string[] sports)[] SportSuffixes =
    [
        ("ice hockey", ["Ice hockey"]), ("hockey", ["Ice hockey"]), ("basketball", ["Basketball"]), ("baseball", ["Baseball"]),
        ("soccer", ["Football"]), ("futbol", ["Football"]), ("football", ["Football", "American football"])
    ];

    public static string? DisplayName(string? key) => key == null ? null : Known.FirstOrDefault(k => k.key == key).name;

    public static bool IsTopTier(string? key) => key != null && TopTier.Contains(key);

    // Big league names other countries use too: Brazil's "Série A", Algeria's "Ligue 1", Russia's "Premier League".
    private static readonly Dictionary<string, string> TopTierHome = new(StringComparer.Ordinal)
    {
        ["premier league"] = "ENG", ["la liga"] = "ESP", ["serie a"] = "ITA", ["bundesliga"] = "GER", ["ligue 1"] = "FRA"
    };

    /// <summary>Top tier, unless the league is known to be another country's namesake.</summary>
    public static bool IsTopTier(string? key, string? country) =>
        IsTopTier(key) && (country is null or "" or "INT" || !TopTierHome.TryGetValue(key!, out var home) ||
                           string.Equals(country, home, StringComparison.OrdinalIgnoreCase));

    // Each country's top divisions, keyed by league name, with the country they belong to.
    private static readonly Dictionary<string, string> NationalTop = new(StringComparer.Ordinal)
    {
        ["allsvenskan"] = "SWE", ["damallsvenskan"] = "SWE", ["shl"] = "SWE", ["handbollsligan"] = "SWE",
        ["svenska superligan"] = "SWE", ["ssl"] = "SWE", ["basketligan"] = "SWE", ["elitserien"] = "SWE",
        ["eliteserien"] = "NOR", ["toppserien"] = "NOR", ["fjordkraft ligaen"] = "NOR",
        ["superliga"] = "DEN", ["superligaen"] = "DEN", ["3f superliga"] = "DEN", ["metal ligaen"] = "DEN",
        ["veikkausliiga"] = "FIN", ["liiga"] = "FIN",
        ["eredivisie"] = "NED", ["primeira liga"] = "POR", ["liga portugal"] = "POR", ["scottish premiership"] = "SCO",
        ["premiership"] = "SCO", ["belgian pro league"] = "BEL", ["jupiler pro league"] = "BEL", ["super lig"] = "TUR",
        ["austrian bundesliga"] = "AUT", ["swiss super league"] = "SUI", ["super league"] = "SUI", ["khl"] = "RUS", ["del"] = "GER",
        ["mls"] = "USA", ["nwsl"] = "USA", ["wnba"] = "USA", ["college football"] = "USA", ["college basketball"] = "USA",
        ["cfl"] = "CAN", ["liga mx"] = "MEX", ["brasileirao"] = "BRA", ["liga profesional"] = "ARG", ["a league"] = "AUS",
        ["afl"] = "AUS", ["nrl"] = "AUS", ["extraliga"] = "CZE", ["ekstraklasa"] = "POL", ["acb"] = "ESP", ["lega basket"] = "ITA",
        ["top 14"] = "FRA", ["premiership rugby"] = "ENG", ["ipl"] = "IND", ["big bash"] = "AUS", ["npb"] = "JPN", ["kbo"] = "KOR",
    };

    // The viewer's country as a three-letter code (SWE), from the Windows region setting.
    public static readonly string HomeCountry = DetectHomeCountry();

    private static string DetectHomeCountry()
    {
        try { return System.Globalization.RegionInfo.CurrentRegion.ThreeLetterISORegionName.ToUpperInvariant(); }
        catch { return "SWE"; }
    }

    /// <summary>
    /// How big a match is regardless of personal taste: 3 for your own country's top leagues and the big international
    /// ones, 2 for other countries' top leagues, 1 for anything with a broadcaster image or live data, 0 otherwise.
    /// </summary>
    public static int Prominence(string? leagueName, string? country, bool covered)
    {
        string? key = Key(leagueName);
        if (IsTopTier(key, country)) return 3;
        if (IsTopTier(key)) return string.Equals(country, HomeCountry, StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        if (key != null && NationalTop.TryGetValue(key, out var home))
            return string.Equals(home, HomeCountry, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(country, HomeCountry, StringComparison.OrdinalIgnoreCase) && home == country ? 3 : 2;
        return covered ? 1 : 0;
    }

    public static bool IsCup(string league) =>
        league.Contains("cup") || league.Contains("champions") || league.Contains("europa") || league.Contains("conference") ||
        league.Contains("friendl") || league.Contains("vanskap") || league.Contains("qualif") || league == "euro";

    public static string Slug(string? sport) => string.IsNullOrEmpty(sport) ? "x" : CatalogService.Normalize(sport).Replace(' ', '-');

    // "Fotboll: UEFA Champions League" or "Live: NHL": the title is the league and nothing else, no show or match.
    public static bool IsLeagueOnly(string title)
    {
        string t = ProgramTitle.WithoutLive(title);
        int pipe = t.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe > 0) t = t[..pipe];
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and <= 24 && SportWords.Contains(CatalogService.Normalize(t[..colon]))) t = t[(colon + 2)..];
        string? key = Key(t);
        return key != null && Find(t)?.key == key;
    }

    // "Ishockey: SHL" or "Fotboll: Allsvenskan" carries sport and league in the title itself.
    public static (string? sport, string? key) FromTitle(string title)
    {
        int colon = title.IndexOf(": ", StringComparison.Ordinal);
        if (colon is <= 0 or > 24)
        {
            var whole = Key(title);
            if (IsTopTier(whole)) return (null, whole);
            return Find(title) is { } hit ? (hit.sport, hit.key) : (null, null);
        }
        string head = CatalogService.Normalize(title[..colon]);
        string tail = title[(colon + 2)..];
        if (SportWords.Contains(head))
        {
            int pipe = tail.IndexOf(" | ", StringComparison.Ordinal);
            return (SportService.Classify(head, head), Key(pipe > 0 ? tail[..pipe] : tail));
        }
        var key = Key(title[..colon]);
        if (IsTopTier(key)) return (null, key);
        return Find(title) is { } found ? (found.sport, found.key) : (null, null);
    }
}
