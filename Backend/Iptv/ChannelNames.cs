using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

// Provider names carry quality, country and stream notes; people call "SE: TV4 SPORT FHD ᴴᴰ" just "TV4 Sport".
public static partial class ChannelNames
{
    public sealed record Info(string Name, int? Quality, string? Country, List<string>? Tags)
    {
        // The name without any country code, for matching against guides.
        public string Core { get; init; } = Name;
    }

    private static readonly string Home = HomeCountry();

    // The viewer's country ("se"), whose code names never show.
    public static string HomeCode => Home;

    [GeneratedRegex(@"\s*\[([^\]]*)\]")]
    private static partial Regex Bracket();

    [GeneratedRegex(@"\s*\(([^()]*)\)")]
    private static partial Regex Paren();

    [GeneratedRegex(@"^\|{0,2}\s*([A-Za-z]{2,6}(?:-[a-z]{2})?)(?:\s+(HD|FHD|SD|UHD|4K))?\s*(\|{1,2}|:|\s-\s)\s*(?=\S)")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^(SE|NO|DK|FI)-(?=[A-Z0-9])")]
    private static partial Regex NordicPrefix();

    [GeneratedRegex(@"\bfull\s?hd\b", RegexOptions.IgnoreCase)]
    private static partial Regex FullHd();

    [GeneratedRegex(@"\b(\d{2})\s?fps\b", RegexOptions.IgnoreCase)]
    private static partial Regex Fps();

    [GeneratedRegex(@"\)\s*Geo$")]
    private static partial Regex GeoAfterCountry();

    [GeneratedRegex(@"^\s*[#=\-*~•★_]{3,}")]
    private static partial Regex Separator();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    // US call signs such as "KTLA" or "WABC".
    [GeneratedRegex(@"^[KW][A-Z]{3}$")]
    private static partial Regex CallSign();

    [GeneratedRegex(@"\s+:(?=\s|$)")]
    private static partial Regex SpaceBeforeColon();

    [GeneratedRegex(@"\.([a-z]{2})(?:@|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CountrySuffix();

    [GeneratedRegex(@"^\[[^\]]*\]\.?")]
    private static partial Regex ProviderCode();

    [GeneratedRegex(@"[/_\-.](2160|1080|720|576|480|360)p(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex UrlResolution();

    private static readonly Dictionary<string, int> QualityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8K"] = 4320, ["4K"] = 2160, ["UHD"] = 2160, ["2160p"] = 2160, ["FHD"] = 1080, ["1080p"] = 1080, ["1080i"] = 1080,
        ["HD"] = 720, ["720p"] = 720, ["SD"] = 576, ["576p"] = 576, ["576i"] = 576, ["480p"] = 480, ["360p"] = 360
    };

    // Words that only describe the stream; null ones are dropped without a note.
    private static readonly Dictionary<string, string?> NoteWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HEVC"] = "hevc", ["H265"] = "hevc", ["H.265"] = "hevc", ["x265"] = "hevc", ["H264"] = null, ["H.264"] = null, ["HDR"] = null,
        ["50fps"] = "50fps", ["60fps"] = "50fps", ["25fps"] = null, ["30fps"] = null, ["5.1"] = "5.1",
        ["Backup"] = "backup", ["Bkp"] = "backup", ["Alt"] = "backup", ["OFFLINE"] = "offline"
    };

    private static readonly HashSet<string> Codes = new((
        "ad ae af ag al ao ar at au az ba bb bd be bf bg bh bi bj bn bo br bs bt bw by bz ca cd cf cg ch ci cl cm cn co cr cu cv cy cz " +
        "de dj dk dm do dz ec ee eg er es fi fj fr ga gb gd ge gh gm gn gq gr gt gw gy hk hn hr ht hu id ie il in iq ir is it jm jo jp " +
        "ke kg kh km kn kp kr kw kz lb lc li lk lr ls lt lu lv ly ma mc md me mg mk ml mm mn mo mr mu mv mw mx my mz na ne ng ni nl no " +
        "np nz om pa pe pg ph pk pl pr ps pt py qa ro rs ru rw sa sb sc se sg si sk sl sm sn so sr ss sv sy sz td tg th tj tl tm tn tr " +
        "tt tw tz ua ug uk us uy uz va vc ve vn vu ws xk ye za zm zw").Split(' '), StringComparer.OrdinalIgnoreCase);

    // Codes that also end ordinary names ("ABC TV", "8:15PM ET", "SUPER CAR") are left out.
    private static readonly HashSet<string> SuffixCodes = new((
        "SE NO DK FI IS UK GB US CA DE AT CH FR BE NL ES PT IT PL CZ SK HU RO GR TR BR MX AR CL CO PE UA RU AU NZ IE IL " +
        "USA SWE NOR FIN GER MEX ARG BRA ESP ITA POR POL TUR RUS UKR ROU BUL CRO SRB BIH MKD MNE ALB AUS GBR DNK DEU FRA NLD").Split(' '), StringComparer.Ordinal);

    private static readonly Dictionary<string, string> CodeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["swe"] = "se", ["nor"] = "no", ["den"] = "dk", ["dnk"] = "dk", ["fin"] = "fi", ["ger"] = "de", ["deu"] = "de", ["gbr"] = "uk",
        ["usa"] = "us", ["can"] = "ca", ["aus"] = "au", ["fra"] = "fr", ["esp"] = "es", ["ita"] = "it", ["por"] = "pt", ["prt"] = "pt",
        ["bra"] = "br", ["mex"] = "mx", ["arg"] = "ar", ["tur"] = "tr", ["pol"] = "pl", ["ned"] = "nl", ["nld"] = "nl", ["bel"] = "be",
        ["aut"] = "at", ["sui"] = "ch", ["gre"] = "gr", ["rus"] = "ru", ["ukr"] = "ua", ["rou"] = "ro", ["bul"] = "bg", ["cro"] = "hr",
        ["hrv"] = "hr", ["srb"] = "rs", ["bih"] = "ba", ["mkd"] = "mk", ["mne"] = "me", ["alb"] = "al", ["slo"] = "si", ["afg"] = "af",
        ["iran"] = "ir", ["iraq"] = "iq", ["kuw"] = "kw", ["syr"] = "sy", ["jor"] = "jo", ["egy"] = "eg", ["yem"] = "ye", ["uae"] = "ae",
        ["saudi"] = "sa", ["pales"] = "ps", ["chil"] = "cl", ["hung"] = "hu", ["mour"] = "mr", ["latino"] = "latam", ["lat"] = "latam",
        ["car"] = "carib", ["carib"] = "carib", ["latam"] = "latam", ["ku"] = "ku"
    };

    private const string Fancy = "ᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻᶫᴀʙᴄᴅᴇꜰɢʜɪᴊᴋʟᴍɴᴏᴘʀꜱᴛᴜᴠᴡʏᴢ⁰¹²³⁴⁵⁶⁷⁸⁹";
    private const string Plain = "ABDEGHIJKLMNOPRTUVWabcdefghijklmnoprstuvwxyzlABCDEFGHIJKLMNOPRSTUVWYZ0123456789";
    private static readonly Dictionary<char, char> Folds = Fancy.Zip(Plain).ToDictionary(p => p.First, p => p.Second);

    private static readonly HashSet<string> CommonWords = new((
        "the and one two six ten new big top max pop hit mix red joy fun gem sky fox " +
        "news kids golf film life live play gold more love home food star time true wild west east best hits rock jazz club cine " +
        "docs real game auto moto fire baby mini mega info zone show epic euro asia arab plus nova blue pink face barn deco crime " +
        "arts dark city land sun bang drama nat geo sur al der die das").Split(' '), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SmallWords = new("de la las los el del di da do dos das du des of and the och og und et y".Split(' '), StringComparer.Ordinal);

    private static readonly HashSet<string> Acronyms = new("ESPN ESPNU DAZN NASA UEFA FIFA ARTE WION IRIB BBCA CBEEBIES TGCOM".Split(' '), StringComparer.Ordinal);

    // For matching names to guides: every "[...]" note goes, including ones the display name keeps ("Sport 1 [UA-UA]").
    public static string Clean(string name) => Matchable(name).Name;

    // The matching name together with the country the name gave.
    public static (string Name, string? Country) Matchable(string name)
    {
        var info = Parse(name);
        string bare = Spaces().Replace(Bracket().Replace(info.Core, " "), " ").Trim();
        return (bare.Length > 0 ? bare : info.Core, info.Country);
    }

    public static Info Parse(string raw)
    {
        string name = raw.Trim();
        if (name.Length == 0) return new Info(name, null, null, null);
        if (Separator().IsMatch(name) || (IsDecoration(name[0]) && IsDecoration(name[^1])))
            return new Info(Spaces().Replace(name, " "), null, null, ["heading"]);

        // "(396p)" is measured; an "HD" next to it is only a claim.
        int? quality = null, exact = null;
        string? country = null;
        List<string>? tags = null;
        string? from = null;
        void Found(string? code, string where)
        {
            if (code == null || country != null) return;
            country = code;
            from = where;
        }
        void Note(string? tag)
        {
            if (tag != null && !(tags ??= new()).Contains(tag)) tags.Add(tag);
        }
        void Quality(int q) => quality = Math.Max(quality ?? 0, q);

        string s = Symbols(name, Note, ref country);
        if (country != null) from = "prefix";
        s = s.Replace('_', ' ');
        s = FullHd().Replace(s, "FHD");
        s = Fps().Replace(s, "$1fps");
        if (GeoAfterCountry().IsMatch(s))
        {
            s = GeoAfterCountry().Replace(s, ")");
            Note("geo");
        }

        s = Bracket().Replace(s, m =>
        {
            bool atStart = m.Index == 0 && m.Length < s.Length;
            var parts = m.Groups[1].Value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool known = parts.Length > 0;
            foreach (var p in parts)
            {
                string l = p.ToLowerInvariant();
                if (l.StartsWith("geo")) Note("geo");
                else if (l.StartsWith("not 24") || l == "live events only") Note("part-time");
                else if (QualityWords.TryGetValue(p, out int q)) Quality(q);
                else known = false;
            }
            if (known) return " ";
            if (atStart && !m.Groups[1].Value.Contains(' '))
            {
                Found(Code(m.Groups[1].Value), "prefix");
                return " ";
            }
            return m.Value;
        });

        s = Paren().Replace(s, m =>
        {
            string p = m.Groups[1].Value.Trim();
            if (p.Length >= 4 && (p[^1] is 'p' or 'i') && int.TryParse(p[..^1], out int lines))
            {
                exact = lines;
                return " ";
            }
            if (QualityWords.TryGetValue(p, out int q))
            {
                Quality(q);
                return " ";
            }
            switch (p.ToLowerInvariant())
            {
                case "geo" or "geo-blocked":
                    Note("geo");
                    return " ";
                case "b" or "backup" or "alt":
                    Note("backup");
                    return " ";
            }
            if (p.Length is 2 or 3 && p.All(char.IsAsciiLetterUpper) && Code(p) is { } c && (p.Length == 2 || SuffixCodes.Contains(p)))
            {
                Found(c, "paren");
                return " ";
            }
            return m.Value;
        });

        s = Spaces().Replace(s, " ").Trim();
        for (int i = 0; i < 2; i++)
        {
            var m = Prefix().Match(s);
            if (!m.Success || Code(m.Groups[1].Value) is not { } c) break;
            string rest = s[m.Length..];
            // "IN | EVANSVILLE | 14 WFIE" is a US state and city, not India.
            if (m.Groups[3].Value.StartsWith('|') && rest.Contains(" | ")) break;
            Found(c, "prefix");
            if (m.Groups[2].Success) Quality(QualityWords[m.Groups[2].Value]);
            s = rest;
        }
        if (QualityPrefix(s) is { } qp)
        {
            Quality(qp.q);
            s = s[qp.length..];
        }
        if (NordicPrefix().Match(s) is { Success: true } np)
        {
            Found(np.Groups[1].Value.ToLowerInvariant(), "prefix");
            s = s[np.Length..];
        }

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1)
        {
            string w = words[^1].TrimEnd('*');
            if (w.Length == 0 || w is "-" or "|" or ":") { }
            else if (QualityWords.TryGetValue(w, out int q))
            {
                // "Rai 4K" and "Love Nature 4K" are channels of their own.
                if (q > 1080) break;
                Quality(q);
            }
            else if (NoteWords.TryGetValue(w, out var tag)) Note(tag);
            // "SE: Eurosport 1 DE" is the German feed in a Swedish list; that code stays in the name.
            else if (SuffixCodes.Contains(w) && (country == null || Code(w) == country)) Found(Code(w), "suffix");
            else if (w.Length == 3 && w[0] == '.' && Codes.Contains(w[1..]) && (country == null || Code(w[1..]) == country)) Found(Code(w[1..]), "paren");
            else if (w.Length is 4 or 5 && w[0] == '-' && w[1..].All(char.IsAsciiLetterUpper)) { }
            else break;
            words.RemoveAt(words.Count - 1);
        }
        // Quality words inside the name ("Fox Sports HD (Argentina)"); a leading one may be the brand ("HD Life").
        for (int i = words.Count - 1; i >= 1; i--)
        {
            if (QualityWords.TryGetValue(words[i], out int q) && q <= 1080 && (words[i].ToUpperInvariant() == words[i] || char.IsAsciiDigit(words[i][0])))
            {
                Quality(q);
                words.RemoveAt(i);
            }
        }

        string result = string.Join(' ', words).Trim(' ', '|', ':', '-', '\u2013', '*');
        result = FixCaps(SpaceBeforeColon().Replace(result, ":"));
        string core = result.Length > 0 ? result : name;
        // Only the viewer's own country code goes; "DK: TV3" keeps its tag, which also keeps the Swedish guide data off it.
        string display = country == null || from == null || country == Home ? core : from switch
        {
            "suffix" => $"{core} {country.ToUpperInvariant()}",
            "paren" => $"{core} ({country.ToUpperInvariant()})",
            _ => $"{country.ToUpperInvariant()}: {core}"
        };
        return new Info(display, exact ?? quality, country, tags) { Core = core };
    }

    // Copies what the name says about the stream onto the item; already cleaned names keep what they had.
    public static void Apply(CatalogItem item)
    {
        var info = Parse(item.Name);
        item.Name = info.Name;
        item.Quality ??= info.Quality;
        item.Country ??= info.Country ?? CountryOf(item.EpgId);
        if (info.Tags != null) item.Tags = item.Tags == null ? info.Tags : item.Tags.Union(info.Tags).ToList();
    }

    // "https://host/live/1080p/index.m3u8" promises 1080 lines.
    public static int? QualityFromUrl(string url)
    {
        var m = UrlResolution().Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static (int q, int length)? QualityPrefix(string s)
    {
        int bar = s.IndexOf(" | ", StringComparison.Ordinal);
        return bar > 0 && QualityWords.TryGetValue(s[..bar], out int q) ? (q, bar + 3) : null;
    }

    private static string? Code(string token)
    {
        if (token.StartsWith("pluto-", StringComparison.OrdinalIgnoreCase)) token = token[6..];
        if (token.Length == 2 && Codes.Contains(token)) return token.ToLowerInvariant() is "gb" ? "uk" : token.ToLowerInvariant();
        return CodeWords.GetValueOrDefault(token);
    }

    private static bool IsDecoration(char c) =>
        c is >= '\u2190' and <= '\u21FF' or >= '\u2500' and <= '\u27BF' or >= '\u2800' and <= '\u28FF' or >= '\u2900' and <= '\u297F' or '\u2022';

    // Small caps and superscripts spell tags ("ꜰʜᴅ", "ᴴᴰ"); circled letters are Free-TV notes (Ⓢ not HD, Ⓖ geo-blocked).
    private static string Symbols(string name, Action<string?> note, ref string? country)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (Folds.ContainsKey(c))
            {
                int start = i;
                var run = new StringBuilder();
                while (i < name.Length && Folds.TryGetValue(name[i], out char p)) { run.Append(p); i++; }
                i--;
                string folded = run.ToString();
                bool digits = folded.All(char.IsAsciiDigit);
                if (QualityWords.ContainsKey(folded)) sb.Append(' ').Append(folded.ToUpperInvariant()).Append(' ');
                else if (digits && start > 0 && char.IsLetterOrDigit(name[start - 1]) && !EndsWithQuality(sb)) sb.Append(folded);
                else sb.Append(' ');
            }
            else if (c is >= '\u24B6' and <= '\u24E9' or >= '\u249C' and <= '\u24B5')
            {
                if (c == 'Ⓖ') note("geo");
                sb.Append(c == 'Ⓢ' ? " SD " : " ");
            }
            else if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.ConvertToUtf32(c, name[i + 1]) is var cp && cp >= 0x1F000)
            {
                i++;
                // Two regional indicators make a flag: the Swedish one is "se".
                if (cp is >= 0x1F1E6 and <= 0x1F1FF && i + 2 < name.Length && char.IsHighSurrogate(name[i + 1]) &&
                    char.ConvertToUtf32(name[i + 1], name[i + 2]) is var cp2 && cp2 is >= 0x1F1E6 and <= 0x1F1FF)
                {
                    string flag = $"{(char)('a' + cp - 0x1F1E6)}{(char)('a' + cp2 - 0x1F1E6)}";
                    country ??= flag == "gb" ? "uk" : flag;
                    i += 2;
                }
                sb.Append(' ');
            }
            else if (IsDecoration(c) || c == '\u00AD') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool EndsWithQuality(StringBuilder sb)
    {
        int end = sb.Length;
        int start = end;
        while (start > 0 && char.IsLetterOrDigit(sb[start - 1])) start--;
        return end > start && QualityWords.ContainsKey(sb.ToString(start, end - start));
    }

    // "TV4 SPORT" reads "TV4 Sport"; short or vowel-less words ("SVT", "CNBC") stay as acronyms.
    private static string FixCaps(string s)
    {
        if (s.Any(char.IsLower) || s.Count(char.IsLetter) < 4) return s;
        var words = s.Split(' ');
        // A single word in capitals is usually how the brand writes itself ("BUZZR", "TGCOM 24").
        if (words.Count(w => w.Any(char.IsLetter)) < 2) return s;
        bool changed = false;
        for (int i = 0; i < words.Length; i++)
        {
            string fixedWord = string.Join('-', words[i].Split('-').Select(part => string.Join('/', part.Split('/').Select(p => FixWord(p, i == 0)))));
            changed |= fixedWord != words[i];
            words[i] = fixedWord;
        }
        return changed ? string.Join(' ', words) : s;
    }

    private static string FixWord(string w, bool first)
    {
        if (w.Length == 0 || !w.All(c => char.IsUpper(c) || c is '\'' or '.' or '!' or '&' or '+')) return w;
        string letters = new(w.Where(char.IsLetter).ToArray());
        string lower = w.ToLowerInvariant();
        if (!first && SmallWords.Contains(lower)) return lower;
        bool vowel = letters.Normalize(NormalizationForm.FormD).IndexOfAny(['A', 'E', 'I', 'O', 'U', 'Y']) >= 0;
        bool word = CommonWords.Contains(letters) || SmallWords.Contains(lower) ||
                    (vowel && !letters.Contains("TV") && !Acronyms.Contains(letters) &&
                     (letters.Length >= 5 || (letters.Length == 4 && !CallSign().IsMatch(letters))));
        if (!word) return w;
        var chars = lower.ToCharArray();
        chars[0] = char.ToUpperInvariant(chars[0]);
        // "L'EQUIPE" becomes "L'Equipe".
        if (chars.Length > 2 && chars[1] == '\'') chars[2] = char.ToUpperInvariant(chars[2]);
        return new string(chars);
    }

    private static string HomeCountry()
    {
        try
        {
            string cc = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
            return cc == "gb" ? "uk" : cc;
        }
        catch
        {
            return "se";
        }
    }

    public static string? CountryOf(string? guideId)
    {
        if (string.IsNullOrEmpty(guideId) || guideId.StartsWith("n:")) return null;
        var m = CountrySuffix().Match(guideId);
        if (!m.Success) return null;
        string cc = m.Groups[1].Value.ToLowerInvariant();
        return cc == "gb" ? "uk" : cc;
    }

    // epgshare01 prefixes channel ids with a provider code that its programmes leave out.
    public static string BareId(string id) => ProviderCode().Replace(id.Trim(), "");

    public static string NameFromId(string id)
    {
        string s = BareId(id);
        int at = s.IndexOf('@');
        if (at > 0) s = s[..at];
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[^1].Length == 2) parts = parts[..^1];
        return string.Join(' ', parts);
    }
}
