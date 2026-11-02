using System.Text.RegularExpressions;

namespace Spana.Backend.Iptv;

// Breaks guide titles like "Studio: Malmö FF - Hammarby IF | Fotboll" into teams, show and category.
public sealed partial class ProgramTitle
{
    private static readonly HashSet<string> ClubSuffixes = new(StringComparer.Ordinal)
    {
        "if", "ff", "bk", "ik", "fc", "hc", "sk", "fk", "is", "ifk", "aif", "afc", "cf", "sc", "sv", "ac", "hk", "bois", "ibk", "ibf"
    };

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "fotboll", "fotball", "football", "soccer", "ishockey", "hockey", "handboll", "innebandy",
        "allsvenskan", "hockeyallsvenskan", "superettan", "damallsvenskan", "shl", "studio", "live", "direkt",
        "highlights", "hojdpunkter", "the", "and", "och", "vs", "v", "mot"
    };

    private static readonly string[] Separators = [" - ", " \u2013 ", " \u2014 ", " vs. ", " vs ", " v. ", " v ", " mot ", " x "];

    // "Rangers at Bruins" and "Lakers @ Celtics" name the away team first.
    private static readonly string[] AwayFirst = [" at ", " @ "];

    private static readonly string[] Versus = [" vs. ", " vs ", " v. ", " v ", " x "];

    [GeneratedRegex(@"^(F[uú]tbol|Baloncesto|Balonmano|Tenis|Ciclismo|Motociclismo|Automovilismo|Atletismo|Boxeo|Rugby|Golf)\.\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SportDot();

    public string Show { get; }
    public string? Category { get; }
    public List<TeamKey> Teams { get; } = new();
    public List<string> TeamNames { get; } = new();
    // The title says it is live ("Live: ...", "En vivo: ...", "(L)").
    public bool Live { get; }
    // The teams were split by "vs", "v" or "at" rather than a dash, so it is a head-to-head even in individual sports.
    internal bool IsVersus { get; private set; }

    [GeneratedRegex(@"^(?:live|en vivo|en directo|directo|en direct|direct|ao vivo|em direto|in diretta|diretta)(?:\s*[:|]|\s+-)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LivePrefix();

    [GeneratedRegex(@"^live\s+(?=\S.*(?: v | vs\.? | - |: ))", RegexOptions.IgnoreCase)]
    private static partial Regex LiveWord();

    [GeneratedRegex(@"(?:\s*[\(\[](?:l|live|en vivo|directo|en directo|ao vivo|live tv)[\)\]]|\s+[-|]\s*(?:live|en vivo|directo)|(?-i:\s+LIVE))$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveSuffix();

    [GeneratedRegex(@"\s+possible\s+overtime$|\s*\((?:ot|so)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex OvertimeSuffix();

    [GeneratedRegex(@"\b(19|20)\d\d\b")]
    private static partial Regex Year();

    public ProgramTitle(string title) : this(title, null) { }

    // Placeholders some guides put before a coming show: "Upcoming (X)", "Upcoming - X".
    public static string WithoutUpcoming(string title)
    {
        string t = title.Trim();
        if (!t.StartsWith("Upcoming", StringComparison.OrdinalIgnoreCase)) return t;
        if (t.Length > 10 && t[8] == ' ' && t[9] == '(' && t.EndsWith(')')) return t[10..^1].Trim();
        if (t.Length > 11 && (t[8..11] == " - " || t[8..10] == ": ")) return t[(t[8] == ':' ? 10 : 11)..].Trim();
        return t;
    }

    // "Live: NHL Hockey" shown as "NHL Hockey"; the card has its own live badge.
    public static string WithoutLive(string title)
    {
        string t = LivePrefix().Replace(title.Trim(), "");
        t = LiveWord().Replace(t, "");
        t = LiveSuffix().Replace(t, "").Trim();
        return t.Length > 0 ? t : title;
    }

    // Teams known from elsewhere (the description, a match source) are used when the title has none.
    internal ProgramTitle(string title, IReadOnlyList<string>? fallbackTeams, bool sportContext = false)
    {
        string t = title.Trim();
        t = WithoutUpcoming(t);
        // Short channel tags in front of the title: "4K |Liverpool - Arsenal".
        int tag = t.IndexOf('|');
        if (tag is > 0 and <= 6) t = t[(tag + 1)..].Trim();

        string before = t;
        t = LivePrefix().Replace(t, "");
        t = LiveWord().Replace(t, "");
        t = LiveSuffix().Replace(t, "").Trim();
        Live = t.Length != before.Length;
        if (t.Length == 0) t = before;

        int pipe = t.LastIndexOf(" | ", StringComparison.Ordinal);
        if (pipe > 0)
        {
            Category = CatalogService.Normalize(t[(pipe + 3)..]);
            t = t[..pipe];
        }

        // "Juventus vs. Napoli; Italian Serie A Soccer" ends with its league.
        int semi = t.IndexOf("; ", StringComparison.Ordinal);
        if (semi > 0)
        {
            Category ??= CatalogService.Normalize(t[(semi + 2)..]);
            t = t[..semi];
        }

        // Spanish guides write "Fútbol. Tenerife - FC Andorra".
        var dotted = SportDot().Match(t);
        if (dotted.Success)
        {
            Category ??= CatalogService.Normalize(dotted.Groups[1].Value);
            t = t[dotted.Length..];
        }

        string? prefix = null;
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and <= 24)
        {
            prefix = t[..colon];
            Category ??= CatalogService.Normalize(prefix);
            t = t[(colon + 2)..];
        }

        int paren = t.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && t.EndsWith(')')) t = t[..paren];
        bool overtime = OvertimeSuffix().IsMatch(t);
        if (overtime) t = OvertimeSuffix().Replace(t, "").Trim();

        Show = CatalogService.Normalize(t);

        // "AFL Mini - Adelaide v Western Bulldogs": the versus part is the matchup.
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        string? inner = dash > 0 ? t.Split(" - ").FirstOrDefault(s => Versus.Any(v => s.Contains(v, StringComparison.Ordinal))) : null;
        if (inner == null || !Split(inner, Versus, false, false) || Teams.Count < 2)
        {
            Teams.Clear();
            TeamNames.Clear();
            if (!Split(t, Separators, false, false))
                Split(t, AwayFirst, true, sportContext || Category != null || overtime);
        }
        // "Australia v India: 1st ODI" names the teams before the colon.
        if (Teams.Count == 0 && prefix != null && Versus.Any(v => prefix.Contains(v, StringComparison.Ordinal)))
        {
            Split(prefix, Versus, false, false);
            if (Teams.Count < 2) { Teams.Clear(); TeamNames.Clear(); }
        }
        // "EHF Men's Champions League: Aalborg Håndbold vs RK Celje": a label too long for a category prefix.
        int label = prefix == null && Teams.Count == 0 ? t.IndexOf(": ", StringComparison.Ordinal) : -1;
        if (label is > 24 and <= 48 && Versus.Any(v => t.IndexOf(v, label, StringComparison.Ordinal) > label))
        {
            Split(t[(label + 2)..], Versus, false, false);
            if (Teams.Count < 2) { Teams.Clear(); TeamNames.Clear(); }
            else Category ??= CatalogService.Normalize(t[..label]);
        }

        // "Liverpool-Arsenal" without spaces: only split when both sides are teams we already know,
        // so names like "Saint-Etienne" stay whole.
        if (Teams.Count == 0 && t.Count(c => c == '-') == 1)
        {
            var parts = t.Split('-', StringSplitOptions.TrimEntries);
            if (parts.All(p => p.Length >= 3 && p.Split(' ').Length <= 4))
            {
                var keys = parts.Select(p => TeamKey.From(StripEvent(p))).ToList();
                if (keys.All(k => k.Core.Count > 0 && MatchDataService.LeagueOf(k) != null))
                {
                    Teams.AddRange(keys);
                    TeamNames.AddRange(parts.Select(StripEvent));
                }
            }
        }

        if (Teams.Count == 0 && fallbackTeams is { Count: 2 })
        {
            foreach (var name in fallbackTeams)
            {
                var key = TeamKey.From(name);
                if (key.Core.Count == 0) { Teams.Clear(); TeamNames.Clear(); break; }
                Teams.Add(key);
                TeamNames.Add(name);
            }
        }
    }

    // Returns whether a separator decided the title (teams found or clearly not a matchup).
    private bool Split(string t, string[] separators, bool awayFirst, bool sportContext)
    {
        foreach (var sep in separators)
        {
            var parts = t.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            if (parts.Any(p => p.Split(' ').Length > 5 || p.IndexOfAny(['(', ')', '|', ':']) >= 0 || Year().IsMatch(p))) return true;
            // "Murder at the Vicarage" is not a game: both sides must look like names, and single words need a sport context.
            if (awayFirst && !parts.All(p => LooksLikeName(p) && (sportContext || p.Contains(' ') || KnownTeam(p)))) return true;
            if (awayFirst) Array.Reverse(parts);
            IsVersus = sep != " - " && sep != " \u2013 " && sep != " \u2014 ";
            foreach (var raw in parts)
            {
                var p = StripEvent(raw);
                var key = TeamKey.From(p);
                if (key.Core.Count == 0) continue;
                Teams.Add(key);
                TeamNames.Add(p);
            }
            return true;
        }
        return false;
    }

    private static bool LooksLikeName(string side)
    {
        var words = side.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= 4 && words.All(w => char.IsUpper(w[0]) || char.IsDigit(w[0]));
    }

    private static bool KnownTeam(string side)
    {
        var key = TeamKey.From(side);
        return key.Core.Count > 0 && (MatchDataService.LeagueOf(key) != null || EspnService.IsTeam(key));
    }

    private static readonly Regex EventWords = new(
        @"\s+(International\s+Championship|World\s+Championship|Championship|Championships|Open|Masters|Grand\s+Prix|Classic|Invitational|Trophy|Tour|Series|Finals?|Semifinal|Kvartsfinal)\b.*$",
        RegexOptions.IgnoreCase);

    // Individual sports often append the event to the second player: "Gary Wilson International Championship".
    public static string StripEvent(string side)
    {
        var cut = EventWords.Replace(side, "").Trim();
        return cut.Length >= 2 ? cut : side;
    }

    public sealed class TeamKey
    {
        public HashSet<string> Core { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Suffix { get; } = new(StringComparer.Ordinal);

        public static TeamKey From(string name)
        {
            var key = new TeamKey();
            foreach (var w in CatalogService.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ClubSuffixes.Contains(w)) key.Suffix.Add(w);
                else if (w.Length > 1 && !Noise.Contains(w) && !w.All(char.IsDigit)) key.Core.Add(Stem(w));
            }
            return key;
        }

        // Swedish genitive: "Djurgårdens IF" and "Djurgården" are the same club.
        private static string Stem(string w) => w.Length > 5 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;

        public bool SameAs(TeamKey other) => Core.SetEquals(other.Core) && Suffix.SetEquals(other.Suffix);
    }

    // 1 for the same club, lower when one name only contains the other ("Hammarby" vs "Hammarby TFF"),
    // and much lower when both carry different club suffixes ("Malmö FF" vs "HK Malmö").
    public static double TeamSimilarity(TeamKey a, TeamKey b)
    {
        if (a.Core.Count == 0 || b.Core.Count == 0) return 0;
        var (small, large) = a.Core.Count <= b.Core.Count ? (a.Core, b.Core) : (b.Core, a.Core);
        if (!small.IsSubsetOf(large)) return 0;
        double score = (double)small.Count / large.Count;
        if (a.Suffix.Count > 0 && b.Suffix.Count > 0 && !a.Suffix.Overlaps(b.Suffix)) score *= 0.25;
        return score;
    }
}
