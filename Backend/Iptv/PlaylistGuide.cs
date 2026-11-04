using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Serilog;
using Spana.Backend.Core;

namespace Spana.Backend.Iptv;

// Guides from public XMLTV files, for playlist accounts and for the channels an Xtream provider's guide leaves empty.
public static partial class PlaylistGuide
{
    private const string EpgShare = "https://epgshare01.online/epgshare01/";
    private const string Mjh = "https://i.mjh.nz/";
    private const string IptvEpg = "https://iptv-epg.org/files/";
    private const int MaxCountries = 8;
    private const int MaxFallbackCountries = 16;
    private const int MaxOtherLinks = 3;
    private const int MaxServiceFiles = 4;
    private const int IptvEpgTop = 3;
    private const long MaxFileBytes = 15_000_000;
    private const long Hour = 3_600_000;

    private static readonly HashSet<string> PlutoTv = ["ar", "br", "ca", "cl", "de", "dk", "es", "fr", "gb", "it", "mx", "no", "se", "us"];
    private static readonly HashSet<string> SamsungTvPlus = ["at", "ca", "ch", "de", "es", "fr", "gb", "in", "it", "kr", "us"];
    private static readonly HashSet<string> Plex = ["au", "ca", "es", "fr", "gb", "mx", "nz", "us"];
    // A week of every channel: 11 to 60 MB for these.
    private static readonly HashSet<string> IptvEpgLarge = ["us", "gb", "nl", "pl"];
    private static readonly Dictionary<string, string[]> MjhExtras = new()
    {
        ["au"] = ["au/all/epg.xml.gz"],
        ["nz"] = ["nz/epg.xml.gz"],
        ["za"] = ["DStv/za.xml.gz"],
    };

    [GeneratedRegex(@"epg_ripper_([A-Z]{2})\d+\.xml\.gz$")]
    private static partial Regex EpgShareFile();

    // Broadcaster files such as epg_ripper_BEIN1.
    [GeneratedRegex(@"epg_ripper_([A-Z][A-Z_]+[A-Z])\d+\.xml\.gz$")]
    private static partial Regex EpgShareService();

    [GeneratedRegex(@"href=""(epg_ripper_[A-Z_]+\d+\.xml\.gz)"">[^<]*</a>\s+(\d{2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2})\s+(\d+)")]
    private static partial Regex ListingRow();

    [GeneratedRegex(@"^\d{2}-([a-z]{2})-")]
    private static partial Regex FileCountryTag();

    [GeneratedRegex(@"(?:epg_ripper_([A-Z]{2})\d+|-([a-z]{2}))\.xml\.gz$")]
    private static partial Regex FileCountryOld();

    // "SE: TV4", "|SE| TV4", "||IT|| Rai 1", "DE : ZDF", "SE - Kanal 5".
    [GeneratedRegex(@"^[\s|\[(]*([A-Za-z]{2,3})(?:\s*[:|]+|\s+[-\u2013]+(?=\s))[\s|]*(?=\S)")]
    private static partial Regex CountryPrefix();

    [GeneratedRegex(@"\s([A-Z]{2})$")]
    private static partial Regex CountryTail();

    // "E4 +1" and "Film4+1" are timeshifts; "ESPN+ 2" is not.
    [GeneratedRegex(@"\s?\+(\d{1,2})(?=\s|$)")]
    private static partial Regex Timeshift();

    [GeneratedRegex(@"^Plus(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ShiftFeed();

    [GeneratedRegex(@"\b[A-Z]{2,3}\b")]
    private static partial Regex UpperCode();

    private static readonly HashSet<string> SkippedServices = new(StringComparer.Ordinal) { "ALL_SOURCES", "DUMMY_CHANNELS", "US_LOCALS" };

    // Gap fillers some public files use; a channel showing nothing is better than these.
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
        { "No Data", "No data available", "No Information", "No programme information", "Channel No Longer Available" };

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
        { "hd", "fhd", "sd", "uhd", "4k", "8k", "hevc", "h265", "h264", "hdr", "fps", "50fps", "60fps", "1080p", "720p", "1080", "720", "hq", "backup", "se", "swe", "sweden", "sverige", "beta" };

    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "se", "no", "dk", "fi", "is", "uk", "gb", "ie", "us", "ca", "au", "nz", "de", "at", "ch", "fr", "be", "nl", "lu", "it", "es", "pt", "br", "mx",
        "ar", "co", "cl", "pe", "ve", "ec", "uy", "py", "bo", "cr", "pr", "gt", "hn", "sv", "ni", "tr", "in", "pk", "bd", "lk", "pl", "cz", "sk", "hu", "ro",
        "bg", "gr", "cy", "ru", "ua", "by", "rs", "hr", "ba", "si", "mk", "al", "il", "ae", "sa", "qa", "kw", "eg", "ma", "dz", "tn", "za", "ng", "gh", "ke",
        "ph", "th", "vn", "my", "sg", "cn", "hk", "tw", "jp", "kr"
    };

    private static readonly Dictionary<string, string> Codes3 = new(StringComparer.Ordinal)
    {
        ["usa"] = "us", ["arg"] = "ar", ["bra"] = "br", ["esp"] = "es", ["ger"] = "de", ["deu"] = "de", ["fra"] = "fr", ["ita"] = "it", ["por"] = "pt",
        ["tur"] = "tr", ["ned"] = "nl", ["nld"] = "nl", ["swe"] = "se", ["nor"] = "no", ["den"] = "dk", ["dnk"] = "dk", ["fin"] = "fi", ["aus"] = "au",
        ["ind"] = "in", ["mex"] = "mx", ["col"] = "co", ["chi"] = "cl", ["chl"] = "cl", ["per"] = "pe", ["can"] = "ca", ["pol"] = "pl", ["rou"] = "ro",
        ["bul"] = "bg", ["gre"] = "gr", ["rus"] = "ru", ["ukr"] = "ua", ["srb"] = "rs", ["cro"] = "hr", ["hrv"] = "hr", ["alb"] = "al", ["isr"] = "il",
        ["irl"] = "ie", ["rsa"] = "za", ["phi"] = "ph", ["pak"] = "pk"
    };

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.Ordinal)
    {
        ["sweden"] = "se", ["swedish"] = "se", ["sverige"] = "se", ["norway"] = "no", ["norwegian"] = "no", ["norge"] = "no", ["denmark"] = "dk",
        ["danish"] = "dk", ["danmark"] = "dk", ["finland"] = "fi", ["finnish"] = "fi", ["suomi"] = "fi", ["united kingdom"] = "uk", ["britain"] = "uk",
        ["british"] = "uk", ["england"] = "uk", ["ireland"] = "ie", ["irish"] = "ie", ["united states"] = "us", ["america"] = "us", ["american"] = "us",
        ["canada"] = "ca", ["canadian"] = "ca", ["australia"] = "au", ["australian"] = "au", ["new zealand"] = "nz", ["germany"] = "de", ["german"] = "de",
        ["deutschland"] = "de", ["austria"] = "at", ["osterreich"] = "at", ["osterrike"] = "at", ["switzerland"] = "ch", ["swiss"] = "ch", ["schweiz"] = "ch",
        ["france"] = "fr", ["french"] = "fr", ["belgium"] = "be", ["belgique"] = "be", ["netherlands"] = "nl", ["dutch"] = "nl", ["holland"] = "nl",
        ["nederland"] = "nl", ["italy"] = "it", ["italian"] = "it", ["italia"] = "it", ["spain"] = "es", ["spanish"] = "es", ["espana"] = "es",
        ["portugal"] = "pt", ["portuguese"] = "pt", ["brazil"] = "br", ["brasil"] = "br", ["mexico"] = "mx", ["argentina"] = "ar", ["colombia"] = "co",
        ["chile"] = "cl", ["peru"] = "pe", ["turkey"] = "tr", ["turkish"] = "tr", ["turkiye"] = "tr", ["india"] = "in", ["indian"] = "in", ["hindi"] = "in",
        ["pakistan"] = "pk", ["bangladesh"] = "bd", ["poland"] = "pl", ["polish"] = "pl", ["polska"] = "pl", ["czech"] = "cz", ["slovakia"] = "sk",
        ["hungary"] = "hu", ["hungarian"] = "hu", ["romania"] = "ro", ["romanian"] = "ro", ["bulgaria"] = "bg", ["bulgarian"] = "bg", ["greece"] = "gr",
        ["greek"] = "gr", ["russia"] = "ru", ["russian"] = "ru", ["ukraine"] = "ua", ["serbia"] = "rs", ["croatia"] = "hr", ["albania"] = "al",
        ["albanian"] = "al", ["israel"] = "il", ["philippines"] = "ph", ["thailand"] = "th", ["south africa"] = "za", ["dstv"] = "za", ["china"] = "cn",
        ["japan"] = "jp", ["korea"] = "kr", ["vietnam"] = "vn", ["malaysia"] = "my"
    };

    private sealed record Source(string Url, string? Country, TimeSpan MaxAge, bool Uncapped = false);
    private sealed record Listed(string Name, bool Usable);
    private readonly record struct NameKey(string Key, string? Alt, string? Country, long Shift, string? Base);
    private sealed record Wanted(CatalogItem Item, string Key, NameKey Name, string? Country, bool UseId);
    private readonly record struct Target(string Key, long Shift);

    private sealed class Index
    {
        public readonly Dictionary<string, List<Target>> Ids = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<Target>> IdShifts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<Target>> Names = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<Target>> NameShifts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string?> Countries = new(StringComparer.OrdinalIgnoreCase);

        public static void Add(Dictionary<string, List<Target>> map, string? key, Target t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new();
            if (!list.Contains(t)) list.Add(t);
        }
    }

    private sealed record Channel(List<NameKey> Names, string? Country, string? Icon);

    private static (DateTimeOffset at, List<Listed> files)? _listing;
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Skipped = new();

    private static string Folder(Account a) => Path.Combine(Storage.CacheRoot, a.Id, "guides");

    private static List<string> Files(Account a) =>
        Directory.GetFiles(Folder(a), "*.xml.gz").Order(StringComparer.Ordinal).ToList();

    public static bool HasFiles(Account a) => Directory.Exists(Folder(a)) && Directory.EnumerateFiles(Folder(a), "*.xml.gz").Any();

    public static Guide? Load(Account a)
    {
        var channels = CatalogService.GetIndex(a, MediaKind.Live)?.All;
        if (channels == null || !HasFiles(a)) return null;
        var files = Files(a);
        var fetchedAt = files.Min(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f)));
        return Build(files, Wants(a, channels, null), fetchedAt, "Playlist guide");
    }

    public static async Task<Guide?> RefreshAsync(Account a, CancellationToken ct)
    {
        var playlist = await M3uCache.GetAsync(a, ct);
        var wanted = Wants(a, playlist.Items[MediaKind.Live], null);
        var sources = await SourcesAsync(wanted, playlist.GuideUrls, MaxCountries, 1, ct);
        var files = await DownloadAsync(a, sources, ct);
        Log.Information("Playlist guide for {Account}: {Files} of {Sources} sources", a.Name, files.Count, sources.Count);
        return files.Count == 0 ? null : Build(files, wanted, DateTimeOffset.UtcNow, "Playlist guide");
    }

    // Public guides for the channels the provider's guide has nothing for; files younger than their max age are reused.
    public static async Task<Guide?> FallbackAsync(Account a, Guide provider, IReadOnlyList<CatalogItem> channels, CancellationToken ct)
    {
        var wanted = Wants(a, channels, provider);
        if (wanted.Count == 0) return null;
        var sources = await SourcesAsync(wanted, [], MaxFallbackCountries, 5, ct);
        var files = await DownloadAsync(a, sources, ct);
        return files.Count == 0 ? null : Build(files, wanted, provider.FetchedAt, "Guide fallback");
    }

    private static List<Wanted> Wants(Account a, IReadOnlyList<CatalogItem> channels, Guide? provider)
    {
        var groups = CatalogService.CategoryNames(a, MediaKind.Live);
        var groupCountry = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool playlist = a.Kind == AccountKind.M3u;
        var list = new List<Wanted>();
        foreach (var c in channels)
        {
            if (provider != null && c.EpgId != null && provider.ByChannel.ContainsKey(c.EpgId)) continue;
            string key = c.EpgId ?? "n:" + CatalogService.Normalize(c.Name);
            if (key.Length <= 2) continue;
            if (!groupCountry.TryGetValue(c.CategoryId, out var gc))
                groupCountry[c.CategoryId] = gc = GroupCountry(groups.GetValueOrDefault(c.CategoryId));
            var name = Keys(c.Name);
            string? fromId = c.EpgId == null || c.EpgId.StartsWith("n:") ? null : Country(ChannelNames.CountryOf(c.EpgId));
            // The viewer's own code is gone from the display name but kept on the item.
            string? own = Country(c.Country) is { } kept && kept != fromId ? kept : null;
            string? named = name.Country ?? own ?? gc;
            // Provider ids are often borrowed from another country ("natgeo.se" in a German group); the name and group are surer there.
            list.Add(new Wanted(c, key, name, playlist ? fromId ?? named : named ?? fromId, playlist || named == null || fromId == null || named == fromId));
        }
        return list;
    }

    private static async Task<List<Source>> SourcesAsync(List<Wanted> wanted, List<string> links, int maxCountries, int minChannels, CancellationToken ct)
    {
        var listing = await EpgShareListingAsync(ct);
        var shared = listing.Where(f => f.Usable).Select(f => EpgShareCountry(f.Name)).OfType<string>().ToHashSet();
        bool HasSource(string cc)
        {
            string mjh = cc == "uk" ? "gb" : cc;
            return shared.Contains(cc) || PlutoTv.Contains(mjh) || SamsungTvPlus.Contains(mjh) || Plex.Contains(mjh) || MjhExtras.ContainsKey(cc);
        }
        // Distinct names, so the SD, HD and FHD copies of a channel count once.
        var counts = wanted.Where(w => w.Country != null).GroupBy(w => w.Country!)
            .ToDictionary(g => g.Key, g => g.Select(w => w.Name.Key).Distinct(StringComparer.Ordinal).Count());
        string home = HomeCountry();
        var countries = counts.Keys
            .Where(c => (counts[c] >= minChannels || c == home) && (listing.Count == 0 || HasSource(c)))
            .OrderByDescending(c => c == home)
            .ThenByDescending(c => counts[c])
            .Take(maxCountries)
            .ToList();

        var sources = new List<Source>();
        void Add(string url, string? cc, double hours)
        {
            if (!sources.Exists(s => s.Url == url)) sources.Add(new Source(url, cc, TimeSpan.FromHours(hours)));
        }
        int others = 0;
        foreach (var link in links)
        {
            if (link.Contains("epgshare01", StringComparison.OrdinalIgnoreCase))
            {
                // Free-TV links every country; only the ones with channels here are worth downloading.
                if (EpgShareCountry(link) is { } cc && countries.Contains(cc)) Add(link, cc, 20);
            }
            else if (others++ < MaxOtherLinks && !sources.Exists(s => s.Url == link))
            {
                sources.Add(new Source(link, null, TimeSpan.FromHours(4), true));
            }
        }

        for (int i = 0; i < countries.Count; i++)
        {
            string cc = countries[i], mjh = cc == "uk" ? "gb" : cc;
            foreach (var f in listing.Where(f => f.Usable && EpgShareCountry(f.Name) == cc)) Add(EpgShare + f.Name, cc, 20);
            if (cc == "us" && listing.FirstOrDefault(f => f.Name.StartsWith("epg_ripper_US_SPORTS")) is { } usSports) Add(EpgShare + usSports.Name, cc, 20);
            if (PlutoTv.Contains(mjh)) Add($"{Mjh}PlutoTV/{mjh}.xml.gz", cc, 4);
            if (SamsungTvPlus.Contains(mjh)) Add($"{Mjh}SamsungTVPlus/{mjh}.xml.gz", cc, 4);
            if (Plex.Contains(mjh)) Add($"{Mjh}Plex/{mjh}.xml.gz", cc, 4);
            foreach (var path in MjhExtras.GetValueOrDefault(cc) ?? []) Add(Mjh + path, cc, 12);
            if (i < IptvEpgTop && !IptvEpgLarge.Contains(mjh)) Add($"{IptvEpg}epg-{mjh}.xml.gz", cc, 24);
        }

        int services = 0;
        foreach (var f in listing)
        {
            var m = EpgShareService().Match(f.Name);
            if (!m.Success || !f.Usable || EpgShareFile().IsMatch(f.Name) || SkippedServices.Contains(m.Groups[1].Value) || m.Groups[1].Value.StartsWith("US_")) continue;
            string token = m.Groups[1].Value.Replace("_", "").ToLowerInvariant();
            if (token.Length < 4 || !wanted.Exists(w => w.Name.Key.Contains(token))) continue;
            if (services++ >= MaxServiceFiles) break;
            Add(EpgShare + f.Name, null, 20);
        }
        return sources;
    }

    private static string? EpgShareCountry(string url)
    {
        var m = EpgShareFile().Match(url);
        return m.Success ? Country(m.Groups[1].Value) : null;
    }

    private static async Task<List<Listed>> EpgShareListingAsync(CancellationToken ct)
    {
        if (_listing is { } l && DateTimeOffset.UtcNow - l.at < TimeSpan.FromDays(1)) return l.files;
        try
        {
            var html = Encoding.UTF8.GetString(await XtreamClient.GetBytesAsync(EpgShare, ct));
            var files = ListingRow().Matches(html).Select(m =>
            {
                bool fresh = !DateTime.TryParseExact(m.Groups[2].Value, "dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ||
                             DateTime.UtcNow - at.ToUniversalTime() < TimeSpan.FromDays(14);
                return new Listed(m.Groups[1].Value, fresh && long.Parse(m.Groups[3].Value) <= MaxFileBytes);
            }).DistinctBy(f => f.Name).ToList();
            if (files.Count == 0)
                files = Regex.Matches(html, @"href=""(epg_ripper_[A-Z_]+\d+\.xml\.gz)""").Select(m => new Listed(m.Groups[1].Value, true)).DistinctBy(f => f.Name).ToList();
            _listing = (DateTimeOffset.UtcNow, files);
            return files;
        }
        catch (Exception ex)
        {
            Log.Debug("epgshare01 listing failed: {Message}", ex.Message);
            return _listing?.files ?? [];
        }
    }

    private static string HomeCountry()
    {
        try { return Country(RegionInfo.CurrentRegion.TwoLetterISORegionName) ?? "se"; }
        catch { return "se"; }
    }

    private static string SafeName(string url)
    {
        var uri = new Uri(url);
        string name = Path.GetFileName(uri.AbsolutePath);
        string host = uri.Host.Split('.')[0];
        string service = uri.Segments.Length > 2 ? uri.Segments[^2].Trim('/') : "";
        return Regex.Replace($"{host}-{service}-{name}".Replace(".xml.gz", "").Replace(".xml", ""), @"[^A-Za-z0-9_-]", "_");
    }

    private static async Task<List<string>> DownloadAsync(Account a, List<Source> sources, CancellationToken ct)
    {
        string dir = Folder(a);
        Directory.CreateDirectory(dir);
        var existing = Directory.GetFiles(dir, "*.xml.gz").ToList();
        var keep = new List<string>();
        long bytes = 0;
        int fetched = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            string suffix = $"-{s.Country ?? "xx"}-{SafeName(s.Url)}.xml.gz";
            string file = Path.Combine(dir, $"{i:D2}{suffix}");
            // The same source at another position keeps its file.
            if (!File.Exists(file) && existing.FirstOrDefault(f => f.EndsWith(suffix, StringComparison.Ordinal)) is { } moved)
            {
                File.Move(moved, file, true);
                existing.Remove(moved);
            }
            bool fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < s.MaxAge;
            bool skip = Skipped.TryGetValue(s.Url, out var at) && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(1);
            if (!fresh && !skip)
            {
                try
                {
                    bytes += await FetchAsync(s.Url, file, s.Uncapped ? long.MaxValue : MaxFileBytes, ct);
                    fetched++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // 404s are normal here; keep an older copy if there is one.
                    if (ex is InvalidDataException || (ex as HttpRequestException)?.StatusCode == HttpStatusCode.NotFound) Skipped[s.Url] = DateTimeOffset.UtcNow;
                    Log.Debug("Guide source {Url} skipped: {Message}", s.Url, ex.Message);
                }
            }
            if (File.Exists(file)) keep.Add(file);
        }
        foreach (var f in Directory.GetFiles(dir))
            if (!keep.Contains(f)) File.Delete(f);
        if (fetched > 0) Log.Information("Guide sources downloaded: {Count} files, {Mb:F1} MB", fetched, bytes / 1e6);
        return keep;
    }

    private static async Task<long> FetchAsync(string url, string file, long cap, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", SettingsService.Current.UserAgent);
        using var res = await XtreamClient.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using var input = await res.Content.ReadAsStreamAsync(ct);
        string tmp = file + ".tmp";
        try
        {
            var head = new byte[2];
            int n = await input.ReadAtLeastAsync(head, 2, false, ct);
            bool gz = n == 2 && head[0] == 0x1f && head[1] == 0x8b;
            // Plain XML is about ten times its gzip size.
            long limit = gz ? cap : cap == long.MaxValue ? cap : cap * 10, total = n;
            await using (var output = File.Create(tmp))
            await using (Stream sink = gz ? output : new GZipStream(output, CompressionLevel.Fastest))
            {
                await sink.WriteAsync(head.AsMemory(0, n), ct);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > limit) throw new InvalidDataException("guide file too large");
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            File.Move(tmp, file, true);
            return total;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static string? FileCountry(string file)
    {
        string name = Path.GetFileName(file);
        var m = FileCountryTag().Match(name);
        if (m.Success) return m.Groups[1].Value == "xx" ? null : m.Groups[1].Value;
        m = FileCountryOld().Match(name);
        return m.Success ? Country(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static string? Country(string? cc)
    {
        if (string.IsNullOrEmpty(cc)) return null;
        cc = cc.ToLowerInvariant();
        return cc == "gb" ? "uk" : cc;
    }

    private static string? Code(string tag)
    {
        string t = tag.ToLowerInvariant();
        if (t.Length == 3) return Codes3.GetValueOrDefault(t);
        return Codes.Contains(t) ? Country(t) : null;
    }

    private static string? GroupCountry(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        var words = CatalogService.Normalize(group).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (CountryNames.TryGetValue(words[i], out var cc)) return cc;
            if (i + 1 < words.Length && CountryNames.TryGetValue(words[i] + " " + words[i + 1], out cc)) return cc;
        }
        foreach (Match m in UpperCode().Matches(group))
            if (m.Value != "AR" && Code(m.Value) is { } code) return code;
        return null;
    }

    // Name keys drop country tags, quality words and spaces: "|SE| TV4 FHD" and "TV4 HD SE" both become "tv4".
    private static NameKey Keys(string name)
    {
        // Clean first: compatibility forms would turn the decorative "ⓧ" into a plain x.
        var (clean, named) = ChannelNames.Matchable(name);
        string s = clean.Normalize(NormalizationForm.FormKC);
        // "AR |" is Arabic as often as Argentina.
        string? cc = named == "ar" ? null : Country(named);
        var m = CountryPrefix().Match(s);
        if (m.Success && Code(m.Groups[1].Value) is { } prefix)
        {
            // "AR |" is Arabic as often as Argentina.
            if (!m.Groups[1].Value.Equals("ar", StringComparison.OrdinalIgnoreCase)) cc ??= prefix;
            s = s[m.Length..];
        }
        m = CountryTail().Match(s);
        if (m.Success && m.Index > 0 && Code(m.Groups[1].Value) is { } tail)
        {
            cc ??= tail;
            s = s[..m.Index];
        }
        long shift = 0;
        m = Timeshift().Match(s);
        if (m.Success && int.Parse(m.Groups[1].Value) is var hours and >= 1 and <= 48)
        {
            shift = hours * Hour;
            s = s.Remove(m.Index, m.Length);
        }
        var words = CatalogService.Normalize(s.Replace("+", " plus ")).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Noise.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        while (words.Count > 1 && Noise.Contains(words[0])) words.RemoveAt(0);
        string key = string.Concat(words);
        // Playlists say "Pluto TV Action"; Pluto's own guide just says "Action".
        string? alt = key.StartsWith("plutotv") && key.Length > 7 ? key[7..] : null;
        if (shift == 0 || key.Length == 0) return new NameKey(key, alt, cc, 0, null);
        string tag = "+" + shift / Hour;
        return new NameKey(key + tag, alt == null ? null : alt + tag, cc, shift, key);
    }

    private static Index BuildIndex(List<Wanted> wanted)
    {
        var ix = new Index();
        foreach (var w in wanted)
        {
            ix.Countries.TryAdd(w.Key, w.Country);
            AddName(ix, w.Name, w.Key, 0);
            if (!w.UseId || w.Item.EpgId is not { } id || id.StartsWith("n:")) continue;
            string bare = ChannelNames.BareId(id);
            Index.Add(ix.Ids, bare, new Target(w.Key, 0));
            int at = bare.IndexOf('@');
            long feedShift = at > 0 && ShiftFeed().Match(bare[(at + 1)..]) is { Success: true } fm ? int.Parse(fm.Groups[1].Value) * Hour : 0;
            if (at > 0) Index.Add(feedShift > 0 ? ix.IdShifts : ix.Ids, bare[..at], new Target(w.Key, feedShift));
            AddName(ix, Keys(ChannelNames.NameFromId(id)), w.Key, feedShift);
        }
        return ix;
    }

    private static void AddName(Index ix, NameKey k, string key, long feedShift)
    {
        if (feedShift > 0)
        {
            Index.Add(ix.NameShifts, k.Key, new Target(key, feedShift));
            return;
        }
        Index.Add(ix.Names, k.Key, new Target(key, 0));
        Index.Add(ix.Names, k.Alt, new Target(key, 0));
        if (k.Shift > 0) Index.Add(ix.NameShifts, k.Base, new Target(key, k.Shift));
    }

    // Files are read in priority order; a channel keeps the first guide channel that has programmes for it.
    private static Guide Build(List<string> files, List<Wanted> wanted, DateTimeOffset fetchedAt, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ix = BuildIndex(wanted);
        var guide = new Guide { FetchedAt = fetchedAt };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        // Nothing in the app looks further than two days ahead or more than minutes back; public files carry up to a week.
        long cutoff = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
        long horizon = DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeMilliseconds();
        foreach (var file in files)
        {
            try
            {
                ReadFile(file, ix, taken, guide, cutoff, horizon, FileCountry(file), pool);
            }
            catch (Exception ex)
            {
                Log.Warning("Unreadable guide file {File}: {Message}", Path.GetFileName(file), ex.Message);
            }
        }
        foreach (var list in guide.ByChannel.Values) list.Sort((x, y) => x.Start.CompareTo(y.Start));
        guide.All.Sort((x, y) => x.End.CompareTo(y.End));
        Log.Information("{What} matched {Matched} of {Channels} channels in {Ms} ms", what, guide.ByChannel.Count, ix.Countries.Count, sw.ElapsedMilliseconds);
        return guide;
    }

    private static string? Share(Dictionary<string, string> pool, string? s)
    {
        if (s == null) return null;
        if (pool.TryGetValue(s, out var shared)) return shared;
        pool[s] = s;
        return s;
    }

    internal static string? WebUrl(string? url) =>
        url != null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ? url.Trim() : null;

    private static void ReadFile(string file, Index ix, HashSet<string> taken, Guide guide, long cutoff, long horizon, string? fileCountry, Dictionary<string, string> pool)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
        using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        using var reader = XmlReader.Create(stream, settings);
        var channels = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<Target>>? targets = null;
        var claimed = new List<string>();
        var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logos = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Name == "channel")
                {
                    string? id = reader.GetAttribute("id");
                    if (id == null) continue;
                    string bare = ChannelNames.BareId(id);
                    var channel = ReadChannel(reader, id, fileCountry, pool);
                    logos[bare] = channel.Icon;
                    if (targets == null) channels[bare] = channel;
                    // Some files list each channel right before its programmes.
                    else
                        foreach (var (guideId, list) in Match(new() { [bare] = channel }, ix, taken, claimed, guide))
                            if (targets.TryGetValue(guideId, out var have)) have.AddRange(list);
                            else targets[guideId] = list;
                }
                else if (reader.Name == "programme")
                {
                    targets ??= Match(channels, ix, taken, claimed, guide);
                    string? channel = reader.GetAttribute("channel");
                    if (channel == null) continue;
                    string guideId = ChannelNames.BareId(channel);
                    if (!targets.TryGetValue(guideId, out var list)) continue;
                    long start = GuideService.ParseTime(reader.GetAttribute("start"));
                    long end = GuideService.ParseTime(reader.GetAttribute("stop"));
                    if (start == 0 || end <= start) continue;

                    var (title, desc, category, icon, repeat) = GuideService.ReadDetails(reader);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    title = Share(pool, title.Trim())!;
                    if (Placeholders.Contains(title)) continue;
                    desc = Share(pool, desc?.Trim());
                    // Some guides repeat the channel logo as every programme's image.
                    string? image = WebUrl(icon) is { } url && url != logos.GetValueOrDefault(guideId) ? Share(pool, url) : null;
                    foreach (var t in list)
                    {
                        if (end + t.Shift < cutoff || start + t.Shift > horizon) continue;
                        var p = new Programme { EpgId = t.Key, Start = start + t.Shift, End = end + t.Shift, Title = title, Description = desc, Category = category, Image = image, Repeat = repeat };
                        if (!guide.ByChannel.TryGetValue(t.Key, out var progs)) guide.ByChannel[t.Key] = progs = new();
                        progs.Add(p);
                        guide.All.Add(p);
                        filled.Add(t.Key);
                    }
                }
            }
        }
        finally
        {
            // A guide channel without programmes leaves its channels to later files.
            foreach (var key in claimed)
            {
                if (filled.Contains(key)) continue;
                taken.Remove(key);
                guide.Logos.Remove(key);
            }
        }
    }

    private static Channel ReadChannel(XmlReader reader, string id, string? fileCountry, Dictionary<string, string> pool)
    {
        var names = new List<NameKey> { Keys(ChannelNames.NameFromId(id)) };
        string? icon = null;
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name") names.Add(Keys(sub.ReadElementContentAsString()));
                else if (sub.NodeType == XmlNodeType.Element && sub.Name == "icon")
                {
                    icon ??= WebUrl(sub.GetAttribute("src"));
                    sub.Read();
                }
                else sub.Read();
            }
        }
        string? country = Country(ChannelNames.CountryOf(id)) ?? names.Select(n => n.Country).FirstOrDefault(c => c != null) ?? fileCountry;
        return new Channel(names, country, Share(pool, icon));
    }

    private static Dictionary<string, List<Target>> Match(Dictionary<string, Channel> channels, Index ix, HashSet<string> taken, List<string> claimed, Guide guide)
    {
        var result = new Dictionary<string, List<Target>>(StringComparer.OrdinalIgnoreCase);
        void Claim(string guideId, Channel channel, List<Target>? targets, bool byName)
        {
            if (targets == null) return;
            foreach (var t in targets)
            {
                // "ESPN 2 (Netherlands)" must not get the American ESPN 2 guide just because the names agree.
                if (byName && channel.Country != null && ix.Countries.GetValueOrDefault(t.Key) is { } own && own != channel.Country) continue;
                if (!taken.Add(t.Key)) continue;
                claimed.Add(t.Key);
                if (!result.TryGetValue(guideId, out var list)) result[guideId] = list = new();
                list.Add(t);
                if (channel.Icon != null) guide.Logos.TryAdd(t.Key, channel.Icon);
            }
        }
        foreach (var (guideId, ch) in channels) Claim(guideId, ch, ix.Ids.GetValueOrDefault(guideId), false);
        foreach (var (guideId, ch) in channels)
            foreach (var k in ch.Names)
            {
                Claim(guideId, ch, ix.Names.GetValueOrDefault(k.Key), true);
                if (k.Alt != null) Claim(guideId, ch, ix.Names.GetValueOrDefault(k.Alt), true);
            }
        // Timeshift channels ("+1", "@Plus1") reuse the base channel's programmes, moved by the offset.
        foreach (var (guideId, ch) in channels)
        {
            Claim(guideId, ch, ix.IdShifts.GetValueOrDefault(guideId), false);
            foreach (var k in ch.Names)
                if (k.Shift == 0) Claim(guideId, ch, ix.NameShifts.GetValueOrDefault(k.Key), true);
        }
        return result;
    }
}
